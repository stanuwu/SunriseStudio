using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

/** One objective row of root slot 58. Raw keeps the whole row, including bytes Studio does not decode. */
public sealed record InvestmentObjective(int Index, int Offset, uint Hash, InvestmentExpression Source,
    bool Optional, bool AllowNegative,
    bool AllowChangeWhenCompleted, bool CountsDown, int CompletionValue, InvestmentExpression TargetAdjustment,
    ReadOnlyMemory<byte> Raw, string Error = "");
/** A reward the item shows on its pursuit screen. Sunrise does not grant from this list. */
public sealed record InvestmentRewardPreview(int Slot, ushort ItemIndex, int Quantity, ushort ConditionRow,
    InvestmentExpression? Condition);
/** Quest is an item in or owning a quest chain. Single is an item with objectives outside any chain. */
public enum InvestmentPursuitShape : byte { None, Quest, Single }
/** One quest step. Value identifies the step and is not a progress count. */
public sealed record InvestmentQuestStep(int Member, int Value, ushort ItemIndex);
public sealed record InvestmentQuestSet(ushort OwnerItem, int Block, ushort ValueSlot, byte Mode,
    IReadOnlyList<InvestmentQuestStep> Steps, string Error = "")
{
    /** Only mode 1 is decoded, as in quest_initialization_reader.cpp kSupportedQuestSetMode. */
    public bool ModeDecoded => Mode == 1;
}
/**
 * An item's objective block and the blocks read beside it. RequireAll and RequireNonOptional are the completion controls
 * Sunrise reads. Diagnostics note what Sunrise would refuse.
 */
public sealed record InvestmentPursuit(int Block, IReadOnlyList<ushort> Objectives, bool RequireAll, bool RequireNonOptional,
    ushort QuestOwner, IReadOnlyList<InvestmentRewardPreview> Previews, int? LifetimeSeconds, InvestmentExpression? Lifetime,
    IReadOnlyList<ushort> PresenceFlags, IReadOnlyList<ushort> InstanceValues, InvestmentQuestSet? Chain,
        IReadOnlyList<string> Diagnostics);

/** Reads objectives, pursuit objective blocks, quest sets, reward previews and lifetimes. */
public static class InvestmentPursuits
{
    public const byte PursuitBucket = InvestmentItemKinds.PursuitBucket;
    /** The item's lifetime expression. */
    internal static readonly int LifetimeField = TagView.FieldOffset<ItemDefinition>("InventoryItemBlock.H34f70b23");
    /**
     * The objective block's require-all and require-non-optional bytes, the eighth and ninth after its objective array. A value
     * above 1 in either leaves the contract unreadable (item_definition_reader.cpp read_pursuit_metadata).
     */
    private static readonly int RequireAllControl = TagView.FieldOffset<ItemObjectiveBlock>(nameof(ItemObjectiveBlock.Objectives))
        + Unsafe.SizeOf<TagArray<ObjectiveReferenceArray>>() + 7, RequireNonOptionalControl = RequireAllControl + 1;
    /**
     * Objective row control bytes: optional, allow negative, allow change when completed, counts down, ending just before the
     * completion value. Sunrise reads only the optional and counts-down bytes, and a value above 1 in either refuses the whole
     * objective table (package_objective_build.cpp read_completion_controls).
     */
    private static readonly int CountsDownControl = TagView.FieldOffset<ObjectiveRow>(nameof(ObjectiveRow.CompletionValue)) - sizeof(uint),
        OptionalControl = CountsDownControl - sizeof(uint), AllowNegativeControl = OptionalControl + 2,
            AllowChangeControl = OptionalControl + 3;
    /** Minimum quest owner and quest-set block sizes, as quest_initialization_reader.cpp derives them from the same layouts. */
    internal static readonly int MinimumQuestOwner = Unsafe.SizeOf<ItemDefinition>(),
        MinimumQuestSet = Math.Min(Math.Min(Unsafe.SizeOf<ItemObjectiveBlock>(), Unsafe.SizeOf<QuestSetBlock>()),
            Unsafe.SizeOf<ItemUnlockBlock>());
    /** Previews hold a count, then six slots of {u32 item, i32 quantity, u32 shared condition row}. */
    internal const int PreviewSlots = 6, PreviewHeader = 4, PreviewStride = 12, PreviewQuantity = 4, PreviewCondition = 8;
    private const ushort Absent = InvestmentCatalog.Absent;

    /**
     * The pursuit page an item belongs on, and its bucket as InvestmentCatalog reads it (255 when unreadable). Never throws.
     * Quest means an objective block naming a quest owner, or an owned quest set. Single means an objective block and neither.
     */
    public static (InvestmentPursuitShape Shape, byte Bucket) Classify(ReadOnlyMemory<byte> item) =>
        (InvestmentItemLinks.Read(item.Span).Shape, InvestmentCatalog.ReadBucket(item.Span));

    internal static InvestmentPursuitShape Shape(bool objectives, ushort questOwner, bool ownsQuestSet) =>
        (objectives && questOwner != Absent) || ownsQuestSet ? InvestmentPursuitShape.Quest
            : objectives ? InvestmentPursuitShape.Single : InvestmentPursuitShape.None;

    /** A row whose optional or counts-down byte is above 1, or whose expression is unreadable, carries an Error and stays listed. */
    internal static IReadOnlyList<InvestmentObjective> ReadObjectives(InvestmentSlotRead slot,
        List<string> diagnostics, CancellationToken token)
    {
        if (!slot.Loaded) { diagnostics.Add("Objectives: " + slot.Diagnostic); return []; }
        var table = slot.Bytes.Span;
        int stride = Unsafe.SizeOf<ObjectiveRow>(),
            completionField = TagView.FieldOffset<ObjectiveRow>(nameof(ObjectiveRow.CompletionValue));
        var rows = TagArray.Checked(table, TagView.FieldOffset<ObjectiveTable>("Table"), ObjectiveRow.Class, stride);
        var objectives = new InvestmentObjective[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            if ((i & 255) == 0) token.ThrowIfCancellationRequested();
            int at = rows.Data + i * stride;
            ref readonly var row = ref TagView.At<ObjectiveRow>(table, at);
            ref readonly int completion = ref TagView.At<int>(table, at + completionField);
            byte optional = TagRead.U8(table, at + OptionalControl), countsDown = TagRead.U8(table, at + CountsDownControl);
            var source = InvestmentUnlocks.ReadExpression(table, TagView.OffsetOf(table, in row.HC198a43a));
            var adjustment = InvestmentUnlocks.ReadExpression(table, TagView.OffsetOf(table, in row.HA32e809c));
            var problems = new List<string>();
            if (optional > 1 || countsDown > 1) problems.Add("The optional or counts-down byte is above 1, so Sunrise refuses the whole objective table.");
            if (source.Error.Length != 0) problems.Add("Source: " + source.Error);
            if (adjustment.Error.Length != 0) problems.Add("Target adjustment: " + adjustment.Error);
            objectives[i] = new(i, at, row.Unk0, source, optional != 0, TagRead.U8(table, at + AllowNegativeControl) != 0,
                TagRead.U8(table, at + AllowChangeControl) != 0, countsDown != 0, completion, adjustment,
                slot.Bytes.Slice(at, stride), string.Join(" ", problems));
        }
        return objectives;
    }

    /**
     * @param readItem Reads another item's definition by index, or null when it cannot be read.
     * @return The item's pursuit, or null when it has no objective block.
     */
    internal static InvestmentPursuit? ReadPursuit(ReadOnlySpan<byte> item, InvestmentItem summary,
        InvestmentTables tables, int itemCount,
        Func<int, ReadOnlyMemory<byte>?> readItem)
    {
        int block = InvestmentItemBlocks.Find(item, nameof(ItemDefinition.ObjectiveItemBlock), ItemObjectiveBlock.Class);
        if (block < 0) return null;
        if (!TagView.FitsThrough<ItemObjectiveBlock>(item, block, nameof(ItemObjectiveBlock.ParentItemIndex)))
            throw new InvalidDataException("Objective block is truncated.");
        var diagnostics = new List<string>();
        int stride = Unsafe.SizeOf<ObjectiveReferenceArray>();
        var list = TagArray.Checked(item, block + TagView.FieldOffset<ItemObjectiveBlock>(nameof(ItemObjectiveBlock.Objectives)),
            ObjectiveReferenceArray.Class, stride);
        var objectives = new ushort[list.Count];
        for (int row = 0; row < objectives.Length; row++)
            objectives[row] = TagView.At<ObjectiveReferenceArray>(item, list.Data + row * stride).ObjectiveIndex;
        if (summary.Bucket == PursuitBucket && objectives.Length > InvestmentItemKinds.PursuitObjectiveCapacity)
            diagnostics.Add($"Sunrise reads at most {InvestmentItemKinds.PursuitObjectiveCapacity} objectives on a pursuit.");
        foreach (ushort index in objectives.Where(index => tables.Objectives.Count != 0
            && index >= tables.Objectives.Count).Distinct())
            diagnostics.Add($"Objective {index} is outside the objective table.");
        bool requireAll = Control(item, block, RequireAllControl, diagnostics);
        bool requireNonOptional = Control(item, block, RequireNonOptionalControl, diagnostics);
        IReadOnlyList<InvestmentRewardPreview> previews = [];
        try { previews = ReadPreviews(item, tables.Unlocks, itemCount, diagnostics); }
        catch (Exception error) when (InvestmentCatalog.Recoverable(error)) { diagnostics.Add("Reward preview: " + error.Message); }
        // Other buckets reuse the lifetime field for other arrays, so only a pursuit reports a bad one.
        var lifetime = InvestmentUnlocks.ReadExpression(item, LifetimeField);
        int? seconds = null;
        if (lifetime.Error.Length != 0)
        {
            if (summary.Bucket == PursuitBucket) diagnostics.Add("Lifetime: " + lifetime.Error);
            lifetime = lifetime with { Error = "" };
        }
        else if (lifetime.Instructions is [{ Opcode: InvestmentUnlocks.ConstantOpcode } constant])
        {
            seconds = (int)constant.Operand;
            if (seconds < 0) diagnostics.Add("Lifetime is negative.");
        }
        IReadOnlyList<ushort> presence = [], instances = [];
        try
        {
            int values = InvestmentItemBlocks.Find(item, nameof(ItemDefinition.UnlockItemBlock), ItemUnlockBlock.Class);
            if (values >= 0)
            {
                presence = ValueRows<ItemPresenceFlag>(item, values,
                    nameof(ItemUnlockBlock.PresenceFlags), ItemPresenceFlag.Class);
                instances = ValueRows<C808077ae>(item, values, nameof(ItemUnlockBlock.HDf67f700), C808077ae.Class);
            }
        }
        catch (Exception error) when (InvestmentCatalog.Recoverable(error)) { diagnostics.Add("Item values: " + error.Message); }
        ushort owner = TagView.Member<ItemObjectiveBlock, ushort>(item, block, nameof(ItemObjectiveBlock.ParentItemIndex));
        InvestmentQuestSet? chain = null;
        if (owner != Absent && owner >= itemCount) diagnostics.Add($"Quest owner {owner} is outside the item index.");
        else if (owner != Absent) chain = ReadQuestSet(owner, readItem(owner), itemCount);
        return new(block, objectives, requireAll, requireNonOptional, owner, previews, seconds,
            lifetime.IsEmpty ? null : lifetime,
            presence, instances, chain, diagnostics);
    }

    /** One completion control byte of the objective block. Sunrise accepts only 0 or 1. */
    private static bool Control(ReadOnlySpan<byte> item, int block, int at, List<string> diagnostics)
    {
        byte value = TagRead.U8(item, block + at);
        if (value > 1) diagnostics.Add($"Completion byte +0x{at:X} holds {value}; Sunrise accepts only 0 or 1.");
        return value != 0;
    }

    /** Each row's first u16 in one of the unlock block's arrays: a presence flag's slot, or an instance value's source slot. */
    private static ushort[] ValueRows<TRow>(ReadOnlySpan<byte> item, int block, string array,
        uint rowClass) where TRow : unmanaged
    {
        int stride = Unsafe.SizeOf<TRow>();
        var rows = TagArray.Checked(item, block + TagView.FieldOffset<ItemUnlockBlock>(array), rowClass, stride);
        var values = new ushort[rows.Count];
        for (int row = 0; row < values.Length; row++) values[row] = TagRead.U16(item, rows.Data + row * stride);
        return values;
    }

    /** Lists the declared slots even when the block breaks a preview rule, and adds a diagnostic for each break. */
    private static IReadOnlyList<InvestmentRewardPreview> ReadPreviews(ReadOnlySpan<byte> item,
        InvestmentUnlocks unlocks, int itemCount,
        List<string> diagnostics)
    {
        int block = InvestmentItemBlocks.Find(item, nameof(ItemDefinition.ValueItemBlock), C80807875.Class);
        if (block < 0) return [];
        if (!TagView.Fits<C80807875>(item, block)) { diagnostics.Add("Reward preview is truncated."); return []; }
        uint count = TagRead.U32(item, block);
        if (count > PreviewSlots) diagnostics.Add($"Reward preview declares {count} slots; Sunrise reads at most {PreviewSlots}.");
        var previews = new List<InvestmentRewardPreview>();
        for (int i = 0; i < PreviewSlots; i++)
        {
            int at = block + PreviewHeader + i * PreviewStride;
            uint reward = TagRead.U32(item, at), condition = TagRead.U32(item, at + PreviewCondition);
            int quantity = TagRead.I32(item, at + PreviewQuantity);
            if (i >= count)
            {
                if (reward != Absent || quantity != 0
                    || condition != Absent) diagnostics.Add($"Unused reward preview slot {i} is not empty.");
                continue;
            }
            if (reward >= Absent) diagnostics.Add($"Reward preview slot {i} names no item.");
            else if (reward >= itemCount) diagnostics.Add($"Reward preview slot {i} names item {reward}, outside the item index.");
            if (quantity < 0) diagnostics.Add($"Reward preview slot {i} has a negative quantity.");
            if (condition > Absent) diagnostics.Add($"Reward preview slot {i} names condition row {condition}, outside the shared rows.");
            previews.Add(new(i, (ushort)Math.Min(reward, Absent), quantity, (ushort)Math.Min(condition, Absent),
                condition < Absent ? unlocks.Shared((int)condition) : null));
        }
        return previews;
    }

    /** The owner's quest-set block, read with the quest_initialization_reader.cpp layout. Steps keep their authored order. */
    internal static InvestmentQuestSet ReadQuestSet(ushort owner, ReadOnlyMemory<byte>? data, int itemCount)
    {
        InvestmentQuestSet Unreadable(string error) => new(owner, -1, Absent, 0, [], error);
        if (data is not { } bytes) return Unreadable("The quest owner could not be read.");
        var span = bytes.Span;
        if (span.Length < MinimumQuestOwner) return Unreadable("The quest owner is too short to hold a quest set.");
        try
        {
            int set = InvestmentItemBlocks.Find(span, nameof(ItemDefinition.SetItemBlock), QuestSetBlock.Class);
            if (set < 0) return Unreadable("The quest owner has no quest set.");
            if (set > span.Length - MinimumQuestSet) return Unreadable("The quest set is truncated.");
            int stride = Unsafe.SizeOf<QuestSetMember>();
            ref readonly var block = ref TagView.Checked<QuestSetBlock>(span, set);
            var members = TagArray.Checked(span, TagView.OffsetOf(span, in block.ItemList), QuestSetMember.Class, stride);
            var problems = new List<string>();
            var steps = new InvestmentQuestStep[members.Count];
            for (int i = 0; i < members.Count; i++)
            {
                ref readonly var member = ref TagView.At<QuestSetMember>(span, members.Data + i * stride);
                steps[i] = new(i, member.StepValue, member.ItemIndex);
                if (member.Reserved != 0) problems.Add($"Quest step {i} has nonzero reserved bytes.");
                if (steps[i].ItemIndex >= itemCount) problems.Add($"Quest step {i} names item {steps[i].ItemIndex}, outside the item index.");
            }
            return new(owner, set, block.ValueSlot, block.Mode, steps, string.Join(" ", problems));
        }
        catch (Exception error) when (InvestmentCatalog.Recoverable(error)) { return Unreadable(error.Message); }
    }
}
