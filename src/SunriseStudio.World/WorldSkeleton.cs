using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;

namespace SunriseStudio.World;

/**
 * The world graph of a destination, apart from the content it places. The scenario side runs from a
 * scenario through its states and tag sets to authored objects. The map side runs from a map root
 * through bubbles and containers to placements, static tables and spawn sets.
 */
public static class WorldSkeleton
{
    /**
     * Classes a world copy owns; any other class it names stays a reference to stock content. A copy
     * owns its static bounds, so an object edit of the stock table never rewrites the copy's culling.
     */
    public static IReadOnlySet<uint> Classes { get; } = new HashSet<uint>
    {
        SScenario.Class, SScenarioStateEntry.Class, SScenarioTagSet.Class, SAuthoredObject.Class,
        SMapRoot.Class, SMapLocationNames.Class, SBubbleParent.Class, SBubbleDefinition.Class,
        SMapContainer.Class, SMapPlacements.Class, SStaticMeshInstances.Class, SInstanceBoundsTable.Class,
        SMapResourceLink.Class, SSpawnPoints.Class,
    };

    /** @return The roots and every skeleton tag they reach through skeleton tags, by tag or wide hash. */
    public static HashSet<uint> Collect(PackageCatalog catalog, RegistryIndex schema, IEnumerable<uint> roots)
    {
        var reached = new HashSet<uint>();
        var pending = new Stack<uint>(roots);
        while (pending.TryPop(out uint tag))
        {
            if (!reached.Add(tag)) continue;
            if (!catalog.TryGetEntry(tag, out var entry)) throw new KeyNotFoundException($"0x{tag:X8} is not installed.");
            foreach (var reference in TagDocument.Parse(catalog.Read(tag), entry.Reference, schema).References.Where(x => !x.IsNull))
            {
                uint? target = reference.Kind == TagReferenceKind.WideRef ? catalog.Resolve64(reference.Value) : (uint)reference.Value;
                if (target is { } next && catalog.TryGetEntry(next, out var linked) && Classes.Contains(linked.Reference)) pending.Push(next);
            }
        }
        return reached;
    }
}
