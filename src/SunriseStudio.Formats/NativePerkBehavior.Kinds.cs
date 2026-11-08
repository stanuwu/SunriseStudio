using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

public static partial class NativePerkBehavior
{
    private const uint AlwaysClass = PerkAlways.Class, TimerClass = PerkTimer.Class;
    private const uint KillClass = PerkKill.Class, DamageClass = PerkDamage.Class, UnequipClass = PerkUnequip.Class;
    private const uint StateClass = PerkWhileState.Class, GatedClass = PerkStateGated.Class, EventClass = PerkEvent.Class;
    private const uint CounterClass = PerkCounter.Class, CounterRowClass = PerkCounterRow.Class;
    private const uint PropertyClass = PerkProperty.Class, AmmoDropClass = PerkAmmoDrop.Class;
    private const uint SpawnClass = PerkSpawn.Class, DrivenSpawnClass = PerkDrivenSpawn.Class, EnergyClass = PerkAbilityEnergy.Class;
    private const uint DamageScaleClass = PerkDamageMultiplier.Class, CounterEffectClass = PerkSetCounter.Class;
    private const uint TransmatClass = PerkTransmat.Class;
    // Damage multiplier bonus children: constant pair, input expression, owner value.
    private const uint BonusClass = PerkDamageBonusConstant.Class, BonusExpressionClass = C80802f18.Class;
    private const uint BonusOwnerClass = C80802f19.Class;
    private const uint DamageRowClass = PerkDamageValueRow.Class;
    private const uint CodeClass = ExpressionCode.Class, ConstantClass = ExpressionConstant.Class, StringClass = SInlineString.Class;
    // Every installed damage multiplier names this label table in both label references.
    private const uint LabelGlobalsTag = PerkLabelTable.Tag;
    private const string LabelGlobalsPath = "content/common/native/sandbox/label_globals.label_globals.tft";
    private const uint EmptyHash = StringHash.Empty, NoTag = uint.MaxValue;
    private const string StatePrefix = "state-";
    private static readonly byte[] ConstantCode = [0x34, 0, 0x3E, 0], PassCode = [0x3C, 0, 0x3E, 0];

    // Groups holds op 31 group rows, Group a group's members, Op32 an extend-timers effect's conditions.
    private enum Role { Top, Row, Inner, Groups, Group, Op32 }
    private enum Width { U8, I8, U32, F32, I32 }
    private sealed record Field(string Key, int Offset, Width Width);

    /** Object property names that installed while-state conditions read; each is the FNV-1 hash. */
    public static IReadOnlyList<PerkBehaviorChoice> StateProperties { get; } =
    [
        new(EmptyHash, "None"),
        .. new[]
        {
            "super_active", "is_thermal", "is_arc", "is_void", "reloading", "nearby_enemy_count", "melee_energy",
            "equipped_item_magazine_fraction", "is_guarding_with_sword", "melee_overcharge_state"
        }.Select(name => new PerkBehaviorChoice(StringBankWriter.Hash(name), name))
    ];

    /** Scalar inputs at fixed payload offsets, keyed by node shape; row fields are relative to the row. */
    private static readonly Dictionary<string, Field[]> Fields = new(StringComparer.Ordinal)
    {
        ["event"] = [new("event", TagView.FieldOffset<PerkEvent>("EventHash"), Width.U32)],
        ["unequip"] = [new("this_weapon", TagView.FieldOffset<PerkUnequip>("ThisWeapon"), Width.U8),
            new("slots", TagView.FieldOffset<PerkUnequip>("Slots"), Width.U8)],
        ["kill"] = [new("slots", TagView.FieldOffset<PerkKill>("Slots"), Width.U8), new("this_weapon", TagView.FieldOffset<PerkKill>("ThisWeapon"), Width.U8),
            new("min_share", TagView.FieldOffset<PerkKill>("MinShare"), Width.F32)],
        ["damage"] = [new("slots", TagView.FieldOffset<PerkDamage>("Slots"), Width.U8),
            new("this_weapon", TagView.FieldOffset<PerkDamage>("ThisWeapon"), Width.U8), new("min_share", TagView.FieldOffset<PerkDamage>("MinShare"), Width.F32),
            new("positive_damage", TagView.FieldOffset<PerkDamage>("PositiveDamage"), Width.U8)],
        ["while-state"] = [new("property", TagView.FieldOffset<PerkWhileState>("Property"), Width.U32),
            new("minimum", TagView.FieldOffset<PerkWhileState>("Minimum"), Width.F32), new("maximum", TagView.FieldOffset<PerkWhileState>("Maximum"), Width.F32),
            new("hold", TagView.FieldOffset<PerkWhileState>("Hold"), Width.F32), new("invert", TagView.FieldOffset<PerkWhileState>("Invert"), Width.U8)],
        ["state"] = [new("property", TagView.FieldOffset<PerkStateGated>("Property"), Width.U32),
            new("minimum", TagView.FieldOffset<PerkStateGated>("Minimum"), Width.F32), new("maximum", TagView.FieldOffset<PerkStateGated>("Maximum"), Width.F32),
            new("invert", TagView.FieldOffset<PerkStateGated>("Invert"), Width.U8)],
        ["counter"] = [new("threshold", TagView.FieldOffset<PerkCounter>("Threshold"), Width.F32),
            new("reset", TagView.FieldOffset<PerkCounter>("Reset"), Width.F32), new("minimum", TagView.FieldOffset<PerkCounter>("Minimum"), Width.F32),
            new("maximum", TagView.FieldOffset<PerkCounter>("Maximum"), Width.F32)],
        ["row"] = [new("row_mode", TagView.FieldOffset<PerkCounterRow>("RowMode"), Width.U8),
            new("row_value", TagView.FieldOffset<PerkCounterRow>("RowValue"), Width.F32),
            new("row_window", TagView.FieldOffset<PerkCounterRow>("RowWindow"), Width.F32)],
        ["spawn"] = [new("target", TagView.FieldOffset<PerkSpawn>("Target"), Width.U8), new("definition", TagView.FieldOffset<PerkSpawn>("Definition"), Width.U32),
            new("keep", TagView.FieldOffset<PerkSpawn>("Keep"), Width.U32), new("removal", TagView.FieldOffset<PerkSpawn>("Removal"), Width.U32),
            new("counter_parameter", TagView.FieldOffset<PerkSpawn>("CounterParameter"), Width.U32)],
        ["driven-spawn"] = [new("target", TagView.FieldOffset<PerkDrivenSpawn>("Target"), Width.U8),
            new("definition", TagView.FieldOffset<PerkDrivenSpawn>("Definition"), Width.U32), new("input", TagView.FieldOffset<PerkDrivenSpawn>("Input"), Width.I8),
            new("normalize", TagView.FieldOffset<PerkDrivenSpawn>("Normalize"), Width.U8)],
        ["ability-energy"] = [new("slot", TagView.FieldOffset<PerkAbilityEnergy>("Slot"), Width.U8),
            new("gate", TagView.FieldOffset<PerkAbilityEnergy>("Gate"), Width.U8), new("amount", TagView.FieldOffset<PerkAbilityEnergy>("Amount"), Width.F32),
            new("cap", TagView.FieldOffset<PerkAbilityEnergy>("Cap"), Width.F32), new("input", TagView.FieldOffset<PerkAbilityEnergy>("Input"), Width.I8)],
        ["damage-multiplier"] = [new("elements", TagView.FieldOffset<PerkDamageMultiplier>("H4b1eab13.Elements"), Width.U32),
            new("any_element", TagView.FieldOffset<PerkDamageMultiplier>("H4b1eab13.AnyElement"), Width.U8),
            new("this_weapon", TagView.FieldOffset<PerkDamageMultiplier>("ThisWeapon"), Width.U8)],
        ["perk-counter"] = [new("mode", TagView.FieldOffset<PerkSetCounter>("Mode"), Width.U8), new("value", TagView.FieldOffset<PerkSetCounter>("Value"), Width.F32)],
        ["transmat"] = [new("key", TagView.FieldOffset<PerkTransmat>("EffectKey"), Width.U32)]
    };

    private static string Shape(string primitive) =>
        primitive.StartsWith(StatePrefix, StringComparison.Ordinal) ? "state" : primitive;

    private static PerkBehaviorNode Blank(string primitive, PerkBehaviorPrimitive spec) => new()
    {
        Primitive = primitive, Title = spec.Name, Kind = spec.Kind, NativeClass = spec.NativeClass,
        Editable = true, Description = spec.Description, Properties = Inputs(Shape(primitive), spec.Kind),
        LabelFilters = EmptyFilters(spec.NativeClass)
    };

    /** Inputs a new node exposes; installed nodes may add read-only details. */
    private static List<PerkBehaviorProperty> Inputs(string shape, PerkBehaviorNodeKind kind)
    {
        var rows = new List<PerkBehaviorProperty>();
        if (kind == PerkBehaviorNodeKind.Condition) rows.Add(Property("probability", "Probability", 1, 0, 1));
        switch (shape)
        {
            case "timer":
                rows.Add(Property("seconds", "Delay (seconds)", 3, 0, 3600));
                break;
            case "property":
                rows.Add(Choices(Property("target", "Target", 0, 0, 1, PerkBehaviorValueKind.Integer,
                    "0 selects the object carrying this perk. 1 selects its owner."), (0, "This object"), (1, "Owner")));
                rows.Add(Property("hash", "Property hash", 0, 1, uint.MaxValue - 1,
                    PerkBehaviorValueKind.Hash, "Select an authored property read by the weapon or its owner."));
                rows.Add(Property("value", "Value", 1, -1000000, 1000000));
                rows.Add(Choices(Property("operation", "Operation", 0, 0, 3, PerkBehaviorValueKind.Integer,
                    "0 sets the value. 1 adds it. 2 subtracts it. 3 multiplies it."),
                    (0, "Set"), (1, "Add"), (2, "Subtract"), (3, "Multiply")));
                rows.Add(Property("restore", "Restore on deactivation", 1, 0, 1, PerkBehaviorValueKind.Boolean));
                break;
            case "ammo-drop":
                foreach (string category in new[] { "primary", "special", "heavy" })
                {
                    string label = char.ToUpperInvariant(category[0]) + category[1..];
                    rows.Add(Property(category + "_interval", label + " interval reduction", 0, 0, 1,
                        description: "The native interval is multiplied by one minus this value."));
                    rows.Add(Property(category + "_chance", label + " chance scale bonus",
                        category == "primary" ? 0.25 : 0, 0, 4,
                        description: "The native chance is multiplied by one plus this value."));
                    rows.Add(Property(category + "_repeat", label + " repeat chance", 0, 0, 1,
                        description: "The native drop loop uses this chance for another allowed drop."));
                }
                break;
            case "event":
                rows.Add(Property("event", "Event hash", EmptyHash, 0, uint.MaxValue, PerkBehaviorValueKind.Hash,
                    "Pick a hash an installed perk uses. Which game event each hash is, is not known."));
                break;
            case "unequip":
                rows.Add(Property("this_weapon", "Only this weapon", 1, 0, 1, PerkBehaviorValueKind.Boolean,
                    "Passes only for the item carrying this perk."));
                rows.Add(Property("slots", "Slot mask", 0, 0, 15, PerkBehaviorValueKind.Integer,
                    "One bit per slot, 1 << slot. 0 accepts any slot."));
                break;
            case "kill":
            case "damage":
                rows.Add(Property("this_weapon", "Only this weapon", 1, 0, 1, PerkBehaviorValueKind.Boolean,
                    "Passes only for damage from the weapon carrying this perk."));
                rows.Add(Property("slots", "Weapon slot mask", 0, 0, 255, PerkBehaviorValueKind.Integer,
                    "One bit per weapon slot. 0 accepts any slot. Slot meanings are assumed."));
                rows.Add(Property("min_share", "Minimum damage share", 0, 0, 1,
                    description: "The owner's share of the damage the target took, from 0 to 1."));
                if (shape == "damage")
                    rows.Add(Property("positive_damage", "Damage above zero", 1, 0, 1, PerkBehaviorValueKind.Boolean,
                        "Passes only when the hit dealt damage."));
                break;
            case "while-state":
            case "state":
                rows.Add(new PerkBehaviorProperty
                {
                    Key = "property", Label = "Object property", Value = EmptyHash, Minimum = 0, Maximum = uint.MaxValue,
                    Kind = PerkBehaviorValueKind.Hash, Choices = [.. StateProperties],
                    Description = "A property of the object carrying the perk. None skips the range test."
                });
                rows.Add(Property("minimum", "Minimum", 1, -1000000, 1000000));
                rows.Add(Property("maximum", "Maximum", 1, -1000000, 1000000));
                if (shape == "while-state")
                    rows.Add(Property("hold", "Hold (seconds)", 0, 0, 3600,
                        description: "The test must stay true this long before the condition passes."));
                rows.Add(Property("invert", "Invert", 0, 0, 1, PerkBehaviorValueKind.Boolean,
                    "Passes when the test fails instead."));
                rows.Add(Property("this_weapon_active", "Only while this weapon is active", 0, 0, 1,
                    PerkBehaviorValueKind.Boolean));
                break;
            case "counter":
                rows.Add(Property("threshold", "Threshold", 3, -1000000, 1000000,
                    description: "Passes when the counter is at or above this value."));
                rows.Add(Property("reset", "Reset level", -1, -1, 1000000,
                    description: "The counter returns to 0 when it reaches this value. -1 turns it off."));
                rows.Add(Property("minimum", "Minimum", -9998, -1000000, 1000000));
                rows.Add(Property("maximum", "Maximum", 100, -1000000, 1000000));
                break;
            case "spawn":
            case "driven-spawn":
                rows.Add(Choices(Property("target", "Target", 0, 0, 3, PerkBehaviorValueKind.Integer,
                    "Event objects exist only when an event activated the perk."),
                    (0, "This object"), (1, "Owner"), (2, "Event object 1"), (3, "Event object 2")));
                rows.Add(Property("definition", "Object definition", 0, 0, uint.MaxValue, PerkBehaviorValueKind.Tag,
                    "An installed object definition (class 80809C0F). It decides what the effect does."));
                if (shape == "spawn")
                {
                    rows.Add(Property("keep", "Keep on removal (hash)", EmptyHash, 0, uint.MaxValue, PerkBehaviorValueKind.Hash,
                        "Empty (0x811C9DC5) ends the object when the perk ends. Any other value keeps it."));
                    rows.Add(Property("removal", "Removal message hash", EmptyHash, 0, uint.MaxValue, PerkBehaviorValueKind.Hash,
                        "Sent to the object when the perk ends. Empty sends nothing."));
                    rows.Add(Property("counter_parameter", "Counter parameter hash", EmptyHash, 0, uint.MaxValue,
                        PerkBehaviorValueKind.Hash, "Passes the perk counter to the object under this name. Empty passes nothing."));
                }
                else
                {
                    rows.AddRange(ExpressionInputs(1));
                    rows.Add(Property("normalize", "Divide by magazine size", 0, 0, 1, PerkBehaviorValueKind.Boolean,
                        "Used by the magazine inputs."));
                }
                break;
            case "ability-energy":
                rows.Add(Choices(Property("slot", "Ability", 0, 0, 7, PerkBehaviorValueKind.Integer,
                    "Slot meanings are assumed from perk names."),
                    (0, "Grenade"), (1, "Super"), (2, "Melee"), (7, "Class ability")));
                rows.Add(Choices(Property("gate", "Gate", 0, 0, 2, PerkBehaviorValueKind.Integer,
                    "Gate meanings are assumed."),
                    (0, "Always"), (1, "Only while the ability is not in use"), (2, "Only while the ability is in use")));
                rows.Add(Property("amount", "Amount", 0.1, -10, 10,
                    description: "Fraction of a full charge. Negative values drain."));
                rows.Add(Property("cap", "Cap", -1, -1, 10,
                    description: "The grant stops at this charge level. -1 turns the cap off."));
                rows.AddRange(ExpressionInputs(0));
                break;
            case "damage-multiplier":
                rows.Add(Property("bonus", "Bonus", 0.1, -10, 10,
                    description: "Damage is multiplied by one plus this value."));
                rows.Add(Property("player_bonus", "Bonus against players", 0, -10, 10,
                    description: "Used instead of Bonus when the target is a player."));
                rows.Add(Property("elements", "Element mask", 0, 0, uint.MaxValue, PerkBehaviorValueKind.Integer,
                    "2 Solar, 4 Arc, 8 Void (assumed). Used when Any element is off."));
                rows.Add(Property("any_element", "Any element", 1, 0, 1, PerkBehaviorValueKind.Boolean));
                rows.Add(Property("this_weapon", "Only this weapon", 1, 0, 1, PerkBehaviorValueKind.Boolean,
                    "Applies only to damage from the weapon carrying this perk."));
                break;
            case "perk-counter":
                rows.Add(Choices(Property("mode", "Mode", 1, 0, 1, PerkBehaviorValueKind.Integer), (0, "Add"), (1, "Set")));
                rows.Add(Property("value", "Value", 1, -1000000, 1000000));
                break;
            case "transmat":
                rows.Add(Property("key", "Effect key", 0, 0, uint.MaxValue, PerkBehaviorValueKind.Hash,
                    "A key from the installed transmat effect table."));
                break;
            case GroupKey:
                rows.Clear();
                rows.Add(Property("window", "Window (seconds)", 0, 0, 3600,
                    description: "A pass keeps the group passing this long. 0 needs a pass on the same event."));
                break;
            default:
                if (SpecOf(shape) is { } spec)
                    rows.AddRange(spec.Inputs.Select(input => new PerkBehaviorProperty
                    {
                        Key = input.Key, Label = input.Label, Value = input.Value, Minimum = input.Minimum,
                        Maximum = input.Maximum, Kind = input.Kind, Description = input.Description, Picker = input.Picker,
                        Choices = [.. (input.Choices ?? []).Select(choice => new PerkBehaviorChoice(choice.Value, choice.Label))]
                    }));
                break;
        }
        foreach (var row in rows)
            row.Picker = (shape, row.Key) switch
            {
                ("event", "event") => "event",
                ("spawn" or "driven-spawn", "definition") => "definitions",
                ("transmat", "key") => "transmat",
                _ => row.Picker
            };
        return rows;
    }

    private static List<PerkBehaviorProperty> ExpressionInputs(int form) =>
    [
        Choices(Property("expression", "Expression", form, 0, 1, PerkBehaviorValueKind.Integer,
            "Constant ignores the input. Pass through uses the input value."), (0, "Constant"), (1, "Pass input through")),
        Property("constant", "Constant", 1, -1000000, 1000000, description: "Used when Expression is Constant."),
        Choices(Property("input", "Input", form == 0 ? -1 : 0, -1, 12, PerkBehaviorValueKind.Integer,
            "Meanings other than the perk counter are assumed."),
            (-1, "None"), (0, "Perk counter"), (1, "Missing rounds"), (2, "Rounds in magazine"), (11, "Target value"))
    ];

    private static List<PerkBehaviorProperty> RowInputs() =>
    [
        Choices(Property("row_mode", "Row: on pass", 0, 0, 2, PerkBehaviorValueKind.Integer),
            (0, "Add"), (1, "Set"), (2, "Multiply")),
        Property("row_value", "Row: value", 1, -1000000, 1000000),
        Property("row_window", "Row: window (seconds)", 0, 0, 3600,
            description: "Each pass restarts the window. When it runs out the counter resets (assumed). 0 turns it off.")
    ];

    /** Checks a new node's inputs against the native rules the property ranges cannot express. */
    private static IEnumerable<string> Semantics(PerkBehaviorNode node)
    {
        double Get(string key) => node.Properties.FirstOrDefault(row => row.Key == key)?.Value ?? 0;
        switch (Shape(node.Primitive))
        {
            case "spawn" or "driven-spawn" when Get("definition") is 0 or NoTag:
                yield return $"{node.Title}: choose an object definition.";
                break;
            case "event" when Get("event") is 0 or EmptyHash:
                yield return $"{node.Title}: choose an event hash.";
                break;
            case "transmat" when Get("key") is 0 or EmptyHash:
                yield return $"{node.Title}: choose an effect key.";
                break;
            case "damage-multiplier" when Get("any_element") == 0 && Get("elements") == 0:
                yield return $"{node.Title}: choose an element or turn on Any element.";
                break;
            case "counter" when Get("minimum") > Get("maximum"):
                yield return $"{node.Title}: the minimum is above the maximum.";
                break;
        }
        if (SpecOf(node.Primitive) is { } kind)
            foreach (var input in kind.Inputs.Where(input => input.Required))
                if (Get(input.Key) is 0 or EmptyHash or NoTag) yield return $"{node.Title}: choose {input.Label}.";
        if (node.Properties.Any(row => row.Key == "any_type") && Get("any_type") == 0 && Get("types") == 0)
            yield return $"{node.Title}: choose a damage type or turn on Any damage type.";
        int children = Shape(node.Primitive) switch
        {
            "counter" or "all-groups" or GroupKey or "extend-timers" => -1, "state" => 1, _ => 0
        };
        if (children == -1 && node.Children.Count == 0) yield return $"{node.Title}: add at least one row.";
        if (children >= 0 && node.Children.Count != children) yield return $"{node.Title} has the wrong number of inner conditions.";
        // Group timers are looked up by group index, so timed groups come first.
        if (node.Primitive == "all-groups" && node.Children.SkipWhile(group => Window(group) > 0).Any(group => Window(group) > 0))
            yield return $"{node.Title}: put groups with a window first.";
        static double Window(PerkBehaviorNode group) => group.Properties.FirstOrDefault(row => row.Key == "window")?.Value ?? 0;
    }

    private static PerkBehaviorProperty Choices(PerkBehaviorProperty property, params (double Value, string Label)[] choices)
    {
        property.Choices = [.. choices.Select(choice => new PerkBehaviorChoice(choice.Value, choice.Label))];
        return property;
    }
}
