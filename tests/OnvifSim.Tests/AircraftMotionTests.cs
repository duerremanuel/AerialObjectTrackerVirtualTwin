using OnvifSim.World;

namespace OnvifSim.Tests;

public class AircraftMotionTests
{
    private static AircraftModel Plane(AircraftPathMode path, AircraftInterpolation interpolation, float speed, params Waypoint[] waypoints) =>
        new() { Id = "a1", Hex = "a00001", Waypoints = [.. waypoints], Path = path, Interpolation = interpolation, Speed = speed };

    private static DateTimeOffset At(double seconds) => DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(seconds * 1000));

    private static AircraftPose Pose(AircraftModel aircraft, double seconds) => FlightPath.Build(aircraft).PoseAt(At(seconds))!.Value;

    private static double Between(AircraftPose a, AircraftPose b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Z - b.Z, 2) + Math.Pow(a.Altitude - b.Altitude, 2));

    // A rounded square 100 m across with the altitude varying round it, so a spline has something to do.
    private static readonly Waypoint[] Square =
        [new(0, 0, 100), new(100, 0, 150), new(100, -100, 120), new(0, -100, 100)];

    [Fact]
    public void OneWaypoint_IsAnAircraftHoveringThere()
    {
        var hover = Plane(AircraftPathMode.Loop, AircraftInterpolation.Spline, 60, new Waypoint(10, -20, 150));
        var pose = Pose(hover, 12345);
        Assert.Equal((10f, -20f, 150f, 0f, 0f), (pose.X, pose.Z, pose.Altitude, pose.GroundSpeed, pose.VerticalSpeed));
    }

    [Fact]
    public void AStraightFlight_GoesAtItsSpeed_ThenBackTheSameWay()
    {
        // East is +X. 100 m at 10 m/s takes 10 s each way.
        var plane = Plane(AircraftPathMode.Reverse, AircraftInterpolation.Linear, 10, new Waypoint(0, 0, 100), new Waypoint(100, 0, 100));

        var outbound = Pose(plane, 5);
        Assert.Equal((50f, 0f, 100f, 90f, 10f), (outbound.X, outbound.Z, outbound.Altitude, outbound.Heading, outbound.GroundSpeed));

        var back = Pose(plane, 12);
        Assert.Equal((80f, 270f), (back.X, back.Heading));
    }

    [Fact]
    public void TwoWaypointsJoinedIntoALoop_AreAFlightThereAndBack()
    {
        var plane = Plane(AircraftPathMode.Loop, AircraftInterpolation.Spline, 10, new Waypoint(0, 0, 100), new Waypoint(100, 0, 100));
        Assert.Equal(80f, Pose(plane, 12).X);
    }

    [Fact]
    public void North_IsMinusZ()
    {
        var plane = Plane(AircraftPathMode.Reverse, AircraftInterpolation.Linear, 10, new Waypoint(0, 0, 100), new Waypoint(0, -100, 100));
        var pose = Pose(plane, 5);
        Assert.Equal((-50f, 0f), (pose.Z, pose.Heading));
    }

    [Fact]
    public void AClimb_SplitsTheSpeedIntoGroundSpeedAndClimbRate()
    {
        // 60 m along and 80 m up is 100 m of flight: at 10 m/s that is 6 m/s over the ground and 8 m/s up.
        var plane = Plane(AircraftPathMode.Reverse, AircraftInterpolation.Linear, 10, new Waypoint(0, 0, 0), new Waypoint(0, -60, 80));

        var up = Pose(plane, 5);
        Assert.Equal(6.0, up.GroundSpeed, 3);
        Assert.Equal(8.0, up.VerticalSpeed, 3);
        Assert.Equal(40.0, up.Altitude, 3);

        var down = Pose(plane, 15);
        Assert.Equal(-8.0, down.VerticalSpeed, 3);
        Assert.Equal(180f, down.Heading);
    }

    [Fact]
    public void AFlightStraightUp_KeepsTheHeadingItHad()
    {
        var plane = Plane(AircraftPathMode.Reverse, AircraftInterpolation.Linear, 10,
            new Waypoint(0, 0, 0), new Waypoint(100, 0, 0), new Waypoint(100, 0, 100));
        var climb = Pose(plane, 15);
        Assert.Equal((100f, 90f, 0f, 10f), (climb.X, climb.Heading, climb.GroundSpeed, climb.VerticalSpeed));
    }

    [Fact]
    public void ALoop_ComesBackToItsStart_EveryRound()
    {
        var plane = Plane(AircraftPathMode.Loop, AircraftInterpolation.Spline, 40, Square);
        var round = FlightPath.Build(plane).Length / 40;

        var start = Pose(plane, 0);
        Assert.Equal((0f, 0f, 100f), (start.X, start.Z, start.Altitude));
        // The clock ticks in milliseconds, which at 40 m/s is 4 cm of flight.
        Assert.True(Between(start, Pose(plane, round)) < 0.1);
        Assert.True(Between(Pose(plane, 3.7), Pose(plane, 3.7 + 5 * round)) < 0.1);
    }

    [Fact]
    public void ASpline_PassesThroughEveryWaypoint()
    {
        var plane = Plane(AircraftPathMode.Loop, AircraftInterpolation.Spline, 40, Square);
        var round = FlightPath.Build(plane).Length / 40;
        var closest = Square.Select(_ => double.MaxValue).ToArray();
        for (double t = 0; t < round; t += 0.01)
        {
            var pose = Pose(plane, t);
            for (int i = 0; i < Square.Length; i++)
                closest[i] = Math.Min(closest[i], Between(pose, new AircraftPose(Square[i].X, Square[i].Z, Square[i].Altitude, 0, 0, 0)));
        }
        Assert.All(closest, distance => Assert.True(distance < 0.3, $"passed {distance:0.00} m from a waypoint"));
    }

    [Fact]
    public void ASpline_IsFlownAtAConstantSpeed()
    {
        var plane = Plane(AircraftPathMode.Loop, AircraftInterpolation.Spline, 50, Square);
        for (double t = 0; t < 12; t += 0.1)
            Assert.InRange(Between(Pose(plane, t), Pose(plane, t + 0.1)), 4.95, 5.0001);
    }

    [Fact]
    public void ASpline_TurnsSmoothly_WhereStraightLegsTurnAtOnce()
    {
        float WidestTurn(AircraftInterpolation interpolation)
        {
            var plane = Plane(AircraftPathMode.Loop, interpolation, 50, Square);
            float widest = 0;
            for (double t = 0; t < 12; t += 0.1)
            {
                float turn = Math.Abs(Pose(plane, t + 0.1).Heading - Pose(plane, t).Heading);
                widest = Math.Max(widest, turn > 180 ? 360 - turn : turn);
            }
            return widest;
        }

        // Straight legs turn the whole 90 degrees between one sample and the next; the spline spreads each
        // corner over many metres.
        Assert.True(WidestTurn(AircraftInterpolation.Linear) > 80);
        Assert.True(WidestTurn(AircraftInterpolation.Spline) < 25);
    }

    [Fact]
    public void ARestartingFlight_IsOutOfSightBetweenFlights()
    {
        var plane = Plane(AircraftPathMode.Restart, AircraftInterpolation.Linear, 10, new Waypoint(0, 0, 100), new Waypoint(100, 0, 100));
        plane.GapSeconds = 5;
        var path = FlightPath.Build(plane);

        Assert.Equal(30f, path.PoseAt(At(3))!.Value.X);
        Assert.Null(path.PoseAt(At(12)));
        // The next flight starts 15 s after the last one began.
        Assert.Equal(5f, path.PoseAt(At(15.5))!.Value.X);
    }

    [Fact]
    public void TheTimeOffset_ShiftsTheFlight()
    {
        var plane = Plane(AircraftPathMode.Reverse, AircraftInterpolation.Linear, 10, new Waypoint(0, 0, 100), new Waypoint(100, 0, 100));
        // Two seconds ahead of the clock: 20 m along already.
        plane.OffsetSeconds = 2;
        Assert.Equal(20f, Pose(plane, 0).X);
        // Twelve seconds behind it: eight seconds into the flight, 80 m along.
        plane.OffsetSeconds = -12;
        Assert.Equal(80f, Pose(plane, 0).X);
    }

    [Fact]
    public void AnAircraftWithoutSpeed_StaysAtTheStart_FacingTheNextWaypoint()
    {
        var plane = Plane(AircraftPathMode.Loop, AircraftInterpolation.Linear, 0, new Waypoint(3, 4, 100), new Waypoint(3, -10, 100), new Waypoint(30, -10, 100));
        var pose = Pose(plane, 999);
        Assert.Equal((3f, 4f, 0f, 0f), (pose.X, pose.Z, pose.Heading, pose.GroundSpeed));
    }

    [Fact]
    public void NoWaypoints_IsNoAircraft()
    {
        Assert.Null(FlightPath.Build(Plane(AircraftPathMode.Loop, AircraftInterpolation.Spline, 10)).PoseAt(At(0)));
    }

    [Fact]
    public void RepeatedWaypoints_DoNotBreakTheCurve()
    {
        var plane = Plane(AircraftPathMode.Loop, AircraftInterpolation.Spline, 30,
            new Waypoint(0, 0, 100), new Waypoint(0, 0, 100), new Waypoint(80, 0, 100), new Waypoint(80, 0, 100), new Waypoint(80, -80, 120));
        for (double t = 0; t < 30; t += 0.25)
        {
            var pose = Pose(plane, t);
            Assert.True(float.IsFinite(pose.X) && float.IsFinite(pose.Z) && float.IsFinite(pose.Altitude) && float.IsFinite(pose.Heading),
                $"not finite at {t} s");
        }
    }

    // Pins the arithmetic the editor repeats in model.ts (flightPathOf): if either side changes, the
    // aircraft in the editor and the ones in the feed would drift apart. The same values are asserted in
    // the editor's own check.
    [Fact]
    public void TheSplineFlight_MatchesTheEditorsReference()
    {
        var plane = Plane(AircraftPathMode.Loop, AircraftInterpolation.Spline, 40, Square);
        foreach (var (seconds, x, z, altitude, heading) in SplineReference)
        {
            var pose = Pose(plane, seconds);
            Assert.Equal(x, pose.X, 2);
            Assert.Equal(z, pose.Z, 2);
            Assert.Equal(altitude, pose.Altitude, 2);
            Assert.Equal(heading, pose.Heading, 1);
        }
    }

    // (seconds, x, z, altitude, heading) of the Square flown as a loop spline at 40 m/s.
    public static readonly (double, double, double, double, double)[] SplineReference =
    [
        (0.5, 15.65, 8.80, 108.36, 108.6),
        (3.3, 106.86, -12.80, 149.21, 19.5),
        (7.1, 46.95, -112.46, 107.64, 268.0),
        (10.9, -1.88, -2.51, 99.09, 143.2),
    ];
}

public class AircraftFleetTests
{
    private static WorldModel WorldWith(params AircraftModel[] aircraft)
    {
        var world = new WorldModel { Geo = new GeoModel { Latitude = 39.2, Longitude = -77.35, Elevation = 10 } };
        world.Aircraft.AddRange(aircraft);
        return world;
    }

    private static AircraftModel Hover(string id, float x, float z, float altitude) =>
        new() { Id = id, Hex = "a00001", Waypoints = [new Waypoint(x, z, altitude)] };

    [Fact]
    public void AnAircraftAboveTheOrigin_IsAtTheGeoAnchor_AtItsAltitude()
    {
        var world = WorldWith(Hover("h", 0, 0, 110));
        var fleet = AircraftFleet.Build(world, new Terrain(world.Terrain));

        var state = Assert.Single(fleet.At(DateTimeOffset.UtcNow));
        Assert.Equal(39.2, state.Geo.Latitude, 9);
        Assert.Equal(-77.35, state.Geo.Longitude, 9);
        Assert.Equal(110, state.Geo.Elevation, 3);
        // The world's y = 0 is the anchor's 10 m above sea level.
        Assert.Equal(100, state.WorldY, 3);
        Assert.False(state.OnGround);
    }

    [Fact]
    public void AnAircraftOnTheTerrain_IsOnTheGround()
    {
        var world = WorldWith(Hover("low", 0, 0, 10.3f), Hover("high", 0, 0, 12));
        var fleet = AircraftFleet.Build(world, new Terrain(world.Terrain));
        Assert.True(fleet.Find("low", DateTimeOffset.UtcNow)!.OnGround);
        Assert.False(fleet.Find("high", DateTimeOffset.UtcNow)!.OnGround);
    }

    [Fact]
    public void ANorthwardOffset_IsALatitudeStep()
    {
        var world = WorldWith(Hover("n", 0, -1000, 100));
        var state = AircraftFleet.Build(world, new Terrain(world.Terrain)).Find("n", DateTimeOffset.UtcNow)!;
        // 1000 m is about 0.009 degrees of latitude.
        Assert.Equal(39.2 + 1000 / WorldGeo.EarthRadiusM * 180 / Math.PI, state.Geo.Latitude, 9);
        Assert.Equal(-77.35, state.Geo.Longitude, 9);
    }

    [Fact]
    public void AnAircraftOutOfSightOnARestartPath_IsNotThere()
    {
        var gone = new AircraftModel
        {
            Id = "gone", Hex = "a00002", Path = AircraftPathMode.Restart, Speed = 10, GapSeconds = 10,
            Interpolation = AircraftInterpolation.Linear, Waypoints = [new Waypoint(0, 0, 100), new Waypoint(10, 0, 100)],
        };
        var world = WorldWith(gone);
        var fleet = AircraftFleet.Build(world, new Terrain(world.Terrain));
        Assert.Single(fleet.At(DateTimeOffset.FromUnixTimeSeconds(0)));
        Assert.Empty(fleet.At(DateTimeOffset.FromUnixTimeSeconds(5)));
        Assert.Null(fleet.Find("gone", DateTimeOffset.FromUnixTimeSeconds(5)));
    }
}
