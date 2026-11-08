using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

public sealed record InvestmentSocketOverride(int Offset, ushort SocketType, ushort PlugItem, ushort PlugSet,
    ushort RollSet, uint Selection)
{
    public bool FixedPlug => Selection == 0xFFFFFFFF;
}
/** The first modifier whose condition holds replaces the entry weight. A negative modifier value keeps the base weight. */
public sealed record InvestmentWeightModifier(int Offset, InvestmentExpression Condition, ushort ValueIndex, float Value);
/** One pool entry. Offset is absolute in the pool table, and Raw covers the whole row. */
public sealed record InvestmentRewardEntry(int Pool, int Index, int Offset, ushort ItemIndex, uint Quantity, ushort NestedPool,
    ushort Slot84Reference, uint Category, float Weight, InvestmentExpression Condition,
    IReadOnlyList<InvestmentWeightModifier> Modifiers, IReadOnlyList<InvestmentSocketOverride> Sockets,
    InvestmentTableField WeightField, InvestmentTableField QuantityField, ReadOnlyMemory<byte> Raw, string Error = "")
{
    /** Entry bytes Studio does not decode, shown raw: the u16 at +2, the eight bytes at +12 and the u32 at +28. */
    public ushort Unknown2 => BinaryPrimitives.ReadUInt16LittleEndian(Raw.Span[2..]);
    public ulong Unknown12 => BinaryPrimitives.ReadUInt64LittleEndian(Raw.Span[12..]);
    public uint Unknown28 => BinaryPrimitives.ReadUInt32LittleEndian(Raw.Span[28..]);
    public bool AnyCategory => Category == InvestmentRewards.AnyCategory;
}
public sealed record InvestmentRewardPool(int Index, int Offset, uint Hash,
    IReadOnlyList<InvestmentRewardEntry> Entries, string Error = "");
/** A wrapper selection draws Count entries from Category. Unknown8 is populated but not decoded. */
public sealed record InvestmentRewardSelection(uint Category, uint Count, uint Unknown8);
/** An item's wrapper block, which names its reward pool. The item opens when acquired only when flag bit 0 is set. */
public sealed record InvestmentRewardWrapper(int Block, ushort Pool, uint Flags, uint Unknown4,
    IReadOnlyList<InvestmentRewardSelection> Selections, ushort AcquiredFlagSlot, string Error = "")
{
    public bool OpensOnAcquisition => (Flags & 1) != 0;
}

/** Reward pools (root slot 88), their entries, and the wrapper blocks items carry (package_reward_build.cpp). */
public static class InvestmentRewards
{
    /** An entry in the empty string's category matches every selection (reward_resolver.cpp kEmptyTag). */
    public const uint AnyCategory = StringHash.Empty;
    /** The most selections Sunrise accepts on one wrapper (rewards/definition.h kSelectionCapacity). */
    public const int SelectionLimit = 4;
    /** Nested pools, automatic wrappers and conditions share Sunrise's traversal depth (rewards/definition.h kTraversalDepth). */
    public const int ServerTraversalDepth = InvestmentUnlocks.ServerNestingLimit;
    /** A selection's third word, which the layout leaves unnamed. */
    private const int SelectionUnknown = 8;
    private const ushort Absent = InvestmentCatalog.Absent;

    /** Every pool row stays at its native index. A bad row keeps its place and carries its Error. */
    internal static IReadOnlyList<InvestmentRewardPool> Read(InvestmentSlotRead slot, int itemCount,
        List<string> diagnostics, CancellationToken token)
    {
        if (!slot.Loaded) { diagnostics.Add("Reward pools: " + slot.Diagnostic); return []; }
        var table = slot.Bytes.Span;
        int stride = Unsafe.SizeOf<RewardPoolRow>(), entryStride = Unsafe.SizeOf<RewardEntry>();
        var rows = TagArray.Checked(table, TagView.FieldOffset<RewardPoolTable>("Table"), RewardPoolRow.Class, stride);
        var pools = new InvestmentRewardPool[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            if ((i & 63) == 0) token.ThrowIfCancellationRequested();
            int at = rows.Data + i * stride; uint hash = TagView.At<RewardPoolRow>(table, at).Unk0;
            string error = hash == 0 ? "Pool has no definition hash." : "";
            IReadOnlyList<InvestmentRewardEntry> entries = [];
            try
            {
                var members = TagArray.Checked(table,
                    at + TagView.FieldOffset<RewardPoolRow>(nameof(RewardPoolRow.Items)), RewardEntry.Class,
                    entryStride);
                var read = new InvestmentRewardEntry[members.Count];
                for (int entry = 0; entry < read.Length; entry++)
                    read[entry] = ReadEntry(table, slot, i, entry, members.Data + entry * entryStride, itemCount, rows.Count);
                entries = read;
            }
            catch (Exception failure) when (InvestmentCatalog.Recoverable(failure)) { error = (error + " " + failure.Message).Trim(); }
            pools[i] = new(i, at, hash, entries, error);
        }
        return pools;
    }

    /** Reads each sub-array independently, so one bad array does not hide the others. */
    private static InvestmentRewardEntry ReadEntry(ReadOnlySpan<byte> table, InvestmentSlotRead slot, int pool, int index,
        int at, int itemCount, int poolCount)
    {
        var problems = new List<string>();
        ref readonly var row = ref TagView.At<RewardEntry>(table, at);
        ushort item = row.Item, nested = row.Pool;
        float weight = row.Weight;
        if (!float.IsFinite(weight) || weight < 0) problems.Add("Weight is negative or not finite.");
        if (item != Absent && item >= itemCount) problems.Add($"Item {item} is outside the item index.");
        if (nested != Absent && nested >= poolCount) problems.Add($"Nested pool {nested} is outside the pool table.");
        var condition = InvestmentUnlocks.ReadExpression(table, TagView.OffsetOf(table, in row.VisibilityData));
        if (condition.Error.Length != 0) problems.Add("Condition: " + condition.Error);
        IReadOnlyList<InvestmentWeightModifier> modifiers = [];
        try
        {
            int stride = Unsafe.SizeOf<RewardModifier>();
            var rows = TagArray.Checked(table, TagView.OffsetOf(table, in row.AlternateData), RewardModifier.Class, stride);
            var read = new InvestmentWeightModifier[rows.Count];
            for (int i = 0; i < read.Length; i++)
            {
                int p = rows.Data + i * stride;
                ref readonly var modifier = ref TagView.At<RewardModifier>(table, p);
                read[i] = new(p, InvestmentUnlocks.ReadExpression(table, p), modifier.ValueIndex, modifier.Value);
            }
            modifiers = read;
        }
        catch (Exception error) when (InvestmentCatalog.Recoverable(error)) { problems.Add("Modifiers: " + error.Message); }
        var sockets = ReadSockets(table, TagView.OffsetOf(table, in row.SocketOverrides), itemCount, problems);
        return new(pool, index, at, item, row.Quantity, nested, row.Supplemental, row.Category, weight,
            condition, modifiers, sockets,
            new(InvestmentTableFieldKind.PoolEntryWeight, slot.Tag, pool, index,
                ViewProperty.Of(table, "Weight", in row.Weight, true)),
            new(InvestmentTableFieldKind.PoolEntryQuantity, slot.Tag, pool, index,
                ViewProperty.Of(table, "Quantity", in row.Quantity, true)),
            slot.Bytes.Slice(at, Unsafe.SizeOf<RewardEntry>()), string.Join(" ", problems));
    }

    /** Socket overrides need a socket type, and a fixed plug must be an installed item (rewards/definition.h valid_socket). */
    internal static IReadOnlyList<InvestmentSocketOverride> ReadSockets(ReadOnlySpan<byte> blob, int field,
        int itemCount, List<string> problems)
    {
        try
        {
            int stride = Unsafe.SizeOf<RewardSocketOverride>();
            var rows = TagArray.Checked(blob, field, RewardSocketOverride.Class, stride);
            var sockets = new InvestmentSocketOverride[rows.Count];
            for (int i = 0; i < rows.Count; i++)
            {
                int p = rows.Data + i * stride;
                ref readonly var row = ref TagView.At<RewardSocketOverride>(blob, p);
                var socket = sockets[i] = new(p, row.SocketType, row.Plug, row.PlugSet, row.RollSet, row.Selection);
                if (socket.SocketType == Absent) problems.Add($"Socket override {i} has no socket type.");
                if (socket.PlugItem != Absent
                    && socket.PlugItem >= itemCount) problems.Add($"Socket override {i} plugs item {socket.PlugItem}, outside the item index.");
            }
            return sockets;
        }
        catch (Exception error) when (InvestmentCatalog.Recoverable(error)) { problems.Add("Socket overrides: " + error.Message); return []; }
    }

    /** @return The item's wrapper block, or null when the item names none. */
    internal static InvestmentRewardWrapper? ReadWrapper(ReadOnlySpan<byte> item)
    {
        int block = InvestmentItemBlocks.Find(item, nameof(ItemDefinition.SackItemBlock), RewardWrapper.Class);
        if (block < 0) return null;
        if (!TagView.FitsThrough<RewardWrapper>(item, block,
            nameof(RewardWrapper.Flags))) throw new InvalidDataException("Reward wrapper is truncated.");
        int stride = Unsafe.SizeOf<RewardSelection>();
        var rows = TagArray.Checked(item,
            block + TagView.FieldOffset<RewardWrapper>("RewardProperties.CategoryList"), RewardSelection.Class,
            stride);
        var selections = new InvestmentRewardSelection[rows.Count];
        for (int i = 0; i < selections.Length; i++)
        {
            int p = rows.Data + i * stride;
            ref readonly var row = ref TagView.At<RewardSelection>(item, p);
            selections[i] = new(row.Unk0, row.Count, TagRead.U32(item, p + SelectionUnknown));
        }
        return new(block, TagView.Member<RewardWrapper, ushort>(item, block, nameof(RewardWrapper.Pool)),
            TagView.Member<RewardWrapper, uint>(item, block, nameof(RewardWrapper.Flags)),
            TagView.Member<RewardWrapper, uint>(item, block, nameof(RewardWrapper.Unk4)), selections,
            InvestmentItemBlocks.Field(item, InvestmentItemBlocks.AcquiredFlag),
            selections.Length > SelectionLimit ? $"More selections than Sunrise accepts ({SelectionLimit})." : "");
    }
}

/** Pools linked through nested-pool entries and automatic wrappers, which share one traversal in Sunrise. */
public sealed class InvestmentRewardGraph
{
    private readonly List<int>[] wrappers;
    private readonly List<(int Pool, int Entry)>[] parents;
    /** Pools on each cycle, in the order the walk met them. */
    public IReadOnlyList<IReadOnlyList<int>> Cycles { get; }
    /** The longest chain below each pool, or -1 for a pool on or above a cycle. */
    public IReadOnlyList<int> Depths { get; }
    public int MaximumDepth { get; }

    private InvestmentRewardGraph(List<int>[] wrappers, List<(int, int)>[] parents,
        IReadOnlyList<IReadOnlyList<int>> cycles, int[] depths)
    {
        this.wrappers = wrappers; this.parents = parents; Cycles = cycles; Depths = depths;
        MaximumDepth = depths.DefaultIfEmpty(0).Max();
    }

    /** Items whose wrapper opens this pool. */
    public IReadOnlyList<int> WrappersOf(int pool) => pool >= 0 && pool < wrappers.Length ? wrappers[pool] : [];
    /** Entries that nest this pool. */
    public IReadOnlyList<(int Pool, int Entry)> ParentsOf(int pool) => pool >= 0 && pool < parents.Length ? parents[pool] : [];
    public int References(int pool) => WrappersOf(pool).Count + ParentsOf(pool).Count;

    /**
     * Links each pool to its nested pools and to the pool of any item it gives whose wrapper opens when acquired. Other
     * wrappers are leaves.
     * @param wrapperPoolByItem Each item's wrapper pool, 0xFFFF when it has none.
     * @param wrapperFlagsByItem Each item's wrapper flags.
     */
    public static InvestmentRewardGraph Build(IReadOnlyList<InvestmentRewardPool> pools, IReadOnlyList<ushort> wrapperPoolByItem,
        IReadOnlyList<uint> wrapperFlagsByItem)
    {
        int count = pools.Count;
        var wrappers = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        var parents = Enumerable.Range(0, count).Select(_ => new List<(int, int)>()).ToArray();
        for (int item = 0; item < wrapperPoolByItem.Count; item++)
            if (wrapperPoolByItem[item] < count) wrappers[wrapperPoolByItem[item]].Add(item);
        var edges = new IReadOnlyList<int>[count];
        foreach (var pool in pools)
        {
            var targets = new List<int>();
            foreach (var entry in pool.Entries)
            {
                if (entry.NestedPool < count) { targets.Add(entry.NestedPool); parents[entry.NestedPool].Add((pool.Index, entry.Index)); }
                if (entry.ItemIndex < wrapperPoolByItem.Count && wrapperPoolByItem[entry.ItemIndex] < count
                    && (wrapperFlagsByItem[entry.ItemIndex] & 1) != 0)
                    targets.Add(wrapperPoolByItem[entry.ItemIndex]);
            }
            edges[pool.Index] = targets;
        }
        var (order, cycles) = InvestmentGraphWalk.Walk(edges);
        var cyclic = cycles.SelectMany(cycle => cycle).ToHashSet();
        var depths = new int[count];
        foreach (int pool in order)
            depths[pool] = cyclic.Contains(pool) || edges[pool].Any(target => depths[target] < 0) ? -1
                : edges[pool].Select(target => depths[target] + 1).DefaultIfEmpty(0).Max();
        return new(wrappers, parents, cycles, depths);
    }

    /** The total weight a selection draws from: entries in its category or in any category. A null category sums every entry. */
    public static double BaseTotal(InvestmentRewardPool pool, uint? category) => pool.Entries
        .Where(entry => category is null || entry.AnyCategory || entry.Category == category)
        .Where(entry => float.IsFinite(entry.Weight) && entry.Weight >= 0).Sum(entry => (double)entry.Weight);
}
