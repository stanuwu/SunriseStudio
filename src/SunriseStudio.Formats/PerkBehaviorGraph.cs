namespace SunriseStudio.Formats;

public enum PerkBehaviorNodeKind { Condition, Effect }
/** A Label value is bit index * 2^32 + label hash; the bit is the hash's row in the label table. */
public enum PerkBehaviorValueKind { Float, Integer, Boolean, Tag, Hash, Label }
public sealed record PerkBehaviorChoice(double Value, string Label);

public sealed class PerkBehaviorProperty
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public PerkBehaviorValueKind Kind { get; set; }
    public double Value { get; set; }
    public double Minimum { get; set; }
    public double Maximum { get; set; } = uint.MaxValue;
    public string Description { get; set; } = "";
    public bool Editable { get; set; } = true;
    public List<PerkBehaviorChoice> Choices { get; set; } = [];
    /** Names an installed value list the value must come from; empty takes any value in range. */
    public string Picker { get; set; } = "";
}

public sealed class PerkBehaviorNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Primitive { get; set; } = "";
    public string Title { get; set; } = "";
    public PerkBehaviorNodeKind Kind { get; set; }
    public uint NativeClass { get; set; }
    public bool Editable { get; set; }
    public string Description { get; set; } = "";
    public List<PerkBehaviorProperty> Properties { get; set; } = [];
    public int OriginalOffset { get; set; } = -1;
    public byte[] NativeBytes { get; set; } = [];
    /** Nested conditions in native order: counter rows, groups and their members, or an inner condition. */
    public List<PerkBehaviorNode> Children { get; set; } = [];
    /** Label filters of the node's label sets, in native order. */
    public List<PerkLabelFilter> LabelFilters { get; set; } = [];
}

/** One label set: a value passes when it has any label of AnyOf, all of AllOf, none of NoneOf, and not all of NotAllOf. */
public sealed class PerkLabelFilter
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Editable { get; set; } = true;
    public List<uint> AnyOf { get; set; } = [];
    public List<uint> AllOf { get; set; } = [];
    public List<uint> NoneOf { get; set; } = [];
    public List<uint> NotAllOf { get; set; } = [];

    /** The four arrays in native order. */
    public List<uint>[] Arrays() => [AnyOf, AllOf, NoneOf, NotAllOf];
}

/** A HUD buff-tray entry shown while the perk is active. */
public sealed class PerkHudStatus
{
    /** Key into the HUD icon library and the status label strings. */
    public uint Label { get; set; }
    /** Shows the countdown of the perk's deactivation timer. */
    public bool ShowTimer { get; set; } = true;
}

/** Each phase is an ordered native array; no arbitrary execution links exist between phases. */
public sealed class PerkBehaviorGraph
{
    public int Version { get; set; } = 1;
    public PerkHudStatus? Hud { get; set; }
    public List<PerkBehaviorNode> Activation { get; set; } = [];
    public List<PerkBehaviorNode> Effects { get; set; } = [];
    public List<PerkBehaviorNode> Deactivation { get; set; } = [];
    public List<string> Diagnostics { get; set; } = [];
    public byte[] OriginalBytes { get; set; } = [];
    /** The program's own tag; damage-resist effects store it. 0 means unknown. */
    public uint ProgramTag { get; set; }
}

/** Opcode is the native dispatch index, the Opcode byte of the condition or effect header. */
public sealed record PerkBehaviorPrimitive(string Key, string Name, PerkBehaviorNodeKind Kind,
    uint NativeClass, string Description, bool CanCreate, int Opcode = -1);
