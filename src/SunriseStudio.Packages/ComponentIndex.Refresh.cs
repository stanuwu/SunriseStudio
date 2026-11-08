using System.Buffers.Binary;
using System.Collections.Immutable;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Packages;

public sealed partial class ComponentIndex
{
    private sealed record ConfigFacts(uint Primary, string? Prompt, bool Particles, bool Audio,
        string? Combatant, string? Pattern, uint? Sound, uint[] Classes, uint[] Dependencies,
        uint[] StringHashes);
    private ImmutableDictionary<uint, ConfigFacts> configs = ImmutableDictionary<uint, ConfigFacts>.Empty;
    private ImmutableDictionary<uint, uint[]> primaryConfigs = ImmutableDictionary<uint, uint[]>.Empty;
    private ImmutableDictionary<uint, uint[]> entityConfigs = ImmutableDictionary<uint, uint[]>.Empty;
    private ImmutableDictionary<uint, uint[]> dependents = ImmutableDictionary<uint, uint[]>.Empty;
    private ImmutableDictionary<uint, uint[]> stringDependents = ImmutableDictionary<uint, uint[]>.Empty;
    private sealed class BuildState
    {
        internal readonly Dictionary<uint, ConfigFacts> Configs = [];
        internal readonly Dictionary<uint, uint[]> Entities = [];
        internal readonly Dictionary<uint, HashSet<uint>> Dependencies = [], Strings = [], Primaries = [];
    }
    private BuildState? building;

    private static void AddLinks(Dictionary<uint, HashSet<uint>> map, uint owner, IEnumerable<uint> links)
    {
        foreach (uint tag in links)
        {
            if (!map.TryGetValue(tag, out var users)) map.Add(tag, users = []);
            users.Add(owner);
        }
    }

    /** Private initial and cache loads freeze each dependency set once before publication. */
    private void FinishBuild()
    {
        var state = building ?? throw new InvalidOperationException("Component build is not active.");
        configs = state.Configs.ToImmutableDictionary();
        entityConfigs = state.Entities.ToImmutableDictionary();
        dependents = Freeze(state.Dependencies); stringDependents = Freeze(state.Strings);
        primaryConfigs = Freeze(state.Primaries);
        building = null;

        static ImmutableDictionary<uint, uint[]> Freeze(Dictionary<uint, HashSet<uint>> map) =>
            map.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.ToArray());
    }

    /** Current validated model links retain entity ownership across config replacement. */
    public IReadOnlyList<uint> Models(uint entity) => (entityConfigs.GetValueOrDefault(entity) ?? [])
        .Select(config => modelTags.GetValueOrDefault(config)).Where(tag => tag != 0).Distinct().ToArray();

    /** True when one of the entity's configs has an ActorGroupComponent root. */
    public bool HasActorGroup(uint entity) => (entityConfigs.GetValueOrDefault(entity) ?? []).Any(config =>
        configs.TryGetValue(config, out var facts) && facts.Primary == ComponentRoles.CombatantComponent);

    /** Snapshots share immutable dependency sets; package reads touch only affected rows. */
    private ComponentIndex(ComponentIndex source)
    {
        slots = source.slots;
        configs = source.configs; entityConfigs = source.entityConfigs; primaryConfigs = source.primaryConfigs;
        dependents = source.dependents; stringDependents = source.stringDependents;
        Copy(source.primaries, primaries); Copy(source.prompts, prompts);
        Copy(source.entityKinds, entityKinds); Copy(source.referenceLabels, referenceLabels);
        Copy(source.soundIds, soundIds); Copy(source.entitySounds, entitySounds);
        Copy(source.patterns, patterns); Copy(source.combatants, combatants);
        Copy(source.objectTypes, objectTypes); Copy(source.nameHashes, nameHashes);
        Copy(source.modelTags, modelTags);
        BuildTime = source.BuildTime;
    }

    private static void Copy<T>(Dictionary<uint, T> from, Dictionary<uint, T> to)
    { to.EnsureCapacity(from.Count); foreach (var pair in from) to.Add(pair.Key, pair.Value); }

    /** Removes owner from tags it no longer links to and adds it to its new tags. */
    private static ImmutableDictionary<uint, uint[]> Links(
        ImmutableDictionary<uint, uint[]> map, uint owner, IEnumerable<uint> before, IEnumerable<uint> after)
    {
        var previous = before.ToHashSet();
        var next = after.ToHashSet();
        foreach (uint tag in previous.Except(next))
            if (map.TryGetValue(tag, out var prior))
            {
                var retained = prior.Where(value => value != owner).ToArray();
                map = retained.Length == 0 ? map.Remove(tag) : map.SetItem(tag, retained);
            }
        foreach (uint tag in next.Except(previous))
        {
            var users = map.GetValueOrDefault(tag) ?? [];
            if (!users.Contains(owner)) map = map.SetItem(tag, [.. users, owner]);
        }
        return map;
    }

    /** Records a config's facts and links; outside a build, null removes the config. */
    private void SetConfig(uint tag, ConfigFacts? facts)
    {
        if (building is { } state)
        {
            if (facts is null) return;
            state.Configs.Add(tag, facts);
            AddLinks(state.Dependencies, tag, facts.Dependencies);
            AddLinks(state.Strings, tag, facts.StringHashes);
            AddLinks(state.Primaries, tag, [facts.Primary]);
            return;
        }
        configs.TryGetValue(tag, out var before);
        dependents = Links(dependents, tag, before?.Dependencies ?? [], facts?.Dependencies ?? []);
        stringDependents = Links(stringDependents, tag, before?.StringHashes ?? [], facts?.StringHashes ?? []);
        primaryConfigs = Links(primaryConfigs, tag, before is null ? [] : [before.Primary], facts is null ? [] : [facts.Primary]);
        configs = facts is null ? configs.Remove(tag) : configs.SetItem(tag, facts);
    }

    /** Rereads one config; a config that fails to parse keeps only its dependencies. */
    private void ReadConfig(PackageCatalog catalog, uint tag, LocalizedStrings? strings, Dictionary<uint, string?>? behaviourPatterns = null)
    {
        primaries.Remove(tag); prompts.Remove(tag); modelTags.Remove(tag);
        if (!catalog.TryGetEntry(tag, out var entry) || !AssetCategories.IsStructured(entry) || entry.Reference != ConfigClass)
        { ObserveSlotConfig(tag, []); SetConfig(tag, null); return; }
        var dependencies = new HashSet<uint>();
        var hashes = new HashSet<uint>();
        bool observedSlots = false;
        try
        {
            byte[] data = catalog.Read(tag);
            ObserveSlotConfig(tag, data);
            observedSlots = true;
            for (int offset = TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.ReferenceScanStart));
                 offset <= data.Length - 4; offset += 4)
            {
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
                if (value >= 0x80000000 && value != uint.MaxValue) dependencies.Add(value);
            }
            if (TryReadModelTag(tag, data, out uint model)) modelTags[tag] = model;
            var classes = new HashSet<uint>();
            var named = new List<(uint Tag, uint Class)>();
            var facts = ComponentRoles.ReadConfig(catalog, data, strings, classes, named);
            string? combatant = null, pattern = null;
            uint? sound = null;
            foreach (var (linked, type) in named)
            {
                if (type == ComponentRoles.CombatantNameRecord)
                {
                    byte[] record = catalog.Read(linked);
                    const string name = nameof(ActorStateMachineDefinition.DisplayNameHash);
                    if (TagView.FitsThrough<ActorStateMachineDefinition>(record, 0, name))
                    {
                        uint hash = TagRead.U32(record, TagView.FieldOffset<ActorStateMachineDefinition>(name));
                        hashes.Add(hash); combatant ??= strings?.Get(hash)?.Trim();
                    }
                }
                else if (type == SoundClass) sound ??= linked;
                else
                {
                    string? found;
                    if (behaviourPatterns is null || !behaviourPatterns.TryGetValue(linked, out found))
                    {
                        found = PatternName(catalog.Read(linked));
                        if (behaviourPatterns is not null) behaviourPatterns[linked] = found;
                    }
                    pattern ??= found;
                }
            }
            primaries[tag] = facts.Primary;
            if (facts.Prompt is { } prompt) prompts[tag] = prompt;
            SetConfig(tag, new(facts.Primary, facts.Prompt, facts.Particles, facts.Audio,
                combatant, pattern, sound, classes.ToArray(), dependencies.ToArray(), hashes.ToArray()));
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException
            or IndexOutOfRangeException or KeyNotFoundException or OverflowException)
        {
            if (!observedSlots) ObserveSlotConfig(tag, []);
            primaries[tag] = 0; prompts.Remove(tag); modelTags.Remove(tag);
            SetConfig(tag, new(0, null, false, false, null, null, null, [], dependencies.ToArray(), hashes.ToArray()));
        }
    }

    /** The component scan shares parsed descriptor facts, never retained package byte arrays. */
    private void ObserveSlotConfig(uint tag, ReadOnlySpan<byte> bytes)
    {
        if (slotBuilder is null && slotUpdates is null) return;
        var seed = ComponentSlotCatalog.ParseConfig(tag, bytes);
        slotBuilder?.ObserveConfig(seed);
        if (slotUpdates is not null) slotUpdates[tag] = seed;
    }

    /** Rereads one entity's configs and names, and its kind from its configs' facts. */
    private void ReadEntity(PackageCatalog catalog, uint tag)
    {
        uint[] oldHashes = nameHashes.TryGetValue(tag, out uint oldHash) ? [oldHash] : [];
        prompts.Remove(tag); combatants.Remove(tag); patterns.Remove(tag); entitySounds.Remove(tag);
        objectTypes.Remove(tag); nameHashes.Remove(tag); entityKinds.Remove(tag);
        uint[] links = [];
        if (catalog.TryGetEntry(tag, out var entry) && AssetCategories.IsStructured(entry) && entry.Reference == EntityClass)
        {
            byte[] data = catalog.Read(tag);
            links = EntityConfigs(data);
            if (ComponentRoles.ObjectTypeIndex(data) is { } type) objectTypes[tag] = type;
            if (ComponentRoles.InternalNameHash(data) is { } hash) nameHashes[tag] = hash;
            var facts = links.Select(config => building is { } state ? state.Configs.GetValueOrDefault(config)
                : configs.GetValueOrDefault(config)).OfType<ConfigFacts>().ToArray();
            string[] names = facts.Select(row => row.Combatant).OfType<string>().Distinct().ToArray();
            if (names.Length == 1) combatants[tag] = names[0];
            string[] paths = facts.Select(row => row.Pattern).OfType<string>().Distinct().ToArray();
            if (paths.Length == 1) patterns[tag] = paths[0];
            uint[] sounds = facts.Where(row => row.Sound.HasValue).Select(row => row.Sound!.Value).Distinct().ToArray();
            if (sounds.Length == 1) entitySounds[tag] = sounds[0];
            string? prompt = facts.Select(row => row.Prompt).FirstOrDefault(value => value is not null);
            if (prompt is not null) prompts[tag] = prompt;
            entityKinds[tag] = ComponentRoles.EntityKind(new(facts.Select(row => row.Primary).Where(value => value != 0).ToHashSet(),
                prompt is not null, facts.Any(row => row.Particles), facts.Any(row => row.Audio), names.Length == 1), slots);
        }
        uint[] hashes = nameHashes.TryGetValue(tag, out uint newHash) ? [newHash] : [];
        if (building is { } pending)
        {
            pending.Entities.Add(tag, links);
            AddLinks(pending.Dependencies, tag, links); AddLinks(pending.Strings, tag, hashes);
            return;
        }
        stringDependents = Links(stringDependents, tag, oldHashes, hashes);
        dependents = Links(dependents, tag, entityConfigs.GetValueOrDefault(tag) ?? [], links);
        entityConfigs = links.Length == 0 ? entityConfigs.Remove(tag) : entityConfigs.SetItem(tag, links);
    }

    private void ReadSound(PackageCatalog catalog, uint tag)
    {
        soundIds.Remove(tag);
        if (!catalog.TryGetEntry(tag, out var entry) || !AssetCategories.IsStructured(entry) || entry.Reference != SoundClass) return;
        byte[] data = catalog.Read(tag);
        const string id = nameof(SWwiseEvent.WwiseEventId);
        if (TagView.FitsThrough<SWwiseEvent>(data, 0, id)) soundIds[tag] = TagRead.U32(data, TagView.FieldOffset<SWwiseEvent>(id));
    }

    /** Labels an unnamed class "uses X" when 90% of its 10 or more configs reference class X. */
    private void RebuildReferenceLabels(IEnumerable<uint>? types = null)
    {
        foreach (uint type in types ?? primaryConfigs.Keys)
        {
            referenceLabels.Remove(type);
            if (IsSlotComponent(type) || ComponentRoles.Label(type) is not null || !primaryConfigs.TryGetValue(type, out var members)
                || members.Length < 10) continue;
            var best = members.Order().SelectMany(tag => configs[tag].Classes
                    .Where(linked => linked != ConfigClass && ClassNames.Get(linked) is not null))
                .GroupBy(linked => linked).Where(values => values.Count() * 10 >= members.Length * 9)
                .OrderByDescending(values => values.Count()).FirstOrDefault();
            if (best is not null) referenceLabels[type] = $"uses {ClassNames.Words(best.Key)}";
        }
    }

    /** Missing references remain indexed so newly added tags invalidate their users. */
    public ComponentIndex Refresh(PackageCatalog catalog, LocalizedStrings? strings, IEnumerable<uint> changedTags,
        IEnumerable<uint> changedStringHashes, out IReadOnlySet<uint> affected)
    {
        var tags = changedTags.ToHashSet();
        foreach (uint hash in changedStringHashes)
            if (stringDependents.TryGetValue(hash, out var users)) tags.UnionWith(users);
        var pending = new Queue<uint>(tags);
        while (pending.TryDequeue(out uint tag))
            if (dependents.TryGetValue(tag, out var users))
                foreach (uint user in users) if (tags.Add(user)) pending.Enqueue(user);
        var changedConfigs = tags.Where(tag => configs.ContainsKey(tag) || IsClass(tag, ConfigClass)).ToArray();
        var changedEntities = tags.Where(tag => entityKinds.ContainsKey(tag) || IsClass(tag, EntityClass)).ToArray();
        var sounds = tags.Where(tag => soundIds.ContainsKey(tag) || IsClass(tag, SoundClass)).ToArray();
        var affectedTags = changedConfigs.Concat(changedEntities).Concat(sounds).ToHashSet();
        ComponentSlotCatalog? refreshedSlots = null;
        IReadOnlySet<uint> changedClasses = new HashSet<uint>();
        if (affectedTags.Count == 0)
        {
            refreshedSlots = slots.Refresh(catalog, tags, out changedClasses);
            if (ReferenceEquals(slots, refreshedSlots)) { affected = affectedTags; return this; }
        }
        var result = new ComponentIndex(this) { slotUpdates = [] };
        foreach (uint tag in sounds) result.ReadSound(catalog, tag);
        foreach (uint tag in changedConfigs) result.ReadConfig(catalog, tag, strings);
        result.slots = refreshedSlots ?? slots.Refresh(catalog, tags, out changedClasses, result.slotUpdates);
        result.slotUpdates = null;
        foreach (uint tag in changedEntities) result.ReadEntity(catalog, tag);
        foreach (uint type in changedClasses)
        {
            var classConfigs = (primaryConfigs.GetValueOrDefault(type) ?? [])
                .Concat(result.primaryConfigs.GetValueOrDefault(type) ?? []).Distinct();
            foreach (uint config in classConfigs)
            {
                affectedTags.Add(config);
                if (!result.dependents.TryGetValue(config, out var entities)) continue;
                foreach (uint entity in entities)
                    if (result.entityKinds.ContainsKey(entity))
                    {
                        result.RefreshEntityKind(entity);
                        affectedTags.Add(entity);
                    }
            }
        }
        if (changedConfigs.Length != 0 || changedClasses.Count != 0)
        {
            var types = changedConfigs.SelectMany(tag => new[] { PrimaryClass(tag), result.PrimaryClass(tag) })
                .Concat(changedClasses).Distinct().ToArray();
            result.RebuildReferenceLabels(types);
            var labels = types.Where(type => referenceLabels.GetValueOrDefault(type) != result.referenceLabels.GetValueOrDefault(type));
            affectedTags.UnionWith(labels.SelectMany(type => result.primaryConfigs.GetValueOrDefault(type) ?? []));
        }
        affected = affectedTags;
        if (affected.Count == 0 && ReferenceEquals(slots, result.slots)) return this;
        return result;

        bool IsClass(uint tag, uint type) => catalog.TryGetEntry(tag, out var entry)
            && AssetCategories.IsStructured(entry) && entry.Reference == type;
    }

    /** A changed class-to-slot mapping reclassifies cached entities without rereading their payloads. */
    private void RefreshEntityKind(uint entity)
    {
        var facts = (entityConfigs.GetValueOrDefault(entity) ?? []).Select(config => configs.GetValueOrDefault(config))
            .OfType<ConfigFacts>().ToArray();
        entityKinds[entity] = ComponentRoles.EntityKind(new(facts.Select(row => row.Primary).Where(value => value != 0).ToHashSet(),
            facts.Any(row => row.Prompt is not null), facts.Any(row => row.Particles), facts.Any(row => row.Audio),
            combatants.ContainsKey(entity)), slots);
    }
}
