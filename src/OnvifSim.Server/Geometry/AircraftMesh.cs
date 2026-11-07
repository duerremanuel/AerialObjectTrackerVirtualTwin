using System.Numerics;
using OnvifSim.World;

namespace OnvifSim.Geometry;

// A low-poly aircraft with its nose towards -Z (north at heading 0), centred on the origin so the point
// of its flight is the middle of it. The same mesh is drawn in the camera pictures and, as a simpler
// stand-in, in the editor. Length runs nose to tail; Wingspan is the wings' reach, a helicopter's rotor
// diameter, or a drone's width over its rotors.
public static class AircraftMesh
{
    private static readonly Vector3 Glass = ColorUtil.Rgb(32, 42, 52);
    private static readonly Vector3 Dark = ColorUtil.Rgb(52, 54, 60);

    public static Mesh Build(AircraftModel aircraft)
    {
        var (body, trim) = aircraft.Kind switch
        {
            AircraftKind.Helicopter => (ColorUtil.Rgb(214, 218, 224), ColorUtil.Rgb(40, 84, 150)),
            AircraftKind.Drone => (Dark, ColorUtil.Rgb(240, 140, 30)),
            _ => (ColorUtil.Rgb(236, 238, 242), ColorUtil.Rgb(196, 52, 44)),
        };
        body = ColorUtil.Parse(aircraft.Color, body);
        trim = ColorUtil.Parse(aircraft.Color2, trim);
        return aircraft.Kind switch
        {
            AircraftKind.Helicopter => Helicopter(aircraft.Length, aircraft.Wingspan, body, trim),
            AircraftKind.Drone => Drone(aircraft.Length, aircraft.Wingspan, body, trim),
            _ => Airplane(aircraft.Length, aircraft.Wingspan, body, trim),
        };
    }

    // A tapered fuselage, a straight wing a little ahead of the middle, a tailplane and a fin.
    private static Mesh Airplane(float length, float span, Vector3 body, Vector3 trim)
    {
        float l = length;
        var m = new Mesh();

        // Built standing up (nose towards +y), then laid down with the nose towards -Z.
        var fuselage = new Mesh();
        Primitives.Frustum(fuselage, 0.025f * l, 0.075f * l, 0.3f * l, 8, body, y0: -0.5f * l);
        Primitives.Frustum(fuselage, 0.075f * l, 0.075f * l, 0.5f * l, 8, body, y0: -0.2f * l, caps: false);
        Primitives.Frustum(fuselage, 0.075f * l, 0.02f * l, 0.2f * l, 8, body, y0: 0.3f * l);
        m.Append(fuselage, Matrix4x4.CreateRotationX(-MathF.PI / 2));

        Part(m, Box(span, 0.02f * l, 0.16f * l, trim, y0: -0.01f * l), 0, -0.04f * l, -0.05f * l);
        Part(m, Box(0.36f * span, 0.015f * l, 0.08f * l, trim), 0, 0, 0.43f * l);
        Part(m, Box(0.015f * l, 0.14f * l, 0.1f * l, trim), 0, 0.03f * l, 0.41f * l);
        Part(m, Box(0.09f * l, 0.04f * l, 0.09f * l, Glass), 0, 0.06f * l, -0.17f * l);
        return m;
    }

    // A rounded cabin, a tail boom with a fin, skids and a rotor of the given diameter on a mast.
    private static Mesh Helicopter(float length, float rotor, Vector3 body, Vector3 trim)
    {
        float l = length;
        var m = new Mesh();
        Primitives.Blob(m, new Vector3(0.1f * l, 0.09f * l, 0.22f * l), new Vector3(0, 0, -0.25f * l), 0, 0, body, Vector3.Zero);
        Part(m, Box(0.035f * l, 0.04f * l, 0.55f * l, body), 0, -0.01f * l, 0.225f * l);
        Part(m, Box(0.012f * l, 0.13f * l, 0.08f * l, trim), 0, -0.01f * l, 0.46f * l);
        Part(m, Box(0.09f * l, 0.05f * l, 0.1f * l, Glass), 0, 0.01f * l, -0.4f * l);
        for (float side = -1; side <= 1; side += 2)
            Part(m, Box(0.02f * l, 0.015f * l, 0.4f * l, Dark), side * 0.07f * l, -0.13f * l, -0.2f * l);
        Part(m, Box(0.02f * l, 0.07f * l, 0.02f * l, Dark), 0, 0.08f * l, -0.2f * l);
        Part(m, Box(rotor, 0.008f * l, 0.025f * l, trim), 0, 0.15f * l, -0.2f * l);
        Part(m, Box(0.025f * l, 0.008f * l, rotor, trim), 0, 0.15f * l, -0.2f * l);
        return m;
    }

    // A square body on crossed arms with a rotor disc at each end, and a mark at the front so the way it
    // faces can be told.
    private static Mesh Drone(float length, float span, Vector3 body, Vector3 trim)
    {
        var m = new Mesh();
        Part(m, Box(0.3f * span, 0.35f * length, 0.6f * length, body, y0: -0.175f * length), 0, 0, 0);
        float reach = span * 0.5f - 0.2f * span;
        float arm = MathF.Sqrt(2) * reach * 2;
        foreach (float angle in new[] { MathF.PI / 4, -MathF.PI / 4 })
        {
            var bar = Box(arm, 0.04f * length, 0.06f * span, Dark);
            m.Append(bar, Matrix4x4.CreateRotationY(angle));
        }
        foreach (var (sx, sz) in new[] { (-1f, -1f), (1f, -1f), (-1f, 1f), (1f, 1f) })
        {
            var disc = new Mesh();
            Primitives.Frustum(disc, 0.2f * span, 0.2f * span, 0.012f * span, 10, ColorUtil.Rgb(120, 124, 130));
            Part(m, disc, sx * reach, 0.03f * length, sz * reach);
        }
        Part(m, Box(0.12f * span, 0.2f * length, 0.15f * length, trim), 0, 0, -0.38f * length);
        return m;
    }

    // A box centred on the origin unless its bottom is given.
    private static Mesh Box(float w, float h, float d, Vector3 color, float? y0 = null)
    {
        var m = new Mesh();
        Primitives.Box(m, w, h, d, color, y0 ?? -h / 2);
        return m;
    }

    private static void Part(Mesh into, Mesh part, float x, float y, float z) =>
        into.Append(part, Matrix4x4.CreateTranslation(x, y, z));
}
