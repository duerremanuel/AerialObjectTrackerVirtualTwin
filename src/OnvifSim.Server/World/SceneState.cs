using OnvifSim.Rendering;

namespace OnvifSim.World;

// The world as the running services see it: the model plus its render-ready scene, rebuilt whenever
// the store gets a new world. Readers take one snapshot and use it for a whole frame.
public sealed class SceneState
{
    // The aircraft's flights are compiled with the scene, so one snapshot answers where they all are.
    public sealed record Snapshot(WorldModel World, RenderScene Scene)
    {
        public AircraftFleet Aircraft { get; } = AircraftFleet.Build(World, Scene.Terrain);
    }

    private volatile Snapshot _current;

    public SceneState(WorldStore store)
    {
        _current = Build(store.Current);
        store.Changed += w => _current = Build(w);
    }

    public Snapshot Current => _current;

    public CameraModel? FindCamera(string id) =>
        _current.World.Cameras.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    private static Snapshot Build(WorldModel world) => new(world, RenderScene.Build(world));
}
