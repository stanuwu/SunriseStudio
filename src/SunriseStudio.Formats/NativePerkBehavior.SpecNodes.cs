using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

public static partial class NativePerkBehavior
{
    // A label value packs its bit index above the 32-bit label hash.
    private const double LabelBitScale = 4294967296.0;
    // Refill fractions: five expressions, each after an 8-byte zero member.
    private static readonly (string Key, int Offset)[] Fractions =
        [("active_fraction", PayloadFields.RefillFractions[0]), ("this_fraction", PayloadFields.RefillFractions[1]),
         ("slot0_fraction", PayloadFields.RefillFractions[2]), ("slot1_fraction", PayloadFields.RefillFractions[3]),
         ("slot2_fraction", PayloadFields.RefillFractions[4])];

    // Movement block: two sets of eight floats; a mode-state flag picks the set.
    private const uint MovementBlockClass = PerkMovementBlock.Class;
    // Component scale names are a u32 array.
    private const uint NameListClass = SU32HashElement.Class;
    private static int NameStride => Unsafe.SizeOf<SU32HashElement>();

    /** Movement block inputs and their offsets inside the block; a property, so static order never matters. */
    private static (Input Input, int Offset, Width Width)[] MovementInputs =>
    [
        (new("name", "Block name hash", -1, Width.U32, EmptyHash, 0, uint.MaxValue, PerkBehaviorValueKind.Hash,
            "Not read on the path Studio decoded."), PayloadFields.MovementName, Width.U32),
        (Flag("allow_first", "Allowed in state A", -1, 1, "Which state is A is not known."), PayloadFields.MovementAllowFirst, Width.U8),
        (Flag("allow_second", "Allowed in state B", -1, 1), PayloadFields.MovementAllowSecond, Width.U8),
        .. new[] { (PayloadFields.MovementFirst, "first", "First set"), (PayloadFields.MovementSecond, "second", "Second set") }
            .SelectMany(set => new[]
        {
            (Number(set.Item2 + "_value0", set.Item3 + ": value 0", -1, 0, -1000000, 1000000, "Copied into the mode."), set.Item1[0], Width.F32),
            (Number(set.Item2 + "_entry", set.Item3 + ": entry speed value", -1, -1, -1, 1000000, "-1 turns it off."), set.Item1[1], Width.F32),
            (Number(set.Item2 + "_value8", set.Item3 + ": value 8", -1, 0, -1000000, 1000000, "Copied into the mode."), set.Item1[2], Width.F32),
            (Number(set.Item2 + "_hmin", set.Item3 + ": horizontal speed minimum", -1, 0, -1000000, 1000000), set.Item1[3], Width.F32),
            (Number(set.Item2 + "_hmax", set.Item3 + ": horizontal speed maximum", -1, 0, -1000000, 1000000), set.Item1[4], Width.F32),
            (Number(set.Item2 + "_value20", set.Item3 + ": value 20", -1, 0, -1000000, 1000000, "Copied into the mode."), set.Item1[5], Width.F32),
            (Number(set.Item2 + "_vmin", set.Item3 + ": vertical speed minimum", -1, 0, -1000000, 1000000), set.Item1[6], Width.F32),
            (Number(set.Item2 + "_vmax", set.Item3 + ": vertical speed maximum", -1, 0, -1000000, 1000000), set.Item1[7], Width.F32)
        })
    ];

    /** StateFlag is 1 exactly for opcodes whose dispatch slot +32 allocates effect state. */
    private static byte StateFlag(int opcode) =>
        opcode is 0 or 1 or 2 or 6 or 7 or 10 or 17 or 18 or 22 or 25 or 26 or 28 or 29 or 30 or 33 or 35 or 41 or 52
            ? (byte)1 : (byte)0;

    /** Condition kinds that test one event and keep no state, so any list of conditions can hold them. */
    public static IReadOnlyList<string> EventPrimitives =>
        ["kill", "damage", "unequip", "event", .. SpecTableKeys(PerkBehaviorNodeKind.Condition).Where(key => key != "all-groups")];

    /** Condition kinds a new group of an all-groups condition can hold. */
    public static IReadOnlyList<string> GroupPrimitives => ["while-state", .. StateGatedKeys, .. EventPrimitives];

    /** State-gated conditions not already in Primitives; the palette lists them apart from the main kinds. */
    public static IReadOnlyList<PerkBehaviorPrimitive> StateGatedPrimitives => stateGated ??=
        [.. StateGatedKeys.Where(key => Primitives.All(row => row.Key != key)).Select(key => PrimitiveOf(key)!)];
    private static IReadOnlyList<PerkBehaviorPrimitive>? stateGated;

    private static IEnumerable<string> StateGatedKeys => EventPrimitives.Select(inner => StatePrefix + inner);

    /** A palette kind by key, including the state-gated form of each event condition. */
    public static PerkBehaviorPrimitive? PrimitiveOf(string key)
    {
        if (Primitives.FirstOrDefault(row => row.Key == key) is { } listed) return listed;
        if (!key.StartsWith(StatePrefix, StringComparison.Ordinal)) return null;
        var inner = Primitives.FirstOrDefault(row => row.Key == key[StatePrefix.Length..]);
        if (inner is null || !EventPrimitives.Contains(inner.Key)) return null;
        return new(key, inner.Name + " while a state holds", PerkBehaviorNodeKind.Condition, GatedClass,
            "Passes when the inner condition passes while an object property is in range. Both probabilities apply.",
            true, 35);
    }

    private static IEnumerable<string> SpecTableKeys(PerkBehaviorNodeKind kind) =>
        SpecTable.All.Where(spec => spec.Kind == kind && spec.CanCreate).Select(spec => spec.Key);

    /** Kinds a new child of this node can be; empty when the node takes no new children. */
    public static IReadOnlyList<string> ChildPrimitives(PerkBehaviorNode parent) =>
        parent.OriginalOffset >= 0 ? [] : parent.Primitive switch
        {
            "counter" => CounterRowPrimitives,
            "all-groups" => [GroupKey],
            GroupKey => GroupPrimitives,
            "extend-timers" => EventPrimitives,
            _ => []
        };

    /** A new child for this parent: a counter row, a group, or a condition. */
    public static PerkBehaviorNode CreateChild(PerkBehaviorNode parent, string primitive)
    {
        if (!ChildPrimitives(parent).Contains(primitive))
            throw new InvalidDataException("This node cannot hold that condition.");
        return parent.Primitive == "counter" ? CreateRow(primitive) : CreateNode(primitive);
    }

    private static PerkBehaviorNode CreateGroup() => new()
    {
        Primitive = GroupKey, Title = "Group", Kind = PerkBehaviorNodeKind.Condition, NativeClass = GroupClass,
        Editable = true, Description = "Passes when any of its conditions passes, or passed within its window.",
        Properties = Inputs(GroupKey, PerkBehaviorNodeKind.Condition)
    };

    private static Role ChildRole(PerkBehaviorNode parent) => parent.NativeClass switch
    {
        CounterClass => Role.Row, GatedClass => Role.Inner, AllGroupsClass => Role.Groups,
        GroupClass => Role.Group, _ => Role.Op32
    };

    /** Decodes the parts a spec's fixed inputs do not cover; false keeps the node native. */
    private static bool ReadSpec(ReadOnlyMemory<byte> bytes, int at, Spec spec, PerkBehaviorNode node, string id,
        List<string> diagnostics)
    {
        try
        {
            foreach (var part in spec.Parts ?? [])
            {
                switch (part.Kind)
                {
                    case "labels":
                        ReadLabel(bytes.Span, at + part.First, at + part.Second, node);
                        break;
                    case "fractions":
                        foreach (var (key, offset) in Fractions)
                        {
                            var (form, constant) = Expression(bytes, at + offset);
                            var property = node.Properties.First(row => row.Key == key);
                            if (form == 0) property.Value = constant;
                            else
                            {
                                node.Properties.Remove(property);
                                node.Properties.Add(Info(key + "_expression", property.Label + ": input expression (kept as is)", 1));
                            }
                        }
                        break;
                    case "groups":
                        var groups = TagArray.Checked(bytes.Span, at + PayloadFields.AllGroupsGroups, GroupClass, PayloadFields.GroupSize);
                        for (int i = 0; i < groups.Count; i++)
                        {
                            int row = groups.Data + i * PayloadFields.GroupSize;
                            ref readonly var installed = ref TagView.At<PerkConditionGroup>(bytes.Span, row);
                            var group = CreateGroup();
                            group.Id = $"{id}/{i}";
                            group.Title = $"Group {i + 1}";
                            group.OriginalOffset = row;
                            group.Properties[0].Value = installed.Window;
                            if (!float.IsFinite(installed.Window)) return false;
                            if (installed.Unknown4 != 0) group.Properties.Add(Info("other", "Other native settings (kept as is)", 1));
                            ReadList(row + PayloadFields.GroupMembers, Role.Group, group, group.Id);
                            node.Children.Add(group);
                        }
                        break;
                    case "timer-conditions":
                        ReadList(at + PayloadFields.TimerConditions, Role.Op32, node, id);
                        break;
                    case "names":
                        var names = TagArray.Checked(bytes.Span, at + part.First, NameListClass, NameStride);
                        var name = node.Properties.First(row => row.Key == "name");
                        if (names.Count == 1) name.Value = TagView.At<SU32HashElement>(bytes.Span, names.Data).KeyHash;
                        else
                        {
                            node.Properties.Remove(name);
                            node.Properties.Add(Info("names", "Component names (kept as is)", names.Count, PerkBehaviorValueKind.Integer));
                        }
                        break;
                    case "expression":
                        ReadExpression(bytes, at + part.First, node);
                        break;
                    case "movement":
                        int block = TagPointer.TryChecked(bytes.Span, at + PayloadFields.MovementBlock);
                        uint blockClass = block >= TagView.ClassWordSize ? TagView.ClassAt(bytes.Span, block) ?? 0 : 0;
                        if (block >= TagView.ClassWordSize && TagView.Fits<PerkMovementBlock>(bytes.Span, block)
                            && blockClass == MovementBlockClass)
                        {
                            foreach (var (input, offset, width) in MovementInputs)
                            {
                                double value = width switch
                                {
                                    Width.U8 => (double)TagRead.U8(bytes.Span, block + offset),
                                    Width.U32 => (double)TagRead.U32(bytes.Span, block + offset),
                                    _ => (double)F32(bytes, block + offset)
                                };
                                if (!double.IsFinite(value) || input.Kind == PerkBehaviorValueKind.Boolean && value > 1) return false;
                                node.Properties.First(row => row.Key == input.Key).Value = value;
                            }
                        }
                        else
                        {
                            node.Properties.Clear();
                            node.Properties.Add(Info("block_class", "Settings block class (kept as is)", blockClass,
                                PerkBehaviorValueKind.Hash));
                        }
                        break;
                }
            }
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException) { return false; }
        // The script's Unk8 is a relative pointer to its source path string.
        if (spec.Key == "script" && TagView.At<PerkScript>(bytes.Span, at).Unk8 is { IsNull: false } path)
            node.Properties.Add(Info("unread", "Source path pointer at +8 (kept as is)", path.Relative,
                PerkBehaviorValueKind.Integer));
        if (OtherSettings(bytes, at, spec)) node.Properties.Add(Info("other", "Other native settings (kept as is)", 1));
        return true;

        void ReadList(int field, Role role, PerkBehaviorNode parent, string prefix)
        {
            int stride = PayloadFields.ConditionRowSize;
            var list = TagArray.Checked(bytes.Span, field, ConditionRowClass, stride);
            for (int j = 0; j < list.Count; j++)
                parent.Children.Add(ReadNode(bytes, TagPointer.Checked(bytes.Span, list.Data + j * stride),
                    PerkBehaviorNodeKind.Condition, role, $"{prefix}/{j}", diagnostics));
        }
    }

    /** One label row with its one matching bit is editable; any other set is kept as installed. */
    private static void ReadLabel(ReadOnlySpan<byte> tag, int rowsField, int maskField, PerkBehaviorNode node)
    {
        var rows = TagArray.Checked(tag, rowsField, LabelRowClass, PayloadFields.LabelRowSize);
        var mask = tag.Slice(maskField, PerkLabelTable.MaskBytes);
        var bits = new List<int>();
        for (int bit = 0; bit < mask.Length * 8; bit++)
            if ((mask[bit / 8] >> (bit % 8) & 1) != 0) bits.Add(bit);
        var label = node.Properties.First(row => row.Key == "label");
        if (rows.Count == 1 && bits.Count == 1) label.Value = bits[0] * LabelBitScale + TagView.At<PerkLabelRow>(tag, rows.Data).LabelHash;
        else
        {
            node.Properties.Remove(label);
            node.Properties.Add(Info("labels", "Labels (kept as is)", rows.Count, PerkBehaviorValueKind.Integer));
        }
    }

    /** True when a byte outside the inputs differs from what a new node of this kind writes. */
    private static bool OtherSettings(ReadOnlyMemory<byte> bytes, int at, Spec spec)
    {
        byte[] template = new byte[spec.Size];
        bool[] skip = new bool[spec.Size];
        foreach (var value in spec.Defaults ?? []) Put(template, value.Offset, value.Width, value.Value);
        Cover(0, spec.Kind == PerkBehaviorNodeKind.Condition ? Unsafe.SizeOf<PerkAlways>() : Unsafe.SizeOf<PerkSwordGuard>());
        foreach (var input in spec.Inputs.Where(input => input.Offset >= 0)) Cover(input.Offset, WidthOf(input.Width));
        foreach (var (offset, length) in (spec.Ignore ?? []).Concat(LabelRanges(spec.Class))) Cover(offset, length);
        var span = bytes.Span.Slice(at, spec.Size);
        for (int i = 0; i < spec.Size; i++)
            if (!skip[i] && span[i] != template[i]) return true;
        return false;

        void Cover(int offset, int length)
        {
            for (int i = offset; i < Math.Min(spec.Size, offset + length); i++) skip[i] = true;
        }
    }

    private static int WidthOf(Width width) => width switch { Width.U8 or Width.I8 => 1, _ => 4 };

    private static void Put(byte[] bytes, int at, Width width, double value)
    {
        switch (width)
        {
            case Width.U8 or Width.I8: bytes[at] = unchecked((byte)(long)value); break;
            case Width.F32: BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), (float)value); break;
            default: BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), unchecked((uint)(long)value)); break;
        }
    }

    /** Native defaults and the copied label table reference of a new node. */
    private static void WriteSpecDefaults(PerkWriter writer, int at, Spec spec)
    {
        foreach (var value in spec.Defaults ?? [])
            switch (value.Width)
            {
                case Width.U8 or Width.I8: writer.U8(at + value.Offset, unchecked((byte)(long)value.Value)); break;
                case Width.F32: writer.F32(at + value.Offset, (float)value.Value); break;
                default: writer.U32(at + value.Offset, unchecked((uint)(long)value.Value)); break;
            }
        if (spec.LabelSet < 0) return;
        // A label set's table path and tag name the installed label table.
        int path = writer.String(LabelGlobalsPath);
        int set = at + spec.LabelSet, pathField = set + PayloadFields.LabelSetPath;
        writer.I64(pathField, path - pathField);
        writer.U32(set + PayloadFields.LabelSetTable, LabelGlobalsTag);
    }

    /** Parts of a new node that are not fixed inputs: lists, labels, expressions and children. */
    private static void WriteSpecParts(PerkWriter writer, PerkBehaviorNode node, int at, Spec spec, ref int index)
    {
        foreach (var part in spec.Parts ?? [])
        {
            switch (part.Kind)
            {
                case "types":
                    WriteTypeList(writer, at + part.First, (uint)Value(node, "types"));
                    break;
                case "labels":
                    WriteLabel(writer, at + part.First, at + part.Second, Value(node, "label"));
                    break;
                case "fractions":
                    foreach (var (key, offset) in Fractions)
                    {
                        WriteExpression(writer, at + offset, 0, (float)Value(node, key));
                        writer.U64(at + offset + PayloadFields.ExpressionFirstOne, 1);
                        writer.U64(at + offset + PayloadFields.ExpressionSecondOne, 1);
                    }
                    break;
                case "selfref":
                    WriteSelfReference(writer, at);
                    break;
                case "groups":
                    int rows = writer.Array(at + PayloadFields.AllGroupsGroups, GroupClass, node.Children.Count,
                        PayloadFields.GroupSize);
                    for (int i = 0; i < node.Children.Count; i++)
                    {
                        var group = node.Children[i];
                        int row = rows + i * PayloadFields.GroupSize;
                        writer.Edit<PerkConditionGroup>(row).Window = (float)Value(group, "window");
                        WriteList(writer, row + PayloadFields.GroupMembers, group.Children, Role.Group, ref index);
                        writer.U64(row + PayloadFields.GroupMask, group.Children.Aggregate(0UL, (mask, child) => mask | Bits(child)));
                    }
                    break;
                case "timer-conditions":
                    WriteList(writer, at + PayloadFields.TimerConditions, node.Children, Role.Op32, ref index);
                    writer.U64(at + PayloadFields.TimerMask, node.Children.Aggregate(0UL, (mask, child) => mask | Bits(child)));
                    break;
                case "names":
                    WriteNames(writer, at + part.First, (uint)Value(node, "name"));
                    break;
                case "expression":
                    int expression = at + part.First;
                    WriteExpression(writer, expression, (int)Value(node, "expression"), (float)Value(node, "constant"));
                    writer.U64(expression + PayloadFields.ExpressionFirstOne, 1);
                    writer.U64(expression + PayloadFields.ExpressionSecondOne, 1);
                    break;
                case "movement":
                    int block = writer.Node(MovementBlockClass, PayloadFields.MovementBlockSize);
                    writer.U32(block + PayloadFields.MovementName, EmptyHash);
                    WriteMovement(writer, node, block);
                    int blockField = at + PayloadFields.MovementBlock;
                    writer.I64(blockField, block - blockField);
                    break;
            }
        }
    }

    private static void WriteNames(PerkWriter writer, int field, uint name)
    {
        int list = writer.Array(field, NameListClass, 1, NameStride);
        writer.Edit<SU32HashElement>(list).KeyHash = name;
    }

    private static void WriteMovement(PerkWriter writer, PerkBehaviorNode node, int block)
    {
        foreach (var (input, offset, width) in MovementInputs)
            if (node.Properties.FirstOrDefault(row => row.Key == input.Key) is { Editable: true } property)
                switch (width)
                {
                    case Width.U8: writer.U8(block + offset, checked((byte)property.Value)); break;
                    case Width.U32: writer.U32(block + offset, checked((uint)property.Value)); break;
                    default: writer.F32(block + offset, (float)property.Value); break;
                }
    }

    /** Constant and pass-through programs are rewritten when changed; any other program is kept. */
    private static void PatchExpression(PerkWriter writer, ReadOnlyMemory<byte> original, PerkBehaviorNode node, int expression)
    {
        var (form, current) = Expression(original, expression);
        if (form == 2 || node.Properties.All(row => row.Key != "constant")) return;
        int wanted = (int)Value(node, "expression");
        float scalar = (float)Value(node, "constant");
        if (wanted != form || wanted == 0 && BitConverter.SingleToInt32Bits(scalar) != BitConverter.SingleToInt32Bits(current))
            WriteExpression(writer, expression, wanted, scalar);
    }

    private static void WriteList(PerkWriter writer, int field, List<PerkBehaviorNode> children, Role role, ref int index)
    {
        int stride = PayloadFields.ConditionRowSize;
        int list = writer.Array(field, ConditionRowClass, children.Count, stride);
        for (int i = 0; i < children.Count; i++)
        {
            int child = WriteNode(writer, children[i], role, ref index);
            int row = list + i * stride;
            writer.I64(row, child - row);
        }
    }

    /** Installed nodes keep every byte the user did not change; changed lists and labels are appended. */
    private static void PatchSpec(PerkWriter writer, ReadOnlyMemory<byte> original, PerkBehaviorNode node, int at, Spec spec)
    {
        foreach (var part in spec.Parts ?? [])
        {
            switch (part.Kind)
            {
                case "types":
                    if (node.Properties.FirstOrDefault(row => row.Key == "types") is { Editable: true } types
                        && (uint)types.Value != TagRead.U32(original.Span, at + part.Second))
                        WriteTypeList(writer, at + part.First, (uint)types.Value);
                    break;
                case "labels":
                    if (node.Properties.FirstOrDefault(row => row.Key == "label") is { Editable: true } label)
                    {
                        var before = new PerkBehaviorNode { Properties = [new() { Key = "label" }] };
                        ReadLabel(original.Span, at + part.First, at + part.Second, before);
                        if (before.Properties[0].Value != label.Value)
                            WriteLabel(writer, at + part.First, at + part.Second, label.Value);
                    }
                    break;
                case "fractions":
                    foreach (var (key, offset) in Fractions)
                        if (node.Properties.FirstOrDefault(row => row.Key == key) is { Editable: true } fraction
                            && Expression(original, at + offset) is (0, var constant)
                            && BitConverter.SingleToInt32Bits(constant) != BitConverter.SingleToInt32Bits((float)fraction.Value))
                            WriteExpression(writer, at + offset, 0, (float)fraction.Value);
                    break;
                case "selfref":
                    if (writer.ProgramTag != 0) WriteSelfReference(writer, at);
                    break;
                case "groups":
                    var rows = TagArray.Checked(original.Span, at + PayloadFields.AllGroupsGroups, GroupClass, PayloadFields.GroupSize);
                    for (int i = 0; i < rows.Count; i++)
                    {
                        var group = node.Children[i];
                        writer.Edit<PerkConditionGroup>(rows.Data + i * PayloadFields.GroupSize).Window = (float)Value(group, "window");
                        foreach (var child in group.Children) PatchNode(writer, original, child, child.OriginalOffset, Role.Group);
                    }
                    break;
                case "timer-conditions":
                    foreach (var child in node.Children) PatchNode(writer, original, child, child.OriginalOffset, Role.Op32);
                    break;
                case "names":
                    var names = TagArray.Checked(original.Span, at + part.First, NameListClass, NameStride);
                    if (node.Properties.FirstOrDefault(row => row.Key == "name") is { Editable: true } name
                        && names.Count == 1 && (uint)name.Value != TagView.At<SU32HashElement>(original.Span, names.Data).KeyHash)
                        WriteNames(writer, at + part.First, (uint)name.Value);
                    break;
                case "expression":
                    PatchExpression(writer, original, node, at + part.First);
                    break;
                case "movement":
                    if (node.Properties.Any(row => row.Key == "name"))
                        WriteMovement(writer, node, TagPointer.TryChecked(original.Span, at + PayloadFields.MovementBlock));
                    break;
            }
        }
    }

    /** Damage types are authored as a u8 list and read as a mask; both must agree. */
    private static void WriteTypeList(PerkWriter writer, int field, uint mask)
    {
        var types = Enumerable.Range(0, 32).Where(bit => (mask >> bit & 1) != 0).ToArray();
        int stride = Unsafe.SizeOf<C80806b02>();
        int list = writer.Array(field, TypeListClass, types.Length, stride);
        for (int i = 0; i < types.Length; i++) writer.U8(list + i * stride, (byte)types[i]);
    }

    /** One label row naming the hash, and the label mask with only that hash's table index set. */
    private static void WriteLabel(PerkWriter writer, int rowsField, int maskField, double value)
    {
        uint hash = (uint)(value % LabelBitScale);
        int bit = (int)(value / LabelBitScale);
        int path = writer.String(LabelGlobalsPath);
        int row = writer.Array(rowsField, LabelRowClass, 1, PayloadFields.LabelRowSize);
        writer.Edit<PerkLabelRow>(row).LabelHash = hash;
        int pathField = row + PayloadFields.LabelRowPath;
        writer.I64(pathField, path - pathField);
        writer.U32(row + PayloadFields.LabelRowTable, LabelGlobalsTag);
        byte[] mask = new byte[PerkLabelTable.MaskBytes];
        mask[bit / 8] = (byte)(1 << bit % 8);
        writer.Copy(maskField, mask);
    }

    /** Damage-resist effects name their own program tag and payload offset. */
    private static void WriteSelfReference(PerkWriter writer, int at)
    {
        ref var resist = ref writer.Edit<PerkDamageResist>(at);
        resist.SelfClass = ResistClass;
        resist.Unknown164 = 0;
        resist.ProgramTag = new TagOffsetPointer(writer.ProgramTag, ResistClass, at);
    }

    /** The program tag from any damage-resist self reference, root or stack branch; 0 when none. */
    private static uint FindProgramTag(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            var span = bytes.Span;
            var effects = new List<int>();
            int stride = PayloadFields.EffectRowSize;
            var main = TagArray.Checked(span, PayloadFields.Effects, EffectRowClass, stride);
            for (int i = 0; i < main.Count; i++) effects.Add(TagPointer.Checked(span, main.Data + i * stride));
            // Stack branch rows hold their own effect arrays.
            if (TagRead.U64(span, PayloadFields.Stack) != 0)
            {
                var stacks = TagArray.Checked(span, PayloadFields.Stack, 0, PayloadFields.BranchSize);
                for (int s = 0; s < stacks.Count; s++)
                {
                    var list = TagArray.Checked(span, stacks.Data + s * PayloadFields.BranchSize + PayloadFields.BranchEffects, 0, stride);
                    for (int i = 0; i < list.Count; i++) effects.Add(TagPointer.Checked(span, list.Data + i * stride));
                }
            }
            foreach (int at in effects)
            {
                if (at < TagView.ClassWordSize || !TagView.Fits<PerkDamageResist>(span, at) || TagView.ClassAt(span, at) != ResistClass)
                    continue;
                ref readonly var resist = ref TagView.At<PerkDamageResist>(span, at);
                if (resist.SelfClass == ResistClass) return resist.ProgramTag.Tag;
            }
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException) { }
        return 0;
    }
}
