using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using OnvifSim.Adsb;
using OnvifSim.World;

namespace OnvifSim.Tests;

// The pieces of the adsb.lol look-alike that need no server: units, field names and the great-circle
// arithmetic of a radius query.
public class AdsbRecordTests
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

    private static AircraftModel Model(Action<AircraftModel>? tweak = null)
    {
        var model = new AircraftModel { Id = "a1", Hex = "A1B2C3", Callsign = "TEST01", Waypoints = [new Waypoint(0, 0, 300)] };
        tweak?.Invoke(model);
        return model;
    }

    private static AircraftState Flying(AircraftModel model, float altitude = 300, float groundSpeed = 50, float climb = 5, bool onGround = false) =>
        new(model, new AircraftPose(0, 0, altitude, 123.5f, groundSpeed, climb), new GeoPosition(39.2, -77.35, altitude), altitude, onGround);

    [Fact]
    public void Units_AreFeetKnotsAndFeetPerMinute()
    {
        var record = AdsbApi.Record(Flying(Model()), Now, null, null);
        Assert.Equal(984, record.AltBaro);
        Assert.Equal(984, record.AltGeom);
        Assert.Equal(97.2, record.Gs);
        Assert.Equal(123.5, record.Track);
        Assert.Equal(984, record.BaroRate);
        Assert.Equal(984, record.GeomRate);
    }

    [Fact]
    public void TheIdentity_IsReportedAsReadsbWritesIt()
    {
        var record = AdsbApi.Record(Flying(Model(m => (m.Registration, m.TypeCode, m.Category, m.Squawk) = ("N172SP", "C172", "A1", "1234"))), Now, null, null);
        Assert.Equal("a1b2c3", record.Hex);
        Assert.Equal("adsb_icao", record.Type);
        // The call sign is padded to eight characters, as the real feed does.
        Assert.Equal("TEST01  ", record.Flight);
        Assert.Equal(("N172SP", "C172", "A1", "1234", "none"), (record.R, record.T, record.Category, record.Squawk, record.Emergency));
        Assert.Equal((39.2, -77.35), (record.Lat, record.Lon));
    }

    [Theory]
    [InlineData("7700", "general")]
    [InlineData("7600", "nordo")]
    [InlineData("7500", "unlawful")]
    [InlineData("1200", "none")]
    public void EmergencySquawks_RaiseTheEmergencyField(string squawk, string emergency)
    {
        Assert.Equal(emergency, AdsbApi.Record(Flying(Model(m => m.Squawk = squawk)), Now, null, null).Emergency);
    }

    [Fact]
    public void AnAircraftOnTheGround_HasNoAltitude_ButTheWordGround()
    {
        var record = AdsbApi.Record(Flying(Model(), altitude: 0.2f, climb: 0, onGround: true), Now, null, null);
        Assert.Equal("ground", record.AltBaro);
        Assert.Null(record.AltGeom);
        Assert.Null(record.BaroRate);
    }

    [Fact]
    public void ThePointQueryAddsDistanceAndBearing_TheHexLookupDoesNot()
    {
        var around = AdsbApi.Record(Flying(Model()), Now, 2.5, 270.04);
        Assert.Equal((2.5, 270.0), (around.Dst, around.Dir));

        var json = JsonSerializer.Serialize(AdsbApi.Record(Flying(Model()), Now, null, null), Json);
        Assert.DoesNotContain("\"dst\"", json);
        Assert.DoesNotContain("\"dir\"", json);
    }

    [Fact]
    public void AnAircraftWithoutACallSign_LeavesFlightOut_AndTheAltitudeIsANumber()
    {
        var json = JsonSerializer.Serialize(AdsbApi.Record(Flying(Model(m => m.Callsign = "")), Now, null, null), Json);
        Assert.DoesNotContain("\"flight\"", json);
        Assert.Contains("\"alt_baro\":984,", json);
        Assert.Contains("\"mlat\":[],\"tisb\":[]", json);
    }

    [Fact]
    public void ADegreeOfLatitude_IsAbout111Kilometres()
    {
        Assert.InRange(AdsbApi.Distance(0, 0, 1, 0), 111_000.0, 111_400.0);
        Assert.InRange(AdsbApi.Distance(0, 0, 0, 1), 111_000.0, 111_400.0);
        // A degree of longitude shrinks with the cosine of the latitude.
        Assert.InRange(AdsbApi.Distance(60, 10, 60, 11), 55_500.0, 55_700.0);
        Assert.Equal(0, AdsbApi.Distance(39.2, -77.35, 39.2, -77.35), 6);
    }

    [Fact]
    public void TheBearing_IsClockwiseFromTrueNorth()
    {
        Assert.Equal(0, AdsbApi.Bearing(0, 0, 1, 0), 6);
        Assert.Equal(90, AdsbApi.Bearing(0, 0, 0, 1), 6);
        Assert.Equal(180, AdsbApi.Bearing(1, 0, 0, 0), 6);
        Assert.Equal(270, AdsbApi.Bearing(0, 1, 0, 0), 6);
    }
}

// The endpoints themselves, against the running simulator. Every test uses aircraft of its own and takes
// them out again, as the class shares one simulator. Aircraft hover (speed 0) so the clock does not matter.
public class AdsbApiTests(SimFactory factory) : IClassFixture<SimFactory>
{
    private WorldStore Store => factory.Services.GetRequiredService<WorldStore>();

    private static JsonContent Json(object value) => JsonContent.Create(value, options: WorldStore.Json);

    private static async Task<string> ReasonOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("detail").GetString()!;

    private static async Task<JsonElement> Read(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    // An aircraft hovering over (x, z) at an altitude in metres above sea level.
    private static async Task Hover(HttpClient client, string id, string hex, float x = 0, float z = 0, float altitude = 300,
        string callsign = "", bool transmitting = true)
    {
        var response = await client.PutAsync($"/api/aircraft/{id}", Json(new
        {
            hex, callsign, transmitting, speed = 0, waypoints = new[] { new { x, z, altitude } },
        }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task Remove(HttpClient client, params string[] ids)
    {
        foreach (var id in ids) await client.DeleteAsync($"/api/aircraft/{id}");
    }

    // Coordinates in a path always use a decimal point, whatever the machine's culture.
    private static string Point(double latitude, double longitude, double radius) =>
        FormattableString.Invariant($"/v2/point/{latitude}/{longitude}/{radius}");

    private static string[] HexesOf(JsonElement answer) =>
        [.. answer.GetProperty("ac").EnumerateArray().Select(a => a.GetProperty("hex").GetString()!)];

    [Fact]
    public async Task APointQuery_ReportsAnAircraftInsideTheRadius_AsAdsbLolDoes()
    {
        var client = factory.CreateClient();
        await Hover(client, "adsb-in", "a1b2c3", callsign: "TEST01");
        try
        {
            var geo = Store.Current.Geo;
            var response = await client.GetAsync(Point(geo.Latitude, geo.Longitude, 1));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());

            var answer = await Read(response);
            Assert.Equal("No error", answer.GetProperty("msg").GetString());
            Assert.Equal(answer.GetProperty("ac").GetArrayLength(), answer.GetProperty("total").GetInt32());
            Assert.InRange(Math.Abs(answer.GetProperty("now").GetInt64() - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), 0L, 10_000L);

            var plane = answer.GetProperty("ac").EnumerateArray().Single(a => a.GetProperty("hex").GetString() == "a1b2c3");
            Assert.Equal("TEST01  ", plane.GetProperty("flight").GetString());
            Assert.Equal(984, plane.GetProperty("alt_baro").GetInt32());
            Assert.Equal(geo.Latitude, plane.GetProperty("lat").GetDouble(), 5);
            Assert.Equal(geo.Longitude, plane.GetProperty("lon").GetDouble(), 5);
            Assert.InRange(plane.GetProperty("dst").GetDouble(), 0.0, 0.01);
        }
        finally
        {
            await Remove(client, "adsb-in");
        }
    }

    [Fact]
    public async Task APointQuery_LeavesOutAircraftBeyondTheRadius_AndOrdersTheRestByDistance()
    {
        var client = factory.CreateClient();
        // 5000 m east is 2.7 nautical miles; 1000 m east is 0.54.
        await Hover(client, "adsb-far", "a1b2c4", x: 5000);
        await Hover(client, "adsb-near", "a1b2c5", x: 1000);
        try
        {
            var geo = Store.Current.Geo;
            string At(double radius) => Point(geo.Latitude, geo.Longitude, radius);

            Assert.Equal(new[] { "a1b2c5" }, HexesOf(await Read(await client.GetAsync(At(1)))).Where(h => h.StartsWith("a1b2c")).ToArray());
            Assert.Equal(new[] { "a1b2c5", "a1b2c4" }, HexesOf(await Read(await client.GetAsync(At(5)))).Where(h => h.StartsWith("a1b2c")).ToArray());

            var far = (await Read(await client.GetAsync(At(5)))).GetProperty("ac").EnumerateArray().Single(a => a.GetProperty("hex").GetString() == "a1b2c4");
            Assert.InRange(far.GetProperty("dst").GetDouble(), 2.65, 2.75);
            // The world's north is true north here, so something due east bears 90.
            Assert.InRange(far.GetProperty("dir").GetDouble(), 89.0, 91.0);
        }
        finally
        {
            await Remove(client, "adsb-far", "adsb-near");
        }
    }

    [Fact]
    public async Task TheOlderLatLonDistSpelling_AnswersTheSame()
    {
        var client = factory.CreateClient();
        await Hover(client, "adsb-old", "a1b2c6");
        try
        {
            var geo = Store.Current.Geo;
            var answer = await Read(await client.GetAsync(FormattableString.Invariant($"/v2/lat/{geo.Latitude}/lon/{geo.Longitude}/dist/1")));
            Assert.Contains("a1b2c6", HexesOf(answer));
        }
        finally
        {
            await Remove(client, "adsb-old");
        }
    }

    [Fact]
    public async Task AHexLookup_ReturnsThatAircraft_AndNothingForAnAddressNobodyTransmits()
    {
        var client = factory.CreateClient();
        await Hover(client, "adsb-h1", "a1b2d1", z: -3000);
        await Hover(client, "adsb-h2", "a1b2d2", z: 3000);
        try
        {
            // Anywhere in the world: a lookup by address has no radius. Upper case and ~ are accepted.
            var one = await Read(await client.GetAsync("/v2/hex/A1B2D1"));
            Assert.Equal(new[] { "a1b2d1" }, HexesOf(one));
            Assert.False(one.GetProperty("ac")[0].TryGetProperty("dst", out _));

            Assert.Equal(new[] { "a1b2d1", "a1b2d2" }, HexesOf(await Read(await client.GetAsync("/v2/hex/a1b2d2,~a1b2d1"))).Order().ToArray());
            Assert.Equal(new[] { "a1b2d2" }, HexesOf(await Read(await client.GetAsync("/v2/icao/a1b2d2"))));

            var none = await client.GetAsync("/v2/hex/ffffff");
            Assert.Equal(HttpStatusCode.OK, none.StatusCode);
            var empty = await Read(none);
            Assert.Equal((0, "No error"), (empty.GetProperty("total").GetInt32(), empty.GetProperty("msg").GetString()));
            Assert.Equal(0, empty.GetProperty("ac").GetArrayLength());
        }
        finally
        {
            await Remove(client, "adsb-h1", "adsb-h2");
        }
    }

    [Fact]
    public async Task AnAircraftWithItsTransponderOff_IsNotReported_ButIsStillInTheWorld()
    {
        var client = factory.CreateClient();
        await Hover(client, "adsb-dark", "a1b2e1", transmitting: false);
        try
        {
            var geo = Store.Current.Geo;
            Assert.Empty(HexesOf(await Read(await client.GetAsync("/v2/hex/a1b2e1"))));
            Assert.DoesNotContain("a1b2e1", HexesOf(await Read(await client.GetAsync(Point(geo.Latitude, geo.Longitude, 10)))));

            var truth = await Read(await client.GetAsync("/api/aircraft/adsb-dark/pose"));
            Assert.True(truth.GetProperty("present").GetBoolean());
            Assert.False(truth.GetProperty("transmitting").GetBoolean());
        }
        finally
        {
            await Remove(client, "adsb-dark");
        }
    }

    [Theory]
    [InlineData("/v2/point/abc/0/10", "lat")]
    [InlineData("/v2/point/91/0/10", "lat")]
    [InlineData("/v2/point/0/181/10", "lon")]
    [InlineData("/v2/point/0/0/251", "radius")]
    [InlineData("/v2/point/0/0/-1", "radius")]
    public async Task AnInvalidPointQuery_Is422_NamingTheParameter(string path, string parameter)
    {
        var response = await factory.CreateClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var detail = (await Read(response)).GetProperty("detail");
        Assert.Contains(detail.EnumerateArray(), p => p.GetProperty("loc")[1].GetString() == parameter);
    }

    [Fact]
    public async Task TheGroundTruthPose_AgreesWithTheFeed()
    {
        var client = factory.CreateClient();
        await Hover(client, "adsb-truth", "a1b2f1", x: 1200, z: -800, altitude: 450);
        try
        {
            var truth = await Read(await client.GetAsync("/api/aircraft/adsb-truth/pose"));
            var feed = (await Read(await client.GetAsync("/v2/hex/a1b2f1"))).GetProperty("ac")[0];
            Assert.Equal(truth.GetProperty("geo").GetProperty("latitude").GetDouble(), feed.GetProperty("lat").GetDouble(), 5);
            Assert.Equal(truth.GetProperty("geo").GetProperty("longitude").GetDouble(), feed.GetProperty("lon").GetDouble(), 5);
            Assert.Equal(450, truth.GetProperty("altitude").GetDouble(), 3);
            Assert.Equal(Math.Round(450 * 3.280839895), feed.GetProperty("alt_baro").GetDouble());
            // 800 m north and 1200 m east of the anchor.
            Assert.True(truth.GetProperty("geo").GetProperty("latitude").GetDouble() > Store.Current.Geo.Latitude);
            Assert.True(truth.GetProperty("geo").GetProperty("longitude").GetDouble() > Store.Current.Geo.Longitude);
        }
        finally
        {
            await Remove(client, "adsb-truth");
        }
    }

    [Fact]
    public async Task AnAircraftWithoutAHex_GetsOneFromItsId_AndKeepsIt()
    {
        var client = factory.CreateClient();
        var body = Json(new { waypoints = new[] { new { x = 0, z = 0, altitude = 100 } } });
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync("/api/aircraft/adsb-auto", body)).StatusCode);
        try
        {
            var hex = Store.Current.Aircraft.Single(a => a.Id == "adsb-auto").Hex;
            Assert.Matches("^a[0-9a-f]{5}$", hex);

            await client.PutAsync("/api/aircraft/adsb-auto", Json(new { waypoints = new[] { new { x = 5, z = 5, altitude = 100 } } }));
            Assert.Equal(hex, Store.Current.Aircraft.Single(a => a.Id == "adsb-auto").Hex);
        }
        finally
        {
            await Remove(client, "adsb-auto");
        }
    }

    [Fact]
    public async Task TheWorldRefusesWhatTheFeedCannotCarry()
    {
        var client = factory.CreateClient();
        var waypoints = new[] { new { x = 0, z = 0, altitude = 100 } };
        await Hover(client, "adsb-twin", "a1b2a1");
        try
        {
            async Task<string> Refused(object body)
            {
                var response = await client.PutAsync("/api/aircraft/adsb-bad", Json(body));
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                return await ReasonOf(response);
            }

            Assert.Contains("hex", await Refused(new { hex = "xyz", waypoints }));
            Assert.Contains("used more than once", await Refused(new { hex = "A1B2A1", waypoints }));
            Assert.Contains("squawk", await Refused(new { hex = "a1b2a2", squawk = "1289", waypoints }));
            Assert.Contains("call sign", await Refused(new { hex = "a1b2a2", callsign = "too long call", waypoints }));
            Assert.Contains("category", await Refused(new { hex = "a1b2a2", category = "Z9", waypoints }));
            Assert.Contains("at least one waypoint", await Refused(new { hex = "a1b2a2", waypoints = Array.Empty<object>() }));
            Assert.Contains("speed", await Refused(new { hex = "a1b2a2", speed = 500, waypoints }));
            Assert.DoesNotContain(Store.Current.Aircraft, a => a.Id == "adsb-bad");
        }
        finally
        {
            await Remove(client, "adsb-twin");
        }
    }

    [Fact]
    public async Task ADeletedAircraft_IsGoneFromTheFeed_AndDeletingItAgainIs404()
    {
        var client = factory.CreateClient();
        await Hover(client, "adsb-gone", "a1b2a3");
        Assert.Equal(new[] { "a1b2a3" }, HexesOf(await Read(await client.GetAsync("/v2/hex/a1b2a3"))));
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/aircraft/adsb-gone")).StatusCode);
        Assert.Empty(HexesOf(await Read(await client.GetAsync("/v2/hex/a1b2a3"))));
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/aircraft/adsb-gone")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/aircraft/adsb-gone/pose")).StatusCode);
    }

    [Fact]
    public async Task ACamerasPose_CarriesItsPlaceOnTheEarth()
    {
        var client = factory.CreateClient();
        foreach (var camera in Store.Current.Cameras.Where(c => string.IsNullOrEmpty(c.MountedOnVehicleId)).Take(2))
        {
            var pose = await Read(await client.GetAsync($"/api/cameras/{camera.Id}/pose"));
            var position = pose.GetProperty("position");
            var expected = WorldGeo.ToGeo(Store.Current, new Vec3(
                position.GetProperty("x").GetSingle(), position.GetProperty("y").GetSingle(), position.GetProperty("z").GetSingle()));
            var geo = pose.GetProperty("geo");
            Assert.Equal(expected.Latitude, geo.GetProperty("latitude").GetDouble(), 7);
            Assert.Equal(expected.Longitude, geo.GetProperty("longitude").GetDouble(), 7);
            Assert.Equal(expected.Elevation, geo.GetProperty("elevation").GetDouble(), 2);
        }
    }
}
