using System.Collections.ObjectModel;
using SunriseStudio.Tags.Generated;

namespace SunriseStudio.Packages;

public sealed partial class PackageCatalog
{
    public bool TryGetInstanceOrigin(uint tag, int index, out PackageInstanceOrigin origin)
    {
        var state = Volatile.Read(ref overlayState);
        if (state.Entries.TryGetValue(tag, out var entry) && entry.InstanceOrigins.TryGetValue(index, out origin!)) return true;
        origin = null!; return false;
    }

    public bool TryGetPlacementOrigin(uint tag, int offset, out PackagePlacementOrigin origin)
    {
        var state = Volatile.Read(ref overlayState);
        if (state.Entries.TryGetValue(tag, out var entry) && entry.PlacementOrigins.TryGetValue(offset, out origin!)) return true;
        origin = null!; return false;
    }

    public bool IsReplacedInstance(uint tag, int index, uint placementTag, int placementOffset) =>
        Volatile.Read(ref overlayState).Replacements.Contains(new(tag, index, placementTag, placementOffset));

    /** Generated aliases are editor metadata and must never claim another installed asset's identity. */
    private PackageOverlayEntry FreezeGeneratedMetadata(PackageOverlayEntry entry)
    {
        if (entry.InstanceOrigins is null || entry.PlacementOrigins is null || entry.Replaces is null)
            throw new InvalidDataException("Generated origin metadata is missing.");
        if (!entry.Generated)
        {
            if (entry.GeneratedSourceTag is not null || entry.InstanceOrigins.Count != 0
                || entry.PlacementOrigins.Count != 0 || entry.Replaces.Count != 0)
                throw new InvalidDataException("Only generated entries may carry origin metadata.");
            return entry;
        }
        if (entry.GeneratedSourceTag is not uint source || !byTag.ContainsKey(source)
            || byTag.ContainsKey(entry.Entry.TagHash) || !PackageWriter.IsNewPackageId(entry.Entry.PackageId))
            throw new InvalidDataException("A generated entry needs an installed source and an unused package.");
        foreach (var (index, origin) in entry.InstanceOrigins)
            if (index < 0 || origin is null || origin.SourceIndex < 0
                || !byTag.TryGetValue(origin.SourceTag, out var original) || original.Reference != SStaticMeshInstances.Class)
                throw new InvalidDataException("Generated instance origin is invalid.");
        foreach (var (offset, origin) in entry.PlacementOrigins)
            if (offset < 0 || origin is null || origin.SourceOffset < 0 || origin.SourceIndex < 0
                || !byTag.TryGetValue(origin.SourceTag, out var original) || original.Reference != SMapPlacements.Class)
                throw new InvalidDataException("Generated placement origin is invalid.");
        foreach (var replacement in entry.Replaces)
            if (replacement is null || replacement.SourceIndex < 0 || replacement.PlacementSourceOffset < 0
                || !byTag.TryGetValue(replacement.SourceTag, out var table) || table.Reference != SStaticMeshInstances.Class
                || !byTag.TryGetValue(replacement.PlacementSourceTag, out var placement) || placement.Reference != SMapPlacements.Class)
                throw new InvalidDataException("Generated instance replacement is invalid.");
        return entry with
        {
            InstanceOrigins = new ReadOnlyDictionary<int, PackageInstanceOrigin>(entry.InstanceOrigins.ToDictionary()),
            PlacementOrigins = new ReadOnlyDictionary<int, PackagePlacementOrigin>(entry.PlacementOrigins.ToDictionary()),
            Replaces = Array.AsReadOnly(entry.Replaces.Distinct().ToArray())
        };
    }
}
