using System.Collections.Immutable;

namespace SunriseStudio.Packages;

/** Each hop names the exact source field that reached the next tag. */
public readonly record struct ComponentSlotHop(uint Tag, uint Class, uint SourceTag, int SourceOffset);

/** Numeric evidence requires an owner slot, a complete chain and a matching primary descriptor. */
public sealed record ComponentSlotEvidence(uint ComponentClass, ushort SlotType, uint OwnerTag,
    uint RegistryKey, int SlotOffset, ushort SlotIndex, int HandleOffset, int SubblockOffset,
    uint ConfigTag, int DescriptorOffset, uint SenseSchema, uint AuthSchema, uint BubbleIndex,
    ImmutableArray<ComponentSlotHop> Path);

internal sealed record ComponentSlotOwner(ImmutableArray<ComponentSlotEvidence> Evidence,
    ImmutableHashSet<uint> Dependencies, string? Error);

/** Existing component scans can seed summaries without retaining or rereading config payloads. */
public sealed class ComponentSlotBuilder
{
    private readonly PackageCatalog catalog;
    private readonly long generation, overlay;
    private readonly Dictionary<uint, ComponentSlotNode> nodes = [];

    public ComponentSlotBuilder(PackageCatalog catalog)
    {
        this.catalog = catalog;
        generation = catalog.ReadGeneration; overlay = catalog.OverlayRevision;
    }

    public void ObserveConfig(ComponentSlotConfigSeed seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ComponentSlotCatalog.RequireSnapshot(catalog, generation, overlay);
        if (catalog.TryGetEntry(seed.Tag, out var entry) && AssetCategories.IsStructured(entry)
            && entry.Reference == ComponentSlotReader.ConfigClass) nodes[seed.Tag] = seed.Node;
    }

    /** Only owner payloads and their unseeded chain nodes need additional package reads. */
    public ComponentSlotCatalog Build(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ComponentSlotCatalog.RequireSnapshot(catalog, generation, overlay);
        var owners = new Dictionary<uint, ComponentSlotOwner>();
        long evidenceCount = 0;
        var tags = catalog.Entries.Select(entry => entry.TagHash)
            .Concat(catalog.GetOverlayEntries().Select(entry => entry.Entry.TagHash)).Distinct().Order();
        foreach (uint tag in tags)
        {
            token.ThrowIfCancellationRequested();
            if (!catalog.TryGetEntry(tag, out var entry) || !AssetCategories.IsStructured(entry)
                || entry.Reference != ComponentSlotReader.OwnerClass) continue;
            var owner = ComponentSlotCatalog.Analyze(tag, Read, token);
            evidenceCount += owner.Evidence.Length;
            if (evidenceCount > ComponentSlotCatalog.MaximumEvidence)
                throw new InvalidDataException("Component role evidence exceeds the snapshot limit.");
            owners.Add(tag, owner);
        }
        token.ThrowIfCancellationRequested();
        ComponentSlotCatalog.RequireSnapshot(catalog, generation, overlay);
        return new(nodes.ToImmutableDictionary(), owners.ToImmutableDictionary());

        ComponentSlotNode Read(uint tag)
        {
            if (!nodes.TryGetValue(tag, out var node)) nodes.Add(tag, node = ComponentSlotCatalog.ReadNode(catalog, tag));
            return node;
        }
    }
}

/** A class with conflicting observed slot types never receives a guessed numeric role. */
public sealed partial class ComponentSlotCatalog
{
    // These extraction bounds cap memory and repeated paths without assigning unknown slot types.
    internal const int MaximumEvidence = 2_000_000;
    private const int MaximumOwnerWork = 262_144;
    private readonly ImmutableDictionary<uint, ComponentSlotNode> nodes;
    private readonly ImmutableDictionary<uint, ComponentSlotOwner> owners;
    private readonly ImmutableDictionary<uint, ImmutableHashSet<uint>> dependents;
    public static ComponentSlotCatalog Empty { get; } = new(
        ImmutableDictionary<uint, ComponentSlotNode>.Empty, ImmutableDictionary<uint, ComponentSlotOwner>.Empty);

    public ImmutableDictionary<uint, ushort> SlotTypes { get; }
    public ImmutableDictionary<uint, ImmutableArray<ComponentSlotEvidence>> Evidence { get; }
    public ImmutableDictionary<uint, ImmutableArray<ushort>> Conflicts { get; }
    public ImmutableArray<string> Diagnostics { get; }
    public int OwnerCount => owners.Count;
    public int EvidenceCount => Evidence.Sum(pair => pair.Value.Length);
    public bool IsComplete => owners.Values.All(owner => owner.Error is null) && Conflicts.Count == 0;
    public string SourceSummary => $"Package owner/slot/descriptor joins: {OwnerCount} owners, {EvidenceCount} observations, "
        + $"{SlotTypes.Count} numeric classes, {Conflicts.Count} conflicts; no generated labels.";

    /** Groups owner evidence by class; one slot type maps the class, several are a conflict. */
    internal ComponentSlotCatalog(ImmutableDictionary<uint, ComponentSlotNode> nodes,
        ImmutableDictionary<uint, ComponentSlotOwner> owners)
    {
        this.nodes = nodes; this.owners = owners;
        Evidence = owners.OrderBy(pair => pair.Key).SelectMany(pair => pair.Value.Evidence)
            .GroupBy(row => row.ComponentClass).ToImmutableDictionary(group => group.Key, group => group.ToImmutableArray());
        var types = Evidence.ToDictionary(pair => pair.Key, pair => pair.Value.Select(row => row.SlotType).Distinct().Order().ToImmutableArray());
        SlotTypes = types.Where(pair => pair.Value.Length == 1).ToImmutableDictionary(pair => pair.Key, pair => pair.Value[0]);
        Conflicts = types.Where(pair => pair.Value.Length > 1).ToImmutableDictionary();
        Diagnostics = owners.OrderBy(pair => pair.Key).Where(pair => pair.Value.Error is not null)
            .Select(pair => $"Owner {pair.Key:X8}: {pair.Value.Error}")
            .Concat(Conflicts.OrderBy(pair => pair.Key).Select(pair => $"Component {pair.Key:X8} has conflicting slot types: {string.Join(", ", pair.Value)}."))
            .ToImmutableArray();
        dependents = owners.SelectMany(pair => pair.Value.Dependencies.Select(tag => (Tag: tag, Owner: pair.Key)))
            .GroupBy(pair => pair.Tag).ToImmutableDictionary(group => group.Key, group => group.Select(pair => pair.Owner).ToImmutableHashSet());
    }

    public bool TryGetSlotType(uint componentClass, out ushort slotType) => SlotTypes.TryGetValue(componentClass, out slotType);

    public static ComponentSlotConfigSeed ParseConfig(uint tag, ReadOnlySpan<byte> bytes) =>
        new(ComponentSlotReader.Parse(tag, ComponentSlotReader.ConfigClass, bytes));

    public static ComponentSlotCatalog Build(PackageCatalog catalog, CancellationToken token = default) =>
        new ComponentSlotBuilder(catalog).Build(token);

    /** @return The slots of one installed authored object that reach a config; an incomplete walk throws. */
    public static ImmutableArray<ComponentSlotEvidence> OwnerSlots(PackageCatalog catalog, uint ownerTag, CancellationToken token = default)
    {
        var read = new Dictionary<uint, ComponentSlotNode>();
        var owner = Analyze(ownerTag, tag => read.TryGetValue(tag, out var node) ? node : read[tag] = ReadNode(catalog, tag), token);
        return owner.Error is null ? owner.Evidence : throw new InvalidDataException($"Owner 0x{ownerTag:X8}: {owner.Error}");
    }

    /** Changed nodes invalidate only owners that depended on them; missing links remain dependencies. */
    public ComponentSlotCatalog Refresh(PackageCatalog catalog, IEnumerable<uint> changedTags,
        out IReadOnlySet<uint> affectedClasses,
        IReadOnlyDictionary<uint, ComponentSlotConfigSeed>? changedConfigs = null,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        long generation = catalog.ReadGeneration, overlay = catalog.OverlayRevision;
        var changed = changedTags.ToHashSet();
        var affectedOwners = new HashSet<uint>();
        foreach (uint tag in changed)
        {
            token.ThrowIfCancellationRequested();
            if (dependents.TryGetValue(tag, out var users)) affectedOwners.UnionWith(users);
            if (owners.ContainsKey(tag) || catalog.TryGetEntry(tag, out var entry)
                && AssetCategories.IsStructured(entry) && entry.Reference == ComponentSlotReader.OwnerClass)
                affectedOwners.Add(tag);
        }
        affectedClasses = ImmutableHashSet<uint>.Empty;
        if (affectedOwners.Count == 0 && !changed.Any(nodes.ContainsKey)) return this;
        var nextNodes = nodes.ToBuilder();
        foreach (uint tag in changed)
        {
            nextNodes.Remove(tag);
            if (changedConfigs?.TryGetValue(tag, out var seed) == true && seed.Tag == tag
                && catalog.TryGetEntry(tag, out var entry) && AssetCategories.IsStructured(entry)
                && entry.Reference == ComponentSlotReader.ConfigClass) nextNodes[tag] = seed.Node;
        }
        var nextOwners = owners.ToBuilder();
        long evidenceCount = owners.Values.Sum(owner => (long)owner.Evidence.Length);
        var touched = affectedOwners.Where(owners.ContainsKey).SelectMany(tag => owners[tag].Evidence.Select(row => row.ComponentClass)).ToHashSet();
        foreach (uint tag in affectedOwners.Order())
        {
            token.ThrowIfCancellationRequested();
            if (!catalog.TryGetEntry(tag, out var entry) || !AssetCategories.IsStructured(entry)
                || entry.Reference != ComponentSlotReader.OwnerClass) { nextOwners.Remove(tag); continue; }
            var owner = Analyze(tag, Read, token);
            evidenceCount += owner.Evidence.Length - (owners.GetValueOrDefault(tag)?.Evidence.Length ?? 0);
            if (evidenceCount > MaximumEvidence) throw new InvalidDataException("Component role evidence exceeds the snapshot limit.");
            nextOwners[tag] = owner; touched.UnionWith(owner.Evidence.Select(row => row.ComponentClass));
        }
        token.ThrowIfCancellationRequested();
        RequireSnapshot(catalog, generation, overlay);
        var result = new ComponentSlotCatalog(nextNodes.ToImmutable(), nextOwners.ToImmutable());
        affectedClasses = touched.Where(type => SlotTypes.GetValueOrDefault(type) != result.SlotTypes.GetValueOrDefault(type)
            || !Conflicts.GetValueOrDefault(type, []).SequenceEqual(result.Conflicts.GetValueOrDefault(type, []))).ToImmutableHashSet();
        return result;

        ComponentSlotNode Read(uint tag)
        {
            if (!nextNodes.TryGetValue(tag, out var node)) nextNodes[tag] = node = ReadNode(catalog, tag);
            return node;
        }
    }

    internal static void RequireSnapshot(PackageCatalog catalog, long generation, long overlay)
    {
        if (catalog.ReadGeneration != generation || catalog.OverlayRevision != overlay)
            throw new InvalidOperationException("Package source changed during component-role extraction.");
    }

    internal static ComponentSlotNode ReadNode(PackageCatalog catalog, uint tag)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || !AssetCategories.IsStructured(entry))
            return ComponentSlotNode.Failed(tag, 0, "Descriptor-chain source is missing or not structured.");
        if (!ComponentSlotReader.Supported(entry.Reference))
            return ComponentSlotNode.Failed(tag, entry.Reference, "Unsupported descriptor-chain class.");
        return ComponentSlotReader.Parse(tag, entry.Reference, catalog.Read(tag));
    }

    /** No evidence from an incomplete owner walk is published. */
    internal static ComponentSlotOwner Analyze(uint ownerTag, Func<uint, ComponentSlotNode> read, CancellationToken token)
    {
        var dependencies = ImmutableHashSet.CreateBuilder<uint>(); dependencies.Add(ownerTag);
        var output = ImmutableArray.CreateBuilder<ComponentSlotEvidence>();
        var owner = read(ownerTag);
        int totalWork = 0;
        if (owner.Error is not null) return new([], dependencies.ToImmutable(), owner.Error);
        try
        {
            foreach (var root in owner.Roots)
            {
                int scheduled = 0;
                Walk(root.Tag, ownerTag, root.Offset, root, []);

                void Walk(uint tag, uint sourceTag, int sourceOffset, ComponentSlotRoot root, ImmutableArray<ComponentSlotHop> path)
                {
                    token.ThrowIfCancellationRequested(); dependencies.Add(tag);
                    if (++totalWork > MaximumOwnerWork || ++scheduled > 256 || path.Length >= 9 || path.Any(hop => hop.Tag == tag))
                        throw new InvalidDataException("Descriptor chain exceeds its depth, node or cycle bound.");
                    var node = read(tag);
                    if (node.Error is not null) throw new InvalidDataException($"{tag:X8}: {node.Error}");
                    path = path.Add(new(tag, node.Class, sourceTag, sourceOffset));
                    if (node.Class == ComponentSlotReader.ConfigClass)
                    {
                        foreach (var candidate in node.Descriptors)
                        {
                            if (candidate.Key != owner.Key || candidate.Index >= owner.Slots.Length
                                || owner.Slots[candidate.Index].Type != candidate.Type) continue;
                            if (output.Count == MaximumOwnerWork) throw new InvalidDataException("Owner evidence exceeds the extraction limit.");
                            output.Add(new(candidate.Class, candidate.Type, ownerTag, owner.Key,
                                owner.Slots[candidate.Index].Offset, candidate.Index, root.Offset, root.SubblockOffset,
                                tag, candidate.Offset, candidate.Sense, candidate.Auth, candidate.Bubble, path));
                        }
                        return;
                    }
                    if (node.Class is not (ComponentSlotReader.IndirectClass or ComponentSlotReader.RedirectClass))
                        throw new InvalidDataException("A placed chain reached an unsupported owner class.");
                    foreach (var next in node.Next) Walk(next.Tag, tag, next.Offset, root, path);
                }
            }
            return new(output.ToImmutable(), dependencies.ToImmutable(), null);
        }
        catch (InvalidDataException error) { return new([], dependencies.ToImmutable(), error.Message); }
    }
}
