namespace OnvifSim.World;

// An aircraft caught at a moment: where it is in the world and on the Earth. WorldY is its height in the
// world's own metres (altitude above sea level less the world's elevation); OnGround is true when it is
// within half a metre of the terrain under it, which ADS-B reports as "ground" instead of an altitude.
public sealed record AircraftState(AircraftModel Aircraft, AircraftPose Pose, GeoPosition Geo, float WorldY, bool OnGround);

// Every aircraft of a world with its flight compiled, so asking where they all are costs a lookup each.
// Built together with the rest of the scene (SceneState) and immutable like it: an edit builds a new one.
public sealed class AircraftFleet
{
    // How near the terrain counts as standing on it.
    public const float GroundTolerance = 0.5f;

    private readonly List<(AircraftModel Aircraft, FlightPath Path)> _flights;
    private readonly GeoModel _geo;
    private readonly Terrain _terrain;

    private AircraftFleet(List<(AircraftModel, FlightPath)> flights, GeoModel geo, Terrain terrain)
    {
        _flights = flights;
        _geo = geo;
        _terrain = terrain;
    }

    public static AircraftFleet Build(WorldModel world, Terrain terrain) =>
        new([.. world.Aircraft.Select(a => (a, FlightPath.Build(a)))], world.Geo, terrain);

    public int Count => _flights.Count;

    // The aircraft in the sky at a moment; one out of sight between two flights of a Restart path is left out.
    public List<AircraftState> At(DateTimeOffset time)
    {
        var states = new List<AircraftState>(_flights.Count);
        foreach (var (aircraft, path) in _flights)
            if (path.PoseAt(time) is { } pose)
                states.Add(StateOf(aircraft, pose));
        return states;
    }

    public AircraftState? Find(string id, DateTimeOffset time) =>
        _flights.Where(f => string.Equals(f.Aircraft.Id, id, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Path.PoseAt(time) is { } pose ? StateOf(f.Aircraft, pose) : null)
            .FirstOrDefault();

    private AircraftState StateOf(AircraftModel aircraft, AircraftPose pose)
    {
        float worldY = pose.Altitude - _geo.Elevation;
        var geo = WorldGeo.ToGeo(_geo, new Vec3(pose.X, worldY, pose.Z));
        bool onGround = worldY - _terrain.HeightAt(pose.X, pose.Z) <= GroundTolerance;
        return new AircraftState(aircraft, pose, geo, worldY, onGround);
    }
}
