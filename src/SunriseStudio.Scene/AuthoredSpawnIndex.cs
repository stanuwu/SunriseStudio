using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

/** One 0x808099D8 spawn row: the tag that holds it and the row's offset in that tag. */
public readonly record struct SpawnInput(uint Tag, int Offset);

/**
 * Rows of one entity that pass the same initial-state inputs: the damage override at +100
 * and the override chain at +120. First is the group's lowest (tag, offset) row.
 */
public sealed record AuthoredSpawn(SpawnInput First, int RowCount);

/** Every authored spawn row in the installed packages, grouped per entity by spawn input. */
public sealed class AuthoredSpawnIndex
{
    private const uint EntityClass = SEntity.Class, ConfigClass = SEntityConfig.Class, PlacementList = SMapPlacements.Class;
    private const uint SpawnRow = AuthoredPlacementEntry.Class, SlotDescriptor = ObjectSlotHandleType4.Class;
    // A member holds six candidate arrays.
    private const int CandidateArrays = 6;
    private readonly Dictionary<uint, AuthoredSpawn[]> spawns;

    private AuthoredSpawnIndex(Dictionary<uint, AuthoredSpawn[]> spawns, int rows, int unreadTags, TimeSpan time)
    {
        this.spawns = spawns;
        RowCount = rows;
        UnreadTagCount = unreadTags;
        BuildTime = time;
    }

    public int RowCount { get; }
    /** Tags whose arrays did not decode; their rows are missing from the index. */
    public int UnreadTagCount { get; }
    public TimeSpan BuildTime { get; }
    public int EntityCount => spawns.Count;

    /** Groups by first row; element 0 has the lowest (tag, offset), first in package order. */
    public IReadOnlyList<AuthoredSpawn> For(uint entity) => spawns.GetValueOrDefault(entity, []);

    /**
     * Reads every placement list and config of the installed packages.
     * @param progress Receives (tags read, tags to read).
     */
    public static AuthoredSpawnIndex Build(PackageCatalog catalog, CancellationToken token = default,
        Action<int, int>? progress = null)
    {
        var timer = Stopwatch.StartNew();
        var sources = catalog.Entries.Where(entry => entry.Type is 8 or 16
            && entry.Reference is PlacementList or ConfigClass).Select(entry => entry.TagHash).Order().ToArray();
        var groups = new ConcurrentDictionary<(uint Entity, string Input), Group>();
        int rows = 0, unread = 0, done = 0;
        var options = new ParallelOptions { CancellationToken = token };
        Parallel.ForEach(Partitioner.Create(0, sources.Length, 256), options, range =>
        {
            for (int i = range.Item1; i < range.Item2; i++)
            {
                token.ThrowIfCancellationRequested();
                uint tag = sources[i];
                try
                {
                    var data = catalog.Read(tag);
                    foreach (int row in Rows(catalog, tag, data))
                    {
                        uint entity = TagView.At<AuthoredPlacementEntry>(data, row).ClassTag;
                        if (!catalog.TryGetEntry(entity, out var entry) || entry.Reference != EntityClass) continue;
                        Interlocked.Increment(ref rows);
                        var input = new SpawnInput(tag, row);
                        groups.AddOrUpdate((entity, Input(data, tag, row)), _ => new(input, 1),
                            (_, group) => new(Lower(group.First, input), group.Count + 1));
                    }
                }
                catch (Exception error) when (error is InvalidDataException or ArgumentException
                    or OverflowException or IndexOutOfRangeException or IOException)
                { Interlocked.Increment(ref unread); }
                int count = Interlocked.Increment(ref done);
                if ((count & 4095) == 0) progress?.Invoke(count, sources.Length);
            }
        });
        progress?.Invoke(sources.Length, sources.Length);
        var result = groups.GroupBy(pair => pair.Key.Entity).ToDictionary(entity => entity.Key,
            entity => entity.Select(pair => new AuthoredSpawn(pair.Value.First, pair.Value.Count))
                .OrderBy(spawn => spawn.First.Tag).ThenBy(spawn => spawn.First.Offset).ToArray());
        return new(result, rows, unread, timer.Elapsed);
    }

    private sealed record Group(SpawnInput First, int Count);

    private static SpawnInput Lower(SpawnInput a, SpawnInput b) =>
        a.Tag < b.Tag || a.Tag == b.Tag && a.Offset <= b.Offset ? a : b;

    /** Spawn rows of one tag: list rows, type-4 slot rows and squad candidate targets. */
    private static IEnumerable<int> Rows(PackageCatalog catalog, uint tag, byte[] data)
    {
        if (!catalog.TryGetEntry(tag, out var entry)) return [];
        ReadOnlySpan<byte> bytes = data;
        if (entry.Reference == PlacementList)
            return TagView.RowOffsets(bytes, SceneReads.Rows<SMapPlacements, AuthoredPlacementEntry>(bytes, 0,
                nameof(SMapPlacements.Placements), SpawnRow));
        var rows = new HashSet<int>();
        if (ConfigSlotDescriptor.Find(bytes, tag) is { } descriptor
            && TagView.At<SlotComponentHeader>(bytes, descriptor) is { SlotType: 4, Handle.Word: SlotDescriptor })
            rows.UnionWith(TagView.RowOffsets(bytes, SceneReads.Rows<ObjectSlotDefinitionType4, AuthoredPlacementEntry>(bytes,
                descriptor, nameof(ObjectSlotDefinitionType4.EmbeddedPlacements), SpawnRow)));
        rows.UnionWith(SquadCandidates(tag, bytes));
        return rows;
    }

    /** Candidate rows point at embedded spawn rows whose class word sits just before them. */
    private static List<int> SquadCandidates(uint tag, ReadOnlySpan<byte> bytes)
    {
        var found = new List<int>();
        if (!TagView.FitsThrough<SEntityConfig>(bytes, 0, nameof(SEntityConfig.Definition))) return found;
        ref readonly var primaryLink = ref TagView.Member<SEntityConfig, RelativePointer<Unknown>>(bytes, 0,
            nameof(SEntityConfig.DefaultInstance));
        ref readonly var secondaryLink = ref TagView.Member<SEntityConfig, RelativePointer<Unknown>>(bytes, 0,
            nameof(SEntityConfig.Definition));
        if (primaryLink.IsNull || secondaryLink.IsNull) return found;
        int primary = TagView.TargetOffset(bytes, in primaryLink)!.Value;
        int secondary = TagView.TargetOffset(bytes, in secondaryLink)!.Value;
        if (primary < TagView.ClassWordSize || secondary < TagView.ClassWordSize
            || !TagView.FitsThrough<SSquadComponent>(bytes, primary, nameof(SSquadComponent.OwnerConfig))
            || !TagView.FitsThrough<SSquadComponentPeer>(bytes, secondary, "H81eeb835.Cells")
            || TagView.TargetClass(bytes, in primaryLink) != SSquadComponent.Class
            || TagView.TargetClass(bytes, in secondaryLink) != SSquadComponentPeer.Class
            || TagView.Member<SSquadComponent, TagOffsetPointer>(bytes, primary, nameof(SSquadComponent.OwnerConfig)).Tag != tag
            || TagView.Member<SSquadComponentPeer, TagOffsetPointer>(bytes, secondary, nameof(SSquadComponentPeer.OwnerConfig)).Tag != tag)
            return found;
        foreach (ref readonly var member in SceneReads.Rows<SSquadComponentPeer, SquadMember>(bytes, secondary, "H81eeb835.Cells",
                     SquadMember.Class))
            for (int array = 0; array < CandidateArrays; array++)
            {
                int field = TagView.OffsetOf(bytes, in member.CharacterChoices.CandidateVariants[array].Candidates);
                foreach (ref readonly var candidate in SceneReads.Rows<SquadCandidate>(bytes, field, SquadCandidate.Class))
                {
                    if (candidate.Blueprint.IsNull) continue;
                    int row = TagView.TargetOffset(bytes, in candidate.Blueprint)!.Value;
                    if (TagView.Fits<AuthoredPlacementEntry>(bytes, row) && TagView.TargetClass(bytes, in candidate.Blueprint) == SpawnRow)
                        found.Add(row);
                }
            }
        return found;
    }

    /** Keys the inputs the initial-state reader takes; a broken chain keys its row alone. */
    private static string Input(byte[] data, uint tag, int row)
    {
        try { return ReadInput(data, row); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException)
        { return $"invalid {tag:X8}+{row:X}"; }
    }

    private static string ReadInput(byte[] data, int row)
    {
        ReadOnlySpan<byte> bytes = data;
        ref readonly var entry = ref TagView.At<AuthoredPlacementEntry>(bytes, row);
        var text = new StringBuilder();
        text.Append(entry.NameHash.ToString("X8"));
        var seen = new HashSet<int>();
        int link = TagView.OffsetOf(bytes, in entry.Payload);
        while (true)
        {
            // Each node starts with the pointer to the next one.
            ref readonly var next = ref TagView.At<RelativePointer<Unknown>>(bytes, link);
            if (next.IsNull) break;
            int node = TagView.TargetOffset(bytes, in next)!.Value;
            // Every node is checked to the end of the variant override, the longest node read here.
            if (node < TagView.ClassWordSize
                || !TagView.FitsThrough<UnknownVariantSpawnOverride>(bytes, node, nameof(UnknownVariantSpawnOverride.RequestedVariant))
                || !seen.Add(node) || seen.Count > 64)
                throw new InvalidDataException("Spawn override chain is invalid.");
            uint type = TagView.TargetClass(bytes, in next)!.Value;
            // Every chain node shares the variant override's header layout.
            var ordinal = TagView.Member<UnknownVariantSpawnOverride, int>(bytes, node, nameof(UnknownVariantSpawnOverride.ComponentOrdinal));
            text.Append($" {ordinal}:{type:X8}");
            if (type == UnknownVariantSpawnOverride.Class)
            {
                foreach (ref readonly var pair in SceneReads.Rows<UnknownVariantSpawnOverride, C8080389f>(bytes, node,
                             nameof(UnknownVariantSpawnOverride.ParameterPairs), null))
                    text.Append($" {pair.Unk0:X8}={pair.Unk4:X8}");
                var requested = TagView.Member<UnknownVariantSpawnOverride, uint>(bytes, node,
                    nameof(UnknownVariantSpawnOverride.RequestedVariant));
                text.Append($" >{requested:X8}");
            }
            link = node;
        }
        return text.ToString();
    }
}
