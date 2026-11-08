using SunriseStudio.Packages;

namespace SunriseStudio.Projects;

public static partial class ProjectBuilder
{
    /**
     * Only decoded native fields extend authored graphs into installed dependencies.
     * An edited entry keeps only edges its original lacks; its manifests already load the rest.
     * A newly referenced installed entry brings its own dependencies.
     */
    private static HashSet<(uint From, uint To)> NativeDependencyEdges(PackageCatalog catalog,
        IReadOnlyDictionary<uint, byte[]> edited, IReadOnlyList<AddedEntry> added)
    {
        var authored = added.ToDictionary(asset => asset.Tag);
        PackageEntry? Resolve(uint tag)
        {
            if (!authored.TryGetValue(tag, out var asset))
                return catalog.TryGetEntry(tag, out var entry) ? entry : null;
            ushort package = checked((ushort)TagHashes.Package(tag));
            return new(tag, asset.Class, (int)((asset.TypeInfo >> 9) & 127), (int)((asset.TypeInfo >> 6) & 7),
                asset.Data.Length, $"w64_sunrise_{package:x4}_0", package, TagHashes.Entry(tag))
                { TypeInfo = asset.TypeInfo };
        }
        var pending = new Queue<uint>(authored.Keys.Concat(edited.Keys));
        var visited = new HashSet<uint>();
        var edges = new HashSet<(uint From, uint To)>();
        while (pending.TryDequeue(out uint tag))
        {
            if (!visited.Add(tag)) continue;
            var entry = Resolve(tag) ?? throw new InvalidDataException($"Native dependency {tag:X8} is missing.");
            bool changed = edited.TryGetValue(tag, out var current);
            byte[] data = authored.TryGetValue(tag, out var own) ? own.Data : changed ? current! : catalog.ReadOriginal(tag);
            HashSet<uint> stock = changed ? PackageNativeDependencies.Read(catalog, tag, entry.Reference, entry.TypeInfo,
                catalog.ReadOriginal(tag), Resolve).ToHashSet() : [];
            foreach (uint target in PackageNativeDependencies.Read(catalog, tag, entry.Reference, entry.TypeInfo, data, Resolve))
            {
                if (stock.Contains(target)) continue;
                edges.Add((tag, target)); pending.Enqueue(target);
            }
        }
        return edges;
    }
}
