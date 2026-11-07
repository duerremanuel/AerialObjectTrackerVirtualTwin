namespace OnvifSim.World;

// ICAO addresses for aircraft that were created without one (a hand-written PUT; the editor deals its
// own). The block A00000..AFFFFF is the one the United States hands out, so a made-up address looks
// like any other when it shows up in a client; it is derived from the id, so the same id gets the same
// address in every world and run, and it steps on past any address already taken.
public static class AircraftIdentity
{
    private const uint Block = 0xA00000;
    private const uint BlockMask = 0x0FFFFF;

    public static string HexFor(string id, IEnumerable<string> taken)
    {
        var used = taken.Select(h => h.ToLowerInvariant()).ToHashSet();
        // FNV-1a over the lower-cased id.
        uint hash = 2166136261;
        foreach (char c in id.ToLowerInvariant())
        {
            hash ^= c;
            hash *= 16777619;
        }
        uint address = Block | (hash & BlockMask);
        while (used.Contains(address.ToString("x6")))
            address = Block | ((address + 1) & BlockMask);
        return address.ToString("x6");
    }
}
