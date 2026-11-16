namespace SunriseStudio.World.Sdk;

public static partial class ModActivitySdk
{
    /**
     * A section whose rows name slots by row: the words that hold a slot row, the words that start a
     * string reference, and the word that holds an actor class row, if any.
     */
    private readonly record struct SlotSection(SdkSection Section, int[] SlotWords, int[] StringWords, int ActorWord = -1);

    private static readonly SlotSection[] SlotSections =
    [
        new(SdkSection.AuthoredSceneSquadEdges, [2, 3], [0]),
        new(SdkSection.TaskTargets, [2, 3], [0]),
        new(SdkSection.CombatObjectiveGroups, [0], []),
        new(SdkSection.ActorAbilities, [0], [], 1),
        new(SdkSection.ActorAbilityTargets, [0], []),
        new(SdkSection.AuthoredSceneEventKeys, [2], [0]),
    ];

    /** Word layout of the actor sequence rows: binding class and tables, table entry range, entry strings and table. */
    private const int BindingGlobalWord = 4, BindingLocalWord = 5, TableEntriesWord = 3, EntryTableWord = 8;
    private static readonly int[] EntryStringWords = [0, 2, 4, 6];

    /** Copies the rows that name only kept slots, in the order of their first slot, which the server searches. */
    private static void AddSlotRows(ActivitySdkTables tables, Source source, Dictionary<uint, uint> slotRows)
    {
        foreach (var section in SlotSections)
        {
            var (words, rowWords) = source.Words(section.Section);
            var kept = new List<uint[]>();
            for (int at = 0; at < words.Length; at += rowWords)
            {
                int start = at;
                if (!section.SlotWords.All(word => slotRows.ContainsKey(words[start + word]))) continue;
                var row = words.AsSpan(at, rowWords).ToArray();
                foreach (int word in section.SlotWords) row[word] = slotRows[row[word]];
                foreach (int word in section.StringWords) MoveText(tables, source, row, word);
                if (section.ActorWord >= 0 && row[section.ActorWord] != ActivitySdkFormat.AbsentIndex)
                    row[section.ActorWord] = ActorClassRow(tables, source, row[section.ActorWord]);
                kept.Add(row);
            }
            var list = tables.Words(section.Section);
            foreach (var row in kept.OrderBy(row => row[section.SlotWords[0]])) list.AddRange(row);
        }
    }

    /** Copies a slot's alias texts and returns their range in the pack being built. */
    private static SdkRange AliasRows(ActivitySdkTables tables, Source source, SdkRange aliases)
    {
        var (words, rowWords) = source.Words(SdkSection.Texts);
        var list = tables.Words(SdkSection.Texts);
        uint first = (uint)(list.Count / rowWords);
        for (uint text = aliases.First; text < aliases.First + aliases.Count; text++)
        {
            var row = words.AsSpan((int)text * rowWords, rowWords).ToArray();
            MoveText(tables, source, row, 0);
            list.AddRange(row);
        }
        return new() { First = aliases.Count == 0 ? 0 : first, Count = aliases.Count };
    }

    /** Copies the state names and action tables of every actor class the pack took, in class order. */
    private static void AddActorRows(ActivitySdkTables tables, Source source)
    {
        var (names, nameWords) = source.Words(SdkSection.ActorStateNames);
        tables.Words(SdkSection.ActorStateNames).AddRange(ActorRowsOf(source, names, nameWords).SelectMany(row => row));

        var (bindings, bindingWords) = source.Words(SdkSection.ActorSequenceBindings);
        var (tableWords, tableRowWords) = source.Words(SdkSection.ActorSequenceTables);
        var (entries, entryWords) = source.Words(SdkSection.ActorSequenceEntries);
        var newTables = tables.Words(SdkSection.ActorSequenceTables);
        var newEntries = tables.Words(SdkSection.ActorSequenceEntries);
        var tableRows = new Dictionary<uint, uint>();
        uint Table(uint from)
        {
            if (from == ActivitySdkFormat.AbsentIndex) return from;
            if (tableRows.TryGetValue(from, out uint known)) return known;
            uint row = (uint)(newTables.Count / tableRowWords);
            var table = tableWords.AsSpan((int)from * tableRowWords, tableRowWords).ToArray();
            uint first = table[TableEntriesWord], count = table[TableEntriesWord + 1];
            table[TableEntriesWord] = (uint)(newEntries.Count / entryWords);
            for (uint entry = first; entry < first + count; entry++)
            {
                var item = entries.AsSpan((int)entry * entryWords, entryWords).ToArray();
                foreach (int word in EntryStringWords) MoveText(tables, source, item, word);
                item[EntryTableWord] = row;
                newEntries.AddRange(item);
            }
            newTables.AddRange(table);
            tableRows.Add(from, row);
            return row;
        }
        var newBindings = tables.Words(SdkSection.ActorSequenceBindings);
        foreach (var row in ActorRowsOf(source, bindings, bindingWords))
        {
            row[BindingGlobalWord] = Table(row[BindingGlobalWord]);
            row[BindingLocalWord] = Table(row[BindingLocalWord]);
            newBindings.AddRange(row);
        }
    }

    /** The rows whose first word names a copied actor class, renumbered and in the new class order. */
    private static IEnumerable<uint[]> ActorRowsOf(Source source, uint[] words, int rowWords)
    {
        var kept = new List<uint[]>();
        for (int at = 0; at < words.Length; at += rowWords)
        {
            if (!source.ActorRows.TryGetValue(words[at], out uint actor)) continue;
            var row = words.AsSpan(at, rowWords).ToArray();
            row[0] = actor;
            kept.Add(row);
        }
        return kept.OrderBy(row => row[0]);
    }

    /** Re-interns the string reference that starts at one word of a copied row. */
    private static void MoveText(ActivitySdkTables tables, Source source, uint[] row, int word)
    {
        var text = tables.Text(source.Pack.Text(new SdkStringRef { Offset = row[word], Length = row[word + 1] }));
        row[word] = text.Offset;
        row[word + 1] = text.Length;
    }
}
