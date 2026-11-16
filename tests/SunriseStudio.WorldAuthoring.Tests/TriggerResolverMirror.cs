using System.Buffers.Binary;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

/**
 * The server's player-trigger lookup over built tags: one object row per placement of every state,
 * type-31 refs joined to a type-60 volume of the same state, then every candidate for one report.
 */
static class TriggerResolverMirror
{
    /** One candidate the resolver compares; rows are catalog rows, the rest is authored identity. */
    public sealed record Candidate(uint RegistryKey, uint ObjectTag, uint VolumeKey, int ObjectRow, int SlotRow, int VolumeIndex, int SlotIndex,
        int Bubble, int State, uint Registry);

    private sealed record Placement(int Row, int Bubble, int State, uint Registry, uint Tag, uint Key, uint[] SlotTypes, int FirstSlot);

    /**
     * Lists every candidate for a report of type-31 slot `slotIndex` on the object with `registryKey`.
     * @param catalog Reads the built tags; the caller sets its overlay.
     */
    public static List<Candidate> Candidates(PackageCatalog catalog, uint scenarioTag, uint registryKey, int slotIndex)
    {
        var placements = Placements(catalog, scenarioTag);
        var keys = placements.Select(item => item.Key).ToHashSet();
        var refs = new Dictionary<uint, List<(int Slot, uint Key, int Index)>>();
        var tables = new Dictionary<uint, List<(uint Key, int Index)>>();
        var result = new List<Candidate>();
        foreach (var source in placements.Where(item => item.Key == registryKey))
        {
            if (!refs.TryGetValue(source.Tag, out var found)) refs[source.Tag] = found = TriggerRefs(catalog, source.Tag, keys);
            foreach (var (slot, key, index) in found.Where(item => item.Slot == slotIndex))
            {
                // A ref joins only when one placement of the target sits in the same state and registry.
                var targets = placements.Where(item => item.Key == key && item.State == source.State && item.Bubble == source.Bubble
                    && item.Registry == source.Registry && index < item.SlotTypes.Length && item.SlotTypes[index] == 60).ToList();
                if (targets.Count != 1) continue;
                var target = targets[0];
                if (!tables.TryGetValue(target.Tag, out var volumes)) tables[target.Tag] = volumes = VolumeTables(catalog, target.Tag);
                foreach (var _ in volumes.Where(table => table.Key == key && table.Index == index))
                    result.Add(new(source.Key, source.Tag, key, source.Row, source.FirstSlot + slot, index, slot, source.Bubble, source.State, source.Registry));
            }
        }
        return result;
    }

    /** The candidate fields the resolver compares; more than one distinct value is ambiguous. */
    public static int Distinct(IEnumerable<Candidate> candidates) =>
        candidates.Select(item => (item.RegistryKey, item.ObjectTag, item.VolumeKey, item.ObjectRow, item.SlotRow, item.VolumeIndex, item.SlotIndex)).Distinct().Count();

    /** Every object of every state's registry, in walk order: bubble, state, list a, b, c, row. */
    private static List<Placement> Placements(PackageCatalog catalog, uint scenarioTag)
    {
        var output = new List<Placement>();
        int slots = 0;
        byte[] scenario = catalog.Read(scenarioTag);
        int count = TagView.Rows(scenario, in TagView.At<SScenario>(scenario, 0).Bubbles, ScenarioBubble.Class).Length;
        for (int bubble = 0; bubble < count; bubble++)
        {
            var bubbles = TagView.Rows(scenario, in TagView.At<SScenario>(scenario, 0).Bubbles, ScenarioBubble.Class);
            var states = TagView.Rows(scenario, in bubbles[bubble].States, SliceSetState.Class).ToArray();
            for (int state = 0; state < states.Length; state++)
            {
                byte[] entry = catalog.Read(states[state].Entry);
                uint registry = TagView.At<SScenarioStateEntry>(entry, 0).Link;
                byte[] set = catalog.Read(registry);
                ref readonly var lists = ref TagView.At<SScenarioTagSet>(set, 0);
                var rows = TagView.Rows(set, in lists.TagSetA, RegistryObjectRef.Class).ToArray()
                    .Concat(TagView.Rows(set, in lists.TagSetB, RegistryObjectRef.Class).ToArray())
                    .Concat(TagView.Rows(set, in lists.TagSetC, RegistryObjectRef.Class).ToArray());
                foreach (var row in rows)
                {
                    if (!catalog.TryGetEntry(row.Tag, out var item) || item.Reference != SAuthoredObject.Class) continue;
                    byte[] data = catalog.Read(row.Tag);
                    ref readonly var authored = ref TagView.At<SAuthoredObject>(data, 0);
                    var types = TagView.Rows(data, in authored.SlotTable.Slots, ObjectSlot.Class).ToArray().Select(slot => slot.SlotType).ToArray();
                    output.Add(new(output.Count, bubble, state, registry, row.Tag, authored.RegistryKey, types, slots));
                    slots += types.Length;
                }
            }
        }
        return output;
    }

    /** Each type-31 slot's keyed refs to a type-60 slot, read from the slot's own config. */
    private static List<(int Slot, uint Key, int Index)> TriggerRefs(PackageCatalog catalog, uint objectTag, HashSet<uint> keys)
    {
        var output = new List<(int, uint, int)>();
        foreach (var slot in ComponentSlotCatalog.OwnerSlots(catalog, objectTag).Where(row => row.SlotType == 31).DistinctBy(row => (row.SlotIndex, row.ConfigTag)))
        {
            byte[] config = catalog.Read(slot.ConfigTag);
            for (int at = 0; at + 8 <= config.Length; at += 4)
            {
                uint key = BinaryPrimitives.ReadUInt32LittleEndian(config.AsSpan(at));
                if (keys.Contains(key) && BinaryPrimitives.ReadUInt16LittleEndian(config.AsSpan(at + 4)) == 60)
                    output.Add((slot.SlotIndex, key, BinaryPrimitives.ReadUInt16LittleEndian(config.AsSpan(at + 6))));
            }
        }
        return output;
    }

    /** The type-60 volume rows of every config the object's handles reach. */
    private static List<(uint Key, int Index)> VolumeTables(PackageCatalog catalog, uint objectTag)
    {
        var output = new List<(uint, int)>();
        byte[] owner = catalog.Read(objectTag);
        var configs = new HashSet<uint>();
        foreach (ref readonly var block in TagView.Rows(owner, in TagView.At<SAuthoredObject>(owner, 0).EntryFilters, ObjectBubbleBlock.Class))
            foreach (ref readonly var handle in TagView.Rows(owner, in block.Handles, PlacedHandle.Class))
                foreach (uint link in Named(catalog, handle.Handle, SEntityLink.Class))
                    foreach (uint config in Named(catalog, link, SEntityConfig.Class)) configs.Add(config);
        foreach (uint config in configs)
        {
            byte[] data = catalog.Read(config);
            for (int field = 0; field + 16 <= data.Length; field += 8)
            {
                if (!TagArray.TryChecked(data, field, 0, 0, out var array) || array.Count == 0 || array.Class != STriggerVolumeRow.Class) continue;
                var (count, first, _) = TagArray.Checked(data, field);
                for (int row = 0; row < count; row++)
                {
                    var volume = TagView.At<STriggerVolumeRow>(data, first + row * System.Runtime.CompilerServices.Unsafe.SizeOf<STriggerVolumeRow>());
                    if (volume.SlotType == 60) output.Add((volume.Key, (int)volume.SlotOrdinal));
                }
            }
        }
        return output;
    }

    /** Tags of one class a tag's words name. */
    private static IEnumerable<uint> Named(PackageCatalog catalog, uint tag, uint classId)
    {
        if (!catalog.TryGetEntry(tag, out _)) yield break;
        byte[] bytes = catalog.Read(tag);
        var seen = new HashSet<uint>();
        for (int at = 0; at + 4 <= bytes.Length; at += 4)
        {
            uint word = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
            if (word != tag && word >= TagHashes.Base && catalog.TryGetEntry(word, out var entry) && entry.Reference == classId && seen.Add(word)) yield return word;
        }
    }
}
