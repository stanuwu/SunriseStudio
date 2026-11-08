using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

public static partial class NativePerkBehavior
{
    /** Reads one payload; a payload the decoders refuse stays a preserved native node. */
    private static PerkBehaviorNode ReadNode(ReadOnlyMemory<byte> bytes, int at, PerkBehaviorNodeKind kind,
        Role role, string id, List<string> diagnostics)
    {
        if (at < TagView.ClassWordSize || at > bytes.Length - (kind == PerkBehaviorNodeKind.Condition ? ConditionHeaderSize : EffectOpcode + sizeof(byte)))
            throw new InvalidDataException("The perk node is truncated.");
        uint type = TagView.ClassAt(bytes.Span, at) ?? 0;
        var node = TryReadNode(bytes, at, type, kind, role, id, diagnostics);
        if (node is null)
        {
            node = new() { Primitive = "native", Title = $"Native operation {type:X8}",
                Kind = kind, NativeClass = type, Editable = false,
                Description = "This operation is preserved. Its implementation and undecoded inputs are read-only." };
            diagnostics.Add($"Native operation {type:X8} has inputs this editor preserves without editing.");
            if (kind == PerkBehaviorNodeKind.Condition)
            {
                ref readonly var header = ref ConditionHeader(bytes.Span, at);
                if (header.ConditionMarkerFf == byte.MaxValue && header.Probability is >= 0 and <= 1)
                {
                    node.Primitive = "native-condition";
                    node.Title = $"Native condition {header.Opcode}";
                    node.Editable = true;
                    node.Properties.Add(Property("probability", "Probability", header.Probability, 0, 1));
                    node.Description = "Probability is editable. Native condition logic and its other inputs are preserved.";
                }
            }
        }
        node.Id = id;
        node.OriginalOffset = at;
        node.NativeBytes = bytes.Span.Slice(at, Math.Min(ConditionHeaderSize, bytes.Length - at)).ToArray();
        node.LabelFilters.Clear();
        if (node.Primitive != "native"
            && LabelSitesOf(type).Select(site => site.Kind + Unsafe.SizeOf<C808094a8>()).DefaultIfEmpty(0).Max() is var labelEnd
            && labelEnd >= 0 && at <= bytes.Length - labelEnd)
        {
            try { ReadLabelFilters(bytes, at, node); }
            catch (InvalidDataException) { node.LabelFilters.Clear(); }
        }
        return node;
    }

    /** Editable inputs require both the node class and the native dispatch opcode to agree. */
    private static PerkBehaviorNode? TryReadNode(ReadOnlyMemory<byte> bytes, int at, uint type,
        PerkBehaviorNodeKind kind, Role role, string id, List<string> diagnostics)
    {
        var tag = bytes.Span;
        string? key = kind == PerkBehaviorNodeKind.Condition ? ConditionKey(tag, at, type, role) : EffectKey(tag, at, type);
        if (key is null) return null;
        var node = Blank(key, Primitives.First(row => row.Key == (key == "state" ? "state-kill" : key)));
        var properties = node.Properties;
        void Set(string property, double value) => properties.First(row => row.Key == property).Value = value;
        if (kind == PerkBehaviorNodeKind.Condition) Set("probability", ConditionHeader(tag, at).Probability);
        if (FieldsOf(key) is { } fields && !ReadFields(bytes, at, fields, node)) return null;
        switch (key)
        {
            case "timer":
                Set("seconds", TagView.At<PerkTimer>(tag, at).Seconds);
                break;
            case "property":
                ReadProperty(bytes, at, node);
                break;
            case "ammo-drop":
            {
                ref readonly var ammo = ref TagView.At<PerkAmmoDrop>(tag, at);
                float[] drops = [ammo.PrimaryInterval, ammo.PrimaryChance, ammo.PrimaryRepeat,
                    ammo.SpecialInterval, ammo.SpecialChance, ammo.SpecialRepeat,
                    ammo.HeavyInterval, ammo.HeavyChance, ammo.HeavyRepeat];
                for (int i = 0; i < properties.Count; i++) properties[i].Value = drops[i];
                break;
            }
            case "while-state" or "state":
            {
                byte selector, bits;
                if (key == "state")
                {
                    ref readonly var gated = ref TagView.At<PerkStateGated>(tag, at);
                    (selector, bits) = (gated.SlotSelector, gated.SlotBits);
                }
                else
                {
                    ref readonly var state = ref TagView.At<PerkWhileState>(tag, at);
                    (selector, bits) = (state.SlotSelector, state.SlotBits);
                }
                if (selector == bits && selector <= 1) Set("this_weapon_active", selector);
                else
                {
                    properties.RemoveAll(row => row.Key == "this_weapon_active");
                    properties.Add(Info("slot_test", "Slot test (selector, bits)", selector | bits << 8,
                        PerkBehaviorValueKind.Integer));
                }
                if (OtherStateTests(bytes, at)) properties.Add(Info("other_tests", "Other native tests (kept as is)", 1));
                break;
            }
            case "kill" or "damage" or "unequip":
                if (OtherFilters(tag, at, key)) properties.Add(Info("other_filters", "Other native filters (kept as is)", 1));
                break;
            case "ability-energy" or "driven-spawn":
            {
                if (!ReadExpression(bytes, at + ExpressionField(key), node)) return null;
                byte slotTable = key == "ability-energy" ? TagView.At<PerkAbilityEnergy>(tag, at).SlotTable : (byte)0;
                if (slotTable != 0)
                    properties.Add(Info("slot_table", "Slot table", slotTable, PerkBehaviorValueKind.Integer));
                break;
            }
            case "damage-multiplier":
                ReadDamageMultiplier(bytes, at, node);
                break;
        }
        if (key == "state")
        {
            int innerAt = InnerTarget(tag, at);
            var inner = ReadNode(bytes, innerAt, PerkBehaviorNodeKind.Condition, Role.Inner, id + "/0", diagnostics);
            node.Children.Add(inner);
            if (PrimitiveOf(StatePrefix + inner.Primitive) is { } spec)
                (node.Primitive, node.Title, node.Description) = (spec.Key, spec.Name, spec.Description);
            else if (inner.Primitive is not ("native" or "native-condition"))
            {
                node.Primitive = StatePrefix + inner.Primitive;
                node.Title = inner.Title + " while a state holds";
                node.Description = "The state test and the inner condition are editable. Both probabilities apply.";
            }
            else
            {
                node.Primitive = StatePrefix + "native";
                node.Title = "On a native event while a state holds";
                node.Description = "The state test is editable. The inner condition is preserved.";
            }
        }
        if (key == "counter" && !ReadRows(bytes, at, node, id, diagnostics)) return null;
        if (SpecOf(key) is { } table && !ReadSpec(bytes, at, table, node, id, diagnostics)) return null;
        foreach (var property in properties)
        {
            property.Minimum = Math.Min(property.Minimum, property.Value);
            property.Maximum = Math.Max(property.Maximum, property.Value);
        }
        return node;
    }

    /** Event targets, restore mode 1, child masks and expressions other than a constant are kept as installed. */
    private static void ReadProperty(ReadOnlyMemory<byte> bytes, int at, PerkBehaviorNode node)
    {
        var tag = bytes.Span;
        ref readonly var view = ref TagView.At<PerkProperty>(tag, at);
        var properties = node.Properties;
        PerkBehaviorProperty Get(string key) => properties.First(row => row.Key == key);
        var target = Get("target");
        target.Value = view.Target;
        target.Editable = target.Value <= 1;
        Get("hash").Value = view.PropertyHash;
        Get("operation").Value = view.Operation;
        byte restore = view.RestoreMode;
        if (restore is 0 or 2) Get("restore").Value = restore == 2 ? 1 : 0;
        else
        {
            properties.Remove(Get("restore"));
            properties.Add(Info("restore_mode", "Restore mode (kept as is)", restore, PerkBehaviorValueKind.Integer));
        }
        int constant = ConstantLocation(bytes, TagView.OffsetOf(tag, in view.HC6e92962.H45dee76f));
        if (constant >= 0) Get("value").Value = TagView.At<ExpressionConstant>(tag, constant).Value.X;
        else
        {
            properties.Remove(Get("value"));
            properties.Add(Info("value_expression", "Value: input expression (kept as is)", 1));
        }
        if (view.Unknown3 != 1 || view.Unknown4 != 0 || view.Unknown72 != 0)
            properties.Add(Info("other", "Other native settings (kept as is)", 1));
    }

    /** OwnsStateRecord must equal the state-record rule, or the node stays native. */
    private static string? ConditionKey(ReadOnlySpan<byte> tag, int at, uint type, Role role)
    {
        ref readonly var header = ref ConditionHeader(tag, at);
        if (header.ConditionMarkerFf != byte.MaxValue) return null;
        byte opcode = header.Opcode, owner = header.OwnsStateRecord;
        string? key = type switch
        {
            AlwaysClass when opcode == 0 => "always",
            TimerClass when opcode == 1 && TagView.Fits<PerkTimer>(tag, at) => "timer",
            EventClass when opcode == 30 && TagView.Fits<PerkEvent>(tag, at) => "event",
            UnequipClass when opcode == 17 && TagView.Fits<PerkUnequip>(tag, at) => "unequip",
            KillClass when opcode == 2 && TagView.Fits<PerkKill>(tag, at) => "kill",
            DamageClass when opcode == 4 && TagView.Fits<PerkDamage>(tag, at) => "damage",
            StateClass when opcode == 20 && TagView.Fits<PerkWhileState>(tag, at) => "while-state",
            GatedClass when opcode == 35 && TagView.Fits<PerkStateGated>(tag, at)
                && TagView.At<PerkStateGated>(tag, at).MustBeZero == 0 && InnerTarget(tag, at) >= 4 => "state",
            CounterClass when opcode == 26 && TagView.Fits<PerkCounter>(tag, at)
                && TagArray.TryChecked(tag, TagView.OffsetOf(tag, in TagView.At<PerkCounter>(tag, at).Rows), CounterRowClass,
                    Unsafe.SizeOf<PerkCounterRow>(), out _) => "counter",
            _ => SpecOf(PerkBehaviorNodeKind.Condition, type, opcode) is { } spec && spec.Size >= 0
                && at <= tag.Length - spec.Size ? spec.Key : null
        };
        if (key is null) return null;
        bool owns = role is not (Role.Inner or Role.Op32) && key switch
        {
            "while-state" => TagView.At<PerkWhileState>(tag, at).Property != EmptyHash,
            "state" => TagView.At<PerkStateGated>(tag, at).Property != EmptyHash,
            "damage" => TagView.At<PerkDamage>(tag, at).StateRecord.Relative != 0,
            _ => false
        };
        return owner == (owns ? 1 : 0) ? key : null;
    }

    /** A class whose own tests fail is looked up in the spec table. */
    private static string? EffectKey(ReadOnlySpan<byte> tag, int at, uint type)
    {
        switch (type)
        {
            case PropertyClass when TagView.Fits<PerkProperty>(tag, at):
            {
                ref readonly var node = ref TagView.At<PerkProperty>(tag, at);
                if (node.Opcode == 10 && node.StateFlag == 1 && node.Target <= 3 && node.Operation <= 3) return "property";
                break;
            }
            case AmmoDropClass when TagView.Fits<PerkAmmoDrop>(tag, at):
            {
                ref readonly var node = ref TagView.At<PerkAmmoDrop>(tag, at);
                if (node.Opcode == 11 && node.StateFlag == 0 && node.MustBeZero4 == 0 && node.MustBeZero8 == 0)
                    return "ammo-drop";
                break;
            }
            case SpawnClass when TagView.Fits<PerkSpawn>(tag, at):
            {
                ref readonly var node = ref TagView.At<PerkSpawn>(tag, at);
                if (node.Opcode == 1 && node.StateFlag == 1 && node.Target <= 3) return "spawn";
                break;
            }
            case DrivenSpawnClass when TagView.Fits<PerkDrivenSpawn>(tag, at):
            {
                ref readonly var node = ref TagView.At<PerkDrivenSpawn>(tag, at);
                if (node.Opcode == 2 && node.StateFlag == 1 && node.Target <= 3) return "driven-spawn";
                break;
            }
            case EnergyClass when TagView.Fits<PerkAbilityEnergy>(tag, at):
            {
                ref readonly var node = ref TagView.At<PerkAbilityEnergy>(tag, at);
                if (node.Opcode == 8 && node.StateFlag == 0 && node.Slot <= 7 && node.Gate <= 2) return "ability-energy";
                break;
            }
            case DamageScaleClass when TagView.Fits<PerkDamageMultiplier>(tag, at):
            {
                ref readonly var node = ref TagView.At<PerkDamageMultiplier>(tag, at);
                if (node.Opcode == 40 && node.StateFlag == 0 && node.MustBeZero2 == 0 && node.MustBeZero4 == 0)
                    return "damage-multiplier";
                break;
            }
            case CounterEffectClass when TagView.Fits<PerkSetCounter>(tag, at):
            {
                ref readonly var node = ref TagView.At<PerkSetCounter>(tag, at);
                if (node.Opcode == 42 && node.StateFlag == 0 && node.Mode <= 1 && node.MustBeZero == 0) return "perk-counter";
                break;
            }
            case TransmatClass when TagView.Fits<PerkTransmat>(tag, at):
                if (TagView.At<PerkTransmat>(tag, at).Opcode47Opcode == 47) return "transmat";
                break;
        }
        return SpecOf(PerkBehaviorNodeKind.Effect, type, TagRead.U8(tag, at + EffectOpcode)) is { } spec && spec.Size >= 0
            && at <= tag.Length - spec.Size
            && (spec.Size <= EffectStateFlag || TagRead.U8(tag, at + EffectStateFlag) == StateFlag(spec.Opcode)) ? spec.Key : null;
    }

    /** Rows keep their native order; a row's own inputs join its condition's inputs. */
    private static bool ReadRows(ReadOnlyMemory<byte> bytes, int at, PerkBehaviorNode node, string id, List<string> diagnostics)
    {
        var tag = bytes.Span;
        var rows = TagView.CheckedRows(tag, in TagView.At<PerkCounter>(tag, at).Rows, CounterRowClass);
        var offsets = TagView.RowOffsets(tag, rows);
        for (int i = 0; i < rows.Length; i++)
        {
            ref readonly var row = ref rows[i];
            int childAt = TagPointer.TryChecked(tag, TagView.OffsetOf(tag, in row.CounterTrigger));
            if (childAt < 4) return false;
            var child = ReadNode(bytes, childAt, PerkBehaviorNodeKind.Condition, Role.Row, $"{id}/{i}", diagnostics);
            child.Editable = true;
            var inputs = RowInputs();
            child.Properties.AddRange(inputs);
            if (!ReadFields(bytes, offsets[i], Fields["row"], child)) return false;
            if (row.RowMode > 2) inputs[0].Editable = false;
            if (row.Unknown9 != 0 || row.Unknown20 != 0 || row.Unknown24 != 0 || row.Unknown28 != 0 || row.Unknown10 != 0)
                child.Properties.Add(Info("row_extra", "Row: other settings (kept as is)", 1));
            foreach (var property in inputs)
            {
                property.Minimum = Math.Min(property.Minimum, property.Value);
                property.Maximum = Math.Max(property.Maximum, property.Value);
            }
            node.Children.Add(child);
        }
        return true;
    }

    private static void ReadDamageMultiplier(ReadOnlyMemory<byte> bytes, int at, PerkBehaviorNode node)
    {
        var tag = bytes.Span;
        ref readonly var view = ref TagView.At<PerkDamageMultiplier>(tag, at);
        int child = TagPointer.TryChecked(tag, TagView.OffsetOf(tag, in view.Bonus));
        uint type = child >= 4 && TagView.Fits<PerkDamageBonusConstant>(tag, child)
            ? TagView.TargetClass(tag, in view.Bonus) ?? 0 : 0;
        if (type == BonusClass)
        {
            ref readonly var bonus = ref TagView.At<PerkDamageBonusConstant>(tag, child);
            node.Properties.First(row => row.Key == "bonus").Value = bonus.Bonus;
            node.Properties.First(row => row.Key == "player_bonus").Value = bonus.PlayerBonus;
        }
        else
        {
            node.Properties.RemoveAll(row => row.Key is "bonus" or "player_bonus");
            node.Properties.Add(Choices(Info("bonus_source", "Bonus source", type switch
            {
                0 => 0, BonusExpressionClass => 2, BonusOwnerClass => 3, _ => 4
            }, PerkBehaviorValueKind.Integer), (0, "None"), (2, "Input expression"), (3, "Owner value"), (4, "Other")));
        }
        if (!AllFiltersPass(tag, at)) node.Properties.Add(Info("filtered", "Other native filters (kept as is)", 1));
        int stride = Unsafe.SizeOf<PerkDamageValueRow>();
        var tables = new[]
        {
            (TagView.OffsetOf(tag, in view.SetDamageValues), "Set"), (TagView.OffsetOf(tag, in view.ScaleDamageValues), "Scale")
        };
        foreach (var (field, name) in tables)
        {
            if (!TagArray.TryChecked(tag, field, DamageRowClass, stride, out var rows)) continue;
            for (int i = 0; i < rows.Count; i++)
            {
                ref readonly var row = ref TagView.At<PerkDamageValueRow>(tag, rows.Data + i * stride);
                node.Properties.Add(Info($"{name.ToLowerInvariant()}_{i}",
                    $"{name} damage value {i + 1} (index {(sbyte)row.Index})", row.Value, PerkBehaviorValueKind.Float));
            }
        }
    }

    /** Every filter other than the editable label filters is empty or off. */
    private static bool AllFiltersPass(ReadOnlySpan<byte> tag, int at)
    {
        ref readonly var view = ref TagView.At<PerkDamageMultiplier>(tag, at);
        return view.HBa690325.RelationFilterKind == 0x20 && view.H4b1eab13.Unknown156 == 0 && view.H4b1eab13.Unknown177 == 1
            && view.Unknown328 == 0 && view.HBa690325.FiltersInline.Filters.Count == 0 && view.H1862ac0e.Filter40.Count == 0
            && view.H1862ac0e.Filter56.Count == 0 && view.SetDamageValues.Count == 0 && view.ScaleDamageValues.Count == 0;
    }

    /** True when an installed filter other than a label filter differs from the form Studio writes. */
    private static bool OtherFilters(ReadOnlySpan<byte> tag, int at, string key)
    {
        if (key == "unequip") return false;
        if (key == "kill")
        {
            ref readonly var kill = ref TagView.At<PerkKill>(tag, at);
            return kill.HBd96c334.AnyDamageType != 1 || kill.HBd96c334.SkipExcludeMask != 1 || kill.Unknown168 != 0
                || kill.Unknown322 != 0 || kill.Unknown324 != EmptyHash || kill.Unknown328 != EmptyHash || kill.Unknown332 != 0
                || BitConverter.SingleToUInt32Bits(kill.Unknown340) != 0xBF800000 || kill.HBd96c334.Unknown288 != 0
                || kill.HBd96c334.Unknown292 != 0;
        }
        ref readonly var damage = ref TagView.At<PerkDamage>(tag, at);
        return damage.H4b1eab13.Unknown144 != 1 || damage.H4b1eab13.Unknown145 != 1
            || damage.Unknown156 != EmptyHash || damage.Unknown165 != 0 || damage.Unknown166 != 0
            || damage.Unknown167 != 0 || damage.H8e428e9f.Filters.Count != 0 || damage.Unknown192 != 0
            || damage.StateRecord.Relative != 0 || damage.H4b1eab13.Unknown120 != 0 || damage.H4b1eab13.Unknown124 != 0;
    }

    /** True when the body differs from the template outside the header, the slot test, the state fields and Invert. */
    private static bool OtherStateTests(ReadOnlyMemory<byte> bytes, int at)
    {
        var template = StateTemplate();
        var span = bytes.Span.Slice(at, template.Length);
        ref readonly var body = ref TagView.At<PerkWhileState>(template);
        int slot = TagView.OffsetOf(template, in body.SlotSelector);
        int slotEnd = TagView.OffsetOf(template, in body.SlotBits) + sizeof(byte);
        int property = TagView.OffsetOf(template, in body.Property);
        int holdEnd = TagView.OffsetOf(template, in body.Hold) + sizeof(float);
        int invert = TagView.OffsetOf(template, in body.Invert);
        int invertEnd = invert + sizeof(byte);
        return !Same(span, template, StateBodyStart, slot) || !Same(span, template, slotEnd, property)
            || !Same(span, template, holdEnd, invert) || !Same(span, template, invertEnd, template.Length);

        static bool Same(ReadOnlySpan<byte> span, byte[] template, int from, int to) =>
            span[from..to].SequenceEqual(template.AsSpan(from, to - from));
    }

    /** Native defaults of the while-state body: every range is open and every optional test is off. */
    private static byte[] StateTemplate()
    {
        byte[] bytes = new byte[Unsafe.SizeOf<PerkWhileState>()];
        ref var body = ref TagView.Edit<PerkWhileState>(bytes);
        body.Range12 = body.Range28 = body.Range36 = body.Range44 = 1;
        body.Range136 = body.Range144 = body.Range152 = body.Range160 = body.Range168 = body.Range176 = body.Range184 = 1;
        body.Minimum = body.Maximum = 1;
        body.Range16 = body.Range20 = -1;
        body.Unknown52 = 32;
        // Every installed node holds 0x20 here.
        body.H9b8b27d3.RelationFilterKind = 0x20;
        body.Unknown200 = NoTag;
        body.Property = EmptyHash;
        return bytes;
    }

    /** Only the constant and pass-through forms are editable; other programs stay read-only. */
    private static bool ReadExpression(ReadOnlyMemory<byte> bytes, int at, PerkBehaviorNode node)
    {
        var (form, constant) = Expression(bytes, at);
        var expression = node.Properties.First(row => row.Key == "expression");
        expression.Value = form;
        if (form == 2)
        {
            expression.Editable = false;
            expression.Choices.Add(new(2, "Other (read-only)"));
            node.Properties.RemoveAll(row => row.Key == "constant");
        }
        else node.Properties.First(row => row.Key == "constant").Value = constant;
        return true;
    }

    /** Returns 0 for a constant, 1 for pass-through, 2 for any other expression program. */
    private static (int Form, float Constant) Expression(ReadOnlyMemory<byte> bytes, int at)
    {
        var tag = bytes.Span;
        // The parent's pointer just before the expression must be null.
        int parent = at - Unsafe.SizeOf<RelativePointer<Unknown>>();
        if (parent < 0 || !TagView.Fits<SCompiledExpression>(tag, at)
            || !TagView.At<RelativePointer<Unknown>>(tag, parent).IsNull) return (2, 0);
        ref readonly var expression = ref TagView.At<SCompiledExpression>(tag, at);
        if (expression.Unknown56One != 1 || expression.Unknown64One != 1
            || !TagArray.TryChecked(tag, TagView.OffsetOf(tag, in expression.Bytecode), CodeClass,
                Unsafe.SizeOf<ExpressionCode>(), out var code)
            || !TagArray.TryChecked(tag, TagView.OffsetOf(tag, in expression.ConstantBuffer), ConstantClass,
                Unsafe.SizeOf<ExpressionConstant>(), out var constants)
            || code.Count != 4) return (2, 0);
        var program = tag.Slice(code.Data, 4);
        if (program.SequenceEqual(PassCode) && constants.Count == 0) return (1, 1);
        if (!program.SequenceEqual(ConstantCode) || constants.Count != 1) return (2, 0);
        var value = TagView.At<ExpressionConstant>(tag, constants.Data).Value;
        uint lane = BitConverter.SingleToUInt32Bits(value.X);
        float scalar = BitConverter.UInt32BitsToSingle(lane);
        if (!float.IsFinite(scalar) || BitConverter.SingleToUInt32Bits(value.Y) != lane
            || BitConverter.SingleToUInt32Bits(value.Z) != lane || BitConverter.SingleToUInt32Bits(value.W) != lane)
            return (2, 0);
        return (0, scalar);
    }

    /** Booleans must be 0 or 1 and floats finite, or the node stays native. */
    private static bool ReadFields(ReadOnlyMemory<byte> bytes, int at, Field[] fields, PerkBehaviorNode node)
    {
        var tag = bytes.Span;
        foreach (var field in fields)
        {
            var property = node.Properties.FirstOrDefault(row => row.Key == field.Key);
            if (property is null) continue;
            // Each arm is widened to double; a float arm would otherwise round the u32 hashes.
            double value = field.Width switch
            {
                Width.U8 => (double)TagRead.U8(tag, at + field.Offset),
                Width.I8 => (double)TagRead.I8(tag, at + field.Offset),
                Width.U32 => (double)TagRead.U32(tag, at + field.Offset),
                Width.I32 => (double)TagRead.I32(tag, at + field.Offset),
                _ => (double)F32(bytes, at + field.Offset)
            };
            if (!double.IsFinite(value) || property.Kind == PerkBehaviorValueKind.Boolean && value > 1) return false;
            property.Value = value;
        }
        return true;
    }

    private static void WriteFields(PerkWriter writer, int at, Field[] fields, PerkBehaviorNode node)
    {
        foreach (var field in fields)
        {
            var property = node.Properties.FirstOrDefault(row => row.Key == field.Key);
            if (property is null || !property.Editable) continue;
            switch (field.Width)
            {
                case Width.U8: writer.U8(at + field.Offset, checked((byte)property.Value)); break;
                case Width.I8: writer.U8(at + field.Offset, unchecked((byte)checked((sbyte)property.Value))); break;
                case Width.U32: writer.U32(at + field.Offset, checked((uint)property.Value)); break;
                case Width.I32: writer.U32(at + field.Offset, unchecked((uint)checked((int)property.Value))); break;
                default: writer.F32(at + field.Offset, (float)property.Value); break;
            }
        }
    }

    /** Existing nodes keep their payload; edited constants receive private storage. */
    private static void PatchNode(PerkWriter writer, ReadOnlyMemory<byte> original,
        PerkBehaviorNode node, int at, Role role)
    {
        if (node.Primitive == "native") return;
        string shape = Shape(node.Primitive);
        if (shape == GroupKey) return;
        // The writer starts as a copy of the original, so offsets read from the original hold for it.
        var tag = original.Span;
        if (node.Kind == PerkBehaviorNodeKind.Condition) writer.Edit<PerkAlways>(at).Probability = (float)Value(node, "probability");
        if (FieldsOf(shape) is { } fields) WriteFields(writer, at, fields, node);
        if (SpecOf(shape) is { } spec) PatchSpec(writer, original, node, at, spec);
        switch (shape)
        {
            case "timer":
                writer.F32(TagView.OffsetOf(tag, in TagView.At<PerkTimer>(tag, at).Seconds), (float)Value(node, "seconds"));
                break;
            case "property":
            {
                ref readonly var view = ref TagView.At<PerkProperty>(tag, at);
                writer.U8(TagView.OffsetOf(tag, in view.Target), (byte)Value(node, "target"));
                writer.U32(TagView.OffsetOf(tag, in view.PropertyHash), (uint)Value(node, "hash"));
                writer.U8(TagView.OffsetOf(tag, in view.Operation), (byte)Value(node, "operation"));
                if (node.Properties.Any(row => row.Key == "restore"))
                    writer.U8(TagView.OffsetOf(tag, in view.RestoreMode), Value(node, "restore") == 0 ? (byte)0 : (byte)2);
                if (node.Properties.All(row => row.Key != "value")) break;
                ref readonly var expression = ref view.HC6e92962.H45dee76f;
                int constant = ConstantLocation(original, TagView.OffsetOf(tag, in expression));
                float value = (float)Value(node, "value");
                if (BitConverter.SingleToInt32Bits(TagView.At<ExpressionConstant>(tag, constant).Value.X)
                    != BitConverter.SingleToInt32Bits(value))
                {
                    constant = writer.Array(TagView.OffsetOf(tag, in expression.ConstantBuffer), ConstantClass, 1,
                        Unsafe.SizeOf<ExpressionConstant>());
                    writer.Edit<ExpressionConstant>(constant).Value = new Vector4(value);
                }
                break;
            }
            case "ammo-drop":
                WriteAmmoDrop(writer, node, at);
                break;
            case "while-state" or "state":
                WriteStateExtras(writer, node, at, role);
                break;
            case "ability-energy" or "driven-spawn":
                PatchExpression(writer, original, node, at + ExpressionField(shape));
                break;
            case "damage-multiplier" when node.Properties.Any(row => row.Key == "bonus"):
            {
                int child = TagPointer.TryChecked(tag, TagView.OffsetOf(tag, in TagView.At<PerkDamageMultiplier>(tag, at).Bonus));
                ref readonly var bonus = ref TagView.At<PerkDamageBonusConstant>(tag, child);
                writer.F32(TagView.OffsetOf(tag, in bonus.Bonus), (float)Value(node, "bonus"));
                writer.F32(TagView.OffsetOf(tag, in bonus.PlayerBonus), (float)Value(node, "player_bonus"));
                break;
            }
            case "counter":
            {
                var rows = TagView.RowOffsets(tag, TagView.CheckedRows(tag, in TagView.At<PerkCounter>(tag, at).Rows, CounterRowClass));
                for (int i = 0; i < rows.Length; i++)
                {
                    WriteFields(writer, rows[i], Fields["row"], node.Children[i]);
                    PatchNode(writer, original, node.Children[i], node.Children[i].OriginalOffset, Role.Row);
                }
                break;
            }
        }
        if (shape == "state") PatchNode(writer, original, node.Children[0], node.Children[0].OriginalOffset, Role.Inner);
        WriteLabelFilters(writer, original, node, at);
    }

    /** Fresh payloads use native defaults for every input the node does not expose. */
    private static int WriteNode(PerkWriter writer, PerkBehaviorNode node, Role role, ref int index)
    {
        string shape = Shape(node.Primitive);
        int size = shape switch
        {
            "always" => Unsafe.SizeOf<PerkAlways>(), "perk-counter" => Unsafe.SizeOf<PerkSetCounter>(),
            "transmat" => Unsafe.SizeOf<PerkTransmat>(), "timer" => Unsafe.SizeOf<PerkTimer>(),
            "event" => Unsafe.SizeOf<PerkEvent>(), "unequip" => Unsafe.SizeOf<PerkUnequip>(),
            "kill" => Unsafe.SizeOf<PerkKill>(), "damage" => Unsafe.SizeOf<PerkDamage>(),
            "while-state" => Unsafe.SizeOf<PerkWhileState>(), "state" => Unsafe.SizeOf<PerkStateGated>(),
            "counter" => Unsafe.SizeOf<PerkCounter>(), "property" => Unsafe.SizeOf<PerkProperty>(),
            "ammo-drop" => Unsafe.SizeOf<PerkAmmoDrop>(), "spawn" => Unsafe.SizeOf<PerkSpawn>(),
            "driven-spawn" => Unsafe.SizeOf<PerkDrivenSpawn>(), "ability-energy" => Unsafe.SizeOf<PerkAbilityEnergy>(),
            "damage-multiplier" => Unsafe.SizeOf<PerkDamageMultiplier>(),
            _ => SpecOf(shape)?.Size ?? throw new InvalidDataException("This native node has no writer.")
        };
        int at = writer.Node(node.NativeClass, size);
        int opcode = PrimitiveOf(node.Primitive)!.Opcode;
        if (node.Kind == PerkBehaviorNodeKind.Condition)
        {
            ref var header = ref writer.Edit<PerkAlways>(at);
            header.Probability = (float)Value(node, "probability");
            header.ConditionMarkerFf = byte.MaxValue;
            header.Opcode = checked((byte)opcode);
            // Extend-timers conditions take no index; every other condition takes the next free one.
            header.ConditionIndex = role == Role.Op32 ? byte.MaxValue : checked((byte)index++);
        }
        else
        {
            writer.U8(at + EffectOpcode, checked((byte)opcode));
            if (size > 1) writer.U8(at + EffectStateFlag, StateFlag(opcode));
        }
        switch (shape)
        {
            case "timer":
                writer.Edit<PerkTimer>(at).Seconds = (float)Value(node, "seconds");
                break;
            case "property":
            {
                ref var property = ref writer.Edit<PerkProperty>(at);
                property.StateFlag = 1;
                property.Target = (byte)Value(node, "target");
                property.Unknown3 = 1;
                property.PropertyHash = (uint)Value(node, "hash");
                WriteExpression(writer, at + TagView.FieldOffset<PerkProperty>("HC6e92962.H45dee76f"), 0,
                    (float)Value(node, "value"));
                // WriteExpression grows the bytes, so the view is taken again.
                property = ref writer.Edit<PerkProperty>(at);
                property.HC6e92962.H45dee76f.Unknown56One = 1;
                property.HC6e92962.H45dee76f.Unknown64One = 1;
                property.Operation = (byte)Value(node, "operation");
                property.RestoreMode = Value(node, "restore") == 0 ? (byte)0 : (byte)2;
                break;
            }
            case "ammo-drop":
                WriteAmmoDrop(writer, node, at);
                break;
            case "unequip":
            {
                ref var unequip = ref writer.Edit<PerkUnequip>(at);
                unequip.HCd4f0043.ItemLabelTable = NoTag;
                unequip.H9e7302e4.ItemFilterKind = byte.MaxValue;
                break;
            }
            case "kill":
            {
                ref var kill = ref writer.Edit<PerkKill>(at);
                kill.HB3c84afa.ItemLabelTable = NoTag;
                kill.H8e428e9f.ItemFilterKind = byte.MaxValue;
                kill.HDcf12b13.H3c11ad32.ItemLabelTable = NoTag;
                kill.HBd96c334.H3c11ad32.ItemFilterKind = byte.MaxValue;
                // Any damage type, and skip the exclude mask; the zero masks alone never pass.
                kill.HBd96c334.AnyDamageType = 1;
                kill.HBd96c334.SkipExcludeMask = 1;
                kill.Unknown324 = EmptyHash;
                kill.Unknown328 = EmptyHash;
                kill.Unknown340 = -1;
                break;
            }
            case "damage":
            {
                ref var damage = ref writer.Edit<PerkDamage>(at);
                damage.H1862ac0e.H3c11ad32.ItemLabelTable = NoTag;
                damage.H4b1eab13.H3c11ad32.ItemFilterKind = byte.MaxValue;
                damage.H4b1eab13.Unknown144 = 1;
                damage.H4b1eab13.Unknown145 = 1;
                damage.Unknown156 = EmptyHash;
                break;
            }
            case "while-state" or "state":
                writer.Copy(at + StateBodyStart, StateTemplate()[StateBodyStart..]);
                break;
            case "spawn" or "driven-spawn":
                // Spawning effects keep a state slot for the spawned object.
                writer.U8(at + EffectStateFlag, 1);
                break;
            case "damage-multiplier":
            {
                writer.Edit<PerkDamageMultiplier>(at).HBa690325.RelationFilterKind = 0x20;
                int path = writer.String(LabelGlobalsPath);
                int filterPath = at + TagView.FieldOffset<PerkDamageMultiplier>("H1862ac0e.H3c11ad32.ItemLabelTablePath");
                int itemPath = at + TagView.FieldOffset<PerkDamageMultiplier>("H8e428e9f.ItemLabelTablePath");
                writer.I64(filterPath, path - filterPath);
                writer.I64(itemPath, path - itemPath);
                ref var multiplier = ref writer.Edit<PerkDamageMultiplier>(at);
                multiplier.H1862ac0e.H3c11ad32.ItemLabelTable = LabelGlobalsTag;
                multiplier.H8e428e9f.ItemLabelTable = LabelGlobalsTag;
                multiplier.H4b1eab13.H3c11ad32.ItemFilterKind = byte.MaxValue;
                multiplier.H4b1eab13.Unknown177 = 1;
                multiplier.H84675955.ItemFilterKind = byte.MaxValue;
                int bonus = writer.Node(BonusClass, Unsafe.SizeOf<PerkDamageBonusConstant>());
                ref var constant = ref writer.Edit<PerkDamageBonusConstant>(bonus);
                constant.Bonus = (float)Value(node, "bonus");
                constant.PlayerBonus = (float)Value(node, "player_bonus");
                int bonusField = at + TagView.FieldOffset<PerkDamageMultiplier>(nameof(PerkDamageMultiplier.Bonus));
                writer.I64(bonusField, bonus - bonusField);
                break;
            }
        }
        var spec = SpecOf(shape);
        if (spec is not null) WriteSpecDefaults(writer, at, spec);
        if (FieldsOf(shape) is { } fields) WriteFields(writer, at, fields, node);
        if (spec is not null) WriteSpecParts(writer, node, at, spec, ref index);
        if (shape is "while-state" or "state") WriteStateExtras(writer, node, at, role);
        if (shape is "ability-energy" or "driven-spawn")
        {
            int expression = at + ExpressionField(shape);
            WriteExpression(writer, expression, (int)Value(node, "expression"), (float)Value(node, "constant"));
            ref var compiled = ref writer.Edit<SCompiledExpression>(expression);
            compiled.Unknown56One = 1;
            compiled.Unknown64One = 1;
        }
        if (shape == "state")
        {
            int inner = WriteNode(writer, node.Children[0], Role.Inner, ref index);
            int innerField = at + TagView.FieldOffset<PerkStateGated>(nameof(PerkStateGated.InnerCondition));
            writer.I64(innerField, inner - innerField);
        }
        if (shape == "counter")
        {
            int stride = Unsafe.SizeOf<PerkCounterRow>();
            int rows = writer.Array(at + TagView.FieldOffset<PerkCounter>(nameof(PerkCounter.Rows)), CounterRowClass,
                node.Children.Count, stride);
            for (int i = 0; i < node.Children.Count; i++)
            {
                int row = rows + i * stride;
                int child = WriteNode(writer, node.Children[i], Role.Row, ref index);
                int trigger = row + TagView.FieldOffset<PerkCounterRow>(nameof(PerkCounterRow.CounterTrigger));
                writer.I64(trigger, child - trigger);
                WriteFields(writer, row, Fields["row"], node.Children[i]);
            }
            ulong mask = node.Children.Aggregate(0UL, (mask, child) => mask | Bits(child));
            writer.Edit<PerkCounter>(at).RowEventMask = mask;
        }
        WriteLabelFilters(writer, null, node, at);
        return at;
    }

    /** OwnsStateRecord marks a condition that owns a state record; an inner condition never does. */
    private static void WriteStateExtras(PerkWriter writer, PerkBehaviorNode node, int at, Role role)
    {
        bool owns = role is not (Role.Inner or Role.Op32) && (uint)Value(node, "property") != EmptyHash;
        // A gated condition starts with the while-state body.
        ref var state = ref writer.Edit<PerkWhileState>(at);
        state.OwnsStateRecord = owns ? (byte)1 : (byte)0;
        if (node.Properties.FirstOrDefault(row => row.Key == "this_weapon_active") is { } active)
        {
            // Selector 1 is this perk's slot; bit 0 requires that slot's weapon to be active.
            state.SlotSelector = (byte)active.Value;
            state.SlotBits = (byte)active.Value;
        }
    }

    /** New programs are appended, so a shared program is never changed in place. */
    private static void WriteExpression(PerkWriter writer, int at, int form, float constant)
    {
        int code = writer.Array(at + TagView.FieldOffset<SCompiledExpression>(nameof(SCompiledExpression.Bytecode)),
            CodeClass, 4, Unsafe.SizeOf<ExpressionCode>());
        writer.Copy(code, form == 0 ? ConstantCode : PassCode);
        int values = writer.Array(at + TagView.FieldOffset<SCompiledExpression>(nameof(SCompiledExpression.ConstantBuffer)),
            ConstantClass, form == 0 ? 1 : 0, Unsafe.SizeOf<ExpressionConstant>());
        if (form == 0) writer.Edit<ExpressionConstant>(values).Value = new Vector4(constant);
    }

    private static void WriteAmmoDrop(PerkWriter writer, PerkBehaviorNode node, int at)
    {
        var expected = Inputs("ammo-drop", PerkBehaviorNodeKind.Effect);
        float Input(int i) => (float)Value(node, expected[i].Key);
        ref var drop = ref writer.Edit<PerkAmmoDrop>(at);
        drop.Opcode = 11;
        (drop.PrimaryInterval, drop.PrimaryChance, drop.PrimaryRepeat) = (Input(0), Input(1), Input(2));
        (drop.SpecialInterval, drop.SpecialChance, drop.SpecialRepeat) = (Input(3), Input(4), Input(5));
        (drop.HeavyInterval, drop.HeavyChance, drop.HeavyRepeat) = (Input(6), Input(7), Input(8));
    }

    /** Native evaluator overrides and multi-instruction expressions remain read-only here. */
    private static int ConstantLocation(ReadOnlyMemory<byte> bytes, int expression)
    {
        var tag = bytes.Span;
        if (!TagView.Fits<SCompiledExpression>(tag, expression)) return -1;
        ref readonly var view = ref TagView.At<SCompiledExpression>(tag, expression);
        if (view.EvaluatorOverride2c != 0) return -1;
        var code = TagArray.Checked(tag, TagView.OffsetOf(tag, in view.Bytecode), CodeClass, Unsafe.SizeOf<ExpressionCode>());
        var constants = TagArray.Checked(tag, TagView.OffsetOf(tag, in view.ConstantBuffer), ConstantClass,
            Unsafe.SizeOf<ExpressionConstant>());
        if (code.Count != 4 || constants.Count != 1
            || !tag.Slice(code.Data, 4).SequenceEqual(ConstantCode)) return -1;
        var value = TagView.At<ExpressionConstant>(tag, constants.Data).Value;
        float scalar = value.X;
        if (!float.IsFinite(scalar)) return -1;
        // Lanes match by bits, as in Expression, so 0.0 and -0.0 differ.
        uint lane = BitConverter.SingleToUInt32Bits(scalar);
        if (BitConverter.SingleToUInt32Bits(value.Y) != lane || BitConverter.SingleToUInt32Bits(value.Z) != lane
            || BitConverter.SingleToUInt32Bits(value.W) != lane) return -1;
        return constants.Data;
    }

    /** Where the compiled expression sits inside an ability-energy or driven-spawn payload. */
    private static int ExpressionField(string shape) => shape == "ability-energy"
        ? TagView.FieldOffset<PerkAbilityEnergy>("H018f4b40.H45dee76f")
        : TagView.FieldOffset<PerkDrivenSpawn>("H7217af80.H45dee76f");

    /** A gated condition's inner condition, or -1 when its pointer is empty or leaves the payload. */
    private static int InnerTarget(ReadOnlySpan<byte> tag, int at) =>
        TagPointer.TryChecked(tag, TagView.OffsetOf(tag, in TagView.At<PerkStateGated>(tag, at).InnerCondition));

    // TODO: use a registry view of the shared condition header; the registry has none yet, and PerkAlways is that header alone.
    private static ref readonly PerkAlways ConditionHeader(ReadOnlySpan<byte> tag, int at) =>
        ref TagView.At<PerkAlways>(tag, at);
    private static int ConditionHeaderSize => Unsafe.SizeOf<PerkAlways>();
    internal static int ConditionOpcode => TagView.FieldOffset<PerkAlways>(nameof(PerkAlways.Opcode));

    // TODO: use a registry view of the shared effect header; the registry has none yet, and PerkSwordGuard is that header alone.
    internal static int EffectOpcode => TagView.FieldOffset<PerkSwordGuard>(nameof(PerkSwordGuard.Opcode));
    private static int EffectStateFlag => TagView.FieldOffset<PerkSwordGuard>(nameof(PerkSwordGuard.StateFlag));

    /** The while-state body starts after the condition header. */
    private static int StateBodyStart =>
        TagView.FieldOffset<PerkWhileState>(nameof(PerkWhileState.ConditionIndex)) + sizeof(byte);

    private static PerkBehaviorProperty Info(string key, string label, double value,
        PerkBehaviorValueKind kind = PerkBehaviorValueKind.Boolean)
    {
        var property = Property(key, label, value, value, value, kind, "Read only; kept as installed.");
        property.Editable = false;
        return property;
    }

    private static float F32(ReadOnlyMemory<byte> bytes, int at) =>
        BinaryPrimitives.ReadSingleLittleEndian(bytes.Span[at..]);

    private static byte[] Ascii(string text) => [.. Encoding.ASCII.GetBytes(text), 0];
}
