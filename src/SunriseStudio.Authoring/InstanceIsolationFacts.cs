using System.Buffers;
using System.Buffers.Binary;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

internal readonly record struct IsolationReference(uint Tag, ulong Wide)
{
    internal uint? Resolve(PackageCatalog catalog) => Tag == uint.MaxValue ? catalog.Resolve64(Wide) : Tag;
    internal uint? Resolve(IsolationTags tags) => Tag == uint.MaxValue ? tags.Resolve64(Wide) : Tag;
}
internal sealed record IsolationNode(uint Tag, uint Class, bool Valid, uint Link,
    InstanceIsolationScope.Placement[] Placements, uint[] Maps, IsolationReference[] Children, uint[] OwnerCandidates);

/** Facts retain typed links and offsets, never whole placement payloads or cached wide-tag resolutions. */
internal static class InstanceIsolationFacts
{
    internal static readonly HashSet<uint> Classes =
    [
        SMapResourceLink.Class, SMapPlacements.Class, SMapRoot.Class, SBubbleParent.Class, SBubbleDefinition.Class,
        SMapContainer.Class, SScenarioStateEntry.Class, SScenarioTagSet.Class
    ];

    internal static IsolationNode Parse(uint tag, uint type, byte[] bytes, IReadOnlySet<uint> owners)
    {
        try
        {
            uint link = 0;
            var placements = new List<InstanceIsolationScope.Placement>();
            var maps = new List<uint>();
            var children = new List<IsolationReference>();
            switch (type)
            {
                case SMapResourceLink.Class:
                    InstanceTagReads.Require<SMapResourceLink>(bytes, 0, nameof(SMapResourceLink.IsolatedFlag));
                    link = InstanceTagReads.Field<SMapResourceLink, uint>(bytes, 0, nameof(SMapResourceLink.InstanceTable)); break;
                case SMapPlacements.Class:
                    int[] rows = InstanceTagReads.Rows<SMapPlacements, AuthoredPlacementEntry>(bytes, 0, nameof(SMapPlacements.Placements),
                        AuthoredPlacementEntry.Class);
                    for (int index = 0; index < rows.Length; index++)
                        foreach (var (auxiliary, marker) in InstanceIsolationScope.Chain(bytes, rows[index]))
                        {
                            if (marker == SStaticInstancesPayload.Class)
                                placements.Add(new(tag, rows[index], index, auxiliary,
                                    TagView.Checked<SStaticInstancesPayload>(bytes, auxiliary).StaticOwner));
                            else if (marker == SPlacementVisibilityConstructor.Class)
                                maps.Add(TagView.Checked<SPlacementVisibilityConstructor>(bytes, auxiliary).VisibilityMap);
                        }
                    break;
                case SMapRoot.Class:
                    foreach (int row in InstanceTagReads.Rows<SMapRoot, MapBubbleRef>(bytes, 0, nameof(SMapRoot.MapEntries), MapBubbleRef.Class))
                    {
                        ref readonly var reference = ref TagView.At<MapBubbleRef>(bytes, row);
                        children.Add(new(reference.NarrowTag, reference.NarrowTag == uint.MaxValue ? reference.WideTag : 0));
                    }
                    break;
                case SBubbleParent.Class:
                    link = InstanceTagReads.Field<SBubbleParent, uint>(bytes, 0, nameof(SBubbleParent.BubbleLink)); break;
                case SBubbleDefinition.Class:
                    foreach (int row in InstanceTagReads.Rows<SBubbleDefinition, SBubbleChildRef>(bytes, 0,
                        nameof(SBubbleDefinition.BubbleChildren), SBubbleChildRef.Class))
                    {
                        ref readonly var reference = ref TagView.At<SBubbleChildRef>(bytes, row);
                        children.Add(new(reference.NarrowTag, reference.NarrowTag == uint.MaxValue ? reference.WideTag : 0));
                    }
                    break;
                case SMapContainer.Class:
                    foreach (int row in InstanceTagReads.Rows<SMapContainer, ContainerMember>(bytes, 0, nameof(SMapContainer.Contents),
                        ContainerMember.Class))
                        children.Add(new(TagView.At<ContainerMember>(bytes, row).MemberTag, 0));
                    break;
                case SScenarioStateEntry.Class:
                    link = InstanceTagReads.Field<SScenarioStateEntry, uint>(bytes, 0, nameof(SScenarioStateEntry.Link)); break;
                case SScenarioTagSet.Class:
                    TagSet(nameof(SScenarioTagSet.TagSetA));
                    TagSet(nameof(SScenarioTagSet.TagSetB));
                    TagSet(nameof(SScenarioTagSet.TagSetC));
                    break;
                default: return new(tag, type, false, 0, [], [], [], []);
            }
            return new(tag, type, true, link, placements.ToArray(), maps.Distinct().ToArray(), children.ToArray(), []);

            void TagSet(string member)
            {
                foreach (int row in InstanceTagReads.Rows<SScenarioTagSet, RegistryObjectRef>(bytes, 0, member, RegistryObjectRef.Class))
                    children.Add(new(TagView.At<RegistryObjectRef>(bytes, row).Tag, 0));
            }
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException or ArgumentException)
        {
            return new(tag, type, false, 0, [], [], [], CandidateOwners(bytes, owners));
        }
    }

    /** Even an unaligned malformed constructor must remain a candidate for scoped refusal. */
    internal static uint[] CandidateOwners(byte[] bytes, IReadOnlySet<uint> owners)
    {
        if (owners.Count == 0) return [];
        var found = new HashSet<uint>();
        var highBytes = SearchValues.Create(owners.Select(owner => (byte)(owner >> 24)).Distinct().ToArray());
        // A little-endian word ends with the owner's high byte.
        const int highByteAt = sizeof(uint) - 1;
        for (int at = 0; at <= bytes.Length - sizeof(uint); at++)
        {
            int skip = bytes.AsSpan(at + highByteAt).IndexOfAny(highBytes);
            if (skip < 0) break;
            at += skip;
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, sizeof(uint)));
            if (owners.Contains(value)) found.Add(value);
        }
        return found.Order().ToArray();
    }
}

/**
 * The installed tags and the project's added entries, as isolation reads them. An added entry can
 * place an installed static owner, so every scope and every shared-row check reads both.
 */
internal sealed class IsolationTags
{
    private readonly ObjectEditContext context;
    private readonly Dictionary<uint, AddedObjectEntry> added = [];
    private readonly Dictionary<uint, byte[]> addedBytes = [];
    private readonly Dictionary<ulong, uint> addedWide = [];

    internal IsolationTags(ObjectEditContext context)
    {
        this.context = context;
        foreach (var entry in context.Options.AddedEntries)
        {
            // An overlay row can also stand for an edited installed tag; that one is read as installed.
            if (context.Catalog.TryGetEntry(entry.Tag, out var known) && context.Catalog.ContainsInstalledPackage(known.PackageId))
                continue;
            added[entry.Tag] = entry;
            foreach (ulong hash in entry.WideHashes) addedWide.TryAdd(hash, entry.Tag);
        }
    }

    internal bool IsAdded(uint tag) => added.ContainsKey(tag);

    /** True when an added entry of the class exists. */
    internal bool HasAdded(uint type) => added.Values.Any(entry => entry.Class == type);

    /** Installed tags of a class, then added ones. */
    internal IEnumerable<uint> OfClass(uint type) => context.Catalog.Entries.Where(entry => entry.Reference == type)
        .Select(entry => entry.TagHash).Concat(added.Values.Where(entry => entry.Class == type).Select(entry => entry.Tag).Order());

    internal uint ClassOf(uint tag) => added.TryGetValue(tag, out var entry) ? entry.Class
        : context.Catalog.TryGetEntry(tag, out var installed) ? installed.Reference : 0;

    /** An added entry's bytes, read once; any other tag reads the compile's current layer. */
    internal byte[] Read(uint tag)
    {
        if (!added.TryGetValue(tag, out var entry)) return context.ReadLayer(tag);
        if (addedBytes.TryGetValue(tag, out var bytes)) return bytes;
        try { return addedBytes[tag] = entry.Read(); }
        catch (Exception error) when (error is IOException or KeyNotFoundException or InvalidOperationException
            or UnauthorizedAccessException or InvalidDataException)
        { throw new InvalidDataException($"Static isolation cannot read added asset {tag:X8}.", error); }
    }

    internal uint? Resolve64(ulong hash) => addedWide.TryGetValue(hash, out uint tag) ? tag : context.Catalog.Resolve64(hash);
}

/** Current raw changes replace original facts only within one compile. */
internal sealed class InstanceIsolationSources
{
    private readonly ObjectEditContext context;
    private readonly IReadOnlyDictionary<uint, IsolationNode>? original;
    private readonly HashSet<uint> owners;
    private readonly Dictionary<uint, IsolationNode> current = [];

    internal InstanceIsolationSources(ObjectEditContext context)
    {
        this.context = context;
        Tags = new IsolationTags(context);
        owners = Tags.OfClass(SMapResourceLink.Class).ToHashSet();
        original = context.IsolationSnapshot?.Nodes;
    }

    internal IsolationTags Tags { get; }

    internal IEnumerable<IsolationNode> OfClass(uint type) => Tags.OfClass(type).Select(Read);

    internal IsolationNode Read(uint tag)
    {
        if (current.TryGetValue(tag, out var node)) return node;
        if (original is not null && !Tags.IsAdded(tag) && !context.Options.RawChangedTags!.Contains(tag)
            && original.TryGetValue(tag, out node)) return node;
        uint type = Tags.ClassOf(tag);
        if (!InstanceIsolationFacts.Classes.Contains(type)) return new(tag, type, false, 0, [], [], [], []);
        return current[tag] = InstanceIsolationFacts.Parse(tag, type, Tags.Read(tag), owners);
    }
}
