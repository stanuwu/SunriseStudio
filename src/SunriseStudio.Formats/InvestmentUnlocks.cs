using System.Globalization;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

/** One native postfix instruction exactly as authored. Offset is absolute within the blob that holds it. */
public readonly record struct InvestmentUnlockInstruction(uint Opcode, uint Operand, int Offset);
/** An expression field: its native rows, or the reason they could not be read. */
public sealed record InvestmentExpression(int Field, IReadOnlyList<InvestmentUnlockInstruction> Instructions, string Error = "")
{
    public bool IsEmpty => Instructions.Count == 0 && Error.Length == 0;
}
/** An expression list: every member must hold. */
public sealed record InvestmentExpressionList(int Field, IReadOnlyList<InvestmentExpression> Members, string Error = "");
public enum InvestmentOperandKind { None, Flag, Value, Constant, Shared, Unknown }
/** A display label for one operand. Proven is false when the label rests on an unconfirmed meaning. */
public sealed record InvestmentOperandLabel(InvestmentOperandKind Kind, string Text, string Detail, bool Proven);
/** A root slot 112 row. Kind picks the flag mapping table and BankRow is the row inside it. */
public sealed record InvestmentFlagSlot(int Slot, uint Hash, ushort Kind, ushort BankRow);
/** A root slot 114 row. Kind picks the value mapping table and BankRow is the row inside it. Aggregation is unconfirmed. */
public sealed record InvestmentValueSlot(int Slot, uint Hash, byte Kind, byte Aggregation, ushort BankRow);
/** One mapping table: the unlock slot each bank row feeds, in bank row order. */
public sealed record InvestmentSlotMap(int Descriptor, IReadOnlyList<short> Destinations);

/** Unlock expressions, decoded for display. Studio never evaluates them. */
public sealed class InvestmentUnlocks
{
    public const uint FlagOpcode = 1, ValueOpcode = 10, ConstantOpcode = 11, SharedOpcode = 12;
    /** Sunrise's limit on nesting through shared rows (rewards/definition.h kTraversalDepth). Studio reports deeper nesting. */
    public const int ServerNestingLimit = 32;
    // Slot rows hold a hash, the kind (a u16 for flags, or a kind byte and an aggregation byte for values), then the bank row.
    // The row layouts name only the hash and the flag bank row.
    private const int SlotKind = 4, SlotAggregation = 5, SlotBankRow = 6;
    /** Flag kind 1 is the account bank, whose mapping array is the first. */
    private const int AccountFlagKind = 1;
    /** Value kinds 1 and 2 are the account and character banks, whose mapping arrays are the first two. */
    private const int AccountValueKind = 1, CharacterValueKind = 2;
    /** The mapping arrays a slot's kind picks from (FlagMap and ValueMap), in kind order. */
    private static readonly int[] FlagMapArrays = [.. new[] { nameof(C80807d36.H826fd8c8), nameof(C80807d36.HC93e5c6b),
        nameof(C80807d36.H1c76a86c), nameof(C80807d36.H201ada27) }.Select(TagView.FieldOffset<C80807d36>)];
    private static readonly int[] ValueMapArrays = [.. new[] { nameof(C80807c80.H826fd8c8), nameof(C80807c80.H1c76a86c),
        nameof(C80807c80.H201ada27), nameof(C80807c80.HD4a626c1) }.Select(TagView.FieldOffset<C80807c80>)];
    /** The opcodes Sunrise evaluates (unlocks_expression.h) and opcode 12, each with its name, infix symbol, pops and pushes. */
    private static readonly Dictionary<uint, (string Name, string Symbol, int Pops, int Pushes)> Opcodes = new()
    {
        [1] = ("flag", "", 0, 1), [2] = ("not", "not ", 1, 1), [3] = ("or", "or", 2, 1), [4] = ("and", "and", 2, 1),
        [8] = ("equal", "=", 2, 1), [10] = ("value", "", 0, 1), [11] = ("constant", "", 0, 1), [12] = ("shared", "", 0, 1),
        [13] = (">", ">", 2, 1), [14] = (">=", ">=", 2, 1), [15] = ("<", "<", 2, 1), [16] = ("<=", "<=", 2, 1),
        [17] = ("add", "+", 2, 1), [22] = ("negate", "-", 1, 1)
    };
    private readonly InvestmentExpression[] shared;
    // Per shared row: its longest opcode-12 chain (-1 when broken or cyclic) and net stack effect (null when undecoded).
    private readonly int[] sharedDepth;
    private readonly int?[] sharedEffect;
    private readonly InvestmentFlagSlot[] flagSlots;
    private readonly InvestmentValueSlot[] valueSlots;
    private readonly InvestmentSlotMap[] flagMaps, valueMaps;
    private readonly Dictionary<uint, (int Class, string Label)> classFlags;
    // Slots whose bank row disagrees with their mapping table, each with the row the table names for it, or -1.
    private readonly Dictionary<int, int> flagDisagreements, valueDisagreements;
    private HashSet<int>? accountFlagSlots, boundValueSlots;
    public static InvestmentUnlocks Empty { get; } = new([], [], [], [], [], new(), new(), new(), []);
    public int SharedCount => shared.Length;
    public IReadOnlyList<InvestmentFlagSlot> FlagSlots => flagSlots;
    public IReadOnlyList<InvestmentValueSlot> ValueSlots => valueSlots;
    public IReadOnlyList<InvestmentSlotMap> FlagMaps => flagMaps;
    public IReadOnlyList<InvestmentSlotMap> ValueMaps => valueMaps;
    /** Cycles, out-of-range references and empty or unreadable shared rows, found once at load. */
    public IReadOnlyList<string> GraphDiagnostics { get; }

    private InvestmentUnlocks(InvestmentExpression[] shared, InvestmentFlagSlot[] flagSlots,
        InvestmentValueSlot[] valueSlots, InvestmentSlotMap[] flagMaps, InvestmentSlotMap[] valueMaps,
        Dictionary<uint, (int Class, string Label)> classFlags, Dictionary<int, int> flagDisagreements,
            Dictionary<int, int> valueDisagreements,
        List<string> graphDiagnostics)
    {
        this.shared = shared; this.flagSlots = flagSlots; this.valueSlots = valueSlots;
        this.flagMaps = flagMaps; this.valueMaps = valueMaps; this.classFlags = classFlags;
        this.flagDisagreements = flagDisagreements; this.valueDisagreements = valueDisagreements;
        (sharedDepth, sharedEffect) = WalkShared(graphDiagnostics);
        GraphDiagnostics = graphDiagnostics;
    }

    /** Reads the slot tables, maps, class flags and shared rows. A missing table leaves its labels generic. */
    internal static InvestmentUnlocks Read(InvestmentRoot root, PackageCatalog catalog, IReadOnlyList<InvestmentItem> items,
        List<string> diagnostics, CancellationToken token)
    {
        ReadOnlyMemory<byte>? Table(int slot, uint tableClass)
        {
            var read = root.ReadSlot(catalog, slot, tableClass);
            if (read.Loaded) return read.Bytes;
            diagnostics.Add("Unlock slots: " + read.Diagnostic); return null;
        }
        T Section<T>(string section, T fallback, Func<T> read)
        {
            try { return read(); }
            catch (Exception error) when (InvestmentCatalog.Recoverable(error)) { diagnostics.Add($"{section}: {error.Message}"); return fallback; }
        }
        token.ThrowIfCancellationRequested();
        var flagSlots = Section<InvestmentFlagSlot[]>("Flag slots", [], () =>
            Table(InvestmentRootSlots.FlagSlots, UnlockFlagTable.Class) is { } table ? FlagSlotRows(table.Span) : []);
        var valueSlots = Section<InvestmentValueSlot[]>("Value slots", [], () =>
            Table(InvestmentRootSlots.ValueSlots, UnlockValueTable.Class) is { } table ? ValueSlotRows(table.Span) : []);
        var flagMaps = Section<InvestmentSlotMap[]>("Flag maps", [], () =>
            Maps<UnlockFlagMapRow>(Table(InvestmentRootSlots.FlagMaps, C80807d36.Class), FlagMapArrays, UnlockFlagMapRow.Class));
        var valueMaps = Section<InvestmentSlotMap[]>("Value maps", [], () =>
            Maps<UnlockValueMapRow>(Table(InvestmentRootSlots.ValueMaps, C80807c80.Class),
                ValueMapArrays, UnlockValueMapRow.Class));
        var flagDisagreements = Disagreeing(flagSlots.Select(row => (row.Slot, FlagMap(row.Kind), (int)row.BankRow)), flagMaps);
        var valueDisagreements = Disagreeing(valueSlots.Select(row => (row.Slot, ValueMap(row.Kind),
            (int)row.BankRow)), valueMaps);
        if (flagDisagreements.Count + valueDisagreements.Count != 0)
            diagnostics.Add($"{flagDisagreements.Count + valueDisagreements.Count} unlock slots disagree with their mapping table; their labels show both rows.");
        token.ThrowIfCancellationRequested();
        var classFlags = Section("Class flags", new Dictionary<uint, (int, string)>(), () =>
            ClassFlags(Table(InvestmentRootSlots.Classes, CharacterClassTable.Class), catalog, items));
        token.ThrowIfCancellationRequested();
        var sharedRows = Section<InvestmentExpression[]>("Shared expressions", [], () =>
        {
            if (Table(InvestmentRootSlots.SharedExpressions, RewardExpressionTable.Class) is not { } table) return [];
            var span = table.Span;
            int stride = Unsafe.SizeOf<RewardExpressionRow>();
            int body = TagView.FieldOffset<RewardExpressionRow>(nameof(RewardExpressionRow.UnlockExpression));
            var rows = TagArray.Checked(span, TagView.FieldOffset<RewardExpressionTable>("Table"),
                RewardExpressionRow.Class, stride);
            var expressions = new InvestmentExpression[rows.Count];
            for (int i = 0; i < rows.Count; i++) expressions[i] = ReadExpression(span, rows.Data + i * stride + body);
            return expressions;
        });
        return new(sharedRows, flagSlots, valueSlots, flagMaps, valueMaps, classFlags, flagDisagreements, valueDisagreements, []);
    }

    /** Never throws: an unreadable field comes back with its Error set. */
    internal static InvestmentExpression ReadExpression(ReadOnlySpan<byte> blob, int field)
    {
        try
        {
            int stride = Unsafe.SizeOf<InvestmentExpressionRow>();
            var rows = TagArray.Checked(blob, field, InvestmentExpressionRow.Class, stride);
            var instructions = new InvestmentUnlockInstruction[rows.Count];
            for (int i = 0; i < rows.Count; i++)
            {
                int at = rows.Data + i * stride;
                ref readonly var row = ref TagView.At<InvestmentExpressionRow>(blob, at);
                instructions[i] = new(row.Opcode, row.Operand, at);
            }
            return new(field, instructions);
        }
        catch (Exception error) when (InvestmentCatalog.Recoverable(error)) { return new(field, [], error.Message); }
    }

    /** Keeps the members separate so the authored structure stays visible. Sunrise joins them with and. */
    internal static InvestmentExpressionList ReadList(ReadOnlySpan<byte> blob, int field)
    {
        try
        {
            int stride = Unsafe.SizeOf<RewardCondition>();
            var rows = TagArray.Checked(blob, field, RewardCondition.Class, stride);
            var members = new InvestmentExpression[rows.Count];
            for (int i = 0; i < rows.Count; i++) members[i] = ReadExpression(blob, rows.Data + i * stride);
            return new(field, members);
        }
        catch (Exception error) when (InvestmentCatalog.Recoverable(error)) { return new(field, [], error.Message); }
    }

    public InvestmentExpression Shared(int row) => row >= 0 && row < shared.Length ? shared[row]
        : new(-1, [], $"Shared expression {row} is outside the table.");

    /** How deep this expression nests through opcode 12, or -1 when a reference is broken or cyclic. */
    public int Depth(InvestmentExpression expression)
    {
        if (expression.Error.Length != 0) return -1;
        int depth = 0;
        foreach (var instruction in expression.Instructions)
        {
            if (instruction.Opcode != SharedOpcode) continue;
            if (!UsableShared(instruction.Operand)) return -1;
            depth = Math.Max(depth, 1 + sharedDepth[instruction.Operand]);
        }
        return depth;
    }

    /** Reasons this expression may not hold in game, in reading order. Empty when Studio finds none. */
    public IReadOnlyList<string> Problems(InvestmentExpression expression)
    {
        if (expression.Error.Length != 0) return [expression.Error];
        var problems = new List<string>();
        int depth = Depth(expression);
        if (depth < 0) problems.Add("Refers to a missing, empty or cyclic shared expression.");
        else if (depth > ServerNestingLimit) problems.Add($"Sunrise refuses expressions nested deeper than {ServerNestingLimit} shared rows.");
        foreach (uint opcode in expression.Instructions.Select(row => row.Opcode).Where(opcode => !Opcodes.ContainsKey(opcode)).Distinct())
            problems.Add($"Studio does not decode opcode {opcode}.");
        return problems;
    }

    /** The character class whose flag this instruction reads, or -1. */
    public int ClassOf(InvestmentUnlockInstruction instruction) =>
        instruction.Opcode == FlagOpcode && classFlags.TryGetValue(instruction.Operand, out var owner) ? owner.Class : -1;

    public static string OpcodeName(uint opcode) => Opcodes.TryGetValue(opcode, out var row) ? row.Name : $"op {opcode}";

    /** True for an opcode Sunrise evaluates (unlocks_expression.cpp decode_opcode). Shared rows are inlined before evaluation. */
    public static bool Evaluated(uint opcode) => opcode != SharedOpcode && Opcodes.ContainsKey(opcode);

    public InvestmentOperandLabel Label(InvestmentUnlockInstruction instruction) => instruction.Opcode switch
    {
        FlagOpcode => FlagLabel(instruction.Operand),
        ValueOpcode => ValueLabel(instruction.Operand),
        ConstantOpcode => new(InvestmentOperandKind.Constant,
            ((int)instruction.Operand).ToString(CultureInfo.InvariantCulture), "", true),
        SharedOpcode => new(InvestmentOperandKind.Shared, $"Shared expression {instruction.Operand}",
            UsableShared(instruction.Operand) ? "" : "Missing, empty or cyclic.", UsableShared(instruction.Operand)),
        _ when Opcodes.ContainsKey(instruction.Opcode) => new(InvestmentOperandKind.None, "", "", true),
        _ => new(InvestmentOperandKind.Unknown, $"op {instruction.Opcode} ({instruction.Operand})",
            "Studio does not decode this instruction.", false)
    };

    /**
     * True when Sunrise binds a flag read of this slot to the account bank: the slot is in the slot table, is not a class
     * flag, and the account mapping table names it (package_reward_build.cpp RewardConditions::bind).
     */
    public bool AccountFlag(uint slot)
    {
        if (slot >= flagSlots.Length || classFlags.ContainsKey(slot) || flagMaps.Length == 0) return false;
        accountFlagSlots ??= flagMaps[FlagMap(AccountFlagKind)].Destinations.Select(destination => (int)destination).ToHashSet();
        return accountFlagSlots.Contains((int)slot);
    }

    /**
     * True when Sunrise binds a value read of this slot to a saved bank: the account or the character value mapping table names
     * it (package_reward_build.cpp RewardConditions::bind). Any other value read is an external input.
     */
    public bool BoundValue(uint slot)
    {
        if (slot >= valueSlots.Length || valueMaps.Length == 0) return false;
        boundValueSlots ??= new[] { ValueMap(AccountValueKind), ValueMap(CharacterValueKind) }
            .SelectMany(map => valueMaps[map].Destinations).Select(destination => (int)destination).ToHashSet();
        return boundValueSlots.Contains((int)slot);
    }

    /** Account, profile and character labels follow Sunrise's bank names; the rest show their raw kind. */
    public InvestmentOperandLabel FlagLabel(uint slot)
    {
        if (classFlags.TryGetValue(slot, out var owner)) return new(InvestmentOperandKind.Flag,
            owner.Label, $"Flag slot {slot}", true);
        if (slot >= flagSlots.Length) return OutsideSlots("Flag", slot, flagSlots.Length);
        var row = flagSlots[slot];
        bool disagrees = flagDisagreements.TryGetValue((int)slot, out int mapped);
        string detail = $"Flag slot {slot}, kind {row.Kind}" + (disagrees ? MapNote(mapped) : "");
        var (text, proven) = row.Kind switch
        {
            1 => ($"Account flag row {row.BankRow}", true),
            2 => ($"Profile flag row {row.BankRow}", true),
            3 => ($"Character flag row {row.BankRow}", true),
            4 => ($"Flag map 4 row {row.BankRow}", false),
            _ => ($"Computed flag 0x{row.Hash:X8}", false)
        };
        return new(InvestmentOperandKind.Flag, text, detail, proven && !disagrees);
    }

    /** Account and character labels follow Sunrise's bank names; the rest show their raw kind. */
    public InvestmentOperandLabel ValueLabel(uint slot)
    {
        if (slot >= valueSlots.Length) return OutsideSlots("Value", slot, valueSlots.Length);
        var row = valueSlots[slot];
        bool disagrees = valueDisagreements.TryGetValue((int)slot, out int mapped);
        string detail = $"Value slot {slot}, kind {row.Kind}" + (disagrees ? MapNote(mapped) : "");
        var (text, proven) = row.Kind switch
        {
            1 => ($"Account value row {row.BankRow}", true),
            2 => ($"Character value row {row.BankRow}", true),
            3 => ($"Value map 3 row {row.BankRow}", false),
            5 => ($"Value map 4 row {row.BankRow}", false),
            4 => ($"Item value (aggregation {row.Aggregation})", false),
            _ => ($"Computed value 0x{row.Hash:X8}", false)
        };
        if (row.Kind == 4) detail += ". Per-item value; 2 may mean the maximum over held items.";
        return new(InvestmentOperandKind.Value, text, detail, proven && !disagrees);
    }

    /** Infix text when every opcode is decoded and the stack balances to one value, otherwise the postfix listing. */
    public string Format(InvestmentExpression expression) => expression.Error.Length != 0 ? "Unreadable: " + expression.Error
        : expression.Instructions.Count == 0 ? "None" : Infix(expression) ?? Postfix(expression);

    public string Format(InvestmentExpressionList list) => list.Error.Length != 0 ? "Unreadable: " + list.Error
        : list.Members.Count == 0 ? "None" : list.Members.Count == 1 ? Format(list.Members[0])
        : string.Join(" and ", list.Members.Select(member => $"({Format(member)})"));

    private string? Infix(InvestmentExpression expression)
    {
        var stack = new Stack<(string Text, bool Grouped)>();
        foreach (var instruction in expression.Instructions)
        {
            if (!Opcodes.TryGetValue(instruction.Opcode, out var opcode) || stack.Count < opcode.Pops) return null;
            if (instruction.Opcode == SharedOpcode
                && (!UsableShared(instruction.Operand) || sharedEffect[instruction.Operand] != 1)) return null;
            if (opcode.Pops == 0) stack.Push((instruction.Opcode == SharedOpcode ? $"shared #{instruction.Operand}" : Label(instruction).Text, false));
            else if (opcode.Pops == 1) stack.Push((opcode.Symbol + Group(stack.Pop()), false));
            else { var right = stack.Pop(); var left = stack.Pop(); stack.Push(($"{Group(left)} {opcode.Symbol} {Group(right)}", true)); }
        }
        return stack.Count == 1 ? stack.Pop().Text : null;
    }

    /** The instructions in order. A shared row reads as it does in infix, marked when it is missing, empty or cyclic. */
    private string Postfix(InvestmentExpression expression) => string.Join(", ", expression.Instructions.Select(instruction =>
    {
        var label = Label(instruction);
        if (instruction.Opcode == SharedOpcode) return $"shared #{instruction.Operand}" + (label.Proven ? "" : " (missing, empty or cyclic)");
        return label.Kind == InvestmentOperandKind.Unknown ? label.Text : label.Text.Length == 0 ? OpcodeName(instruction.Opcode)
            : $"{OpcodeName(instruction.Opcode)} {label.Text}";
    }));

    private static string Group((string Text, bool Grouped) value) => value.Grouped ? $"({value.Text})" : value.Text;
    private bool UsableShared(uint row) => row < shared.Length && shared[row].Instructions.Count != 0 && sharedDepth[row] >= 0;
    private static string MapNote(int mapped) => mapped < 0 ? "; its mapping table names no row" : $"; its mapping table names row {mapped}";
    private static InvestmentOperandLabel OutsideSlots(string kind, uint slot,
        int count) => new(kind == "Flag" ? InvestmentOperandKind.Flag
        : InvestmentOperandKind.Value, $"{kind} slot {slot}",
            count == 0 ? $"The {kind.ToLowerInvariant()} slot table is not loaded."
        : $"Outside the {kind.ToLowerInvariant()} slot table.", false);
    /** Flag kinds 1-4 pick maps +8/+24/+40/+56. */
    private static int FlagMap(int kind) => kind is >= 1 and <= 4 ? kind - 1 : -1;
    /** Value kinds 1/2/3/5 pick maps +8/+24/+40/+56. Kind 4 is a per-item value with no map. */
    private static int ValueMap(int kind) => kind switch { 1 => 0, 2 => 1, 3 => 2, 5 => 3, _ => -1 };

    private static InvestmentFlagSlot[] FlagSlotRows(ReadOnlySpan<byte> table)
    {
        int stride = Unsafe.SizeOf<UnlockFlagBinding>();
        var rows = TagArray.Checked(table, TagView.FieldOffset<UnlockFlagTable>("Table"), UnlockFlagBinding.Class, stride);
        var slots = new InvestmentFlagSlot[rows.Count];
        for (int index = 0; index < rows.Count; index++)
        {
            int at = rows.Data + index * stride;
            ref readonly var row = ref TagView.At<UnlockFlagBinding>(table, at);
            slots[index] = new(index, row.UnlockHash, TagRead.U16(table, at + SlotKind), row.BankIndex);
        }
        return slots;
    }

    private static InvestmentValueSlot[] ValueSlotRows(ReadOnlySpan<byte> table)
    {
        int stride = Unsafe.SizeOf<UnlockValueBinding>();
        var rows = TagArray.Checked(table, TagView.FieldOffset<UnlockValueTable>("Table"), UnlockValueBinding.Class, stride);
        var slots = new InvestmentValueSlot[rows.Count];
        for (int index = 0; index < rows.Count; index++)
        {
            int at = rows.Data + index * stride;
            slots[index] = new(index, TagView.At<UnlockValueBinding>(table, at).UnlockHash, TagRead.U8(table, at + SlotKind),
                TagRead.U8(table, at + SlotAggregation), TagRead.U16(table, at + SlotBankRow));
        }
        return slots;
    }

    /** Each mapping row names the unlock slot its bank row feeds. */
    private static InvestmentSlotMap[] Maps<TRow>(ReadOnlyMemory<byte>? table, int[] arrays, uint rowClass) where TRow : unmanaged
    {
        if (table is not { } bytes) return [];
        var span = bytes.Span;
        int stride = Unsafe.SizeOf<TRow>(), destination = TagView.FieldOffset<TRow>(nameof(UnlockFlagMapRow.DestinationSlot));
        var maps = new InvestmentSlotMap[arrays.Length];
        for (int map = 0; map < maps.Length; map++)
        {
            var rows = TagArray.Checked(span, arrays[map], rowClass, stride);
            var destinations = new short[rows.Count];
            for (int row = 0; row < rows.Count; row++) destinations[row] = TagRead.I16(span,
                rows.Data + row * stride + destination);
            maps[map] = new(arrays[map], destinations);
        }
        return maps;
    }

    /** A slot agrees when its kind's map names it at its bank row. A disagreeing slot maps to the first row that does, or -1. */
    private static Dictionary<int, int> Disagreeing(IEnumerable<(int Slot, int Map, int Row)> slots, InvestmentSlotMap[] maps)
    {
        var result = new Dictionary<int, int>();
        if (maps.Length == 0) return result;
        foreach (var (slot, map, row) in slots)
        {
            if (map < 0) continue;
            var destinations = maps[map].Destinations;
            if (row < destinations.Count && destinations[row] == slot) continue;
            int named = -1;
            for (int i = 0; i < destinations.Count && named < 0; i++) if (destinations[i] == slot) named = i;
            result[slot] = named;
        }
        return result;
    }

    /** A class's flag is the one flag instruction gating use of its default finisher (package_reward_build.cpp read_class_flag). */
    private static Dictionary<uint,
        (int Class, string Label)> ClassFlags(ReadOnlyMemory<byte>? classes, PackageCatalog catalog,
            IReadOnlyList<InvestmentItem> items)
    {
        var result = new Dictionary<uint, (int Class, string Label)>();
        if (classes is not { } bytes) return result;
        int stride = Unsafe.SizeOf<CharacterClassRow>();
        var rows = TagArray.Checked(bytes.Span, TagView.FieldOffset<CharacterClassTable>("Table"),
            CharacterClassRow.Class, stride);
        for (int i = 0; i < rows.Count; i++)
        {
            int finisher = TagView.At<CharacterClassRow>(bytes.Span, rows.Data + i * stride).DefaultFinisherItem;
            if (finisher >= items.Count) continue;
            byte[] item = catalog.Read(items[finisher].Tag);
            // The finisher's use condition holds one expression at its start.
            int block = InvestmentItemBlocks.Find(item, nameof(ItemDefinition.FinisherItemBlock), UseCondition.Class);
            if (block < 0) continue;
            var expression = ReadExpression(item, block);
            if (expression.Error.Length != 0) throw new InvalidDataException(expression.Error);
            if (expression.Instructions is not [{ Opcode: FlagOpcode } flag]) continue;
            result.TryAdd(flag.Operand, (i, $"Character class {i} ({items[finisher].Name})"));
        }
        return result;
    }

    /** Walks the opcode-12 edges once. It needs no depth cap, because the walk is iterative and stops at every cycle. */
    private (int[] Depth, int?[] Effect) WalkShared(List<string> diagnostics)
    {
        int count = shared.Length;
        var edges = shared.Select(row => (IReadOnlyList<int>)row.Instructions.Where(instruction => instruction.Opcode == SharedOpcode)
            .Select(instruction => instruction.Operand < count ? (int)instruction.Operand : -1).ToArray()).ToArray();
        var (order, cycles) = InvestmentGraphWalk.Walk(edges);
        var depth = new int[count]; var effect = new int?[count];
        var cyclic = cycles.SelectMany(cycle => cycle).ToHashSet();
        int outside = 0, empty = 0, unreadable = 0, firstOutside = -1, firstEmpty = -1, firstUnreadable = -1;
        foreach (int row in order)
        {
            bool broken = cyclic.Contains(row) || shared[row].Error.Length != 0;
            if (shared[row].Error.Length != 0 && unreadable++ == 0) firstUnreadable = row;
            int deepest = 0;
            foreach (int target in edges[row])
            {
                if (target < 0) { broken = true; if (outside++ == 0) firstOutside = row; continue; }
                if (shared[target].Instructions.Count == 0) { broken = true; if (empty++ == 0) firstEmpty = row; continue; }
                if (depth[target] < 0) broken = true;
                else deepest = Math.Max(deepest, 1 + depth[target]);
            }
            depth[row] = broken ? -1 : deepest;
            if (broken) continue;
            int? net = 0;
            foreach (var instruction in shared[row].Instructions)
                net = instruction.Opcode == SharedOpcode ? net + effect[instruction.Operand]
                    : Opcodes.TryGetValue(instruction.Opcode, out var opcode) ? net + opcode.Pushes - opcode.Pops : null;
            effect[row] = net;
        }
        if (outside != 0) diagnostics.Add($"{outside} shared expression references leave the table (first in row {firstOutside}).");
        if (empty != 0) diagnostics.Add($"{empty} shared expression references name an empty row (first in row {firstEmpty}).");
        if (unreadable != 0) diagnostics.Add($"{unreadable} shared expressions could not be read (first: row {firstUnreadable}).");
        foreach (var cycle in cycles.Take(InvestmentTables.ListedCycles)) diagnostics.Add($"Shared expressions form a cycle: {string.Join(", ", cycle)}.");
        if (cycles.Count > 8) diagnostics.Add($"{cycles.Count - 8} more shared expression cycles.");
        return (depth, effect);
    }
}

/** An iterative three-colour depth-first walk shared by the unlock and reward graphs. */
internal static class InvestmentGraphWalk
{
    /**
     * @param edges Each node's targets; a negative or out-of-range target is skipped, so callers report it.
     * @return Every node in finish order, and every cycle found, listed from the node it returns to.
     */
    public static (List<int> Order, List<int[]> Cycles) Walk(IReadOnlyList<IReadOnlyList<int>> edges)
    {
        int count = edges.Count;
        var colour = new byte[count]; var order = new List<int>(count); var cycles = new List<int[]>();
        var stack = new List<(int Node, int Edge)>();
        for (int start = 0; start < count; start++)
        {
            if (colour[start] != 0) continue;
            colour[start] = 1; stack.Add((start, 0));
            while (stack.Count != 0)
            {
                var (node, edge) = stack[^1];
                if (edge == edges[node].Count) { colour[node] = 2; order.Add(node); stack.RemoveAt(stack.Count - 1); continue; }
                stack[^1] = (node, edge + 1);
                int target = edges[node][edge];
                if (target < 0 || target >= count) continue;
                if (colour[target] == 0) { colour[target] = 1; stack.Add((target, 0)); }
                else if (colour[target] == 1)
                    cycles.Add(stack.Skip(stack.FindIndex(frame => frame.Node == target)).Select(frame => frame.Node).ToArray());
            }
        }
        return (order, cycles);
    }
}
