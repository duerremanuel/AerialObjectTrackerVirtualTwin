using Microsoft.Extensions.Time.Testing;
using OnvifSim.Geometry;
using OnvifSim.Rendering;
using OnvifSim.Streaming;
using OnvifSim.World;

namespace OnvifSim.Tests;

// Aircraft are drawn in the cameras' pictures like walkers and cars, whether or not they transmit.
public class AircraftRenderingTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch.AddDays(20000));

    private const int Width = 320;
    private const int Height = 180;

    // Flat ground and one camera at 5 m looking north.
    private static SceneState.Snapshot Sky(params AircraftModel[] aircraft)
    {
        var world = new WorldModel();
        world.Cameras.Add(new CameraModel { Id = "cam", Position = new Vec3(0, 5, 0) });
        world.Aircraft.AddRange(aircraft);
        return new SceneState.Snapshot(world, RenderScene.Build(world));
    }

    private static AircraftModel Hovering(string id, float z, float altitude = 25, Action<AircraftModel>? tweak = null)
    {
        var aircraft = new AircraftModel { Id = id, Hex = "a00001", Waypoints = [new Waypoint(0, z, altitude)], Speed = 0 };
        tweak?.Invoke(aircraft);
        return aircraft;
    }

    private byte[] Picture(SceneState.Snapshot snapshot, CachingFrameRenderer? renderer = null) =>
        (renderer ?? new CachingFrameRenderer(_time)).RenderFrame(snapshot, snapshot.World.Cameras[0], default, Width, Height).Rgba.ToArray();

    private static int PixelsDiffering(byte[] a, byte[] b)
    {
        int differing = 0;
        for (int i = 0; i < a.Length; i += 4)
            if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2]) differing++;
        return differing;
    }

    [Fact]
    public void AnAircraftInFrontOfTheCamera_IsInThePicture()
    {
        var empty = Picture(Sky());
        var withPlane = Picture(Sky(Hovering("a1", z: -80)));
        // 11 m of wing at 80 m is several pixels wide at this size.
        Assert.True(PixelsDiffering(empty, withPlane) > 40, "the aircraft left no mark on the picture");
    }

    [Fact]
    public void AnAircraftBehindTheCamera_IsNot()
    {
        Assert.Equal(Picture(Sky()), Picture(Sky(Hovering("a1", z: 80))));
    }

    [Fact]
    public void AnAircraftWithItsTransponderOff_IsStillInThePicture()
    {
        var loud = Picture(Sky(Hovering("a1", z: -80)));
        var silent = Picture(Sky(Hovering("a1", z: -80, tweak: a => a.Transmitting = false)));
        Assert.Equal(loud, silent);
    }

    [Fact]
    public void ABiggerAircraft_CoversMorePixels()
    {
        int Marks(float scale) => PixelsDiffering(Picture(Sky()),
            Picture(Sky(Hovering("a1", z: -80, tweak: a => (a.Length, a.Wingspan) = (8.3f * scale, 11 * scale)))));
        Assert.True(Marks(3) > Marks(1) * 2);
    }

    [Fact]
    public void TheThreeKinds_AreEachDrawn()
    {
        var empty = Picture(Sky());
        foreach (var kind in Enum.GetValues<AircraftKind>())
        {
            // A drone is small: bring it close.
            float distance = kind == AircraftKind.Drone ? -12 : -60;
            var plane = Hovering("a1", z: distance, altitude: kind == AircraftKind.Drone ? 8 : 20, tweak: a =>
            {
                a.Kind = kind;
                if (kind == AircraftKind.Drone) (a.Length, a.Wingspan) = (0.5f, 0.7f);
            });
            Assert.True(PixelsDiffering(empty, Picture(Sky(plane))) > 10, $"{kind} left no mark on the picture");
        }
    }

    [Fact]
    public void AFlyingAircraftInView_MakesTheCameraDrawAgain_ButOneOutOfViewDoesNot()
    {
        var renderer = new CachingFrameRenderer(_time);
        var snapshot = Sky(Hovering("seen", z: -80), Hovering("behind", z: 80));
        var camera = snapshot.World.Cameras[0];

        renderer.RenderFrame(snapshot, camera, default, Width, Height);
        _time.Advance(TimeSpan.FromSeconds(1));
        renderer.RenderFrame(snapshot, camera, default, Width, Height);
        Assert.Equal(2, renderer.RenderCount);

        var behindOnly = Sky(Hovering("behind", z: 80));
        var quiet = new CachingFrameRenderer(_time);
        quiet.RenderFrame(behindOnly, behindOnly.World.Cameras[0], default, Width, Height);
        _time.Advance(TimeSpan.FromSeconds(1));
        quiet.RenderFrame(behindOnly, behindOnly.World.Cameras[0], default, Width, Height);
        Assert.Equal(1, quiet.RenderCount);
    }

    [Fact]
    public void AnAircraftThatFliesAcrossTheView_MovesInThePicture()
    {
        // Crossing from west to east 40 m ahead of the camera, whose view is 56 m wide there, at 5 m/s: it
        // starts at -20 m and is at -10 m two seconds later, both in view.
        var crossing = new AircraftModel
        {
            Id = "a1", Hex = "a00001", Speed = 5, Interpolation = AircraftInterpolation.Linear, Path = AircraftPathMode.Reverse,
            Waypoints = [new Waypoint(-20, -40, 20), new Waypoint(20, -40, 20)],
        };
        var snapshot = Sky(crossing);
        var renderer = new CachingFrameRenderer(_time);
        var before = renderer.RenderFrame(snapshot, snapshot.World.Cameras[0], default, Width, Height).Rgba.ToArray();
        _time.Advance(TimeSpan.FromSeconds(2));
        var after = renderer.RenderFrame(snapshot, snapshot.World.Cameras[0], default, Width, Height).Rgba.ToArray();
        Assert.True(PixelsDiffering(before, after) > 40);
    }
}

public class AircraftMeshTests
{
    [Fact]
    public void EveryKind_BuildsAMeshAboutItsSize()
    {
        foreach (var kind in Enum.GetValues<AircraftKind>())
        {
            var aircraft = new AircraftModel { Kind = kind, Length = 8, Wingspan = 10 };
            var mesh = AircraftMesh.Build(aircraft);
            Assert.True(mesh.TriangleCount > 10, $"{kind} is hardly a mesh");
            var (_, radius) = mesh.BoundingSphere();
            Assert.InRange(radius, 3.0f, 8.0f);
        }
    }

    [Fact]
    public void AnAirplane_HasItsNoseTowardsMinusZ_AndItsWingsAcross()
    {
        var mesh = AircraftMesh.Build(new AircraftModel { Kind = AircraftKind.Airplane, Length = 10, Wingspan = 12 });
        // The nose tip is half a length ahead of the middle, the tail half a length behind.
        Assert.InRange(mesh.Positions.Min(p => p.Z), -5.05f, -4.9f);
        Assert.InRange(mesh.Positions.Max(p => p.Z), 4.2f, 5.05f);
        Assert.InRange(mesh.Positions.Max(p => p.X), 5.9f, 6.1f);
        Assert.InRange(mesh.Positions.Min(p => p.X), -6.1f, -5.9f);
    }

    [Fact]
    public void TheHeading_TurnsTheAircraftClockwiseSeenFromAbove()
    {
        var aircraft = new AircraftModel { Id = "a1", Kind = AircraftKind.Airplane, Length = 10, Wingspan = 12 };
        // Heading 90 is east: the nose, half a length from the middle, ends up on +X.
        var east = SceneBuilder.AircraftChunk(aircraft, new AircraftPose(100, 200, 50, 90, 0, 0), 50);
        var nose = east.Mesh.Positions.OrderByDescending(p => p.X).First();
        Assert.InRange(nose.X, 104.9f, 105.1f);
        Assert.InRange(nose.Z, 199f, 201f);
        Assert.Equal(50f, east.Center.Y, 0);
    }
}
