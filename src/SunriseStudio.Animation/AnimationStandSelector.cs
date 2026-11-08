using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Animation;

/** A stand pick: the stand set, the definition name row its first weighted row names, that row's slot and clip. */
public sealed record AnimationStandChoice(uint DefinitionTag, int Set, int NameRow, int Slot, uint ClipTag);

/**
 * The standing clip a body's sequence selector picks for a weapon's third-person tag.
 * The tag tree picks a stand set; the set's first weighted row names the clip.
 */
public static class AnimationStandSelector
{
    // The animator has 24 tag slots.
    private const int Slots = 24;
    private const int PostureSlot = 1, WeaponSlot = 3, StanceSlot = 10, SurfaceSlot = 11, ClassSlot = 20, GenderSlot = 21, RaceSlot = 22;
    private const int MaximumDepth = 512;

    /**
     * Query names whose FNV-1 hash is a key of the body query table. Each is a one or two word action name, or a
     * longer one whose family or alias is also a key.
     */
    public static readonly IReadOnlyList<string> KnownQueries =
    [
        "ready", "put_away", "sprint_ready", "alert_enter", "alert_exit", "reload_enter", "reload_continue",
        "reload_continue_empty", "reload_special", "throw_grenade", "throw_knife", "sword_throw", "melee_lunge",
        "hunter_melee", "titan_melee", "melee_warlock", "hunter_melee_recover", "titan_melee_recover",
        "warlock_melee_recover", "melee_airborne_heavy", "knife_melee_airborne", "finisher_melee", "guard_enter",
        "guard_idle", "guard_exit", "sword_guard_enter", "sword_guard_exit", "double_jump", "glide", "glide_activate",
        "glide_deactivate", "lift", "airborne", "land_hard", "land_dead_front", "land_dead_back", "crouch_slide",
        "crouch_slide_long", "juggernaut_slide", "dodge_front", "dodge_back", "dodge_left", "dodge_right",
        "dodge_front_air", "dodge_back_air", "dodge_left_air", "dodge_right_air", "cast_blink", "cast_exit",
        "arc_beam", "novabomb", "hunter_smoke_bomb", "bow_cast_ground", "bow_cast_air", "radiance", "radiance_exit",
        "radiance_air", "radiance_air_exit", "respawn", "self_resurrection", "ghost_idle", "ghost_exit",
        "interact_ghost_cast", "level_up", "driver_emote_front", "driver_emote_back", "driver_emote_left",
        "driver_emote_right", "driver_land_hard"
    ];

    /** The hash a tag or query of this spelling has. */
    public static uint TagHash(string spelling) => AnimationClipNames.Hash(spelling);

    /** Weapon tags of the stand tree's weapon slot, by their spelling. */
    public static readonly IReadOnlyList<string> WeaponTags =
    [
        "unarmed", "rifle", "pistol", "sidearm", "smg", "shotgun", "sniper_rifle", "fusion_rifle", "machine_gun",
        "rocket_launcher", "grenade_launcher", "sword", "bow"
    ];

    /** The weapon tag the game sets when a weapon names none. */
    public static readonly uint Unarmed = AnimationClipNames.Hash("unarmed");
    // The game's defaults: standing on the ground, in the combat stance.
    private static readonly uint Stand = AnimationClipNames.Hash("stand"), Ground = AnimationClipNames.Hash("ground"),
        Combat = AnimationClipNames.Hash("combat");

    /**
     * Reads the body's selector component and picks its stand clip for one weapon tag.
     * @param weaponTag The weapon's third-person tag; 0 or the empty hash means unarmed.
     * @param guardianClass Lower-case class name, such as titan; empty leaves the slot unset. Gender and race likewise.
     * @return Null when the body has no selector component.
     */
    public static AnimationStandChoice? Select(PackageCatalog catalog, uint entityTag, uint weaponTag,
        string guardianClass = "", string gender = "", string race = "")
    {
        if (Selector(catalog, entityTag) is not var (config, resource, definition)) return null;
        var tags = new uint[Slots];
        Array.Fill(tags, StringHash.Empty);
        tags[PostureSlot] = Stand;
        tags[WeaponSlot] = weaponTag is 0 or StringHash.Empty ? Unarmed : weaponTag;
        tags[StanceSlot] = Combat;
        tags[SurfaceSlot] = Ground;
        tags[ClassSlot] = AnimationClipNames.Hash(guardianClass);
        tags[GenderSlot] = AnimationClipNames.Hash(gender);
        tags[RaceSlot] = AnimationClipNames.Hash(race);
        return Pick(catalog, definition, Field(config, resource, "TagVocabulary"), Field(config, resource, "StandSets"),
            Field(config, resource, "StandTree"), tags);
    }

    /**
     * Names of the action queries that can play each clip, over every branch of each query.
     * @param names Query names to look for; a name whose hash the table lacks is skipped.
     * @return Names by clip tag, in the order of names; empty when the body has no selector.
     */
    public static IReadOnlyDictionary<uint, IReadOnlyList<string>> QueryNames(PackageCatalog catalog, uint entityTag,
        IReadOnlyList<string> names)
    {
        var result = new Dictionary<uint, IReadOnlyList<string>>();
        if (Selector(catalog, entityTag) is not var (config, resource, definitionTag)) return result;
        uint tableTag = Field(config, resource, "QueryTable");
        if (!IsClass(catalog, tableTag, SAnimationQueryTable.Class)) throw new InvalidDataException("The stand selector names no query table.");
        byte[] table = catalog.Read(tableTag);
        var queries = AnimationControlReader.Rows<SAnimationQuery>(table,
            TagView.FieldOffset<SAnimationQueryTable>(nameof(SAnimationQueryTable.Queries)), SAnimationQuery.Class);
        var byHash = new Dictionary<uint, int>();
        foreach (ref readonly var key in AnimationControlReader.Rows<SAnimationQueryKey>(table,
            TagView.FieldOffset<SAnimationQueryTable>(nameof(SAnimationQueryTable.Keys)), SAnimationQueryKey.Class))
            byHash.TryAdd(key.QueryNameHash, checked((int)key.QueryIndex));
        byte[] definition = catalog.Read(definitionTag);
        var rows = AnimationNameRows.Read(definition);
        var clips = Clips(definition);
        var found = new Dictionary<uint, List<string>>();
        foreach (string name in names)
        {
            if (!byHash.TryGetValue(AnimationClipNames.Hash(name), out int query)) continue;
            if ((uint)query >= (uint)queries.Length) throw new InvalidDataException($"Query {name} is outside its table.");
            foreach (int nameRow in QueryRows(table, in queries[query]))
            {
                if ((uint)nameRow >= (uint)rows.Length) throw new InvalidDataException($"Query {name} names row {nameRow}, past the definition.");
                int slot = rows[nameRow].Slot;
                if ((uint)slot >= (uint)clips.Length) continue;
                uint clip = clips[slot].Clip;
                if (!found.TryGetValue(clip, out var list)) found.Add(clip, list = []);
                if (!list.Contains(name)) list.Add(name);
            }
        }
        foreach (var (clip, list) in found) result.Add(clip, list.AsReadOnly());
        return result;
    }

    /** Name rows of one query: kind 1 is one weighted set; kind 2 is a tag tree whose sets are all taken. */
    private static IEnumerable<int> QueryRows(byte[] table, ref readonly SAnimationQuery query)
    {
        uint kind = query.Kind;
        int target = TagView.TargetOrSelf(table, in query.Target);
        int[] sets = kind switch
        {
            1 when AnimationControlReader.ClassBefore(table, target) == SAnimationSingleQuery.Class =>
                [target + TagView.FieldOffset<SAnimationSingleQuery>(nameof(SAnimationSingleQuery.H506e8443))],
            2 when AnimationControlReader.ClassBefore(table, target) == SAnimationTreeQuery.Class =>
                TagView.RowOffsets(table, AnimationControlReader.Rows<SAnimationQuerySet>(table,
                    target + TagView.FieldOffset<SAnimationTreeQuery>(nameof(SAnimationTreeQuery.Sets)), SAnimationQuerySet.Class)),
            _ => throw new InvalidDataException($"Query kind {kind} is not read.")
        };
        return sets.SelectMany(set => SetRows(table, set)).Select(row => checked((int)row));
    }

    /** The name rows of the query set at `set`. */
    private static uint[] SetRows(byte[] table, int set)
    {
        var rows = AnimationControlReader.Rows<SAnimationQueryRow>(table,
            set + TagView.FieldOffset<SAnimationQuerySet>(nameof(SAnimationQuerySet.Rows)), SAnimationQueryRow.Class);
        var nameRows = new uint[rows.Length];
        for (int i = 0; i < nameRows.Length; i++) nameRows[i] = rows[i].NameRow;
        return nameRows;
    }

    /** The body's selector config, its resource offset and its animation definition; null when it has none. */
    private static (byte[] Config, int Resource, uint Definition)? Selector(PackageCatalog catalog, uint entityTag)
    {
        if (!IsClass(catalog, entityTag, SEntity.Class)) throw new NotSupportedException("A stand pick needs an entity.");
        byte[] entity = catalog.Read(entityTag);
        foreach (ref readonly var row in AnimationControlReader.Rows<SPatternComponent>(entity,
            TagView.FieldOffset<SEntity>(nameof(SEntity.Components)), SPatternComponent.Class))
        {
            uint configTag = row.Config;
            if (!IsClass(catalog, configTag, SEntityConfig.Class)) continue;
            byte[] config = catalog.Read(configTag);
            int resource = TagView.TargetOrSelf(config,
                in AnimationControlReader.Member<SEntityConfig, RelativePointer<Unknown>>(config, 0, nameof(SEntityConfig.Definition)));
            if (AnimationControlReader.ClassBefore(config, resource) != SAnimationStandSelector.Class) continue;
            uint definition = Field(config, resource, "AnimationDefinition");
            if (!IsClass(catalog, definition, SAnimationDefinition.Class))
                throw new InvalidDataException("The stand selector names no animation definition.");
            return (config, resource, definition);
        }
        return null;
    }

    /** One tag link of the selector resource, read alone. */
    private static uint Field(byte[] config, int resource, string member) =>
        AnimationControlReader.Member<SAnimationStandSelector, uint>(config, resource, member);

    /** The clip tag of each slot of an animation definition. */
    private static ReadOnlySpan<SAnimationClipSlot> Clips(byte[] definition) => AnimationControlReader.Rows<SAnimationClipSlot>(
        definition, TagView.FieldOffset<SAnimationDefinition>(nameof(SAnimationDefinition.Animations)), SAnimationClipSlot.Class);

    /** Evaluates the stand tree for the tags and returns the clip its set's first weighted row names. */
    private static AnimationStandChoice Pick(PackageCatalog catalog, uint definitionTag, uint vocabularyTag, uint setsTag,
        uint treeTag, uint[] tags)
    {
        byte[] vocabulary = catalog.Read(vocabularyTag);
        var context = new (uint[] Mask, uint Hash)[Slots];
        for (int slot = 0; slot < Slots; slot++) context[slot] = (Mask(vocabulary, slot, tags[slot]), tags[slot]);
        var tree = new StandTree(catalog.Read(treeTag));
        int leaf = tree.Evaluate(context);
        short[] sets = tree.LeafSets();
        if ((uint)leaf >= (uint)sets.Length || sets[leaf] < 0) throw new InvalidDataException($"Stand leaf {leaf} names no set.");
        int set = sets[leaf];
        byte[] setData = catalog.Read(setsTag);
        var setRows = AnimationControlReader.Rows<SAnimationStandSet>(setData,
            TagView.FieldOffset<C8080345f>(nameof(C8080345f.H62c7dfa1)), SAnimationStandSet.Class);
        if (set >= setRows.Length) throw new InvalidDataException($"Stand set {set} is outside its table.");
        var weighted = AnimationControlReader.Rows<SAnimationStandSetRow>(setData,
            TagView.OffsetOf(setData, in setRows[set].H17cc582b.WeightedRows), SAnimationStandSetRow.Class);
        if (weighted.Length == 0) throw new InvalidDataException($"Stand set {set} is empty.");
        int nameRow = checked((int)weighted[0].NameRow);
        byte[] definition = catalog.Read(definitionTag);
        var rows = AnimationNameRows.Read(definition);
        if ((uint)nameRow >= (uint)rows.Length) throw new InvalidDataException($"Stand set {set} names row {nameRow}, past the definition.");
        int slotIndex = rows[nameRow].Slot;
        var clips = Clips(definition);
        if ((uint)slotIndex >= (uint)clips.Length) throw new InvalidDataException($"Name row {nameRow} names slot {slotIndex}, past the clips.");
        return new(definitionTag, set, nameRow, slotIndex, clips[slotIndex].Clip);
    }

    /** The tag's mask in the slot's group; all clear when the slot has no group or the group lacks the tag. */
    private static uint[] Mask(byte[] vocabulary, int slot, uint hash)
    {
        int group = AnimationControlReader.Member<AnimationTagVocabulary, short>(vocabulary, 0, $"SlotGroupIndices[{slot}]");
        var groups = AnimationControlReader.Rows<SAnimationTagGroup>(vocabulary,
            TagView.FieldOffset<AnimationTagVocabulary>(nameof(AnimationTagVocabulary.TagGroups)), SAnimationTagGroup.Class);
        if (group < 0 || group >= groups.Length) return new uint[4];
        foreach (ref readonly var row in AnimationControlReader.Rows<SAnimationTagRow>(vocabulary,
            TagView.OffsetOf(vocabulary, in groups[group].Tags), SAnimationTagRow.Class))
            if (row.TagHash == hash)
                return [row.Mask[0], row.Mask[1], row.Mask[2], row.Mask[3]];
        return new uint[4];
    }

    private static bool IsClass(PackageCatalog catalog, uint tag, uint type) =>
        catalog.TryGetEntry(tag, out var entry) && entry.Reference == type;

    /** The stand tree. A node result of 0 or more continues at branch result - 1; a negative one is leaf ~result. */
    private sealed class StandTree
    {
        private readonly byte[] bytes;
        private readonly SAnimationStandNode[] nodes;
        private readonly SAnimationStandBranch[] branches;

        public StandTree(byte[] bytes)
        {
            this.bytes = bytes;
            nodes = AnimationControlReader.Rows<SAnimationStandNode>(bytes,
                TagView.FieldOffset<C80803478>("HD5df35bc.Nodes"), SAnimationStandNode.Class).ToArray();
            branches = AnimationControlReader.Rows<SAnimationStandBranch>(bytes,
                TagView.FieldOffset<C80803478>("HD5df35bc.Branches"), SAnimationStandBranch.Class).ToArray();
        }

        /** The leaf for this context; the tree's fallback leaf when no branch decides. */
        public int Evaluate((uint[] Mask, uint Hash)[] context)
        {
            int leaf = Branch(0, context, 0);
            return leaf == -1 ? AnimationControlReader.Member<C80803478, short>(bytes, 0, "HD5df35bc.FallbackLeaf") : leaf;
        }

        /** The set each leaf picks; a negative one picks none. */
        public short[] LeafSets()
        {
            var rows = AnimationControlReader.Rows<SI16Element>(bytes,
                TagView.FieldOffset<C80803478>("HD5df35bc.LeafSets"), SI16Element.Class);
            var sets = new short[rows.Length];
            for (int i = 0; i < sets.Length; i++) sets[i] = rows[i].Value;
            return sets;
        }

        /** Walks the slot's tag from one node; when the walk decides nothing, the start node's result applies. */
        private int Branch(int index, (uint[] Mask, uint Hash)[] context, int depth)
        {
            if (depth > MaximumDepth) throw new InvalidDataException("The stand tree does not end.");
            var branch = branches[Index(index, branches.Length)];
            int slot = branch.Slot;
            if (slot >= Slots) throw new InvalidDataException($"A stand branch names slot {slot}.");
            int start = Index(branch.Node, nodes.Length);
            int found = Walk(start, context[slot], context, depth + 1);
            if (found != -1) return found;
            return Result(start, context, depth + 1);
        }

        /**
         * Scans siblings for the tag's hash. A node whose mask the tag's mask holds is searched first;
         * when that search finds nothing the scan stops on that node.
         */
        private int Walk(int node, (uint[] Mask, uint Hash) tag, (uint[] Mask, uint Hash)[] context, int depth)
        {
            if (depth > MaximumDepth) throw new InvalidDataException("The stand tree does not end.");
            int found = -1;
            while (nodes[node].TagHash != tag.Hash)
            {
                if (Holds(tag.Mask, node))
                {
                    short child = nodes[node].Child;
                    if (child != -1) found = Walk(Index(child, nodes.Length), tag, context, depth + 1);
                    if (found == -1) break;
                }
                short sibling = nodes[node].Sibling;
                if (sibling == -1) return found;
                node = Index(sibling, nodes.Length);
            }
            return Result(node, context, depth + 1);
        }

        private int Result(int node, (uint[] Mask, uint Hash)[] context, int depth)
        {
            short result = nodes[node].Result;
            return result >= 0 ? Branch(result - 1, context, depth) : ~result;
        }

        /** Bit 31 of word 3 marks a real mask on both sides. */
        private bool Holds(uint[] mask, int node)
        {
            ref readonly var stored = ref nodes[node];
            if ((stored.Mask[3] & mask[3] & 0x80000000) == 0) return false;
            for (int word = 0; word < 4; word++)
                if ((stored.Mask[word] & ~mask[word]) != 0) return false;
            return true;
        }

        private static int Index(int value, int count) =>
            (uint)value < (uint)count ? value : throw new InvalidDataException($"Stand tree index {value} is outside its array.");
    }
}
