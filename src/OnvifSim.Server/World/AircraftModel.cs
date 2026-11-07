using System.Text.Json.Serialization;

namespace OnvifSim.World;

// What an aircraft looks like in the pictures and in the editor; the shape is the only difference, the
// ADS-B side is the same for all of them (a drone can carry a transponder too).
public enum AircraftKind { Airplane, Helicopter, Drone }

// How the flight continues past the last waypoint. Loop joins the last waypoint to the first and goes
// round; Reverse flies the same curve back; Restart is gone from the sky for GapSeconds (it neither
// shows in the pictures nor transmits) and then appears at the first waypoint again.
public enum AircraftPathMode { Loop, Reverse, Restart }

// Straight legs between waypoints, or a smooth curve through them (centripetal Catmull-Rom: it passes
// through every waypoint and has no kinks, cusps or self-crossing loops inside a leg; at a sharp corner
// it swings a little wide of the waypoints, as any curve through them must).
public enum AircraftInterpolation { Linear, Spline }

// A point of a flight: a place on the ground plane (X east, Z south, as everywhere in the world) and an
// altitude above sea level in metres. The world's Geo.Elevation is the altitude of world y = 0, so a
// waypoint sits at world y = Altitude - Geo.Elevation.
public readonly record struct Waypoint(float X, float Z, float Altitude);

// An aircraft flying a path over and over, at a steady pace. Like walkers and cars, where it is follows
// from the clock, so every camera, every client and the ADS-B feed see it at the same spot. It carries
// the identity an ADS-B receiver would report (hex, callsign, squawk ...) and may be silenced to
// fly as a non-cooperative target that only the cameras can see.
public sealed class AircraftModel
{
    // URL-safe and unique in the world: it addresses the aircraft in the editor API.
    public string Id { get; set; } = "";
    public AircraftKind Kind { get; set; } = AircraftKind.Airplane;

    // One waypoint is an aircraft hovering there; the first is where a flight starts.
    public List<Waypoint> Waypoints { get; set; } = [];
    public AircraftInterpolation Interpolation { get; set; } = AircraftInterpolation.Spline;
    public AircraftPathMode Path { get; set; } = AircraftPathMode.Loop;
    // Metres per second along the path (true speed: a climb is part of it). 60 is about 117 knots.
    public float Speed { get; set; } = 60;
    // Seconds spent out of sight between two flights of a Restart path.
    public float GapSeconds { get; set; }
    // Shifts the flight in time, so aircraft sharing a path (or the clock's start) are not on top of
    // each other. The flight is at its first waypoint at second -OffsetSeconds of the Unix clock.
    public float OffsetSeconds { get; set; }

    // The 24-bit ICAO address, six hex digits: what /v2/hex/{hex} looks it up by.
    public string Hex { get; set; } = "";
    // Flight number or call sign, at most 8 characters (ADS-B's "flight").
    public string Callsign { get; set; } = "";
    // Tail number ("r") and ICAO type designator ("t"), shown in the feed when present.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Registration { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TypeCode { get; set; }
    // ADS-B emitter category: A1 light, A3 large, A7 rotorcraft, B6 UAV and so on.
    public string Category { get; set; } = "A1";
    // Four octal digits. 7700, 7600 and 7500 raise the feed's emergency field.
    public string Squawk { get; set; } = "1200";
    // Whether the feed reports it. Off, it is still in the pictures: a target no transponder tells about.
    public bool Transmitting { get; set; } = true;

    // Overall length nose to tail, and wingspan (rotor diameter for a helicopter, motor to motor for a
    // drone), in metres.
    public float Length { get; set; } = 8.3f;
    public float Wingspan { get; set; } = 11;
    // Body paint, and the wings and tail; null takes the kind's.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Color { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Color2 { get; set; }
}
