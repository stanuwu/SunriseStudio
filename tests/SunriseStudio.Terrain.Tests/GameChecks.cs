using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;

/** Placement and re-decoding against a real installation through a read overlay; nothing is written to it. */
internal static class GameChecks
{
    // The clay preset of World Studio: retail terrain technique 80EAB60A with blend map 80FE7A40.
    private static readonly GroundMaterial Clay = new(0x80EAB60A, 0x80FE7A40);

    /** @param keySource An executable holding the package keys, when Sunrise has not exported them. */
    internal static void Run(string gameRoot, string? keySource, string temporary)
    {
        using var catalog = PackageCatalog.Open(gameRoot, keySource);
        var bubbles = catalog.Entries.Where(e => e.Reference == GroundPlacement.BubbleClass).Select(e => e.TagHash).Order().ToArray();
        var refused = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var sites = new List<GroundSite>();
        foreach (uint bubble in bubbles)
            try { sites.Add(GroundPlacement.Find(catalog, bubble, [])); }
            catch (InvalidDataException error) { refused[error.Message] = refused.GetValueOrDefault(error.Message) + 1; }
        Check(sites.Count > 0, "No installed bubble has free terrain and collision slots: " + string.Join("; ", refused.Select(p => $"{p.Value} {p.Key}")));
        var site = sites[0];
        ushort package = Enumerable.Range(0x0C00, 0x100).Select(i => (ushort)i).First(id => !catalog.ContainsInstalledPackage(id));
        var grid = GroundChecks.Synthetic();
        var prepared = GroundAssets.PrepareNew(catalog, [], [], "Census ground", grid, Clay, package, site.Bubble);
        var bytes = new Dictionary<uint, byte[]>(prepared.Placement);
        var overlays = new List<PackageOverlayEntry>();
        foreach (var (tag, edited) in prepared.Placement)
        {
            catalog.TryGetEntry(tag, out var entry);
            overlays.Add(new(entry with { Size = edited.Length }));
        }
        foreach (var addition in prepared.Additions.Where(a => a.Native is not null))
        {
            var native = addition.Native!;
            uint tag = GroundAssets.TagOf(native.PackageId, native.EntryIndex!.Value);
            bytes[tag] = addition.Data;
            overlays.Add(new(new PackageEntry(tag, native.Reference, (int)(native.TypeInfo >> 9 & 127), (int)(native.TypeInfo >> 6 & 7),
                addition.Data.Length, $"w64_sunrise_{package:x4}_0", package, native.EntryIndex.Value)
            { TypeInfo = native.TypeInfo }, native.Name));
        }
        catalog.SetOverlayEntries(overlays);
        catalog.ReadOverride = tag => bytes.GetValueOrDefault(tag);
        var terrains = prepared.Build.Terrains.Select(t => Tag(prepared, t.Terrain)).ToHashSet();
        int groups = 0;
        foreach (uint terrain in terrains)
        {
            var scene = new AssetSceneLoader(catalog).LoadMesh(terrain);
            Check(scene.DecodeErrorCount == 0 && scene.Instances.Count > 0 && scene.Instances.All(i => i.Mesh.MaterialTag == Clay.Technique),
                $"Generated STerrain 0x{terrain:X8} did not decode against the installation.");
            groups += scene.Instances.Count;
        }
        var bubbleScene = new AssetSceneLoader(catalog).LoadBubble(site.Bubble);
        int drawn = bubbleScene.Instances.Count(i => i.Layer == SceneLayer.Terrain && terrains.Contains(i.Mesh.TagHash));
        Check(drawn == groups, $"The bubble draws {drawn} of the ground's {groups} terrain groups.");
        var (terrainTable, collisionTable) = GroundAssets.Tables(prepared.Definition, prepared.Build.Entries.Count);
        var collision = new StaticCollisionReader(catalog).ReadObjectLists([collisionTable]);
        Check(collision.Undecoded.Count == 0 && collision.Entities.Single().Instances.Count == prepared.Build.Payloads
            && collision.Entities[0].Instances.All(i => i.Shape is { Indices.Length: > 0 }), "The ground's collision did not decode against the installation.");
        var (_, _, rows) = GroundPlacement.RegistryBank(catalog, site.Registry);
        Console.WriteLine($"PASS ground game bubbles={bubbles.Length} sites={sites.Count} refused={string.Join("; ", refused.Select(p => $"{p.Value} {p.Key}"))} " +
            $"site=0x{site.Bubble:X8}/0x{site.Container:X8} index={site.NextIndex} installed-rows={rows.Count} key-words={string.Join("/", rows.Select(r => $"{r.Key >> 32:X8}").Distinct())} groups={groups} table=0x{terrainTable:X8} " +
            $"collision={prepared.Build.Payloads} temporary={Directory.Exists(temporary)}");
    }

    private static uint Tag(PreparedGround prepared, string key) => GroundAssets.TagOf(prepared.Definition.PackageId,
        prepared.Definition.FirstEntry + prepared.Build.Entries.ToList().FindIndex(e => e.Key == key));

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
