namespace OnvifSim.World;

// Where an aircraft is at a moment. X and Z are the world's ground coordinates, Altitude is metres above
// sea level (world y = Altitude - Geo.Elevation), Heading is the track in degrees clockwise from world
// north (-Z). GroundSpeed is the horizontal part of the aircraft's speed and VerticalSpeed the climb
// (negative in a descent), both in metres per second.
public readonly record struct AircraftPose(float X, float Z, float Altitude, float Heading, float GroundSpeed, float VerticalSpeed);

// An aircraft's flight, compiled once from its waypoints: the curve through them sampled finely enough
// that straight lines between samples are the curve for every purpose here, with the distance flown
// at each sample. Flying it at a steady pace is then a lookup by distance, so the speed is constant
// along a spline too (a parameter-based spline would hurry through short stretches), and the same
// moment always gives the same place, wherever it is asked, like RouteMotion does for walkers and cars.
// The editor repeats this arithmetic in model.ts (flightPathOf); keep the two in step.
public sealed class FlightPath
{
    // A spline leg gets one sample per ~5 m of chord, at least 8 and at most 64.
    private const double ChordPerSample = 5;
    private const int MinSamples = 8;
    private const int MaxSamples = 64;
    // Two waypoints this close (in the spline's knot spacing) count as one, so no divisor is zero.
    private const double MinKnot = 1e-4;

    private readonly double[] _x;
    private readonly double[] _z;
    private readonly double[] _altitude;
    // Metres flown, along the 3D curve, from the start to each sample.
    private readonly double[] _distance;
    // For the leg from sample i to i + 1: its heading, and the shares of its length that run level and up.
    private readonly double[] _heading;
    private readonly double[] _levelShare;
    private readonly double[] _climbShare;
    private readonly double _speed;
    private readonly double _offset;
    private readonly double _gap;
    private readonly AircraftPathMode _mode;

    // Metres of the curve, one way round or one way along.
    public double Length { get; }
    public int Samples => _x.Length;

    private FlightPath(AircraftModel aircraft, List<(double X, double Z, double A)> curve, AircraftPathMode mode)
    {
        int n = curve.Count;
        (_x, _z, _altitude) = (new double[n], new double[n], new double[n]);
        _distance = new double[n];
        for (int i = 0; i < n; i++)
        {
            (_x[i], _z[i], _altitude[i]) = (curve[i].X, curve[i].Z, curve[i].A);
            if (i > 0) _distance[i] = _distance[i - 1] + Math.Sqrt(Sq(_x[i] - _x[i - 1]) + Sq(_z[i] - _z[i - 1]) + Sq(_altitude[i] - _altitude[i - 1]));
        }
        Length = n == 0 ? 0 : _distance[n - 1];

        int legs = Math.Max(0, n - 1);
        (_heading, _levelShare, _climbShare) = (new double[legs], new double[legs], new double[legs]);
        var known = new bool[legs];
        for (int i = 0; i < legs; i++)
        {
            double dx = _x[i + 1] - _x[i], dz = _z[i + 1] - _z[i], da = _altitude[i + 1] - _altitude[i];
            double level = Math.Sqrt(dx * dx + dz * dz), leg = _distance[i + 1] - _distance[i];
            _levelShare[i] = leg > 0 ? level / leg : 0;
            _climbShare[i] = leg > 0 ? da / leg : 0;
            if (level > 1e-9)
            {
                _heading[i] = Normalize(Math.Atan2(dx, -dz) * 180 / Math.PI);
                known[i] = true;
            }
        }
        // A leg straight up or down has no heading of its own: it keeps the one before it, or the first
        // one there is.
        int first = Array.IndexOf(known, true);
        for (int i = 0; i < legs; i++)
            if (!known[i]) _heading[i] = i > 0 ? _heading[i - 1] : first >= 0 ? _heading[first] : 0;

        _speed = aircraft.Speed;
        _offset = aircraft.OffsetSeconds;
        _gap = Math.Max(0, aircraft.GapSeconds);
        _mode = mode;
    }

    public static FlightPath Build(AircraftModel aircraft)
    {
        var waypoints = aircraft.Waypoints.Select(w => (X: (double)w.X, Z: (double)w.Z, A: (double)w.Altitude)).ToList();
        int n = waypoints.Count;
        // Two waypoints joined into a loop are a line flown there and back.
        var mode = aircraft.Path == AircraftPathMode.Loop && n < 3 ? AircraftPathMode.Reverse : aircraft.Path;
        bool closed = mode == AircraftPathMode.Loop;
        bool spline = aircraft.Interpolation == AircraftInterpolation.Spline && n >= 3;

        var curve = new List<(double X, double Z, double A)>();
        int segments = closed ? n : n - 1;
        for (int i = 0; i < segments; i++)
        {
            var p1 = waypoints[i];
            var p2 = waypoints[(i + 1) % n];
            if (!spline)
            {
                curve.Add(p1);
                continue;
            }
            var p0 = closed ? waypoints[(i - 1 + n) % n] : i > 0 ? waypoints[i - 1] : Reflect(p2, p1);
            var p3 = closed ? waypoints[(i + 2) % n] : i + 2 < n ? waypoints[i + 2] : Reflect(p1, p2);
            int samples = Math.Clamp((int)Math.Ceiling(Distance(p1, p2) / ChordPerSample), MinSamples, MaxSamples);
            for (int k = 0; k < samples; k++) curve.Add(CatmullRom(p0, p1, p2, p3, (double)k / samples));
        }
        // The curve ends where the last leg does: back at the start for a loop, at the last waypoint otherwise.
        if (n > 0) curve.Add(waypoints[closed ? 0 : n - 1]);
        return new FlightPath(aircraft, curve, mode);
    }

    // Where the aircraft is at a moment, or null while a Restart path has it out of sight.
    public AircraftPose? PoseAt(DateTimeOffset time)
    {
        if (Samples == 0) return null;
        if (Length <= 0 || _speed <= 0) return Hovering();

        double now = time.ToUnixTimeMilliseconds() / 1000.0 + _offset;
        double flight = Length / _speed;
        double cycle = _mode switch
        {
            AircraftPathMode.Loop => flight,
            AircraftPathMode.Reverse => 2 * flight,
            _ => flight + _gap,
        };
        double c = Mod(now, cycle);
        bool back = false;
        if (_mode == AircraftPathMode.Restart && c > flight) return null;
        if (_mode == AircraftPathMode.Reverse && c > flight)
        {
            back = true;
            c = 2 * flight - c;
        }
        return At(c * _speed, back);
    }

    private AircraftPose Hovering() =>
        new((float)_x[0], (float)_z[0], (float)_altitude[0], _heading.Length > 0 ? (float)_heading[0] : 0, 0, 0);

    // The pose after flying `along` metres of the curve; back means flying it from its far end.
    private AircraftPose At(double along, bool back)
    {
        // The last leg that starts at or before the distance (the loop skips zero-length legs by itself).
        int lo = 0, hi = Samples - 2;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_distance[mid] <= along) lo = mid;
            else hi = mid - 1;
        }
        double leg = _distance[lo + 1] - _distance[lo];
        double f = leg > 0 ? Math.Clamp((along - _distance[lo]) / leg, 0, 1) : 0;
        double heading = back ? Normalize(_heading[lo] + 180) : _heading[lo];
        double climb = _speed * _climbShare[lo] * (back ? -1 : 1);
        return new AircraftPose(
            (float)(_x[lo] + (_x[lo + 1] - _x[lo]) * f),
            (float)(_z[lo] + (_z[lo + 1] - _z[lo]) * f),
            (float)(_altitude[lo] + (_altitude[lo + 1] - _altitude[lo]) * f),
            (float)heading, (float)(_speed * _levelShare[lo]), (float)climb);
    }

    // Centripetal Catmull-Rom (alpha 0.5) between p1 and p2, by the Barry-Goldman pyramid; u runs 0..1.
    private static (double X, double Z, double A) CatmullRom(
        (double X, double Z, double A) p0, (double X, double Z, double A) p1,
        (double X, double Z, double A) p2, (double X, double Z, double A) p3, double u)
    {
        double t0 = 0;
        double t1 = t0 + Knot(p0, p1);
        double t2 = t1 + Knot(p1, p2);
        double t3 = t2 + Knot(p2, p3);
        double t = t1 + (t2 - t1) * u;
        var a1 = Mix(p0, p1, (t1 - t) / (t1 - t0), (t - t0) / (t1 - t0));
        var a2 = Mix(p1, p2, (t2 - t) / (t2 - t1), (t - t1) / (t2 - t1));
        var a3 = Mix(p2, p3, (t3 - t) / (t3 - t2), (t - t2) / (t3 - t2));
        var b1 = Mix(a1, a2, (t2 - t) / (t2 - t0), (t - t0) / (t2 - t0));
        var b2 = Mix(a2, a3, (t3 - t) / (t3 - t1), (t - t1) / (t3 - t1));
        return Mix(b1, b2, (t2 - t) / (t2 - t1), (t - t1) / (t2 - t1));
    }

    private static double Knot((double X, double Z, double A) a, (double X, double Z, double A) b) =>
        Math.Max(Math.Sqrt(Distance(a, b)), MinKnot);

    private static (double X, double Z, double A) Mix((double X, double Z, double A) a, (double X, double Z, double A) b, double wa, double wb) =>
        (a.X * wa + b.X * wb, a.Z * wa + b.Z * wb, a.A * wa + b.A * wb);

    // The point beyond `through`, as far from it as `from` is on the other side: the missing neighbour of an
    // end waypoint, which makes the curve leave it as if the path went straight on.
    private static (double X, double Z, double A) Reflect((double X, double Z, double A) from, (double X, double Z, double A) through) =>
        (2 * through.X - from.X, 2 * through.Z - from.Z, 2 * through.A - from.A);

    private static double Distance((double X, double Z, double A) a, (double X, double Z, double A) b) =>
        Math.Sqrt(Sq(b.X - a.X) + Sq(b.Z - a.Z) + Sq(b.A - a.A));

    private static double Sq(double v) => v * v;
    private static double Mod(double value, double modulus) => ((value % modulus) + modulus) % modulus;
    private static double Normalize(double degrees) => Mod(degrees, 360);
}
