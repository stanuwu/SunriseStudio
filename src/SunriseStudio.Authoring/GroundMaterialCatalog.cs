using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

/** A terrain material the installation uses: its part technique, its most used blend map, and how many STerrain use it. */
public sealed record GroundMaterialChoice(string Name, GroundMaterial Material, int Terrains);

/** Terrain materials read from the installed STerrain. */
public static class GroundMaterialCatalog
{
    /** Every installed terrain technique, most used first, with its most used installed blend map. */
    public static IReadOnlyList<GroundMaterialChoice> Scan(PackageCatalog catalog, CancellationToken token = default)
    {
        var uses = new Dictionary<uint, (int Terrains, Dictionary<uint, int> Dyemaps)>();
        foreach (var entry in catalog.Entries)
        {
            if (entry.Reference != STerrain.Class) continue;
            token.ThrowIfCancellationRequested();
            try
            {
                byte[] data = catalog.ReadOriginal(entry.TagHash);
                ref readonly var terrain = ref TagView.Checked<STerrain>(data, 0);
                var parts = TagView.Rows(data, in terrain.Parts, UnknownTerrainPart.Class);
                if (parts.IsEmpty) continue;
                uint technique = parts[0].Material;
                if (!uses.TryGetValue(technique, out var use)) use = (0, []);
                foreach (ref readonly var group in TagView.Rows(data, in terrain.Groups, UnknownTerrainGroup.Class))
                    use.Dyemaps[group.DyeTexture] = use.Dyemaps.GetValueOrDefault(group.DyeTexture) + 1;
                uses[technique] = (use.Terrains + 1, use.Dyemaps);
            }
            catch (Exception error) when (error is InvalidDataException or IOException or KeyNotFoundException) { }
        }
        var result = new List<GroundMaterialChoice>();
        foreach (var (technique, use) in uses.OrderByDescending(p => p.Value.Terrains).ThenBy(p => p.Key))
        {
            if (!catalog.TryGetEntry(technique, out _)) continue;
            uint dyemap = use.Dyemaps.Where(p => p.Key == GroundMaterial.NoDyemap || catalog.TryGetEntry(p.Key, out _))
                .OrderByDescending(p => p.Value).ThenBy(p => p.Key).Select(p => p.Key).DefaultIfEmpty(GroundMaterial.NoDyemap).First();
            result.Add(new(catalog.GetName(technique) ?? $"Terrain {technique:X8}", new(technique, dyemap), use.Terrains));
        }
        return result;
    }
}
