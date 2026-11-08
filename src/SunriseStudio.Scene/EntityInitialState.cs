using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

/**
 * One variant parameter: its key, the values a preview may pick, and the value the read used.
 * Runtime is set for the parameter the game fills from player state; its values come from part labels.
 */
public sealed record EntityVariantParameter(uint Key, IReadOnlyList<uint> Values, uint Value, bool Runtime);

/** Plain names for variant keys, values and object channels; each word's FNV-1 hash is the authored value. */
public static class EntityVariantNames
{
    private static readonly string[] Words =
    [
        "type", "faction", "unit", "unit_type", "minor", "major", "miniboss", "ultra", "taken", "taken_titan",
        "taken_hydra", "titan", "hydra", "goblin", "hobgoblin", "harpy", "minotaur", "cyclops", "sol_divisive", "past",
        "future", "titan_base", "titan_past", "titan_future", "undying_mind", "weapon_type", "damage", "destroyed", "body",
        "head", "mid_torso", "lower_torso", "top_head", "weakspot_hidden", "weakspot_active", "eye_color",
        "eye_intensity", "teleport"
    ];
    private static readonly Dictionary<uint, string> ByHash = Words.ToDictionary(StringBankWriter.Hash);

    /** The word whose hash is this value; null when none is known. */
    public static string? Name(uint hash) => ByHash.GetValueOrDefault(hash);
}

/** Stored spawn inputs and supported post-init selectors determine this preview, not live state. */
public sealed record EntityInitialState(bool[]? EnabledParts, string? Unresolved)
{
    /** Parameters of the variant component; Value is zero for one the read could not resolve. */
    public IReadOnlyList<EntityVariantParameter> Parameters { get; init; } = [];
    /** Names of the authored variant rows; a preview may request any of them. */
    public IReadOnlyList<uint> Variants { get; init; } = [];
    /** Name of the variant row whose region filters apply; zero when none applies. */
    public uint SelectedVariant { get; init; }
    /** Set when no variant has positive weight; marks the parts that every variant row disables. */
    public bool[]? DisabledByEveryVariant { get; init; }
    public bool? Enabled(int part) => EnabledParts is { } parts && (uint)part < parts.Length ? parts[part] : null;
    public bool EveryVariantDisables(int part) =>
        DisabledByEveryVariant is { } parts && (uint)part < parts.Length && parts[part];
}

/** Resolves native part filters while keeping unsupported initializers explicit. */
public sealed class EntityInitialStateReader(PackageCatalog catalog)
{
    private const uint PostInitHash = 0x242D8C9A;
    private const uint Damage = UnknownDamageComponent.Class, Character = UnknownCharacterComponent.Class, Variant = UnknownVariantComponent.Class, RegionState = UnknownRegionStateComponent.Class;
    private const uint RegionKey = 0x51E7A18D, VariantKey = 0x4C58EF8F, StateKey = 0x5266EA90;
    private const uint ShapeKey = 0x0B3B1D30, InitialShape = 0xA7E66243, RuntimeParameter = EntityPartLabels.RuntimeKey;
    // An empty spawn value for the runtime parameter is replaced with this value.
    private const uint RuntimeEmptyValue = 0x1B3DDFC5;
    // Classes whose post-init method adds one constant filter, and the health-region class.
    private const uint ConstantFilterA = UnknownConstantFilterComponentA.Class, ConstantFilterB = UnknownConstantFilterComponentB.Class, HealthRegions = UnknownHealthRegionsComponent.Class;
    private const uint HealthStateKey = 0x389FBDD7;
    /** The native matcher has 512 part bits and a three-bit disable counter per part. */
    private const int PartCapacity = 512, DisableCountMaximum = 7;
    public ISet<uint>? Reads { get; init; }
    private readonly Dictionary<uint, byte[]> data = [];
    private readonly List<uint> variantNames = [];
    private readonly List<EntityVariantParameter> parameterInfo = [];
    private uint selectedVariant;
    /** Region filters of every row of each variant component with no positive admitted weight. */
    private readonly List<Condition[][]> unselected = [];
    private sealed record Component(uint Tag, byte[] Data, int Offset, byte[] Resource, int ResourceOffset);
    private sealed record Condition((uint Key, uint Value)[] Required, (uint Key, uint Value)[] Excluded);
    private sealed record Spawn(uint DamageOverride, Dictionary<int, (byte[] Data, int Offset)> Overrides);

    /**
     * A zero spawn tag selects the native dynamic-class constructor defaults.
     * @param previewVariant Variant row name requested as a spawn would; zero requests none.
     * @param spawnPairs Variant parameter pairs the spawn passes; they apply after the authored spawn row's pairs.
     * @param picks Parameter values the user chose; they apply last, and a runtime pick stands in for player state.
     */
    public EntityInitialState Read(uint entityTag, uint componentConfig, uint spawnTag = 0, int spawnOffset = -1,
        uint previewVariant = 0, IReadOnlyList<(uint Key, uint Value)>? spawnPairs = null,
        IReadOnlyDictionary<uint, uint>? picks = null)
    {
        data.Clear();
        variantNames.Clear();
        parameterInfo.Clear();
        unselected.Clear();
        selectedVariant = 0;
        try
        {
            var definition = Get(entityTag);
            var configs = EntityFields.Rows<SEntity, SPatternComponent>(definition, 0, nameof(SEntity.Components),
                SPatternComponent.Class);
            var spawn = ReadSpawn(entityTag, spawnTag, spawnOffset, configs.Length);
            var conditions = new List<Condition>();
            var descriptors = EntityFields.Rows<SEntity, ActorDescriptorRow>(definition, 0,
                $"{nameof(SEntity.H83bfb495)}.{nameof(C80809c1c.InterfaceDescriptors)}", ActorDescriptorRow.Class);
            var buckets = EntityFields.Rows<SEntity, UnknownEntityInterfaceMatch>(definition, 0,
                $"{nameof(SEntity.H83bfb495)}.{nameof(C80809c1c.H72b7ff87)}.{nameof(C80809c23.Buckets)}",
                UnknownEntityInterfaceMatch.Class);
            int match = -1, matches = 0;
            for (int bucket = 0; bucket < buckets.Length; bucket++)
                if (buckets[bucket].InterfaceHash == PostInitHash) { if (matches++ == 0) match = bucket; }
            if (matches > 1) throw new InvalidDataException("Duplicate initial-state interface.");
            if (matches == 1)
            {
                int first = buckets[match].FirstDescriptor, count = buckets[match].DescriptorCount & 0x7FFF;
                if (first > descriptors.Length - count) throw new InvalidDataException("Initial-state interface exceeds its rows.");
                foreach (ref readonly var row in descriptors.Slice(first, count))
                {
                    if (row.Condition != 0) throw new NotSupportedException("Conditional initial-state interface.");
                    var map = Get(row.InterfaceMapHash);
                    ref readonly var interfaceMap = ref TagView.At<SInterfaceMap>(map);
                    var methods = EntityFields.Rows(map, in interfaceMap.Slots, UnknownInterfaceMethod.Class);
                    if (interfaceMap.UnkC != C80809683.Class || methods.Length == 0)
                        throw new InvalidDataException("Initial-state interface has another type.");
                    uint type = methods[0].ComponentType, method = methods[0].MethodIndex;
                    var component = Resolve(row.Config.Tag, row.Config.Offset, type);
                    int ordinal = -1;
                    for (int config = 0; config < configs.Length; config++)
                        if (configs[config].Config == component.Tag) { ordinal = config; break; }
                    if (ordinal < 0) throw new NotSupportedException("Initial-state component belongs to another object.");
                    spawn.Overrides.TryGetValue(ordinal, out var optional);
                    switch (type, method)
                    {
                        case (Damage, 42): ReadDamage(component, spawn.DamageOverride, conditions); break;
                        case (Character, 9): conditions.Add(new([], [(ShapeKey, InitialShape)])); break;
                        case (Variant, 3): ReadVariant(component, optional, spawnPairs ?? [], previewVariant, picks, conditions); break;
                        case (RegionState, 3): ReadRegion(component, conditions); break;
                        case (ConstantFilterA, 6): conditions.Add(new([], [(0x3692AC32, 0xA88A7C4A)])); break;
                        case (ConstantFilterB, 4): conditions.Add(new([], [(EntityPartLabels.GearAppliedKey, EntityPartLabels.False)])); break;
                        case (HealthRegions, 15): ReadHealthRegions(component, conditions); break;
                        default: throw new NotSupportedException($"Initial-state component {type:X8}, method {method}.");
                    }
                }
            }
            var target = Get(componentConfig);
            var parts = LogicalParts(target);
            if (parts.Length > PartCapacity) throw new InvalidDataException("Part count exceeds the native matcher capacity.");
            var enabled = new bool[parts.Length];
            var everyVariant = new bool[parts.Length];
            for (int part = 0; part < parts.Length; part++)
            {
                var labels = Labels(target, in parts[part].HCd67ad14.Labels);
                int disabled = conditions.Count(condition => Matches(condition, labels));
                if (disabled > DisableCountMaximum) throw new InvalidDataException("Initial filters overflow a native part counter.");
                enabled[part] = disabled == 0;
                everyVariant[part] = unselected.Any(rows => rows.All(row => row.Any(condition => Matches(condition, labels))));
            }
            return new(enabled, null)
            {
                Variants = variantNames.Distinct().ToArray(), SelectedVariant = selectedVariant,
                DisabledByEveryVariant = unselected.Count == 0 ? null : everyVariant, Parameters = ParameterList(componentConfig)
            };
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException
            or IndexOutOfRangeException or OverflowException or KeyNotFoundException)
        { return new(null, error.Message) { Variants = variantNames.Distinct().ToArray(), Parameters = ParameterList(componentConfig) }; }
    }

    /** The recorded parameters; a runtime one with no authored values lists the values its part labels name. */
    private EntityVariantParameter[] ParameterList(uint componentConfig)
    {
        var labels = new List<(uint Key, uint Value)>();
        if (parameterInfo.Any(parameter => parameter is { Runtime: true, Values.Count: 0 }))
            try
            {
                var target = Get(componentConfig);
                foreach (ref readonly var part in LogicalParts(target))
                    labels.AddRange(Labels(target, in part.HCd67ad14.Labels));
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or IndexOutOfRangeException
                or OverflowException or KeyNotFoundException) { }
        return parameterInfo.Select(parameter => parameter is { Runtime: true, Values.Count: 0 }
            ? parameter with { Values = labels.Where(label => label.Key == parameter.Key).Select(label => label.Value).Distinct().ToArray() }
            : parameter).ToArray();
    }

    /** Logical parts of the config's resource; cloth has its own layout, every other resource uses the model one. */
    private static ReadOnlySpan<UnknownLogicalPart> LogicalParts(ReadOnlySpan<byte> tag)
    {
        int resource = TagView.TargetOffset(tag,
            in TagView.Member<SEntityConfig, RelativePointer<Unknown>>(tag, 0, nameof(SEntityConfig.Definition)))
            ?? throw new InvalidDataException("Initial-state config has no model resource.");
        return TagView.ClassAt(tag, resource) == SClothModelComponent.Class
            ? EntityFields.Rows<SClothModelComponent, UnknownLogicalPart>(tag, resource,
                nameof(SClothModelComponent.LogicalParts), UnknownLogicalPart.Class)
            : EntityFields.Rows<SModelComponent, UnknownLogicalPart>(tag, resource,
                nameof(SModelComponent.LogicalParts), UnknownLogicalPart.Class);
    }

    /** The export's back-reference names the component whose constructor ran. */
    private Component Resolve(uint tag, long exportOffset, uint type)
    {
        var exported = Get(tag);
        int start = Offset(exported, exportOffset, 16);
        // A null export pointer names no component.
        if (new TagPointer(exported, start).Target is not { } header
            || !TagView.Fits<TagOffsetPointer>(exported, header) || TagOffsetPointer.Read(exported, header).Word != type)
            throw new InvalidDataException("Initial-state export has another component type.");
        var export = TagOffsetPointer.Read(exported, header);
        uint owner = export.Tag;
        var component = Get(owner);
        int offset = Offset(component, export.Offset, 48);
        var link = TagOffsetPointer.Read(component, offset);
        var resource = Get(link.Tag);
        int resourceOffset = Offset(resource, link.Offset, 16);
        var back = TagOffsetPointer.Read(resource, resourceOffset);
        if (back.Tag != owner || back.Word != type || back.Offset != offset)
            throw new InvalidDataException("Initial-state component resource is not reciprocal.");
        return new(owner, component, offset, resource, resourceOffset);
    }

    /** Spawn overrides are keyed by the root's authored component ordinal. */
    private Spawn ReadSpawn(uint entityTag, uint tag, int offset, int componentCount)
    {
        if (tag == 0) return new(StringHash.Empty, []);
        var source = Get(tag);
        if (!TagView.Fits<AuthoredPlacementEntry>(source, offset)
            || TagView.At<AuthoredPlacementEntry>(source, offset).ClassTag != entityTag)
            throw new NotSupportedException("Placed spawn inputs are unavailable.");
        ref readonly var entry = ref TagView.At<AuthoredPlacementEntry>(source, offset);
        // The parent at +48 and the bone at +96 place the entity; no part filter reads them.
        var overrides = new Dictionary<int, (byte[], int)>();
        var seen = new HashSet<int>();
        // Each override node starts like C80803d86: next link, then component ordinal.
        for (int? next = TagView.TargetOffset(source, in entry.Payload); next is { } node;)
        {
            if (node < TagView.ClassWordSize || !TagView.Fits<C80803d86>(source, node) || !seen.Add(node)
                || seen.Count > componentCount)
                throw new InvalidDataException("Spawn override chain is invalid.");
            ref readonly var link = ref TagView.At<C80803d86>(source, node);
            int ordinal = link.ComponentOrdinal;
            if (ordinal >= 0 && (ordinal >= componentCount || !overrides.TryAdd(ordinal, (source, node))))
                throw new InvalidDataException("Spawn override ordinal is invalid.");
            next = TagView.TargetOffset(source, in link.Next);
        }
        return new(entry.NameHash, overrides);
    }

    /** A named spawn override replaces the per-part damage-state filters. */
    private static void ReadDamage(Component component, uint damageOverride, List<Condition> conditions)
    {
        var resource = component.Resource;
        int at = component.ResourceOffset;
        if (damageOverride != StringHash.Empty)
        {
            var named = EntityFields.Rows<SCombatantDamagePayload, NamedDamageCondition>(resource, at,
                nameof(SCombatantDamagePayload.HBfa81d24));
            int found = -1, matches = 0;
            for (int i = 0; i < named.Length; i++)
                if (named[i].NameHash == damageOverride) { found = i; matches++; }
            if (matches != 1) throw new NotSupportedException("Damage override is not one named initial state.");
            conditions.Add(ReadCondition(in named[found]));
            return;
        }
        var authored = EntityFields.Rows<SCombatantDamagePayload, UnknownDamageState>(resource, at,
            nameof(SCombatantDamagePayload.H7a9059b4), UnknownDamageState.Class);
        var current = EntityFields.Rows<UnknownDamageComponent, UnknownDamageStateCurrent>(component.Data, component.Offset,
            nameof(UnknownDamageComponent.DamageStates), UnknownDamageStateCurrent.Class);
        if (authored.Length != current.Length) throw new InvalidDataException("Damage-state counts disagree.");
        for (int i = 0; i < authored.Length; i++)
        {
            var choices = EntityFields.Rows(resource, in authored[i].PartFilters, UnknownDamageChoice.Class);
            int selected = current[i].SelectedChoice;
            if (selected >= choices.Length) throw new InvalidDataException("Damage-state index is outside its choices.");
            conditions.Add(ReadCondition(in choices[selected]));
        }
    }

    /** Parts without the selector's state label remain enabled. */
    private static void ReadRegion(Component component, List<Condition> conditions)
    {
        var resource = component.Resource;
        int at = component.ResourceOffset;
        var choices = EntityFields.Rows<RegionStateResource, UnknownRegionStateChoice>(resource, at,
            nameof(RegionStateResource.StateChoices), UnknownRegionStateChoice.Class);
        int selected = TagView.Member<UnknownRegionStateComponent, int>(component.Data, component.Offset,
            nameof(UnknownRegionStateComponent.SelectedChoice));
        if ((uint)selected >= choices.Length) throw new NotSupportedException("Region state has no initial choice.");
        uint region = TagView.Member<RegionStateResource, uint>(resource, at, nameof(RegionStateResource.Region));
        conditions.Add(new(region == StringHash.Empty ? [] : [(RegionKey, region)], [(StateKey, choices[selected].State)]));
    }

    /**
     * Each enabled health block filters its region by its start state.
     * The constructor sets the state when the start health exceeds the block's Unk138 float.
     */
    private static void ReadHealthRegions(Component component, List<Condition> conditions)
    {
        var resource = component.Resource;
        int at = component.ResourceOffset;
        if (!TagView.FitsThrough<C80803910>(resource, at, nameof(C80803910.HDafa0277)))
            throw new InvalidDataException("Health region block leaves its tag.");
        foreach (var (member, startHealth) in new[]
        {
            (nameof(C80803910.H3f32b3a9), 1f), (nameof(C80803910.H7ae028c7), 0f), (nameof(C80803910.HDafa0277), 0f)
        })
        {
            ref readonly var block = ref TagView.Member<C80803910, C80803913>(resource, at, member);
            if (block.Enabled == 0) continue;
            uint state = startHealth > block.Unk138 ? block.Unk1a8 : block.Unk1ac;
            conditions.Add(new([(RegionKey, block.Unk1a4)], [(HealthStateKey, state)]));
        }
    }

    /**
     * Stored parameters admit variants before an explicit choice or weighted fallback.
     * @param pairs Spawn pairs; each one with a key overrides its parameter like an authored pair.
     * @param preview Variant row name to request; its own labels replace the stored parameters.
     * @param picks User choices; they replace all of the above.
     */
    private void ReadVariant(Component component, (byte[] Data, int Offset) optional,
        IReadOnlyList<(uint Key, uint Value)> pairs, uint preview, IReadOnlyDictionary<uint, uint>? picks,
        List<Condition> conditions)
    {
        ReadOnlySpan<byte> bytes = component.Resource;
        int at = component.ResourceOffset;
        var variants = EntityFields.Rows<UnknownVariantResource, UnknownVariantRow>(bytes, at,
            nameof(UnknownVariantResource.Variants), UnknownVariantRow.Class);
        foreach (ref readonly var row in variants) variantNames.Add(row.VariantName);
        uint requested = StringHash.Empty;
        var overrides = new Dictionary<uint, uint>();
        if (optional.Data is { } input)
        {
            if (TagView.ClassAt(input, optional.Offset) != UnknownVariantSpawnOverride.Class)
                throw new NotSupportedException("Variant spawn override has another type.");
            // A later pair for the same key wins.
            foreach (ref readonly var row in EntityFields.Rows<UnknownVariantSpawnOverride, C8080389f>(input, optional.Offset,
                nameof(UnknownVariantSpawnOverride.ParameterPairs)))
            {
                uint key = row.Unk0, value = row.Unk4;
                overrides[key] = key == RuntimeParameter && value == StringHash.Empty ? RuntimeEmptyValue : value;
            }
            requested = TagView.Member<UnknownVariantSpawnOverride, uint>(input, optional.Offset,
                nameof(UnknownVariantSpawnOverride.RequestedVariant));
        }
        // 0x7FF742B14630 copies only pairs whose key is not empty.
        foreach (var (key, value) in pairs)
            if (key != StringHash.Empty) overrides[key] = key == RuntimeParameter && value == StringHash.Empty ? RuntimeEmptyValue : value;
        if (preview != 0)
        {
            int index = -1;
            for (int row = 0; row < variants.Length; row++)
                if (variants[row].VariantName == preview) { index = row; break; }
            if (index < 0) throw new NotSupportedException("Preview variant is not an authored variant row.");
            foreach (var (key, value) in Labels(bytes, in variants[index].VariantRequirements.Labels)) overrides[key] = value;
            requested = preview;
        }
        foreach (var (key, value) in picks ?? new Dictionary<uint, uint>()) overrides[key] = value;
        var parameters = new List<Condition>();
        var parameterRows = EntityFields.Rows<UnknownVariantResource, C808038aa>(bytes, at,
            nameof(UnknownVariantResource.Parameters));
        var current = EntityFields.Rows<UnknownVariantComponent, SU32HashElement>(component.Data, component.Offset,
            nameof(UnknownVariantComponent.H128a3e16));
        if (current.Length != parameterRows.Length) throw new InvalidDataException("Variant parameter counts disagree.");
        // Every row is recorded before an unresolved one stops the read, so the picker lists them all.
        Exception? unresolved = null;
        for (int i = 0; i < parameterRows.Length; i++)
        {
            ref readonly var row = ref parameterRows[i];
            uint key = row.Unk4;
            var values = EntityFields.Rows(bytes, in row.Values);
            uint[] choices = new uint[values.Length];
            for (int choice = 0; choice < values.Length; choice++) choices[choice] = values[choice].KeyHash;
            uint value = overrides.GetValueOrDefault(key, current[i].KeyHash);
            bool runtime = key == RuntimeParameter;
            // An empty or unlisted value takes choice 0; the runtime key reads player state unless the user picked it.
            if ((value == StringHash.Empty || !choices.Contains(value)) && !(runtime && picks?.ContainsKey(key) == true))
            {
                if (runtime) unresolved ??= new NotSupportedException("Variant parameter needs player state.");
                else if (choices.Length == 0) unresolved ??= new InvalidDataException("Variant parameter has no choices.");
                value = runtime || choices.Length == 0 ? 0 : choices[0];
            }
            parameterInfo.Add(new(key, choices, value, runtime));
            parameters.Add(new([], [(key, value)]));
        }
        if (unresolved is not null) throw unresolved;
        // Indices into variants; a row is admitted when no parameter condition disables it.
        var admitted = new List<int>();
        for (int row = 0; row < variants.Length; row++)
        {
            bool rejected = false;
            foreach (var condition in parameters)
                if (Matches(condition, Labels(bytes, in variants[row].VariantRequirements.Labels))) { rejected = true; break; }
            if (!rejected) admitted.Add(row);
        }
        var regions = EntityFields.Rows<UnknownVariantResource, SU32HashElement>(bytes, at,
            nameof(UnknownVariantResource.RegionKeys));
        conditions.AddRange(parameters);
        int chosen = -1;
        if (requested != StringHash.Empty)
            for (int i = 0; i < admitted.Count; i++)
                if (variants[admitted[i]].VariantName == requested) { chosen = i; break; }
        if (chosen < 0 && preview != 0) throw new NotSupportedException("Preview variant fails its parameter choices.");
        if (chosen < 0)
        {
            // Enumerable.Sum over floats adds in double; keep that rounding.
            double total = 0;
            foreach (int row in admitted) total += variants[row].Weight;
            float weight = (float)total;
            // With no admitted weight the native selector returns -1; region filters are skipped.
            if (weight <= 0)
            {
                if (variants.Length != 0)
                {
                    var rows = new Condition[variants.Length][];
                    for (int row = 0; row < variants.Length; row++) rows[row] = Filters(bytes, regions, in variants[row]);
                    unselected.Add(rows);
                }
                return;
            }
            // A zero random draw can select a zero-weight row before the first positive row.
            if (admitted.Count != 1 || !float.IsFinite(weight))
                throw new NotSupportedException("Initial variant is a weighted random choice.");
            chosen = 0;
        }
        conditions.AddRange(Filters(bytes, regions, in variants[admitted[chosen]]));
        if (selectedVariant == 0) selectedVariant = variants[admitted[chosen]].VariantName;
    }

    /** Region filters of one variant row: each region key pairs with the row's value at the same index. */
    private static Condition[] Filters(ReadOnlySpan<byte> tag, ReadOnlySpan<SU32HashElement> regions, in UnknownVariantRow row)
    {
        var values = EntityFields.Rows(tag, in row.RegionValues);
        if (regions.Length != values.Length) throw new InvalidDataException("Variant region counts disagree.");
        var filters = new Condition[regions.Length];
        for (int i = 0; i < regions.Length; i++)
        {
            uint key = regions[i].KeyHash;
            filters[i] = new Condition(key == StringHash.Empty ? [] : [(RegionKey, key)], [(VariantKey, values[i].KeyHash)]);
        }
        return filters;
    }

    /** Each native condition has room for three required and three excluded key/value pairs. */
    private static Condition ReadCondition(in UnknownDamageChoice row)
    {
        var condition = ConditionOf(row.RequiredCount, row.ExcludedCount);
        for (int i = 0; i < condition.Required.Length; i++)
            condition.Required[i] = (row.RequiredPairs[i].Unk0, row.RequiredPairs[i].Unk4);
        for (int i = 0; i < condition.Excluded.Length; i++)
            condition.Excluded[i] = (row.ExcludedPairs[i].Unk0, row.ExcludedPairs[i].Unk4);
        return condition;
    }

    /** A named override holds the same condition after its name. */
    private static Condition ReadCondition(in NamedDamageCondition row)
    {
        var condition = ConditionOf(row.RequiredCount, row.ExcludedCount);
        for (int i = 0; i < condition.Required.Length; i++)
            condition.Required[i] = (row.RequiredPairs[i].Unk0, row.RequiredPairs[i].Unk4);
        for (int i = 0; i < condition.Excluded.Length; i++)
            condition.Excluded[i] = (row.ExcludedPairs[i].Unk0, row.ExcludedPairs[i].Unk4);
        return condition;
    }

    /** An empty condition sized for the counts; a count over the three fixed pairs throws. */
    private static Condition ConditionOf(int required, int excluded) =>
        required is < 0 or > 3 || excluded is < 0 or > 3
            ? throw new InvalidDataException("Part filter count exceeds its fixed fields.")
            : new(new (uint, uint)[required], new (uint, uint)[excluded]);

    private static HashSet<(uint, uint)> Labels(ReadOnlySpan<byte> tag, ref readonly TagArray<UnknownPartLabel> array)
    {
        var labels = new HashSet<(uint, uint)>();
        foreach (ref readonly var row in EntityFields.Rows(tag, in array, UnknownPartLabel.Class))
            labels.Add((row.LabelKey, row.LabelValue));
        return labels;
    }

    private static bool Matches(Condition condition, HashSet<(uint Key, uint Value)> labels) =>
        condition.Required.All(labels.Contains) && condition.Excluded.Any(pair =>
            labels.Any(label => label.Key == pair.Key) && !labels.Contains(pair));

    private static int Offset(byte[] source, long offset, int size) => offset >= 0 && offset <= source.Length - size
        ? (int)offset : throw new InvalidDataException("Initial-state pointer is outside its tag.");

    private byte[] Get(uint tag)
    {
        Reads?.Add(tag);
        if (!data.TryGetValue(tag, out var value)) data[tag] = value = catalog.Read(tag);
        return value;
    }
}
