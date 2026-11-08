using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

public static partial class NativePerkBehavior
{
    private const uint AllGroupsClass = PerkAllGroups.Class, GroupClass = PerkConditionGroup.Class;
    private const uint ExtendTimersClass = PerkExtendTimers.Class, ResistClass = PerkDamageResist.Class;
    private const uint LabelRowClass = PerkLabelRow.Class, TypeListClass = C80806b02.Class;
    private const string GroupKey = "group";

    /** One scalar input at a fixed payload offset. Required inputs refuse 0 and the empty values. */
    private sealed record Input(string Key, string Label, int Offset, Width Width, double Value, double Minimum,
        double Maximum, PerkBehaviorValueKind Kind = PerkBehaviorValueKind.Float, string Description = "",
        (double Value, string Label)[]? Choices = null, string Picker = "", bool Required = false);

    /** A fixed byte value a new node writes; installed nodes that differ show a read-only note. */
    private sealed record Fixed(int Offset, Width Width, double Value);

    /** A payload part the generic code cannot write, with the payload offsets it reads; -1 when unused. */
    private sealed record Part(string Kind, int First = -1, int Second = -1);

    /**
     * A table-driven node kind. Parts name the payload parts the generic code cannot write.
     * Ignore lists byte ranges (offset, length) left out of the "other settings" comparison.
     * LabelSet is the offset of a label set whose reference copies the installed label table, or -1.
     */
    private sealed record Spec(string Key, string Name, PerkBehaviorNodeKind Kind, uint Class, int Opcode, int Size,
        string Description, Input[] Inputs, Fixed[]? Defaults = null, (int Offset, int Length)[]? Ignore = null,
        Part[]? Parts = null, bool CanCreate = true, int LabelSet = -1);

    /** Built on first use, so it never depends on the order of static initializers. */
    private static class SpecTable
    {
        public static readonly Spec[] All = Build();
        public static readonly Dictionary<string, Spec> ByKey = All.ToDictionary(spec => spec.Key, StringComparer.Ordinal);
        public static readonly Dictionary<string, Field[]> Fields = All.ToDictionary(spec => spec.Key,
            spec => spec.Inputs.Where(input => input.Offset >= 0).Select(input => new Field(input.Key, input.Offset, input.Width))
                .ToArray(), StringComparer.Ordinal);
    }

    private static Spec? SpecOf(string key) => SpecTable.ByKey.GetValueOrDefault(key);

    /** Fixed-offset inputs of a node shape, from the hand-written table or the spec table. */
    private static Field[]? FieldsOf(string shape) =>
        Fields.TryGetValue(shape, out var fields) ? fields : SpecTable.Fields.GetValueOrDefault(shape);

    private static Spec? SpecOf(PerkBehaviorNodeKind kind, uint type, int opcode) =>
        SpecTable.All.FirstOrDefault(spec => spec.Kind == kind && spec.Class == type && spec.Opcode == opcode);

    /** A spec whose payload is the view T; `type` is T's class. */
    private static Spec Node<T>(string key, string name, PerkBehaviorNodeKind kind, uint type, int opcode,
        string description, Input[] inputs, Fixed[]? Defaults = null, (int Offset, int Length)[]? Ignore = null,
        Part[]? Parts = null, int LabelSet = -1) where T : unmanaged =>
        new(key, name, kind, type, opcode, Unsafe.SizeOf<T>(), description, inputs, Defaults, Ignore, Parts,
            LabelSet: LabelSet);

    // --- Byte ranges of view members.
    private static int ArrayBytes => TagArray.FieldSize;
    private static int PointerBytes => Unsafe.SizeOf<RelativePointer<Unknown>>();

    private static (int Offset, int Length) Whole<T>(string member, int size) where T : unmanaged => (TagView.FieldOffset<T>(member), size);

    /** From the start of `first` to the end of `last`, a member of `lastSize` bytes. */
    private static (int Offset, int Length) Between<T>(string first, string last, int lastSize) where T : unmanaged
    {
        int start = TagView.FieldOffset<T>(first);
        return (start, TagView.FieldOffset<T>(last) + lastSize - start);
    }

    /** From the start of `first` to the end of T. */
    private static (int Offset, int Length) Tail<T>(string first) where T : unmanaged
    {
        int start = TagView.FieldOffset<T>(first);
        return (start, Unsafe.SizeOf<T>() - start);
    }

    /** The table path and table tag of the label set at `set`. */
    private static (int Offset, int Length) TableReference<T>(string set) where T : unmanaged =>
        Between<T>(set + ".ItemLabelTablePath", set + ".ItemLabelTable", sizeof(uint));

    /** A damage type list and the type mask read beside it. */
    private static Part Types<T>(string list, string mask) where T : unmanaged => new("types", TagView.FieldOffset<T>(list), TagView.FieldOffset<T>(mask));

    /** One label row array and the label bit mask. */
    private static Part Labels<T>() where T : unmanaged => new("labels", TagView.FieldOffset<T>("LabelRows"), TagView.FieldOffset<T>("LabelBitMask"));

    // --- Shared input shapes.
    private static Input Flag(string key, string label, int offset, double value = 0, string description = "") =>
        new(key, label, offset, Width.U8, value, 0, 1, PerkBehaviorValueKind.Boolean, description);

    private static Input Mask(string key, string label, int offset, double value, double maximum, string description) =>
        new(key, label, offset, Width.U8, value, 0, maximum, PerkBehaviorValueKind.Integer, description);

    private static Input Number(string key, string label, int offset, double value, double minimum, double maximum,
        string description = "") => new(key, label, offset, Width.F32, value, minimum, maximum, Description: description);

    private static Input HashInput(string key, string label, int offset, string picker, string description,
        bool required = true, (double, string)[]? choices = null) =>
        new(key, label, offset, Width.U32, EmptyHash, 0, uint.MaxValue, PerkBehaviorValueKind.Hash, description,
            choices, picker, required);

    private static Input TagInput(string key, string label, int offset, string picker, string description,
        bool required = true, (double, string)[]? choices = null) =>
        new(key, label, offset, Width.U32, NoTag, 0, uint.MaxValue, PerkBehaviorValueKind.Tag, description,
            choices, picker, required);

    private static Input Target(string key, string label, int offset, double value = 0) =>
        new(key, label, offset, Width.U8, value, 0, 3, PerkBehaviorValueKind.Integer,
            "Event objects exist only when an event activated the perk.", TargetChoices);

    // Properties, not fields: the spec table may be built before the other static fields.
    private static (double, string)[] TargetChoices =>
        [(0, "This object"), (1, "Owner"), (2, "Event object 1"), (3, "Event object 2")];
    private static (double, string)[] PositionChoices => [(0, "This object"), (1, "Event position")];

    /** Item conditions share one form: own item, slot mask, a label set and its filter kind. */
    private static Spec ItemCondition<T>(string key, string name, uint type, int opcode, string description, bool copyLabels,
        params Input[] extra) where T : unmanaged =>
        Node<T>(key, name, PerkBehaviorNodeKind.Condition, type, opcode, description,
            [Flag("this_weapon", "Only this item", TagView.FieldOffset<T>("ThisWeapon"), 1, "Passes only for the item carrying this perk."),
             .. (extra.Length == 0
                 ? new[] { Mask("slots", "Slot mask", TagView.FieldOffset<T>("Slots"), 0, 15, "One bit per slot. 0 accepts any slot.") }
                 : extra)],
            [new(TagView.FieldOffset<T>("HCd4f0043.ItemLabelTable"), Width.U32, NoTag), new(TagView.FieldOffset<T>("H9e7302e4.ItemFilterKind"), Width.U8, 0xFF)],
            [TableReference<T>("HCd4f0043")], LabelSet: copyLabels ? TagView.FieldOffset<T>("HCd4f0043") : -1);

    private static Spec[] Build() =>
    [
        ItemCondition<PerkItemActive>("item-active", "On item readied", PerkItemActive.Class, 16,
            "Passes when an item becomes active, for example when a weapon is drawn.", true),
        ItemCondition<PerkItemEquipped>("item-equipped", "On item equipped", PerkItemEquipped.Class, 14,
            "Passes when an item is equipped into a slot. A new perk also sees items already equipped.", true),
        ItemCondition<PerkItemUnequipped>("item-unequipped", "On item unequipped", PerkItemUnequipped.Class, 15,
            "Passes when an item is unequipped.", true),
        ItemCondition<PerkWeaponTick>("weapon-tick", "On weapon counter tick", PerkWeaponTick.Class, 13,
            "Passes when a weapon counter rises. What the counter counts is not known.", false),
        ItemCondition<PerkItemEvent>("item-event", "On item event", PerkItemEvent.Class, 18,
            "Passes on an item event per slot. Assumed: the weapon fired.", false),
        ItemCondition<PerkReload>("reload", "On reload", PerkReload.Class, 19,
            "Passes when the active weapon reloads (assumed from perk names).", true,
            Flag("allow_off", "Pass when the event flag is off", TagView.FieldOffset<PerkReload>("AllowOff"), 1),
            Flag("allow_on", "Pass when the event flag is on", TagView.FieldOffset<PerkReload>("AllowOn"), 0, "What the flag means is not known."),
            Mask("slots", "Slot mask", TagView.FieldOffset<PerkReload>("Slots"), 0, 15, "One bit per slot. 0 accepts any slot."),
            Number("kill_window", "Recent kill window (seconds)", TagView.FieldOffset<PerkReload>("KillWindow"), 0, 0, 3600,
                "Needs a kill with that slot's weapon this recently. 0 turns it off.")),
        Node<PerkShot>("shot", "On shot resolved", PerkBehaviorNodeKind.Condition, PerkShot.Class, 27,
            "Passes when a shot hits or misses.",
            [Flag("this_weapon", "Only this weapon", TagView.FieldOffset<PerkShot>("ThisWeapon"), 1),
             Mask("slots", "Slot mask", TagView.FieldOffset<PerkShot>("Slots"), 0, 15, "One bit per slot. 0 accepts any slot."),
             new("mode", "Result", TagView.FieldOffset<PerkShot>("Mode"), Width.U8, 0, 0, 3, PerkBehaviorValueKind.Integer,
                 "Hit with test uses a native check whose meaning is not known.",
                 [(0, "Any"), (1, "Miss"), (2, "Hit"), (3, "Hit with test")])],
            [new(TagView.FieldOffset<PerkShot>("H9e7302e4.ItemFilterKind"), Width.U8, 0xFF),
             new(TagView.FieldOffset<PerkShot>("H4779c18d.RelationFilter0x20"), Width.U32, 0x20)],
            [TableReference<PerkShot>("HCd4f0043")], LabelSet: TagView.FieldOffset<PerkShot>("HCd4f0043")),
        Node<PerkOwnerMessage>("owner-message", "On owner message", PerkBehaviorNodeKind.Condition, PerkOwnerMessage.Class, 32,
            "Passes on an owner message. Assumed: the owner died.", [],
            [new(TagView.FieldOffset<PerkOwnerMessage>("H0fa4246e.ItemLabelTable"), Width.U32, NoTag),
             new(TagView.FieldOffset<PerkOwnerMessage>("H22e9686e.ItemFilterKind"), Width.U8, 0xFF)],
            [TableReference<PerkOwnerMessage>("H0fa4246e")], LabelSet: TagView.FieldOffset<PerkOwnerMessage>("H0fa4246e")),
        Node<PerkAbilityUsed>("ability-used", "On ability used", PerkBehaviorNodeKind.Condition, PerkAbilityUsed.Class, 8,
            "Passes when an ability in the mask is used.",
            [new("abilities", "Ability mask", TagView.FieldOffset<PerkAbilityUsed>("Abilities"), Width.U8, 1, 1, 255, PerkBehaviorValueKind.Integer,
                "Bit 0 grenade, bit 1 super, bit 7 class ability (assumed).")]),
        Node<PerkAbilityStart>("ability-start", "On ability event 7", PerkBehaviorNodeKind.Condition, PerkAbilityStart.Class, 7,
            "Passes on ability message state 1 for an ability in the mask. The meaning is not known.",
            [new("abilities", "Ability mask", TagView.FieldOffset<PerkAbilityStart>("Abilities"), Width.U8, 2, 1, 255,
                PerkBehaviorValueKind.Integer, "Bit 0 grenade, bit 1 super, bit 2 melee, bit 7 class ability (assumed).")]),
        Node<PerkAbilityEnd>("ability-end", "On ability event 9", PerkBehaviorNodeKind.Condition, PerkAbilityEnd.Class, 9,
            "Passes on ability message state 2 for an ability in the mask. The meaning is not known.",
            [new("abilities", "Ability mask", TagView.FieldOffset<PerkAbilityEnd>("Abilities"), Width.U8, 2, 1, 255,
                PerkBehaviorValueKind.Integer, "Bit 0 grenade, bit 1 super, bit 2 melee, bit 7 class ability (assumed).")]),
        Node<PerkAmmoPickup>("ammo-pickup", "On ammo picked up", PerkBehaviorNodeKind.Condition, PerkAmmoPickup.Class, 6,
            "Passes when ammo is picked up for a weapon slot in the mask.",
            [new("slots", "Weapon slot mask", TagView.FieldOffset<PerkAmmoPickup>("Slots"), Width.U8, 7, 1, 7, PerkBehaviorValueKind.Integer,
                 "1 kinetic, 2 energy, 4 power (assumed)."),
             Mask("ammo", "Ammo type mask", TagView.FieldOffset<PerkAmmoPickup>("Ammo"), 0, 7,
                 "1 primary, 2 special, 4 heavy (assumed). 0 accepts any.")]),
        Node<PerkIncident>("incident", "On incident", PerkBehaviorNodeKind.Condition, PerkIncident.Class, 12,
            "Passes on a world incident of the chosen type. Whether it checks the owner is not known.",
            [HashInput("incident", "Incident type", TagView.FieldOffset<PerkIncident>("Incident"), "incident", "From the types installed perks use."),
             HashInput("role", "Role", TagView.FieldOffset<PerkIncident>("Role"), "role", "From the roles installed perks use.", false,
                 [(EmptyHash, "None")])]),
        Node<PerkEvent2>("event-2", "On gameplay event (second stream)", PerkBehaviorNodeKind.Condition, PerkEvent2.Class, 29,
            "Passes when the perk receives the chosen hash on the second event stream.",
            [HashInput("event", "Event hash", TagView.FieldOffset<PerkEvent2>("Event"), "event29", "From the hashes installed perks use.")]),
        Node<PerkKillCredit>("kill-credit", "On kill credit", PerkBehaviorNodeKind.Condition, PerkKillCredit.Class, 3,
            "Passes once for each listed player when a kill is credited.",
            [Number("min_share", "Minimum damage share", TagView.FieldOffset<PerkKillCredit>("MinShare"), 0, 0, 1),
             Number("max_share", "Maximum damage share", TagView.FieldOffset<PerkKillCredit>("MaxShare"), 1, 0, 1)],
            [new(TagView.FieldOffset<PerkKillCredit>("H89f64381.ItemLabelTable"), Width.U32, NoTag),
             new(TagView.FieldOffset<PerkKillCredit>("H10376382.ItemFilterKind"), Width.U8, 0xFF),
             new(TagView.FieldOffset<PerkKillCredit>("H242d8818.ItemLabelTable"), Width.U32, NoTag),
             new(TagView.FieldOffset<PerkKillCredit>("H976db95d.ItemFilterKind"), Width.U8, 0xFF),
             new(TagView.FieldOffset<PerkKillCredit>("MaxShare"), Width.F32, 1), new(TagView.FieldOffset<PerkKillCredit>("Unknown284"), Width.U32, EmptyHash)],
            [TableReference<PerkKillCredit>("H89f64381"), TableReference<PerkKillCredit>("H242d8818")]),
        Node<PerkOwnerDamaged>("owner-damaged", "On owner damaged", PerkBehaviorNodeKind.Condition, PerkOwnerDamaged.Class, 5,
            "Passes when the owner takes damage.",
            [Number("min_share", "Minimum damage share", TagView.FieldOffset<PerkOwnerDamaged>("MinShare"), 0, 0, 1),
             Number("distance", "Maximum attacker distance", TagView.FieldOffset<PerkOwnerDamaged>("Distance"), -1, -1, 100000,
                 "-1 turns the test off."),
             Flag("self_damage", "Allow the owner as attacker", TagView.FieldOffset<PerkOwnerDamaged>("SelfDamage")),
             Flag("attacker_gone", "Attacker must be gone", TagView.FieldOffset<PerkOwnerDamaged>("AttackerGone")),
             Mask("slots", "Slot mask", TagView.FieldOffset<PerkOwnerDamaged>("Slots"), 0, 255, "0 accepts any slot."),
             Mask("parts", "Damage part mask", TagView.FieldOffset<PerkOwnerDamaged>("Parts"), 0, 15,
                 "Bit i needs damage part i above zero. 0 turns it off."),
             new("types", "Damage types", TagView.FieldOffset<PerkOwnerDamaged>("H4b1eab13.Types"), Width.U32, 0, 0, 255,
                 PerkBehaviorValueKind.Integer,
                 "Bit per damage type: 2 Solar, 4 Arc, 8 Void (assumed). Used when Any damage type is off."),
             Flag("any_type", "Any damage type", TagView.FieldOffset<PerkOwnerDamaged>("H4b1eab13.AnyType"), 1)],
            [new(TagView.FieldOffset<PerkOwnerDamaged>("H1862ac0e.H3c11ad32.ItemLabelTable"), Width.U32, NoTag),
             new(TagView.FieldOffset<PerkOwnerDamaged>("H4b1eab13.H3c11ad32.ItemFilterKind"), Width.U8, 0xFF),
             new(TagView.FieldOffset<PerkOwnerDamaged>("H4b1eab13.Unknown145"), Width.U8, 1),
             new(TagView.FieldOffset<PerkOwnerDamaged>("Unknown160"), Width.U32, EmptyHash),
             new(TagView.FieldOffset<PerkOwnerDamaged>("Distance"), Width.F32, -1)],
            [TableReference<PerkOwnerDamaged>("H1862ac0e.H3c11ad32"), Whole<PerkOwnerDamaged>("H1862ac0e.Filter40", ArrayBytes)],
            [Types<PerkOwnerDamaged>("H1862ac0e.Filter40", "H4b1eab13.Types")]),
        Node<PerkAbilityGained>("ability-gained", "On ability gained", PerkBehaviorNodeKind.Condition, PerkAbilityGained.Class, 10,
            "Passes when the owner gains the chosen ability definition.",
            [TagInput("definition", "Ability definition", TagView.FieldOffset<PerkAbilityGained>("Definition"), "definitions",
                "An installed object definition.")],
            [new(TagView.FieldOffset<PerkAbilityGained>("Definition"), Width.U32, NoTag)]),
        Node<PerkAbilityLost>("ability-lost", "On ability lost", PerkBehaviorNodeKind.Condition, PerkAbilityLost.Class, 11,
            "Passes when the owner loses the chosen ability definition.",
            [TagInput("definition", "Ability definition", TagView.FieldOffset<PerkAbilityLost>("Definition"), "definitions",
                "An installed object definition.")],
            [new(TagView.FieldOffset<PerkAbilityLost>("Definition"), Width.U32, NoTag)]),
        Node<PerkCrouch>("crouch", "On crouch change", PerkBehaviorNodeKind.Condition, PerkCrouch.Class, 22,
            "Passes when a state flag changes to the chosen value. Assumed: crouching.",
            [Flag("state", "State", TagView.FieldOffset<PerkCrouch>("State"), 1)]),
        Node<PerkFlag25>("flag-25", "On flag 25 change", PerkBehaviorNodeKind.Condition, PerkFlag25.Class, 25,
            "Passes when flag 25 changes to the chosen value. The flag's meaning is not known.",
            [Flag("state", "State", TagView.FieldOffset<PerkFlag25>("State"), 1)]),
        Node<PerkFlag24>("flag-24", "On action state change", PerkBehaviorNodeKind.Condition, PerkFlag24.Class, 24,
            "Passes when an action state starts (true) or ends (false). Which actions raise it is not known.",
            [Flag("state", "State", TagView.FieldOffset<PerkFlag24>("State"), 1)]),
        Node<PerkPlayerKilled>("player-killed", "On another player killed", PerkBehaviorNodeKind.Condition,
            PerkPlayerKilled.Class, 21, "Raised for every other player when a player is killed. Filters on the damage type.",
            [new("types", "Damage types", TagView.FieldOffset<PerkPlayerKilled>("H3e6b544e.Types"), Width.U32, 0, 0, 255,
                 PerkBehaviorValueKind.Integer,
                 "Bit per damage type: 2 Solar, 4 Arc, 8 Void (assumed). Used when Any damage type is off."),
             Flag("any_type", "Any damage type", TagView.FieldOffset<PerkPlayerKilled>("H3e6b544e.AnyType"), 1),
             Flag("last_standing", "Only with no other living teammate", TagView.FieldOffset<PerkPlayerKilled>("LastStanding"), 0,
                 "Fails while another player of the owner's group is alive (assumed).")],
            [new(TagView.FieldOffset<PerkPlayerKilled>("H3d948b57.ItemLabelTable"), Width.U32, NoTag),
             new(TagView.FieldOffset<PerkPlayerKilled>("H4df062e8.ItemFilterKind"), Width.U8, 0xFF),
             new(TagView.FieldOffset<PerkPlayerKilled>("H242d8818.ItemLabelTable"), Width.U32, NoTag),
             new(TagView.FieldOffset<PerkPlayerKilled>("H976db95d.ItemFilterKind"), Width.U8, 0xFF),
             new(TagView.FieldOffset<PerkPlayerKilled>("HC5cda3cd.H3c11ad32.ItemLabelTable"), Width.U32, NoTag),
             new(TagView.FieldOffset<PerkPlayerKilled>("H3e6b544e.H3c11ad32.ItemFilterKind"), Width.U8, 0xFF),
             new(TagView.FieldOffset<PerkPlayerKilled>("H3e6b544e.Unknown337"), Width.U8, 1)],
            [TableReference<PerkPlayerKilled>("H3d948b57"), TableReference<PerkPlayerKilled>("H242d8818"),
             TableReference<PerkPlayerKilled>("HC5cda3cd.H3c11ad32"), Whole<PerkPlayerKilled>("HC5cda3cd.Filter40", ArrayBytes)],
            [Types<PerkPlayerKilled>("HC5cda3cd.Filter40", "H3e6b544e.Types")]),
        Node<PerkFlag36>("flag-36", "On flag 36 change", PerkBehaviorNodeKind.Condition, PerkFlag36.Class, 36,
            "Passes when flag 36 changes to the chosen value. The flag's meaning is not known.",
            [Flag("state", "State", TagView.FieldOffset<PerkFlag36>("State"), 1)]),
        Node<PerkAim>("aim", "On aim change", PerkBehaviorNodeKind.Condition, PerkAim.Class, 23,
            "Passes when aiming down sights starts or stops.",
            [Flag("state", "Aiming", TagView.FieldOffset<PerkAim>("State"), 1),
             Flag("grounded", "Not airborne", TagView.FieldOffset<PerkAim>("Grounded"), description: "Assumed meaning."),
             Flag("this_weapon", "Only this weapon", TagView.FieldOffset<PerkAim>("ThisWeapon"), 1)]),
        Node<PerkSpawned>("spawned", "On player spawned", PerkBehaviorNodeKind.Condition, PerkSpawned.Class, 28,
            "Passes when the player spawns for a reason in the mask.",
            [new("reasons", "Spawn reason mask", TagView.FieldOffset<PerkSpawned>("Reasons"), Width.U8, 0x3C, 1, 255,
                PerkBehaviorValueKind.Integer, "Bit per spawn reason. Reason meanings are not known.")]),
        Node<PerkAllGroups>("all-groups", "All groups pass", PerkBehaviorNodeKind.Condition, AllGroupsClass, 31,
            "Passes when every group passes. A group passes when any of its conditions passes, "
            + "or passed within its window.", [], Ignore: [Whole<PerkAllGroups>("Groups", ArrayBytes)],
            Parts: [new("groups")]),
        Node<PerkPickup>("pickup", "On object picked up", PerkBehaviorNodeKind.Condition, PerkPickup.Class, 34,
            "Passes when the player picks up an object.",
            [TagInput("definition", "Pickup definition", TagView.FieldOffset<PerkPickup>("Definition"), "definitions",
                 "Any accepts every pickup.", false, [(NoTag, "Any")]),
             Number("first_min", "First value minimum", TagView.FieldOffset<PerkPickup>("FirstMin"), 0, -1000000, 1000000),
             Number("first_max", "First value maximum", TagView.FieldOffset<PerkPickup>("FirstMax"), 1, -1000000, 1000000),
             Number("second_min", "Second value minimum", TagView.FieldOffset<PerkPickup>("SecondMin"), 0, -1000000, 1000000),
             Number("second_max", "Second value maximum", TagView.FieldOffset<PerkPickup>("SecondMax"), 1, -1000000, 1000000)],
            [new(TagView.FieldOffset<PerkPickup>("Definition"), Width.U32, NoTag), new(TagView.FieldOffset<PerkPickup>("FirstMax"), Width.F32, 1),
             new(TagView.FieldOffset<PerkPickup>("SecondMax"), Width.F32, 1)]),
        Node<PerkTrackedTimer>("tracked-timer", "On tracked entry timer", PerkBehaviorNodeKind.Condition,
            PerkTrackedTimer.Class, 37,
            "Passes when a tracked entry's timer runs out. Needs a Register a tracked target effect with the same name.",
            [HashInput("name", "Tracked name", TagView.FieldOffset<PerkTrackedTimer>("Name"), "tracked", "From the names installed perks use.")]),
        Node<PerkTrackedGone>("tracked-gone", "On tracked object gone", PerkBehaviorNodeKind.Condition,
            PerkTrackedGone.Class, 38,
            "Passes when a tracked object no longer exists and is far enough away. Needs a Register a tracked "
            + "target effect with the same name.",
            [HashInput("name", "Tracked name", TagView.FieldOffset<PerkTrackedGone>("Name"), "tracked", "From the names installed perks use."),
             Number("distance", "Minimum distance", TagView.FieldOffset<PerkTrackedGone>("Distance"), 100, 0, 100000)],
            [new(TagView.FieldOffset<PerkTrackedGone>("Name"), Width.U32, EmptyHash)]),
        Node<PerkMessage>("message", "On hashed message", PerkBehaviorNodeKind.Condition, PerkMessage.Class, 40,
            "Passes on a message with the chosen hash. Its relation filters stay empty, so they pass.",
            [HashInput("hash", "Message hash", TagView.FieldOffset<PerkMessage>("Hash"), "message", "Any accepts every message.", false,
                [(EmptyHash, "Any")])],
            [new(TagView.FieldOffset<PerkMessage>("Hash"), Width.U32, EmptyHash),
             new(TagView.FieldOffset<PerkMessage>("H1416e2d4.RelationFilter0x20"), Width.U32, 0x20),
             new(TagView.FieldOffset<PerkMessage>("H35cd14d7.RelationFilter0x20"), Width.U32, 0x20),
             new(TagView.FieldOffset<PerkMessage>("HC57e5d40.RelationFilter0x20"), Width.U32, 0x20),
             new(TagView.FieldOffset<PerkMessage>("HAf0fb15a.RelationFilter0x20"), Width.U32, 0x20)]),
        Node<PerkWeaponEvent>("weapon-event", "On weapon event 41", PerkBehaviorNodeKind.Condition, PerkWeaponEvent.Class, 41,
            "Passes on weapon event 41 for a slot in the mask. The event's meaning is not known.",
            [Mask("slots", "Slot mask", TagView.FieldOffset<PerkWeaponEvent>("Slots"), 0, 15, "0 accepts any slot.")]),
        Node<PerkFinisher>("finisher", "On finisher phase", PerkBehaviorNodeKind.Condition, PerkFinisher.Class, 42,
            "Passes when a finisher reaches the chosen phase.",
            [new("phase", "Phase", TagView.FieldOffset<PerkFinisher>("Phase"), Width.U8, 1, 0, 2, PerkBehaviorValueKind.Integer, "",
                [(0, "Phase 0"), (1, "Phase 1"), (2, "Phase 2")])],
            [new(TagView.FieldOffset<PerkFinisher>("Phase"), Width.U8, 1)]),

        Node<PerkScript>("script", "Run a behavior script", PerkBehaviorNodeKind.Effect, PerkScript.Class, 48,
            "Runs an installed behavior script with three target objects when the perk activates.",
            [Target("target_a", "Target A", TagView.FieldOffset<PerkScript>("TargetA")), Target("target_b", "Target B", TagView.FieldOffset<PerkScript>("TargetB")),
             Target("target_c", "Target C", TagView.FieldOffset<PerkScript>("TargetC")),
             TagInput("root", "Behavior script", TagView.FieldOffset<PerkScript>("Root"), "behaviors", "An installed script (class 8080941E).")],
            [new(TagView.FieldOffset<PerkScript>("Root"), Width.U32, NoTag)],
            [Whole<PerkScript>("Unk8", PointerBytes)]),
        Node<PerkAbilityKey>("ability-key", "Add ability modifier key", PerkBehaviorNodeKind.Effect, PerkAbilityKey.Class, 7,
            "Adds or removes a modifier key on one ability while the perk is active. It does nothing unless the "
            + "ability has the key.",
            [new("slot", "Ability", TagView.FieldOffset<PerkAbilityKey>("Slot"), Width.U8, 0, 0, 7, PerkBehaviorValueKind.Integer,
                 "Slot meanings are assumed.",
                 [(0, "Grenade"), (1, "Super"), (2, "Melee"), (3, "Jump"), (4, "Slot 4"), (7, "Class ability")]),
             HashInput("key", "Modifier key", TagView.FieldOffset<PerkAbilityKey>("Key"), "modifier", "From the keys installed perks use."),
             Flag("remove", "Remove instead of add", TagView.FieldOffset<PerkAbilityKey>("Remove"))],
            [new(TagView.FieldOffset<PerkAbilityKey>("Key"), Width.U32, EmptyHash)]),
        Node<PerkSpawnOnce>("spawn-once", "Spawn an object once", PerkBehaviorNodeKind.Effect, PerkSpawnOnce.Class, 3,
            "Creates an object when the perk activates. The object outlives the perk.",
            [Flag("snap", "Snap to ground", TagView.FieldOffset<PerkSpawnOnce>("Snap"), description: "Assumed meaning."),
             Flag("face", "Face the event object", TagView.FieldOffset<PerkSpawnOnce>("Face")),
             new("position", "Position", TagView.FieldOffset<PerkSpawnOnce>("Position"), Width.U8, 0, 0, 1, PerkBehaviorValueKind.Integer, "",
                 PositionChoices),
             TagInput("definition", "Object definition", TagView.FieldOffset<PerkSpawnOnce>("Definition"), "definitions",
                 "An installed object definition.")],
            [new(TagView.FieldOffset<PerkSpawnOnce>("Definition"), Width.U32, NoTag)]),
        Node<PerkOrbs>("orbs", "Create orbs", PerkBehaviorNodeKind.Effect, PerkOrbs.Class, 5,
            "Creates pickups for the player and allies when the perk activates. Assumed: Orbs of Light.",
            [new("position", "Position", TagView.FieldOffset<PerkOrbs>("Position"), Width.U8, 0, 0, 1, PerkBehaviorValueKind.Integer, "",
                 PositionChoices),
             new("count", "Count", TagView.FieldOffset<PerkOrbs>("Count"), Width.I32, 1, 0, 100, PerkBehaviorValueKind.Integer),
             Number("value", "Value", TagView.FieldOffset<PerkOrbs>("Value"), 1, 0, 10),
             TagInput("definition", "Definition", TagView.FieldOffset<PerkOrbs>("Definition"), "definitions",
                 "Default uses the game's own definition.", false, [(NoTag, "Default")])],
            [new(TagView.FieldOffset<PerkOrbs>("Count"), Width.I32, 1), new(TagView.FieldOffset<PerkOrbs>("Value"), Width.F32, 1),
             new(TagView.FieldOffset<PerkOrbs>("Definition"), Width.U32, NoTag)]),
        Node<PerkAmmoCrate>("ammo-crate", "Create an ammo drop", PerkBehaviorNodeKind.Effect, PerkAmmoCrate.Class, 13,
            "Creates an ammo drop when the perk activates. Each category's weight sets how likely it is.",
            [new("position", "Position", TagView.FieldOffset<PerkAmmoCrate>("Position"), Width.U8, 1, 0, 1, PerkBehaviorValueKind.Integer, "",
                 PositionChoices),
             Flag("this_player", "Drop for this player", TagView.FieldOffset<PerkAmmoCrate>("ThisPlayer"), 1),
             Flag("allies", "Share with allies", TagView.FieldOffset<PerkAmmoCrate>("Allies")),
             TagInput("attached", "Attached object", TagView.FieldOffset<PerkAmmoCrate>("Attached"), "definitions", "None attaches nothing.",
                 false, [(NoTag, "None")]),
             Number("primary_min", "Primary minimum", TagView.FieldOffset<PerkAmmoCrate>("PrimaryMin"), 0, 0, 1000),
             Number("primary_max", "Primary maximum", TagView.FieldOffset<PerkAmmoCrate>("PrimaryMax"), 0, 0, 1000),
             Number("primary_weight", "Primary weight", TagView.FieldOffset<PerkAmmoCrate>("PrimaryWeight"), 0, 0, 1000),
             Number("special_min", "Special minimum", TagView.FieldOffset<PerkAmmoCrate>("SpecialMin"), 0, 0, 1000),
             Number("special_max", "Special maximum", TagView.FieldOffset<PerkAmmoCrate>("SpecialMax"), 0, 0, 1000),
             Number("special_weight", "Special weight", TagView.FieldOffset<PerkAmmoCrate>("SpecialWeight"), 1, 0, 1000),
             Number("heavy_min", "Heavy minimum", TagView.FieldOffset<PerkAmmoCrate>("HeavyMin"), 0, 0, 1000),
             Number("heavy_max", "Heavy maximum", TagView.FieldOffset<PerkAmmoCrate>("HeavyMax"), 0, 0, 1000),
             Number("heavy_weight", "Heavy weight", TagView.FieldOffset<PerkAmmoCrate>("HeavyWeight"), 0, 0, 1000)],
            [new(TagView.FieldOffset<PerkAmmoCrate>("ThisPlayer"), Width.U8, 1), new(TagView.FieldOffset<PerkAmmoCrate>("Attached"), Width.U32, NoTag)]),
        AmmoGrant<PerkAmmoRounds>("ammo-rounds", "Add ammo (rounds)", PerkAmmoRounds.Class, 14, Width.I32,
            "Adds whole rounds to each weapon slot that passes.", "Multiply by rounds per shot", "Assumed meaning."),
        AmmoGrant<PerkAmmoFraction>("ammo-fraction", "Add ammo (fraction)", PerkAmmoFraction.Class, 15, Width.F32,
            "Adds a fraction of capacity to each weapon slot that passes.", "Base is magazine size"),
        Node<PerkRefill>("refill", "Refill magazine from reserves", PerkBehaviorNodeKind.Effect, PerkRefill.Class, 16,
            "Moves a fraction of the magazine from reserves into the magazine.",
            [Flag("overflow", "Allow over magazine size", TagView.FieldOffset<PerkRefill>("Overflow")),
             Flag("magazine_base", "Base is magazine size", TagView.FieldOffset<PerkRefill>("MagazineBase"), 1),
             Flag("feedback", "Post reload feedback", TagView.FieldOffset<PerkRefill>("Feedback"), description: "Assumed meaning."),
             new("input", "Input", TagView.FieldOffset<PerkRefill>("Input"), Width.I8, -1, -1, 12, PerkBehaviorValueKind.Integer,
                 "Meanings other than the perk counter are assumed.",
                 [(-1, "None"), (0, "Perk counter"), (1, "Missing rounds"), (2, "Rounds in magazine")]),
             Flag("normalize", "Divide by magazine size", TagView.FieldOffset<PerkRefill>("Normalize")),
             Number("active_fraction", "Active weapon fraction", -1, 0, -10, 10),
             Number("this_fraction", "This weapon fraction", -1, 0, -10, 10),
             Number("slot0_fraction", "Slot 0 fraction", -1, 0, -10, 10),
             Number("slot1_fraction", "Slot 1 fraction", -1, 0, -10, 10),
             Number("slot2_fraction", "Slot 2 fraction", -1, 0, -10, 10)],
            [new(TagView.FieldOffset<PerkRefill>("H3c11ad32.ItemLabelTable"), Width.U32, NoTag),
             new(TagView.FieldOffset<PerkRefill>("HD6be8d22.ItemFilterKind"), Width.U8, 0xFF), new(TagView.FieldOffset<PerkRefill>("Input"), Width.U8, 0xFF)],
            [TableReference<PerkRefill>("H3c11ad32"), Tail<PerkRefill>("HB309caea")], [new("fractions")]),
        Node<PerkDamageResist>("damage-resist", "Owner damage multiplier", PerkBehaviorNodeKind.Effect, ResistClass,
            33, "Multiplies the owner's damage by this value while the perk is active. Assumed: damage taken.",
            [Number("multiplier", "Multiplier", TagView.FieldOffset<PerkDamageResist>("Multiplier"), 1, 0, 10),
             Number("distance", "Maximum source distance", TagView.FieldOffset<PerkDamageResist>("Distance"), -1, -1, 100000,
                 "-1 turns the test off."),
             new("types", "Damage types", TagView.FieldOffset<PerkDamageResist>("H4b1eab13.Types"), Width.U32, 0, 0, 255,
                 PerkBehaviorValueKind.Integer,
                 "Bit per damage type: 2 Solar, 4 Arc, 8 Void (assumed). Used when Any damage type is off."),
             Flag("any_type", "Any damage type", TagView.FieldOffset<PerkDamageResist>("H4b1eab13.AnyType"), 1)],
            [new(TagView.FieldOffset<PerkDamageResist>("Unknown8"), Width.U8, 0xFF), new(TagView.FieldOffset<PerkDamageResist>("Distance"), Width.F32, -1),
             new(TagView.FieldOffset<PerkDamageResist>("H1862ac0e.H3c11ad32.ItemLabelTable"), Width.U32, NoTag),
             new(TagView.FieldOffset<PerkDamageResist>("H4b1eab13.H3c11ad32.ItemFilterKind"), Width.U8, 0xFF),
             new(TagView.FieldOffset<PerkDamageResist>("H4b1eab13.Unknown153"), Width.U8, 1)],
            [TableReference<PerkDamageResist>("H1862ac0e.H3c11ad32"), Whole<PerkDamageResist>("H1862ac0e.Filter40", ArrayBytes),
             Whole<PerkDamageResist>("H4b1eab13.H3c11ad32.ItemCompiledFilter", PointerBytes),
             Tail<PerkDamageResist>("SelfClass")],
            [Types<PerkDamageResist>("H1862ac0e.Filter40", "H4b1eab13.Types"), new("selfref")]),
        Node<PerkExtendTimers>("extend-timers", "Extend timers on event", PerkBehaviorNodeKind.Effect, ExtendTimersClass, 32,
            "While the perk is active, a passing condition adds seconds to running timers, up to the cap.",
            [Number("seconds", "Seconds added", TagView.FieldOffset<PerkExtendTimers>("Seconds"), 1, -3600, 3600),
             Number("cap", "Cap (seconds)", TagView.FieldOffset<PerkExtendTimers>("Cap"), 10, 0, 3600)],
            Ignore: [Tail<PerkExtendTimers>("Conditions")], Parts: [new("timer-conditions")]),
        Node<PerkDamageLabels>("damage-labels", "Add labels to damage", PerkBehaviorNodeKind.Effect, PerkDamageLabels.Class, 37,
            "Adds a label to damage records that pass the type filter.",
            [new("label", "Label", -1, Width.U32, 0, 0, 320.0 * 4294967296 - 1, PerkBehaviorValueKind.Label,
                 "From the installed label table.", Picker: "labels", Required: true),
             new("types", "Damage types", TagView.FieldOffset<PerkDamageLabels>("HB56fca67.Types"), Width.U32, 0, 0, 255,
                 PerkBehaviorValueKind.Integer,
                 "Bit per damage type: 2 Solar, 4 Arc, 8 Void (assumed). Used when Any damage type is off."),
             Flag("any_type", "Any damage type", TagView.FieldOffset<PerkDamageLabels>("HB56fca67.AnyType"), 1)],
            [new(TagView.FieldOffset<PerkDamageLabels>("H09f93d12.H3c11ad32.ItemLabelTable"), Width.U32, NoTag),
             new(TagView.FieldOffset<PerkDamageLabels>("HB56fca67.H3c11ad32.ItemFilterKind"), Width.U8, 0xFF),
             new(TagView.FieldOffset<PerkDamageLabels>("HB56fca67.Unknown145"), Width.U8, 1)],
            [TableReference<PerkDamageLabels>("H09f93d12.H3c11ad32"), Whole<PerkDamageLabels>("H09f93d12.Filter40", ArrayBytes),
             Tail<PerkDamageLabels>("LabelRows")],
            [Types<PerkDamageLabels>("H09f93d12.Filter40", "HB56fca67.Types"), Labels<PerkDamageLabels>()]),
        Node<PerkEventLabels>("event-labels", "Add labels to item events", PerkBehaviorNodeKind.Effect, PerkEventLabels.Class, 54,
            "Adds a label to event 54 records on this perk's items.",
            [new("label", "Label", -1, Width.U32, 0, 0, 320.0 * 4294967296 - 1, PerkBehaviorValueKind.Label,
                "From the installed label table.", Picker: "labels", Required: true)],
            [new(TagView.FieldOffset<PerkEventLabels>("H3c11ad32.ItemLabelTable"), Width.U32, NoTag),
             new(TagView.FieldOffset<PerkEventLabels>("H0488330b.ItemFilterKind"), Width.U8, 0xFF),
             new(TagView.FieldOffset<PerkEventLabels>("Unknown160"), Width.U8, 1)],
            [TableReference<PerkEventLabels>("H3c11ad32"),
             Between<PerkEventLabels>("LabelRows", "LabelBitMask", PerkLabelTable.MaskBytes)],
            [Labels<PerkEventLabels>()]),
        Node<PerkProjectile>("projectile", "Override projectile", PerkBehaviorNodeKind.Effect, PerkProjectile.Class, 26,
            "Writes an object definition into this weapon while the perk is active. Assumed: the projectile.",
            [TagInput("definition", "Object definition", TagView.FieldOffset<PerkProjectile>("Definition"), "definitions",
                "An installed object definition.")],
            [new(TagView.FieldOffset<PerkProjectile>("Definition"), Width.U32, NoTag)], [Whole<PerkProjectile>("Unk8", PointerBytes)]),
        Node<PerkWeaponFloats>("weapon-floats", "Set three weapon values", PerkBehaviorNodeKind.Effect, PerkWeaponFloats.Class, 29,
            "Sets three weapon values while the perk is active. What they control is not known.",
            [Number("first", "First value", TagView.FieldOffset<PerkWeaponFloats>("First"), 0, -1000000, 1000000),
             Number("second", "Second value", TagView.FieldOffset<PerkWeaponFloats>("Second"), 0, -1000000, 1000000),
             Number("third", "Third value", TagView.FieldOffset<PerkWeaponFloats>("Third"), 0, -1000000, 1000000)]),
        Node<PerkSimObject>("sim-object", "Post a sim object", PerkBehaviorNodeKind.Effect, PerkSimObject.Class, 43,
            "Posts a sim object or incident for a player owner when the perk activates.",
            [HashInput("hash", "Definition hash", TagView.FieldOffset<PerkSimObject>("Hash"), "sim", "From the hashes installed perks use.")],
            [new(TagView.FieldOffset<PerkSimObject>("Hash"), Width.U32, EmptyHash)]),
        Node<PerkWeaponMode>("weapon-mode", "Set weapon element choice", PerkBehaviorNodeKind.Effect, PerkWeaponMode.Class, 6,
            "Sets a weapon byte while the perk is active. Assumed: the element choice.",
            [new("value", "Value", TagView.FieldOffset<PerkWeaponMode>("Value"), Width.I8, 0, -128, 127, PerkBehaviorValueKind.Integer),
             Flag("keep", "Keep after the perk ends", TagView.FieldOffset<PerkWeaponMode>("Keep"))]),
        Node<PerkWeaponPair>("weapon-pair", "Set two weapon bytes", PerkBehaviorNodeKind.Effect, PerkWeaponPair.Class, 28,
            "Sets two weapon bytes while the perk is active. What they control is not known.",
            [Mask("first", "First value", TagView.FieldOffset<PerkWeaponPair>("First"), 0, 255, ""),
             Mask("second", "Second value", TagView.FieldOffset<PerkWeaponPair>("Second"), 0, 255, "")]),
        Node<PerkItemFlag>("item-flag", "Add a named flag to items", PerkBehaviorNodeKind.Effect, PerkItemFlag.Class, 41,
            "Adds a named flag to items while the perk is active. Properties can read the same name.",
            [Flag("this_item", "This item", TagView.FieldOffset<PerkItemFlag>("ThisItem"), 1),
             Mask("slots", "Other slots", TagView.FieldOffset<PerkItemFlag>("Slots"), 0, 7, "Bits 0 to 2 add slot items."),
             HashInput("flag", "Flag name hash", TagView.FieldOffset<PerkItemFlag>("Flag"), "", "Any name hash.")],
            [new(TagView.FieldOffset<PerkItemFlag>("ThisItem"), Width.U8, 1), new(TagView.FieldOffset<PerkItemFlag>("Flag"), Width.U32, EmptyHash)]),
        Node<PerkSwordGuard>("sword-guard", "Enable sword guard", PerkBehaviorNodeKind.Effect, PerkSwordGuard.Class, 27,
            "Turns on a weapon behavior while the perk is active. Assumed: sword guard, as in every installed use.", []),
        Node<PerkModeTag>("mode-tag", "Set weapon mode tag", PerkBehaviorNodeKind.Effect, PerkModeTag.Class, 25,
            "Sets a mode tag on this weapon while the perk is active. While-state rows can test it.",
            [HashInput("tag", "Mode tag hash", TagView.FieldOffset<PerkModeTag>("Tag"), "", "Any name hash.")],
            [new(TagView.FieldOffset<PerkModeTag>("Tag"), Width.U32, EmptyHash)]),
        Node<PerkChannelHash>("channel-hash", "Set a channel hash", PerkBehaviorNodeKind.Effect, PerkChannelHash.Class, 35,
            "Sets a hash on a component channel while the perk is active. What it controls is not known.",
            [Target("target", "Target", TagView.FieldOffset<PerkChannelHash>("Target")),
             new("channel", "Channel", TagView.FieldOffset<PerkChannelHash>("Channel"), Width.U8, 14, 3, 14, PerkBehaviorValueKind.Integer, "",
                 [(3, "3"), (14, "14")]),
             HashInput("hash", "Hash", TagView.FieldOffset<PerkChannelHash>("Hash"), "channel", "From the hashes installed perks use.", false,
                 [(EmptyHash, "Default")]),
             Flag("owner_too", "Also the owner", TagView.FieldOffset<PerkChannelHash>("OwnerToo"))],
            [new(TagView.FieldOffset<PerkChannelHash>("Channel"), Width.U8, 14), new(TagView.FieldOffset<PerkChannelHash>("Hash"), Width.U32, EmptyHash)]),
        Node<PerkComponent>("component", "Apply a component config", PerkBehaviorNodeKind.Effect, PerkComponent.Class, 4,
            "Applies an installed component config to the target when the perk activates.",
            [Target("target", "Target", TagView.FieldOffset<PerkComponent>("Target")),
             TagInput("config", "Component config", TagView.FieldOffset<PerkComponent>("Config"), "configs",
                 "An installed config (class 80809C36).")],
            [new(TagView.FieldOffset<PerkComponent>("Config"), Width.U32, NoTag)], [Whole<PerkComponent>("Unk8", PointerBytes)]),
        Node<PerkWeaponCount>("weapon-count", "Raise a weapon flag count", PerkBehaviorNodeKind.Effect, PerkWeaponCount.Class, 30,
            "Raises a weapon counter while the perk is active. What it controls is not known.",
            [Flag("enable", "Enabled", TagView.FieldOffset<PerkWeaponCount>("Enable"), 1)]),
        Node<PerkStatScale>("stat-scale", "Scale component values", PerkBehaviorNodeKind.Effect, PerkStatScale.Class, 53,
            "When the perk activates, changes the named component values on the target by an expression of the input.",
            [new("name", "Component name", -1, Width.U32, EmptyHash, 0, uint.MaxValue, PerkBehaviorValueKind.Hash,
                 "From the names installed perks use.", Picker: "scaled", Required: true),
             Number("cap", "Cap", TagView.FieldOffset<PerkStatScale>("Cap"), -1, -1, 1000000, "-1 turns the cap off."),
             new("mode", "Mode", TagView.FieldOffset<PerkStatScale>("Mode"), Width.U8, 0, 0, 2, PerkBehaviorValueKind.Integer, "",
                 [(0, "Add"), (1, "Set"), (2, "Multiply")]),
             Target("source", "Input object", TagView.FieldOffset<PerkStatScale>("Source"), 0),
             Target("target", "Target object", TagView.FieldOffset<PerkStatScale>("Target"), 0),
             .. ExpressionInputs(1).Select(property => new Input(property.Key, property.Label,
                 property.Key == "input" ? TagView.FieldOffset<PerkStatScale>("Input") : -1, property.Key == "input" ? Width.I8 : Width.F32,
                 property.Value, property.Minimum, property.Maximum, property.Kind, property.Description,
                 [.. property.Choices.Select(choice => (choice.Value, choice.Label))])),
             Flag("normalize", "Divide by magazine size", TagView.FieldOffset<PerkStatScale>("Normalize"))],
            [new(TagView.FieldOffset<PerkStatScale>("Cap"), Width.F32, -1)],
            [Whole<PerkStatScale>("ComponentNames", ArrayBytes),
             Between<PerkStatScale>("H653a15a2", "H156535d7", Unsafe.SizeOf<C80808e76>())],
            [new("names", TagView.FieldOffset<PerkStatScale>("ComponentNames")), new("expression", TagView.FieldOffset<PerkStatScale>("H156535d7.H45dee76f"))]),
        Node<PerkRegisterTarget>("register-target", "Register a tracked target", PerkBehaviorNodeKind.Effect,
            PerkRegisterTarget.Class, 49, "Stores a target object under a name that tracked-entry conditions read.",
            [Target("target", "Target", TagView.FieldOffset<PerkRegisterTarget>("Target"), 3),
             HashInput("name", "Tracked name", TagView.FieldOffset<PerkRegisterTarget>("Name"), "tracked", "From the names installed perks use.")],
            [new(TagView.FieldOffset<PerkRegisterTarget>("Target"), Width.U8, 3), new(TagView.FieldOffset<PerkRegisterTarget>("Name"), Width.U32, EmptyHash)]),
        Node<PerkRadar>("radar", "Set radar values", PerkBehaviorNodeKind.Effect, PerkRadar.Class, 18,
            "Sets radar values while the perk is active. -1 leaves a value unchanged.",
            [Number("first", "First value", TagView.FieldOffset<PerkRadar>("First"), -1, -1, 100000),
             Number("second", "Second value", TagView.FieldOffset<PerkRadar>("Second"), -1, -1, 100000),
             Number("third", "Third value", TagView.FieldOffset<PerkRadar>("Third"), -1, -1, 100000)],
            [new(TagView.FieldOffset<PerkRadar>("First"), Width.F32, -1), new(TagView.FieldOffset<PerkRadar>("Second"), Width.F32, -1),
             new(TagView.FieldOffset<PerkRadar>("Third"), Width.F32, -1)]),
        Node<PerkRadarCount>("radar-count", "Raise the radar counter", PerkBehaviorNodeKind.Effect, PerkRadarCount.Class, 20,
            "Raises a radar counter while the perk is active.", [Flag("enable", "Enabled", TagView.FieldOffset<PerkRadarCount>("Enable"), 1)],
            [new(TagView.FieldOffset<PerkRadarCount>("Enable"), Width.U8, 1)]),
        Node<PerkTeamStat>("team-stat", "Add to a team stat", PerkBehaviorNodeKind.Effect, PerkTeamStat.Class, 52,
            "Adds an amount to the owner's team stat while the perk is active.",
            [HashInput("stat", "Stat name", TagView.FieldOffset<PerkTeamStat>("Stat"), "stat", "From the names installed perks use."),
             Number("amount", "Amount", TagView.FieldOffset<PerkTeamStat>("Amount"), 1, -1000000, 1000000)],
            [new(TagView.FieldOffset<PerkTeamStat>("Stat"), Width.U32, EmptyHash)]),
        Node<PerkMovement>("movement", "Movement mode", PerkBehaviorNodeKind.Effect, PerkMovement.Class, 36,
            "Puts the owner into a movement mode with these settings when the perk activates. Assumed: hover. "
            + "Speed limits are decoded; the other values are copied into the mode.",
            [.. MovementInputs.Select(field => field.Input)], Ignore: [Whole<PerkMovement>("MovementBlock", PointerBytes)],
            Parts: [new("movement")]),
        Node<PerkWeaponOverride>("weapon-override", "Push a weapon override byte", PerkBehaviorNodeKind.Effect,
            PerkWeaponOverride.Class, 51, "Pushes a byte onto this weapon's override stack while the perk is active.",
            [Mask("value", "Value", TagView.FieldOffset<PerkWeaponOverride>("Value"), 0, 255, "")]),
        Node<PerkSlotBytes>("slot-bytes", "Set slot weapon byte", PerkBehaviorNodeKind.Effect, PerkSlotBytes.Class, 22,
            "Sets a byte on slot weapons while the perk is active. What it controls is not known.",
            [Flag("this_slot", "This perk's slot", TagView.FieldOffset<PerkSlotBytes>("ThisSlot"), 1),
             Mask("slots", "Other slots", TagView.FieldOffset<PerkSlotBytes>("Slots"), 0, 15, "One bit per slot."),
             Mask("value", "Value", TagView.FieldOffset<PerkSlotBytes>("Value"), 0, 255, "")]),
        Node<PerkOwnerFlag>("owner-flag", "Set owner flag", PerkBehaviorNodeKind.Effect, PerkOwnerFlag.Class, 0,
            "Sets a flag on the owner while the perk is active. What it controls is not known.", []),
        Node<PerkSlotFlag>("slot-flag", "Set slot flag", PerkBehaviorNodeKind.Effect, PerkSlotFlag.Class, 24,
            "Sets a flag on this perk's slot while the perk is active. Assumed: drop the magazine.", []),
        Node<PerkRadarByte>("radar-byte", "Set radar byte", PerkBehaviorNodeKind.Effect, PerkRadarByte.Class, 17,
            "Sets a radar byte while the perk is active.", [Mask("value", "Value", TagView.FieldOffset<PerkRadarByte>("Value"), 1, 255, "")],
            [new(TagView.FieldOffset<PerkRadarByte>("Value"), Width.U8, 1)])
    ];

    /** Ops 14 and 15 share one form; only the amount width and the Scale flag differ. */
    private static Spec AmmoGrant<T>(string key, string name, uint type, int opcode, Width width, string description,
        string scaleLabel, string scaleNote = "") where T : unmanaged
    {
        var kind = width == Width.F32 ? PerkBehaviorValueKind.Float : PerkBehaviorValueKind.Integer;
        string[] labels = ["This weapon", "Slot 0", "Slot 1", "Slot 2", "Primary ammo", "Special ammo", "Heavy ammo"];
        string[] keys = ["this_weapon", "slot0", "slot1", "slot2", "primary", "special", "heavy"];
        string[] members =
            ["ThisWeaponAmount", "Slot0Amount", "Slot1Amount", "Slot2Amount", "PrimaryAmount", "SpecialAmount", "HeavyAmount"];
        return Node<T>(key, name, PerkBehaviorNodeKind.Effect, type, opcode, description,
            [Flag("magazine", "Into the magazine", TagView.FieldOffset<T>("Magazine")), Flag("overflow", "Allow over magazine size", TagView.FieldOffset<T>("Overflow")),
             Flag("scale", scaleLabel, TagView.FieldOffset<T>("Scale"), description: scaleNote),
             Flag("counter", "Multiply by the perk counter", TagView.FieldOffset<T>("Counter")),
             .. keys.Select((value, i) => new Input(value, labels[i], TagView.FieldOffset<T>(members[i]), width, 0, -1000000, 1000000, kind))],
            [new(TagView.FieldOffset<T>("H3c11ad32.ItemLabelTable"), Width.U32, NoTag), new(TagView.FieldOffset<T>("HD6be8d22.ItemFilterKind"), Width.U8, 0xFF)],
            [TableReference<T>("H3c11ad32")]);
    }
}
