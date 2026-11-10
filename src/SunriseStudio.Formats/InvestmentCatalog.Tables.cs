using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

/**
 * An item's references to other investment tables, read while the catalog loads the item's definition. A reference that
 * cannot be read holds Absent.
 */
internal readonly record struct InvestmentItemLinks(ushort WrapperPool, byte WrapperFlags, ushort QuestOwner, ushort[] Objectives)
{
    private const ushort Absent = InvestmentCatalog.Absent;
    public static readonly InvestmentItemLinks None = new(Absent, 0, Absent, []);
    public InvestmentPursuitShape Shape { get; init; }
    /** True for a pursuit whose lifetime expression is one literal row with a positive operand. Sunrise reports it as a bounty. */
    public bool Expires { get; init; }

    /** Never throws or reports. A malformed block reads as absent here, and opening the item diagnoses it. */
    public static InvestmentItemLinks Read(ReadOnlySpan<byte> item)
    {
        var links = None;
        int wrapper = Block(item, nameof(ItemDefinition.SackItemBlock), RewardWrapper.Class);
        if (wrapper >= 0 && TagView.FitsThrough<RewardWrapper>(item, wrapper, nameof(RewardWrapper.Flags)))
            links = links with { WrapperPool = TagView.Member<RewardWrapper, ushort>(item, wrapper, nameof(RewardWrapper.Pool)),
                WrapperFlags = TagView.Member<RewardWrapper, byte>(item, wrapper, nameof(RewardWrapper.Flags)) };
        int objectives = Block(item, nameof(ItemDefinition.ObjectiveItemBlock), ItemObjectiveBlock.Class);
        if (objectives >= 0 && TagView.FitsThrough<ItemObjectiveBlock>(item, objectives,
            nameof(ItemObjectiveBlock.ParentItemIndex)))
        {
            links = links with { QuestOwner = TagView.Member<ItemObjectiveBlock,
                ushort>(item, objectives, nameof(ItemObjectiveBlock.ParentItemIndex)),
                Expires = Expiring(item) };
            int stride = Unsafe.SizeOf<ObjectiveReferenceArray>();
            if (TagArray.TryChecked(item,
                objectives + TagView.FieldOffset<ItemObjectiveBlock>(nameof(ItemObjectiveBlock.Objectives)),
                    ObjectiveReferenceArray.Class, stride, out var list))
            {
                var listed = new ushort[list.Count];
                for (int row = 0; row < listed.Length; row++) listed[row] = TagView.At<ObjectiveReferenceArray>(item,
                    list.Data + row * stride).ObjectiveIndex;
                links = links with { Objectives = listed };
            }
        }
        int set = Block(item, nameof(ItemDefinition.SetItemBlock), QuestSetBlock.Class);
        return links with { Shape = InvestmentPursuits.Shape(objectives >= 0, links.QuestOwner, set >= 0) };
    }

    private static int Block(ReadOnlySpan<byte> item, string pointer, uint expectedClass)
    {
        try { return InvestmentItemBlocks.Find(item, pointer, expectedClass); }
        catch (Exception error) when (InvestmentCatalog.Recoverable(error)) { return -1; }
    }

    /**
     * Sunrise reads a pursuit's lifetime as the operand of its one literal expression row, and reports a held pursuit with a
     * positive lifetime as a bounty (item_definition_reader.cpp read_lifetime, studio_investment_payloads.cpp held_pursuit).
     */
    private static bool Expiring(ReadOnlySpan<byte> item) => InvestmentUnlocks.ReadExpression(item,
        InvestmentPursuits.LifetimeField)
        .Instructions is [{ Opcode: InvestmentUnlocks.ConstantOpcode } only] && only.Operand is > 0 and <= int.MaxValue;
}

/** Blocks of an item definition, located through the item layout. */
internal static class InvestmentItemBlocks
{
    /**
     * Follows one of the item's block pointers. The block's class word sits just before it.
     * @param pointer An ItemDefinition block pointer member.
     * @return The block's offset, or -1 when the pointer is null.
     */
    public static int Find(ReadOnlySpan<byte> item, string pointer, uint expectedClass)
    {
        int field = TagView.FieldOffset<ItemDefinition>(pointer);
        int target = TagPointer.Checked(item, field);
        if (target < 0) return -1;
        if (TagView.ClassAt(item,
            target) != expectedClass) throw new InvalidDataException($"Unexpected block class at 0x{field:X}.");
        return target;
    }
}

/**
 * What an objective's progress reads once Sunrise binds its program: a program Sunrise cannot bind, one flag or value no
 * mapping table binds to a saved bank, one saved flag, one saved value, one class flag, several instructions, or one other
 * instruction such as a constant.
 */
public enum InvestmentProgressKind { Unreadable, External, Flag, Value, ClassFlag, Several, Other }
/** Instruction is the program's only instruction, or default when the kind is Unreadable or Several. */
public readonly record struct InvestmentProgressSource(InvestmentProgressKind Kind, InvestmentUnlockInstruction Instruction);

/** The root-slot tables of one catalog. A table that cannot be read leaves a diagnostic and an empty list. */
public sealed partial class InvestmentTables
{
    /** Diagnostics name at most this many shared expression cycles, so many cycles do not bury other problems. */
    internal const int ListedCycles = 8;
    private readonly Dictionary<int, List<int>> objectiveUsers;
    private readonly Dictionary<byte, InvestmentBucket> bucketsById = [];
    private readonly Dictionary<byte, int[]> itemsByBucket;
    private readonly InvestmentItemLinks[] links;
    private readonly string[] objectiveText;
    // Each installed objective's progress source, by objective index.
    private readonly InvestmentProgressSource[] progressSources;
    public InvestmentRoot? Root { get; }
    public InvestmentUnlocks Unlocks { get; }
    public IReadOnlyList<InvestmentProgression> Progressions { get; }
    /** Progressions with at least one reward row. Any of them may be the pass Sunrise serves, so Studio does not pick one. */
    public IReadOnlyList<InvestmentProgression> PassCandidates { get; }
    public IReadOnlyList<InvestmentObjective> Objectives { get; }
    /** Bucket descriptors in table order, or none when the table cannot be read. */
    public IReadOnlyList<InvestmentBucket> Buckets { get; }
    /** Why Sunrise would refuse the whole bucket table. Studio lists the rows either way. */
    public IReadOnlyList<string> BucketTableProblems { get; }
    public uint BucketTableTag => TableTag(InvestmentRootSlots.Buckets);
    /** The bucket table's entry class, which Sunrise does not check, or 0 when the table is not installed. */
    public uint BucketTableClass { get; }
    public IReadOnlyList<string> Diagnostics { get; }

    private InvestmentTables(InvestmentRoot? root, InvestmentUnlocks unlocks,
        IReadOnlyList<InvestmentProgression> progressions,
            IReadOnlyList<InvestmentObjective> objectives,
        InvestmentItemLinks[] links, List<string> diagnostics, IReadOnlyList<InvestmentItem> items,
        IReadOnlyList<InvestmentBucket> buckets, IReadOnlyList<string> bucketProblems, uint bucketClass, string[] objectiveText)
    {
        Root = root; Unlocks = unlocks; Progressions = progressions; Objectives = objectives; this.objectiveText = objectiveText;
        progressSources = [.. objectives.Select(ReadProgressSource)];
        Buckets = buckets; BucketTableProblems = bucketProblems; BucketTableClass = bucketClass;
        foreach (var bucket in buckets) bucketsById.TryAdd(bucket.Id, bucket);
        itemsByBucket = items.GroupBy(item => item.Bucket).ToDictionary(group => group.Key, group => group
            .OrderBy(item => item.Name,
                StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Index).Select(item => item.Index).ToArray());
        PassCandidates = progressions.Where(row => row.Rewards.Count != 0).ToArray();
        this.links = links;
        objectiveUsers = new();
        for (int item = 0; item < links.Length; item++)
            foreach (ushort objective in links[item].Objectives.Distinct()) Add(objectiveUsers, objective, item);
        diagnostics.AddRange(unlocks.GraphDiagnostics);
        Diagnostics = diagnostics;
    }

    public uint TableTag(int slot) => Root is not null && slot >= 0 && slot < Root.SlotCount ? Root.SlotTag(slot) : 0;
    /** The first descriptor of a bucket id, or null when the installed table has none. */
    public InvestmentBucket? Bucket(byte id) => bucketsById.GetValueOrDefault(id);
    /** Installed items whose definition names the bucket, sorted by name. */
    public IReadOnlyList<int> ItemsInBucket(byte id) => itemsByBucket.GetValueOrDefault(id) ?? [];
    /** The objective's progress description, or "" when the install gives none. */
    public string ObjectiveText(int objective) => objective >= 0
        && objective < objectiveText.Length ? objectiveText[objective] : "";
    /** Items whose objective block lists `objective`. */
    public IReadOnlyList<int> ObjectiveUsers(int objective) => objectiveUsers.GetValueOrDefault(objective) ?? [];
    /** The quest owner the item's objective block names, or -1. */
    public int QuestOwner(int item) => item >= 0 && item < links.Length
        && links[item].QuestOwner != ushort.MaxValue ? links[item].QuestOwner : -1;
    /** Which pursuit page the item belongs on (InvestmentPursuits.Classify), read once at load. */
    public InvestmentPursuitShape PursuitShape(int item) => item >= 0
        && item < links.Length ? links[item].Shape : InvestmentPursuitShape.None;
    /** True when Sunrise would report the held item as a bounty: a pursuit with a positive installed lifetime. */
    public bool Expires(int item) => item >= 0 && item < links.Length && links[item].Expires;
    /** The item's wrapper pool, or -1 when the item has no readable wrapper. */
    public int WrapperPool(int item) => item >= 0 && item < links.Length
        && links[item].WrapperPool != ushort.MaxValue ? links[item].WrapperPool : -1;
    /** The wrapper flag of a wrapper that opens as soon as it is acquired (Sunrise rewards/definition.h kOpenOnAcquisition). */
    private const byte OpenOnAcquisition = 1;
    /** True when the item's wrapper opens as soon as it is acquired; false without a wrapper. */
    public bool OpensOnAcquisition(int item) => WrapperPool(item) >= 0 && (links[item].WrapperFlags & OpenOnAcquisition) != 0;

    /**
     * The progress lane an objective fills: its one instruction reads an unbound value that the pursuit's instance values
     * list at that lane. The join is inferred, so the UI marks it as possibly inaccurate.
     * @return The lane, or -1 when the objective does not read one unbound value the pursuit lists.
     */
    public int ProgressLane(InvestmentObjective objective, InvestmentPursuit pursuit)
    {
        if (objective.Source.Instructions is not [var read] || !ExternalRead(read)) return -1;
        for (int lane = 0; lane < pursuit.InstanceValues.Count; lane++) if (pursuit.InstanceValues[lane] == read.Operand) return lane;
        return -1;
    }

    /**
     * What an objective's progress reads once shared rows are inlined, as package_objective_build.cpp read_objective and
     * package_reward_build.cpp RewardConditions::append_expression bind it. A program Sunrise refuses
     * (InvestmentUnlocks.InlinedCount) is Unreadable. The pursuit's instance values are not consulted, since Sunrise's
     * item-progress rule does not read them either. The installed objectives' sources are read once when the tables load.
     */
    public InvestmentProgressSource ProgressSource(InvestmentObjective objective) =>
        objective.Index >= 0 && objective.Index < progressSources.Length && ReferenceEquals(Objectives[objective.Index], objective)
            ? progressSources[objective.Index] : ReadProgressSource(objective);

    private InvestmentProgressSource ReadProgressSource(InvestmentObjective objective)
    {
        int count = Unlocks.InlinedCount(objective.Source, out var only);
        if (count == 0) return new(InvestmentProgressKind.Unreadable, default);
        if (count > 1) return new(InvestmentProgressKind.Several, default);
        // A flag or value read that no saved bank holds is an external input, which pursuit_edit.cpp saved_row never writes.
        var kind = only.Opcode switch
        {
            InvestmentUnlocks.FlagOpcode => Unlocks.ClassOf(only) >= 0 ? InvestmentProgressKind.ClassFlag
                : Unlocks.BoundFlag(only.Operand) ? InvestmentProgressKind.Flag : InvestmentProgressKind.External,
            InvestmentUnlocks.ValueOpcode => ExternalRead(only) ? InvestmentProgressKind.External : InvestmentProgressKind.Value,
            _ => InvestmentProgressKind.Other
        };
        return new(kind, only);
    }

    /**
     * True when a value read stays external as Sunrise binds it: a slot that neither the account nor the character value
     * mapping table names, so no saved bank holds it (package_reward_build.cpp RewardConditions::bind).
     */
    private bool ExternalRead(InvestmentUnlockInstruction read) => read.Opcode == InvestmentUnlocks.ValueOpcode
        && read.Operand < Unlocks.ValueSlots.Count && !Unlocks.BoundValue(read.Operand);

    /**
     * Reads the root, unlocks, progressions, objectives, objective text and buckets in that order. A section
     * that fails leaves a diagnostic and does not stop the next.
     * @param text Resolves a (bank, string hash) pair through the catalog's string banks.
     */
    internal static InvestmentTables Read(PackageCatalog catalog, InvestmentSources sources, IReadOnlyList<InvestmentItem> items,
        InvestmentItemLinks[] links, Func<uint, uint, string> text, CancellationToken token)
    {
        var diagnostics = new List<string>();
        token.ThrowIfCancellationRequested();
        var root = InvestmentRoot.Locate(catalog, sources.Root, out string located);
        if (root is null)
        {
            diagnostics.Add(located);
            return new(null, InvestmentUnlocks.Empty, [], [], links, diagnostics, items, [], [], 0, []);
        }
        T Section<T>(string section, T fallback, Func<T> read)
        {
            token.ThrowIfCancellationRequested();
            return InvestmentCatalog.ReadSection(diagnostics, section, fallback, read);
        }
        if (Section("Investment root", 0u, () => root.SlotTag(InvestmentRootSlots.Items)) != sources.Items)
            diagnostics.Add("The item index differs from the investment root's item table; reward item names may be wrong.");
        var unlocks = Section("Unlock slots", InvestmentUnlocks.Empty,
            () => InvestmentUnlocks.Read(root, catalog, items, diagnostics, token));
        var progressions = Section<IReadOnlyList<InvestmentProgression>>("Progressions", [], () => InvestmentSeasonPass.Read(
            root.ReadSlot(catalog, InvestmentRootSlots.Progressions, C80807cd9.Class), items.Count, unlocks, diagnostics, token));
        var objectives = Section<IReadOnlyList<InvestmentObjective>>("Objectives", [], () => InvestmentPursuits.ReadObjectives(
            root.ReadSlot(catalog, InvestmentRootSlots.Objectives, ObjectiveTable.Class), diagnostics, token));
        var objectiveText = Section<string[]>("Objective text", [],
            () => InvestmentObjectiveText.Read(catalog, sources.ObjectiveDisplays,
            objectives, text, diagnostics, token));
        var bucketProblems = new List<string>();
        var buckets = Section<IReadOnlyList<InvestmentBucket>>("Buckets", [], () => InvestmentBuckets.Read(
            root.ReadSlotAnyClass(catalog, InvestmentRootSlots.Buckets), bucketProblems, token));
        uint bucketClass = InvestmentRootSlots.Buckets < root.SlotCount
            && catalog.TryGetEntry(root.SlotTag(InvestmentRootSlots.Buckets), out var bucketEntry) ? bucketEntry.Reference : 0;
        return new(root, unlocks, progressions, objectives, links, diagnostics, items, buckets,
            bucketProblems, bucketClass, objectiveText);
    }

    private static void Add<T>(Dictionary<int, List<T>> index, int key, T value)
    {
        if (!index.TryGetValue(key, out var list)) index[key] = list = [];
        list.Add(value);
    }
}

public sealed partial class InvestmentCatalog
{
    private readonly object tablesLock = new();
    private InvestmentTables? tables;
    public bool TablesLoaded => Volatile.Read(ref tables) is not null;

    /** Loads the root-slot tables once per catalog, on the caller's worker. A cancelled load caches nothing. */
    public InvestmentTables Tables(CancellationToken token = default)
    {
        if (Volatile.Read(ref tables) is { } loaded) return loaded;
        var built = InvestmentTables.Read(catalog, sources, Items, links, Text, token);
        lock (tablesLock) return tables ??= built;
    }

    /**
     * Only an item with an objective block loads the tables. A quest chain reads its owner's definition,
     * which is this item's own bytes when the item owns its set.
     */
    private InvestmentPursuit? ReadPursuit(InvestmentItem item, ReadOnlyMemory<byte> data, CancellationToken token)
    {
        if (InvestmentItemBlocks.Find(data.Span, nameof(ItemDefinition.ObjectiveItemBlock),
            ItemObjectiveBlock.Class) < 0) return null;
        return InvestmentPursuits.ReadPursuit(data.Span, item, Tables(token), Items.Count,
            index => index == item.Index ? data : ReadDefinition(index));
    }

    /** The quest set an item owns, for an owner without objectives of its own. A set that cannot be read carries its Error. */
    public InvestmentQuestSet ReadQuestSet(int owner, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (owner < 0 || owner >= Items.Count || owner >= ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(owner),
            "The quest owner is outside the item index.");
        return InvestmentPursuits.ReadQuestSet((ushort)owner, ReadDefinition(owner), Items.Count);
    }

    /** An item's definition bytes, or null when they cannot be read as an item. */
    private ReadOnlyMemory<byte>? ReadDefinition(int index)
    {
        try { RequireClass(catalog, Items[index].Tag, ItemClass); return catalog.Read(Items[index].Tag); }
        catch (Exception error) when (Recoverable(error)) { return null; }
    }
}
