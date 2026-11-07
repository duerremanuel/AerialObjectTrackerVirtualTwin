using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OnvifSim.World;

namespace OnvifSim.Adsb;

// The simulated aircraft, served the way adsb.lol serves real ones: same paths (at the server's root, so a
// client only needs its base URL pointed here), same JSON envelope and the fields of a readsb aircraft
// record, in the same units (feet, knots, feet per minute, nautical miles). Only what a tracker needs is
// there:
//
//   GET /v2/point/{lat}/{lon}/{radius}          aircraft within radius nautical miles of a point
//   GET /v2/lat/{lat}/lon/{lon}/dist/{radius}   the same, in the older readsb spelling
//   GET /v2/hex/{hex}  and  /v2/icao/{hex}      one aircraft by its ICAO address (several, comma-separated)
//
// An aircraft out of sight on a Restart path, or with its transponder off, is not reported. The answer
// is what the world holds at the moment of the request: there is no receiver delay, noise or dropout.
public static class AdsbApi
{
    // adsb.lol's own limit on the radius of a point query, nautical miles.
    public const double MaxRadiusNm = 250;

    private const double MetresPerNauticalMile = 1852;
    private const double FeetPerMetre = 3.280839895;
    private const double KnotsPerMetrePerSecond = 1.943844492;
    private const double FeetPerMinutePerMetrePerSecond = 196.8503937;
    // The sphere the distance and bearing of a query are worked out on.
    private const double EarthRadiusMetres = 6371008.8;

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static void MapAdsb(this WebApplication app)
    {
        app.MapGet("/v2/point/{lat}/{lon}/{radius}", Around);
        app.MapGet("/v2/lat/{lat}/lon/{lon}/dist/{radius}", Around);
        app.MapGet("/v2/hex/{hex}", ByHex);
        app.MapGet("/v2/icao/{hex}", ByHex);
    }

    private static IResult Around(string lat, string lon, string radius, HttpContext http, SceneState scene, TimeProvider time)
    {
        var problems = new List<Problem>();
        double? latitude = Number(lat, "lat", -90, 90, problems);
        double? longitude = Number(lon, "lon", -180, 180, problems);
        double? range = Number(radius, "radius", 0, MaxRadiusNm, problems);
        if (problems.Count > 0) return Reply(http, new { detail = problems }, StatusCodes.Status422UnprocessableEntity);

        var now = time.GetUtcNow();
        var found = scene.Current.Aircraft.At(now)
            .Where(s => s.Aircraft.Transmitting)
            .Select(s => (State: s, Distance: Distance(latitude!.Value, longitude!.Value, s.Geo.Latitude, s.Geo.Longitude)))
            .Where(f => f.Distance <= range!.Value * MetresPerNauticalMile)
            .OrderBy(f => f.Distance)
            .Select(f => Record(f.State, now, f.Distance / MetresPerNauticalMile,
                Bearing(latitude!.Value, longitude!.Value, f.State.Geo.Latitude, f.State.Geo.Longitude)))
            .ToList();
        return Reply(http, Envelope(found, now));
    }

    private static IResult ByHex(string hex, HttpContext http, SceneState scene, TimeProvider time)
    {
        var wanted = hex.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(h => h.TrimStart('~').ToLowerInvariant())
            .ToHashSet();
        var now = time.GetUtcNow();
        var found = scene.Current.Aircraft.At(now)
            .Where(s => s.Aircraft.Transmitting && wanted.Contains(s.Aircraft.Hex.ToLowerInvariant()))
            .Select(s => Record(s, now, null, null))
            .ToList();
        // Like the real service, an address nobody transmits is an empty answer, not an error.
        return Reply(http, Envelope(found, now));
    }

    private static AdsbResponse Envelope(List<AdsbAircraft> aircraft, DateTimeOffset now)
    {
        long ms = now.ToUnixTimeMilliseconds();
        return new AdsbResponse { Ac = aircraft, Msg = "No error", Now = ms, Total = aircraft.Count, Ctime = ms, Ptime = 0 };
    }

    // One aircraft as readsb writes it. dst (nautical miles) and dir (degrees) say where it is from the point
    // asked about, and only a query around a point has one.
    internal static AdsbAircraft Record(AircraftState state, DateTimeOffset now, double? distanceNm, double? bearing)
    {
        var a = state.Aircraft;
        var pose = state.Pose;
        int feet = (int)Math.Round(state.Geo.Elevation * FeetPerMetre);
        int rate = (int)Math.Round(pose.VerticalSpeed * FeetPerMinutePerMetrePerSecond);
        return new AdsbAircraft
        {
            Hex = a.Hex.ToLowerInvariant(),
            Type = "adsb_icao",
            Flight = a.Callsign.Length > 0 ? a.Callsign.PadRight(8) : null,
            R = string.IsNullOrEmpty(a.Registration) ? null : a.Registration,
            T = string.IsNullOrEmpty(a.TypeCode) ? null : a.TypeCode,
            // The pressure altitude of a standard atmosphere is the height above sea level.
            AltBaro = state.OnGround ? "ground" : feet,
            AltGeom = state.OnGround ? null : feet,
            Gs = Math.Round(pose.GroundSpeed * KnotsPerMetrePerSecond, 1),
            Track = Math.Round(pose.Heading, 1),
            BaroRate = state.OnGround ? null : rate,
            GeomRate = state.OnGround ? null : rate,
            Squawk = a.Squawk,
            Emergency = a.Squawk switch { "7700" => "general", "7600" => "nordo", "7500" => "unlawful", _ => "none" },
            Category = a.Category,
            Lat = Math.Round(state.Geo.Latitude, 6),
            Lon = Math.Round(state.Geo.Longitude, 6),
            Messages = (int)(now.ToUnixTimeMilliseconds() / 500 % 1_000_000),
            Dst = distanceNm is { } d ? Math.Round(d, 3) : null,
            Dir = bearing is { } b ? Math.Round(b, 1) : null,
        };
    }

    // Reads a path number, noting a problem in the shape of the service's validation errors when it is not
    // one or lies outside its range.
    private static double? Number(string text, string name, double min, double max, List<Problem> problems)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
        {
            problems.Add(new Problem("float_parsing", ["path", name], "Input should be a valid number, unable to parse string as a number", text));
            return null;
        }
        if (value < min)
            problems.Add(new Problem("greater_than_equal", ["path", name], $"Input should be greater than or equal to {min.ToString(CultureInfo.InvariantCulture)}", text));
        else if (value > max)
            problems.Add(new Problem("less_than_equal", ["path", name], $"Input should be less than or equal to {max.ToString(CultureInfo.InvariantCulture)}", text));
        else
            return value;
        return null;
    }

    // Great-circle distance in metres (haversine).
    internal static double Distance(double lat1, double lon1, double lat2, double lon2)
    {
        double p1 = Radians(lat1), p2 = Radians(lat2);
        double dp = p2 - p1, dl = Radians(lon2 - lon1);
        double h = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
        return 2 * EarthRadiusMetres * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    // Initial bearing from the first point to the second, degrees clockwise from true north.
    internal static double Bearing(double lat1, double lon1, double lat2, double lon2)
    {
        double p1 = Radians(lat1), p2 = Radians(lat2), dl = Radians(lon2 - lon1);
        double y = Math.Sin(dl) * Math.Cos(p2);
        double x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
        return ((Math.Atan2(y, x) * 180 / Math.PI) % 360 + 360) % 360;
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180;

    // Browsers may call the feed too (tar1090-style pages do), and adsb.lol allows any origin.
    private static IResult Reply(HttpContext http, object body, int status = StatusCodes.Status200OK)
    {
        http.Response.Headers.AccessControlAllowOrigin = "*";
        return Results.Json(body, Json, statusCode: status);
    }

    internal sealed record Problem(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("loc")] string[] Loc,
        [property: JsonPropertyName("msg")] string Msg,
        [property: JsonPropertyName("input")] string Input);
}

// The envelope of every answer. now and ctime are milliseconds since the Unix epoch.
public sealed class AdsbResponse
{
    [JsonPropertyName("ac")] public List<AdsbAircraft> Ac { get; set; } = [];
    [JsonPropertyName("msg")] public string Msg { get; set; } = "No error";
    [JsonPropertyName("now")] public long Now { get; set; }
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("ctime")] public long Ctime { get; set; }
    [JsonPropertyName("ptime")] public long Ptime { get; set; }
}

// An aircraft record, in readsb's field names and order. Fields it leaves out when it has no value (a
// call sign nobody set, the altitude of an aircraft on the ground) are left out here too.
public sealed class AdsbAircraft
{
    [JsonPropertyName("hex")] public string Hex { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "adsb_icao";
    [JsonPropertyName("flight")] public string? Flight { get; set; }
    [JsonPropertyName("r")] public string? R { get; set; }
    [JsonPropertyName("t")] public string? T { get; set; }
    // Feet above the standard pressure datum, or the string "ground".
    [JsonPropertyName("alt_baro")] public object? AltBaro { get; set; }
    [JsonPropertyName("alt_geom")] public int? AltGeom { get; set; }
    [JsonPropertyName("gs")] public double? Gs { get; set; }
    [JsonPropertyName("track")] public double? Track { get; set; }
    [JsonPropertyName("baro_rate")] public int? BaroRate { get; set; }
    [JsonPropertyName("geom_rate")] public int? GeomRate { get; set; }
    [JsonPropertyName("squawk")] public string? Squawk { get; set; }
    [JsonPropertyName("emergency")] public string Emergency { get; set; } = "none";
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("lat")] public double Lat { get; set; }
    [JsonPropertyName("lon")] public double Lon { get; set; }
    // The quality figures of a good ADS-B v2 transponder with a GPS fix.
    [JsonPropertyName("nic")] public int Nic { get; set; } = 8;
    [JsonPropertyName("rc")] public int Rc { get; set; } = 186;
    [JsonPropertyName("seen_pos")] public double SeenPos { get; set; } = 0.1;
    [JsonPropertyName("version")] public int Version { get; set; } = 2;
    [JsonPropertyName("nic_baro")] public int NicBaro { get; set; } = 1;
    [JsonPropertyName("nac_p")] public int NacP { get; set; } = 9;
    [JsonPropertyName("nac_v")] public int NacV { get; set; } = 1;
    [JsonPropertyName("sil")] public int Sil { get; set; } = 3;
    [JsonPropertyName("sil_type")] public string SilType { get; set; } = "perhour";
    [JsonPropertyName("gva")] public int Gva { get; set; } = 2;
    [JsonPropertyName("sda")] public int Sda { get; set; } = 2;
    [JsonPropertyName("alert")] public int Alert { get; set; }
    [JsonPropertyName("spi")] public int Spi { get; set; }
    [JsonPropertyName("mlat")] public string[] Mlat { get; set; } = [];
    [JsonPropertyName("tisb")] public string[] Tisb { get; set; } = [];
    [JsonPropertyName("messages")] public int Messages { get; set; }
    [JsonPropertyName("seen")] public double Seen { get; set; } = 0.1;
    [JsonPropertyName("rssi")] public double Rssi { get; set; } = -18.0;
    // Nautical miles and degrees from the point of a point query; absent in a lookup by address.
    [JsonPropertyName("dst")] public double? Dst { get; set; }
    [JsonPropertyName("dir")] public double? Dir { get; set; }
}
