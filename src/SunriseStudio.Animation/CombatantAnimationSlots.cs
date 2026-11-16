using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Animation;

/** What a query key asks the body to do, from its spelling. */
public enum CombatantBehaviour { Unknown, Idle, Locomotion, Fire, Melee, Flinch, Evade, Death, Throw, Reaction, Spawn, Ability }

/** One definition name row and the clip its slot names; slot -1 and tag 0 mean no clip. */
public sealed record CombatantSlotClip(int NameRow, uint NameHash, string? RowName, int ClipSlot, uint ClipTag,
    float Seconds, byte Kind);

/** One key of the selector's query table and every name row its sets can pick. */
public sealed record CombatantQuery(uint KeyHash, string? Name, CombatantBehaviour Behaviour, int QueryIndex,
    uint QueryKind, IReadOnlyList<CombatantSlotClip> Rows);

/** An enemy's query keys, and the name rows no query reaches. */
public sealed record CombatantAnimationSet(uint EntityTag, uint SelectorConfig, uint DefinitionTag, uint QueryTable,
    IReadOnlyList<CombatantQuery> Queries, IReadOnlyList<CombatantSlotClip> OtherRows, int NameRowCount, int ClipSlotCount);

/** Lists which clips an enemy's sequence selector can play for each named action query. */
public static class CombatantAnimationSlots
{
    private const uint EmptyHash = 0x811C9DC5;

    /** Spellings whose FNV-1 hash is a query key or a name row of an installed enemy selector. */
    public static readonly IReadOnlyList<string> KnownNames =
    [
        "airborne", "airborne_land_high", "brace", "cloak", "dive_left", "dive_right", "evade_back", "evade_front",
        "evade_left", "evade_right", "finisher_enter", "fire_1", "flinch", "land_dead_back", "land_dead_front", "melee",
        "melee_leap", "new_target", "skiff_exit", "sprint_stop", "surprise_front", "taunt", "throw_grenade",
        "combat_rifle_any_idle", "combat_rifle_any_idle_var1", "combat_rifle_any_idle_var2"
    ];

    private static readonly Dictionary<uint, string> spelled = KnownNames.ToDictionary(Hash);

    /** FNV-1 over the name's characters, as the game hashes tags and queries. */
    public static uint Hash(string text)
    {
        uint result = EmptyHash;
        foreach (char value in text) result = unchecked(result * 0x01000193) ^ value;
        return result;
    }

    /** The behaviour a spelling names; Unknown for an unspelled key. */
    public static CombatantBehaviour Behaviour(string? name) => name switch
    {
        null => CombatantBehaviour.Unknown,
        _ when name.Contains("idle", StringComparison.Ordinal) => CombatantBehaviour.Idle,
        _ when name.StartsWith("land_dead", StringComparison.Ordinal) => CombatantBehaviour.Death,
        _ when name.StartsWith("airborne", StringComparison.Ordinal) || name.StartsWith("sprint", StringComparison.Ordinal)
            => CombatantBehaviour.Locomotion,
        _ when name.StartsWith("fire", StringComparison.Ordinal) => CombatantBehaviour.Fire,
        _ when name.StartsWith("melee", StringComparison.Ordinal) || name.StartsWith("finisher", StringComparison.Ordinal)
            => CombatantBehaviour.Melee,
        _ when name == "flinch" => CombatantBehaviour.Flinch,
        _ when name.StartsWith("evade", StringComparison.Ordinal) || name.StartsWith("dive", StringComparison.Ordinal)
            || name == "brace" => CombatantBehaviour.Evade,
        _ when name.StartsWith("throw", StringComparison.Ordinal) => CombatantBehaviour.Throw,
        _ when name is "taunt" or "new_target" || name.StartsWith("surprise", StringComparison.Ordinal)
            => CombatantBehaviour.Reaction,
        _ when name.EndsWith("_exit", StringComparison.Ordinal) => CombatantBehaviour.Spawn,
        _ when name == "cloak" => CombatantBehaviour.Ability,
        _ => CombatantBehaviour.Unknown
    };

    /**
     * Reads the entity's sequence selector, its definition and its query table.
     * @return Null when the entity has no selector component.
     */
    public static CombatantAnimationSet? Read(PackageCatalog catalog, uint entityTag)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (ClassOf(catalog, entityTag) != SEntity.Class) throw new NotSupportedException("A combatant read needs an entity.");
        byte[] entity = catalog.Read(entityTag);
        var (count, data, _) = TagArray.Checked(entity, TagView.FieldOffset<SEntity>(nameof(SEntity.Components)),
            SPatternComponent.Class, Unsafe.SizeOf<SPatternComponent>());
        int configField = TagView.FieldOffset<SPatternComponent>(nameof(SPatternComponent.Config));
        for (int i = 0; i < count; i++)
        {
            uint configTag = U32(entity, data + i * Unsafe.SizeOf<SPatternComponent>() + configField);
            if (ClassOf(catalog, configTag) != SEntityConfig.Class) continue;
            byte[] config = catalog.Read(configTag);
            int resource = Resource(config);
            if (resource < 0 || TagView.ClassAt(config, resource) != SAnimationStandSelector.Class) continue;
            return ReadSelector(catalog, entityTag, configTag, config, resource);
        }
        return null;
    }

    /** Walks every query key to its name rows; the rows nothing reaches are kept apart. */
    private static CombatantAnimationSet ReadSelector(PackageCatalog catalog, uint entityTag, uint configTag, byte[] config,
        int resource)
    {
        uint definitionTag = Field(config, resource, nameof(SAnimationStandSelector.AnimationDefinition));
        uint tableTag = Field(config, resource, nameof(SAnimationStandSelector.QueryTable));
        if (ClassOf(catalog, definitionTag) != SAnimationDefinition.Class)
            throw new InvalidDataException("The selector names no animation definition.");
        if (ClassOf(catalog, tableTag) != SAnimationQueryTable.Class) throw new InvalidDataException("The selector names no query table.");
        byte[] definition = catalog.Read(definitionTag);
        uint[] clips = ClipSlots(definition);
        var rows = NameRows(definition, clips);
        byte[] table = catalog.Read(tableTag);
        var (keyCount, keyData, _) = TagArray.Checked(table, TagView.FieldOffset<SAnimationQueryTable>(nameof(SAnimationQueryTable.Keys)),
            SAnimationQueryKey.Class, Unsafe.SizeOf<SAnimationQueryKey>());
        var (queryCount, queryData, _) = TagArray.Checked(table,
            TagView.FieldOffset<SAnimationQueryTable>(nameof(SAnimationQueryTable.Queries)), SAnimationQuery.Class,
            Unsafe.SizeOf<SAnimationQuery>());
        var queries = new List<CombatantQuery>();
        var reached = new HashSet<int>();
        for (int k = 0; k < keyCount; k++)
        {
            int at = keyData + k * Unsafe.SizeOf<SAnimationQueryKey>();
            uint key = U32(table, at);
            int index = checked((int)U32(table, at + 4));
            if ((uint)index >= (uint)queryCount) throw new InvalidDataException($"Query key {key:X8} names query {index}, past its table.");
            int query = queryData + index * Unsafe.SizeOf<SAnimationQuery>();
            uint kind = U32(table, query);
            var picked = new List<CombatantSlotClip>();
            foreach (int row in QueryRows(table, query, kind))
            {
                if ((uint)row >= (uint)rows.Length) throw new InvalidDataException($"Query {key:X8} names row {row}, past the definition.");
                if (!picked.Contains(rows[row])) picked.Add(rows[row]);
                reached.Add(row);
            }
            string? name = spelled.GetValueOrDefault(key);
            queries.Add(new(key, name, Behaviour(name), index, kind, picked.AsReadOnly()));
        }
        var other = rows.Where(row => !reached.Contains(row.NameRow)).ToArray();
        return new(entityTag, configTag, definitionTag, tableTag, queries.AsReadOnly(), Array.AsReadOnly(other), rows.Length,
            clips.Length);
    }

    /** Kind 1 holds one weighted set; kind 2 is a tag tree whose sets are all listed. */
    private static IEnumerable<int> QueryRows(byte[] table, int query, uint kind)
    {
        long relative = BinaryPrimitives.ReadInt64LittleEndian(table.AsSpan(query + 8));
        if (relative == 0) return [];
        int target = checked((int)(query + 8 + relative));
        uint targetClass = TagView.ClassAt(table, target) ?? 0;
        if (kind == 1 && targetClass == SAnimationSingleQuery.Class) return SetRows(table, target);
        if (kind == 2 && targetClass == SAnimationTreeQuery.Class)
        {
            var (count, data, _) = TagArray.Checked(table, target, SAnimationQuerySet.Class, Unsafe.SizeOf<SAnimationQuerySet>());
            return Enumerable.Range(0, count).SelectMany(set => SetRows(table, data + set * Unsafe.SizeOf<SAnimationQuerySet>())).ToArray();
        }
        throw new InvalidDataException($"Query kind {kind} over class {targetClass:X8} is not read.");
    }

    private static int[] SetRows(byte[] table, int set)
    {
        var (count, data, _) = TagArray.Checked(table, set, SAnimationQueryRow.Class, Unsafe.SizeOf<SAnimationQueryRow>());
        return Enumerable.Range(0, count).Select(i => checked((int)U32(table, data + i * Unsafe.SizeOf<SAnimationQueryRow>()))).ToArray();
    }

    private static uint[] ClipSlots(byte[] definition)
    {
        var (count, data, _) = TagArray.Checked(definition, TagView.FieldOffset<SAnimationDefinition>(nameof(SAnimationDefinition.Animations)),
            SAnimationClipSlot.Class, Unsafe.SizeOf<SAnimationClipSlot>());
        return Enumerable.Range(0, count).Select(i => U32(definition, data + i * Unsafe.SizeOf<SAnimationClipSlot>())).ToArray();
    }

    private static CombatantSlotClip[] NameRows(byte[] definition, uint[] clips)
    {
        var (count, data, _) = TagArray.Checked(definition,
            TagView.FieldOffset<SAnimationDefinition>(nameof(SAnimationDefinition.MotionDefinitions)), SAnimationNameRow.Class,
            Unsafe.SizeOf<SAnimationNameRow>());
        var rows = new CombatantSlotClip[count];
        for (int i = 0; i < count; i++)
        {
            ref readonly var row = ref TagView.Checked<SAnimationNameRow>(definition, data + i * Unsafe.SizeOf<SAnimationNameRow>());
            int slot = row.ClipSlot;
            uint clip = (uint)slot < (uint)clips.Length ? clips[slot] : 0;
            rows[i] = new(i, row.NameHash, spelled.GetValueOrDefault(row.NameHash), slot, clip, row.DurationSeconds, row.Kind);
        }
        return rows;
    }

    /** The config's resource offset, or -1 when it names none. */
    private static int Resource(byte[] config)
    {
        int field = TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition));
        if (field + 8 > config.Length) return -1;
        long relative = BinaryPrimitives.ReadInt64LittleEndian(config.AsSpan(field));
        long target = field + relative;
        return relative == 0 || target < 4 || target >= config.Length ? -1 : (int)target;
    }

    private static uint Field(byte[] config, int resource, string member)
    {
        int at = resource + TagView.FieldOffset<SAnimationStandSelector>(member);
        if (at + 4 > config.Length) throw new InvalidDataException("The selector resource ends early.");
        return U32(config, at);
    }

    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4));

    private static uint ClassOf(PackageCatalog catalog, uint tag) => catalog.TryGetEntry(tag, out var entry) ? entry.Reference : 0;
}
