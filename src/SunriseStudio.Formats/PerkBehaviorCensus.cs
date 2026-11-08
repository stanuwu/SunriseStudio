using System.Collections;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

/** One native node kind installed perk programs use; a kind is its class and opcode. */
public sealed record PerkInstalledKind(PerkBehaviorNodeKind Kind, uint NativeClass, byte Opcode, int Nodes, int Programs);

/** Installed node kinds, plus the installed values the editor's pickers offer. */
public sealed class PerkCensus(IReadOnlyList<PerkInstalledKind> kinds) : IReadOnlyList<PerkInstalledKind>
{
    /** Event hashes installed event conditions test, nested ones included. */
    public IReadOnlyList<PerkBehaviorChoice> EventHashes { get; init; } = [];
    /** Keys of the installed transmat effect table. */
    public IReadOnlyList<PerkBehaviorChoice> TransmatKeys { get; init; } = [];
    /** Object definitions a spawn effect can create; installed spawn targets come first. */
    public IReadOnlyList<PerkBehaviorChoice> ObjectDefinitions { get; init; } = [];
    /** Other installed value lists, keyed by the picker name a property carries. */
    public IReadOnlyDictionary<string, IReadOnlyList<PerkBehaviorChoice>> Values { get; init; } =
        new Dictionary<string, IReadOnlyList<PerkBehaviorChoice>>();

    /** The installed label table, for label filters; null when the tag is missing. */
    public PerkLabelTable? LabelTable { get; init; }

    /** The installed values for a property's picker; empty when the census has none. */
    public IReadOnlyList<PerkBehaviorChoice> Picker(string name) => name switch
    {
        "event" => EventHashes, "transmat" => TransmatKeys, "definitions" => ObjectDefinitions,
        _ => Values.GetValueOrDefault(name) ?? []
    };

    public int Count => kinds.Count;
    public PerkInstalledKind this[int index] => kinds[index];
    public IEnumerator<PerkInstalledKind> GetEnumerator() => kinds.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/** Counts the node kinds of every installed perk program, so the editor can list the ones it cannot write. */
public static class PerkBehaviorCensus
{
    /** Spawn effects create objects of this class. */
    public const uint ObjectDefinitionClass = SEntity.Class;
    // Behavior scripts and component configs are the other tag classes effects reference.
    private const uint BehaviorClass = ObjectBehaviorRoot.Class, ConfigClass = SEntityConfig.Class;
    // Transmat effects select one row of this table by key.
    private const uint TransmatTableTag = 0x80B9E5C4, TransmatRowClass = C808064a2.Class;
    private const uint CounterClass = PerkCounter.Class, GatedClass = PerkStateGated.Class, AllGroupsClass = PerkAllGroups.Class;
    private const uint ExtendTimersClass = PerkExtendTimers.Class, TransmatClass = PerkTransmat.Class, ScaleClass = PerkStatScale.Class;
    private static readonly int ConditionRowSize = Unsafe.SizeOf<C808040ba>(), EffectRowSize = Unsafe.SizeOf<C808040ac>();

    /** Payload fields whose installed values feed a picker: (class, offset, picker). */
    private static readonly (uint Class, int Offset, string Picker)[] Sources =
    [
        (PerkEvent.Class, TagView.FieldOffset<PerkEvent>(nameof(PerkEvent.EventHash)), "event"),
        (PerkEvent2.Class, TagView.FieldOffset<PerkEvent2>(nameof(PerkEvent2.Event)), "event29"),
        (PerkIncident.Class, TagView.FieldOffset<PerkIncident>(nameof(PerkIncident.Incident)), "incident"),
        (PerkIncident.Class, TagView.FieldOffset<PerkIncident>(nameof(PerkIncident.Role)), "role"),
        (PerkTrackedTimer.Class, TagView.FieldOffset<PerkTrackedTimer>(nameof(PerkTrackedTimer.Name)), "tracked"),
        (PerkTrackedGone.Class, TagView.FieldOffset<PerkTrackedGone>(nameof(PerkTrackedGone.Name)), "tracked"),
        (PerkRegisterTarget.Class, TagView.FieldOffset<PerkRegisterTarget>(nameof(PerkRegisterTarget.Name)),
            "tracked"),
        (PerkMessage.Class, TagView.FieldOffset<PerkMessage>(nameof(PerkMessage.Hash)), "message"),
        (PerkAbilityKey.Class, TagView.FieldOffset<PerkAbilityKey>(nameof(PerkAbilityKey.Key)), "modifier"),
        (PerkSimObject.Class, TagView.FieldOffset<PerkSimObject>(nameof(PerkSimObject.Hash)), "sim"),
        (PerkTeamStat.Class, TagView.FieldOffset<PerkTeamStat>(nameof(PerkTeamStat.Stat)), "stat"),
        (PerkChannelHash.Class, TagView.FieldOffset<PerkChannelHash>(nameof(PerkChannelHash.Hash)), "channel"),
        (TransmatClass, TagView.FieldOffset<PerkTransmat>(nameof(PerkTransmat.EffectKey)), "transmat"),
        (PerkSpawn.Class, TagView.FieldOffset<PerkSpawn>(nameof(PerkSpawn.Definition)), "definitions"),
        (PerkDrivenSpawn.Class, TagView.FieldOffset<PerkDrivenSpawn>(nameof(PerkDrivenSpawn.Definition)),
            "definitions"),
        (PerkSpawnOnce.Class, TagView.FieldOffset<PerkSpawnOnce>(nameof(PerkSpawnOnce.Definition)),
            "definitions"),
        (PerkOrbs.Class, TagView.FieldOffset<PerkOrbs>(nameof(PerkOrbs.Definition)), "definitions"),
        (PerkAmmoCrate.Class, TagView.FieldOffset<PerkAmmoCrate>(nameof(PerkAmmoCrate.Attached)),
            "definitions"),
        (PerkProjectile.Class, TagView.FieldOffset<PerkProjectile>(nameof(PerkProjectile.Definition)),
            "definitions"),
        (PerkAbilityGained.Class, TagView.FieldOffset<PerkAbilityGained>(nameof(PerkAbilityGained.Definition)),
            "definitions"),
        (PerkAbilityLost.Class, TagView.FieldOffset<PerkAbilityLost>(nameof(PerkAbilityLost.Definition)),
            "definitions"),
        (PerkPickup.Class, TagView.FieldOffset<PerkPickup>(nameof(PerkPickup.Definition)), "definitions"),
        (PerkScript.Class, TagView.FieldOffset<PerkScript>(nameof(PerkScript.Root)), "behaviors"),
        (PerkComponent.Class, TagView.FieldOffset<PerkComponent>(nameof(PerkComponent.Config)), "configs")
    ];

    // Root and stack-branch condition arrays: activation, deactivation, cooldown.
    private static readonly int[] RootConditions =
    [
        TagView.FieldOffset<SSandboxPerkProgram>("HB38b5954.ActivationConditions"),
        TagView.FieldOffset<SSandboxPerkProgram>("HB38b5954.DeactivationConditions"),
        TagView.FieldOffset<SSandboxPerkProgram>("HB38b5954.CooldownConditions")
    ];
    private static readonly int[] BranchConditions =
    [
        TagView.FieldOffset<C8080407d>(nameof(C8080407d.ActivationConditions)),
        TagView.FieldOffset<C8080407d>(nameof(C8080407d.DeactivationConditions)),
        TagView.FieldOffset<C8080407d>(nameof(C8080407d.CooldownConditions))
    ];
    private static readonly int RootEffects =
        TagView.FieldOffset<SSandboxPerkProgram>("HB38b5954.Effects");
    private static readonly int RootStack =
        TagView.FieldOffset<SSandboxPerkProgram>(nameof(SSandboxPerkProgram.StackBranches));
    private static readonly int BranchEffects = TagView.FieldOffset<C8080407d>(nameof(C8080407d.Effects));
    private static readonly int TimerConditions =
        TagView.FieldOffset<PerkExtendTimers>(nameof(PerkExtendTimers.Conditions));
    private static readonly int CounterRows = TagView.FieldOffset<PerkCounter>(nameof(PerkCounter.Rows));
    private static readonly int GatedInner =
        TagView.FieldOffset<PerkStateGated>(nameof(PerkStateGated.InnerCondition));
    private static readonly int AllGroupsGroups = TagView.FieldOffset<PerkAllGroups>(nameof(PerkAllGroups.Groups));
    private static readonly int GroupMembers =
        TagView.FieldOffset<PerkConditionGroup>(nameof(PerkConditionGroup.Members));
    private static readonly int ScaleNames =
        TagView.FieldOffset<PerkStatScale>(nameof(PerkStatScale.ComponentNames));
    private static readonly int TransmatRows = TagView.FieldOffset<C80806493>(nameof(C80806493.H09a0f1b0));

    public static PerkCensus Read(PackageCatalog catalog, CancellationToken token = default)
    {
        var nodes = new Dictionary<(PerkBehaviorNodeKind, uint, byte), (int Nodes, HashSet<uint> Programs)>();
        var uses = Sources.Select(source => source.Picker).Append("scaled").Distinct()
            .ToDictionary(name => name, _ => new Dictionary<uint, HashSet<uint>>());
        foreach (uint tag in catalog.Entries.Where(entry => entry.Reference == NativePerkBehavior.Class && AssetCategories.IsStructured(entry))
            .Select(entry => entry.TagHash).Distinct())
        {
            token.ThrowIfCancellationRequested();
            byte[] bytes;
            try { bytes = catalog.Read(tag); } catch (Exception error) when (error is IOException or InvalidDataException) { continue; }
            foreach (var (node, type, kind) in Walk(bytes))
            {
                var key = (kind, type, bytes[node + (kind == PerkBehaviorNodeKind.Condition ? NativePerkBehavior.ConditionOpcode : NativePerkBehavior.EffectOpcode)]);
                if (!nodes.TryGetValue(key, out var row)) nodes[key] = row = (0, []);
                row.Programs.Add(tag);
                nodes[key] = (row.Nodes + 1, row.Programs);
                var found = Sources.Where(source => source.Class == type && node + source.Offset + sizeof(uint) <= bytes.Length)
                    .Select(source => (source.Picker, Value: TagRead.U32(bytes, node + source.Offset)));
                // Component scale effects hold their names in a u32 array.
                if (type == ScaleClass && TagView.FitsThrough<PerkStatScale>(bytes, node, nameof(PerkStatScale.ComponentNames)))
                    found = found.Concat(Rows(bytes, node + ScaleNames, Unsafe.SizeOf<SU32HashElement>())
                        .Select(row => ("scaled", TagView.At<SU32HashElement>(bytes, row).KeyHash)));
                foreach (var (picker, value) in found)
                {
                    if (value is StringHash.Empty or uint.MaxValue) continue;
                    var users = uses[picker];
                    if (!users.TryGetValue(value, out var programs)) users[value] = programs = [];
                    programs.Add(tag);
                }
            }
        }
        var kinds = nodes.Select(pair => new PerkInstalledKind(pair.Key.Item1, pair.Key.Item2, pair.Key.Item3,
                pair.Value.Nodes, pair.Value.Programs.Count))
            .OrderBy(kind => kind.Kind).ThenByDescending(kind => kind.Programs).ToArray();
        var values = uses.Where(pair => pair.Key is not ("event" or "transmat" or "definitions" or "behaviors" or "configs"))
            .ToDictionary(pair => pair.Key, pair => Installed(pair.Value));
        values["behaviors"] = Tags(catalog, BehaviorClass, uses["behaviors"]);
        values["configs"] = Tags(catalog, ConfigClass, uses["configs"]);
        var labels = LabelTable(catalog);
        // A label value packs the label's bit above its hash, so a pick sets both.
        values["labels"] = [.. (labels?.Entries ?? []).Where(entry => !entry.Group)
            .Select(entry => new PerkBehaviorChoice(entry.Bit * 4294967296.0 + entry.Hash, $"{entry.Name} (bit {entry.Bit})"))];
        return new PerkCensus(kinds)
        {
            EventHashes = Installed(uses["event"]),
            TransmatKeys = TransmatTable(catalog, uses["transmat"]),
            ObjectDefinitions = Tags(catalog, ObjectDefinitionClass, uses["definitions"]),
            LabelTable = labels,
            Values = values
        };
    }

    /** The installed label table; null when the tag is missing or unreadable. */
    private static PerkLabelTable? LabelTable(PackageCatalog catalog)
    {
        if (!catalog.ContainsTag(PerkLabelTable.Tag)) return null;
        try { return PerkLabelTable.Parse(catalog.Read(PerkLabelTable.Tag)); }
        catch (Exception error) when (error is IOException or InvalidDataException) { return null; }
    }

    private static IReadOnlyList<PerkBehaviorChoice> Installed(Dictionary<uint, HashSet<uint>> uses) =>
        [.. uses.OrderByDescending(pair => pair.Value.Count).ThenBy(pair => pair.Key)
            .Select(pair => new PerkBehaviorChoice(pair.Key, $"0x{pair.Key:X8} ({Perks(pair.Value.Count)})"))];

    /** Every installed tag of a class; tags installed perks use come first. */
    private static IReadOnlyList<PerkBehaviorChoice> Tags(PackageCatalog catalog, uint type, Dictionary<uint, HashSet<uint>> uses) =>
        [.. catalog.Entries.Where(entry => entry.Reference == type).Select(entry => entry.TagHash).Distinct()
            .OrderByDescending(tag => uses.GetValueOrDefault(tag)?.Count ?? 0).ThenBy(tag => tag)
            .Select(tag => new PerkBehaviorChoice(tag, Label(catalog, tag)
                + (uses.TryGetValue(tag, out var users) ? $" ({Perks(users.Count)})" : "")))];

    private static IReadOnlyList<PerkBehaviorChoice> TransmatTable(PackageCatalog catalog, Dictionary<uint, HashSet<uint>> users)
    {
        if (!catalog.ContainsTag(TransmatTableTag)) return [];
        byte[] bytes;
        try { bytes = catalog.Read(TransmatTableTag); } catch (Exception error) when (error is IOException or InvalidDataException) { return []; }
        if (!TagArray.TryChecked(bytes, TransmatRows, TransmatRowClass, Unsafe.SizeOf<C808064a2>(), out var rows)) return [];
        return [.. Enumerable.Range(0, rows.Count).Select(index => rows.Data + index * Unsafe.SizeOf<C808064a2>())
            .Select(row => TagView.At<C808064a2>(bytes, row))
            .Select(row => (Key: row.Unk0, Definition: row.Unk8))
            .Select(row => new PerkBehaviorChoice(row.Key, $"0x{row.Key:X8} -> {Label(catalog, row.Definition)}"
                + (users.TryGetValue(row.Key, out var programs) ? $" ({Perks(programs.Count)})" : "")))];
    }

    /** Each node and its class, from phases, stack branches, rows, groups, inners and timers. */
    private static IEnumerable<(int Node, uint Class, PerkBehaviorNodeKind Kind)> Walk(byte[] bytes)
    {
        var found = new List<(int, uint, PerkBehaviorNodeKind)>();
        var seen = new HashSet<int>();
        foreach (int field in RootConditions)
            foreach (var (node, type) in Nodes(bytes, field, ConditionRowSize)) Condition(node, type, 0);
        foreach (var (node, type) in Nodes(bytes, RootEffects, EffectRowSize)) Effect(node, type);
        if (TagView.Fits<TagArray<C8080407d>>(bytes, RootStack) && new TagArray(bytes, RootStack).Count is > 0 and < 4096)
        {
            var stack = new TagArray(bytes, RootStack);
            long count = stack.Count, rows = stack.Header.Offset + TagArray.HeaderSize;
            int size = Unsafe.SizeOf<C8080407d>();
            for (int i = 0; i < count && rows + i * size + size <= bytes.Length; i++)
            {
                int row = (int)(rows + i * size);
                foreach (int offset in BranchConditions)
                    foreach (var (node, type) in Nodes(bytes, row + offset, ConditionRowSize)) Condition(node, type, 0);
                foreach (var (node, type) in Nodes(bytes, row + BranchEffects, EffectRowSize)) Effect(node, type);
            }
        }
        return found;

        void Effect(int node, uint type)
        {
            if (!seen.Add(node)) return;
            found.Add((node, type, PerkBehaviorNodeKind.Effect));
            if (TagView.Fits<PerkExtendTimers>(bytes, node) && type == ExtendTimersClass)
                foreach (var (child, childType) in Nodes(bytes, node + TimerConditions, ConditionRowSize))
                    Condition(child, childType, 1);
        }

        void Condition(int node, uint type, int depth)
        {
            if (depth > 8 || node + Unsafe.SizeOf<PerkAlways>() > bytes.Length || !seen.Add(node)) return;
            found.Add((node, type, PerkBehaviorNodeKind.Condition));
            if (type == CounterClass && TagView.Fits<PerkCounter>(bytes, node))
                foreach (var (child, childType) in Nodes(bytes, node + CounterRows, Unsafe.SizeOf<PerkCounterRow>()))
                    Condition(child, childType, depth + 1);
            if (type == GatedClass && TagView.Fits<PerkStateGated>(bytes, node))
            {
                int inner = TagPointer.TryChecked(bytes, node + GatedInner);
                if (inner >= TagView.ClassWordSize) Condition(inner, TagView.ClassAt(bytes, inner) ?? 0, depth + 1);
            }
            if (type == AllGroupsClass && TagView.Fits<PerkAllGroups>(bytes, node))
                foreach (int group in Rows(bytes, node + AllGroupsGroups, Unsafe.SizeOf<PerkConditionGroup>()))
                    foreach (var (child, childType) in Nodes(bytes, group + GroupMembers, ConditionRowSize))
                        Condition(child, childType, depth + 1);
        }
    }

    private static string Label(PackageCatalog catalog, uint tag) =>
        catalog.GetName(tag) is { Length: > 0 } name ? $"{name} (0x{tag:X8})" : $"0x{tag:X8}";

    private static string Perks(int count) => count == 1 ? "1 perk" : $"{count} perks";

    /** Row offsets of one typed array; a malformed array yields none. */
    private static IEnumerable<int> Rows(byte[] bytes, int field, int stride)
    {
        if (!TagView.Fits<TagArray<Unknown>>(bytes, field)) yield break;
        var array = new TagArray(bytes, field);
        long count = array.Count, header = array.Header.Offset;
        if (count is <= 0 or > 4096 || header < TagView.ClassWordSize || header + TagArray.HeaderSize + count * stride > bytes.Length) yield break;
        for (int index = 0; index < count; index++) yield return (int)(header + TagArray.HeaderSize + index * stride);
    }

    /** Node offsets and classes of one pointer array; a malformed array yields none. */
    private static IEnumerable<(int Node, uint Class)> Nodes(byte[] bytes, int field, int stride)
    {
        foreach (int at in Rows(bytes, field, stride))
        {
            long node = at + TagRead.I64(bytes, at);
            if (node >= TagView.ClassWordSize && node < bytes.Length) yield return ((int)node, TagView.ClassAt(bytes, (int)node) ?? 0);
        }
    }
}
