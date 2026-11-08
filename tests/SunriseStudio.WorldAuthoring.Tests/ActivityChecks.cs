using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using SunriseStudio.World;

/**
 * The Testbed's free-roam activity, copied from EDZ free roam into the four installed catalogues,
 * reads back the way Sunrise reads it, and a project build describes all four for the runtime merge.
 */
static class ActivityChecks
{
    private const int Rows = 8, IndexStride = 16, DestinationStride = 56;
    private static readonly int NameAt = TagView.FieldOffset<ActivityDefinition>(nameof(ActivityDefinition.InternalName)),
        TemplateAt = TagView.FieldOffset<ActivityDefinition>(nameof(ActivityDefinition.GameplaySettingsHash)),
        DestinationAt = TagView.FieldOffset<ActivityDefinition>(nameof(ActivityDefinition.Destination));
    private static readonly TextReference Title = new(7, 0x1234ABCD), Description = new(7, 0x5678ABCD);

    public static void Run(string gameRoot)
    {
        using var catalog = PackageCatalog.Open(gameRoot);
        var schema = StudioRegistry.Index;
        // The pointer lists from the generated views must match the registry.
        WorldBuild.Check(Pointers(schema, ActivityRows.RecordClass).SequenceEqual(ActivityRows.RecordPointers)
            && Pointers(schema, ActivityRows.TextRecordClass).SequenceEqual(ActivityRows.TextRecordPointers),
            "The record pointer lists differ from the registry.");

        byte[] stock = catalog.ReadOriginal(ActivityRows.ActivityTable);
        var templates = ActivityRows.FreeRoam(stock);
        var edz = templates.Single(x => x.Name == "edz_freeroam");
        WorldBuild.Check(edz.Index == 8 && edz.Destination == 9, $"EDZ free roam is row {edz.Index}, destination {edz.Destination}.");
        var request = new ActivityRequest("testbed", edz, "worldauthoring-tests/testbed") { Title = Title, Description = Description };
        var result = ActivityRows.Append(catalog.ReadOriginal, request);
        WorldBuild.Check(result.ActivityIndex == 1170 && result.DestinationIndex == 48 && result.Edits.Count == 4,
            $"The new rows are activity {result.ActivityIndex} and destination {result.DestinationIndex}.");

        CheckActivity(stock, result.Edits[ActivityRows.ActivityTable], result);
        CheckText(catalog.ReadOriginal(ActivityRows.ActivityTextTable), result.Edits[ActivityRows.ActivityTextTable], result);
        CheckDestination(catalog.ReadOriginal(ActivityRows.DestinationTable), result.Edits[ActivityRows.DestinationTable], result, null);
        CheckDestination(catalog.ReadOriginal(ActivityRows.DestinationTextTable), result.Edits[ActivityRows.DestinationTextTable], result, Title);
        bool refused = false;
        try { ActivityRows.Append(tag => result.Edits[tag], request); }
        catch (InvalidDataException) { refused = true; }
        WorldBuild.Check(refused, "A second activity named testbed was added.");
        Console.WriteLine($"world_activity templates={templates.Count} activity={result.ActivityIndex} destination={result.DestinationIndex} "
            + $"hash=0x{result.ActivityHash:X8} destination_hash=0x{result.DestinationHash:X8}");
        Build(catalog, schema, result);
    }

    /** The exe record reads as Sunrise's activity reader reads it, and points where its template does. */
    private static void CheckActivity(byte[] stock, byte[] built, ActivityResult result)
    {
        var (count, data) = Array(built, Rows);
        WorldBuild.Check(count == 1171, $"The activity index holds {count} rows.");
        var records = Enumerable.Range(0, count).Select(index => Target(built, data + index * IndexStride + 8)).ToArray();
        // Sunrise needs records in index order, outside the index.
        WorldBuild.Check(records.Zip(records.Skip(1)).All(pair => pair.First < pair.Second) && records.All(x => x < data - 20 || x >= data + count * IndexStride),
            "The activity records are out of order or inside the index.");
        var (_, stockData) = Array(stock, Rows);
        for (int index = 0; index < 1170; index++)
            WorldBuild.Check(records[index] == Target(stock, stockData + index * IndexStride + 8), $"Installed activity {index} moved.");
        int record = records[1170], template = records[8];
        WorldBuild.Check(U32(built, data + 1170 * IndexStride) == result.ActivityHash && U32(built, record) == result.ActivityHash
            && U32(built, record - 4) == ActivityRows.RecordClass && Text(built, Target(built, record + NameAt)) == "testbed"
            && U32(built, record + TemplateAt) == ActivityRows.FreeRoamTemplate && built[record + DestinationAt] == 48
            && built[record + 218] == built[template + 218], "The new activity record is wrong.");
        foreach (int word in ActivityRows.RecordPointers.Where(x => x != NameAt))
            WorldBuild.Check(Named(built, record + word) == Named(built, template + word), $"Activity record word +{word} names another target.");
    }

    /** The client record follows its row, carries its own text block, and keeps the template's other targets. */
    private static void CheckText(byte[] stock, byte[] built, ActivityResult result)
    {
        var (count, data) = Array(built, Rows);
        WorldBuild.Check(count == 1171 && U32(built, data + 1170 * IndexStride) == result.ActivityHash, "The client index lacks the new row.");
        int record = Target(built, data + 1170 * IndexStride + 8), template = Target(built, data + 8 * IndexStride + 8);
        int text = Target(built, record), templateText = Target(built, template);
        WorldBuild.Check(U32(built, record - 4) == ActivityRows.TextRecordClass && text == record + 136, "The client record has no text block of its own.");
        byte[] expected = built.AsSpan(templateText, 36).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(4), Title.Bank);
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(8), Title.Hash);
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(12), Description.Bank);
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(16), Description.Hash);
        WorldBuild.Check(built.AsSpan(text, 36).SequenceEqual(expected), "The new text block is not the template's with the new strings.");
        foreach (int word in ActivityRows.TextRecordPointers.Where(x => x != 0))
            WorldBuild.Check(Named(built, record + word) == Named(built, template + word), $"Client record word +{word} names another target.");
        // Only the size word and the index descriptor change in the installed bytes.
        WorldBuild.Check(stock.AsSpan(Rows + 16).SequenceEqual(built.AsSpan(Rows + 16, stock.Length - Rows - 16)),
            "Installed client bytes outside the index descriptor changed.");
    }

    /** The new destination row shares the template's arrays under its own hash, with the new name on the client side. */
    private static void CheckDestination(byte[] stock, byte[] built, ActivityResult result, TextReference? name)
    {
        var (count, data) = Array(built, Rows);
        WorldBuild.Check(count == 49, $"A destination table holds {count} rows.");
        int row = data + 48 * DestinationStride, template = data + 9 * DestinationStride;
        WorldBuild.Check(U32(built, row) == result.DestinationHash, "The new destination row has another hash.");
        var pointers = name is null ? ActivityRows.DestinationPointers : ActivityRows.DestinationTextPointers;
        foreach (int word in pointers) WorldBuild.Check(Named(built, row + word) == Named(built, template + word), $"Destination word +{word} moved.");
        if (name is { } text)
            WorldBuild.Check(U32(built, row + 4) == text.Bank && U32(built, row + 8) == text.Hash, "The new destination has another name.");
        WorldBuild.Check(stock.Length < built.Length, "The destination table did not grow.");
    }

    /** The project build describes all four tables as table merges, with the destination byte as a site. */
    private static void Build(PackageCatalog catalog, RegistryIndex schema, ActivityResult result)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "sunrise-activity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = SunriseStudio.Projects.StudioProject.Create(Path.Combine(scratch, "project"), "Testbed");
            project.ApplyEdits(result.Edits, catalog);
            project.Build(catalog, tagSchema: schema);
            string dump = Directory.EnumerateFiles(scratch, "merge-sidecars.json", SearchOption.AllDirectories).Single();
            using var json = JsonDocument.Parse(File.ReadAllBytes(dump));
            var entries = json.RootElement.GetProperty("sidecars").EnumerateArray()
                .SelectMany(x => x.GetProperty("entries").EnumerateArray()).ToDictionary(x => x.GetProperty("tag").GetString()!);
            foreach (uint tag in result.Edits.Keys)
            {
                var entry = entries[tag.ToString("X8")];
                WorldBuild.Check(entry.GetProperty("kind").GetString() == "Table",
                    $"Table 0x{tag:X8} is {entry.GetProperty("kind").GetString()}: {entry.GetProperty("reason")}.");
            }
            var sites = entries[ActivityRows.ActivityTable.ToString("X8")].GetProperty("sites").EnumerateArray().ToArray();
            WorldBuild.Check(sites.Length == 1 && sites[0].GetProperty("rule").GetString() == "destination" && sites[0].GetProperty("count").GetInt32() == 1,
                "The destination byte is not a site.");
            var groups = json.RootElement.GetProperty("pairGroups").EnumerateArray().Select(x => x.GetProperty("group").GetString()).ToHashSet();
            WorldBuild.Check(groups.SetEquals(["activity", "destination"]), $"The build grows pair groups {string.Join(',', groups)}.");
            Console.WriteLine($"world_activity_build tables={result.Edits.Count} kinds=Table sites={sites.Length}");
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }

    private static IReadOnlyList<int> Pointers(RegistryIndex schema, uint classId) =>
        TagRelocator.PointerOffsets(schema, classId, 0, 0).Order().ToArray();

    /** The absolute target of a relative word, or -1 for none. */
    private static long Named(byte[] bytes, int at) => I64(bytes, at) is var relative && relative == 0 ? -1 : at + relative;

    private static (int Count, int Data) Array(byte[] bytes, int field)
    {
        int header = (int)(field + 8 + I64(bytes, field + 8));
        WorldBuild.Check(U32(bytes, header - 4) == TagArray.Marker && I64(bytes, header) == I64(bytes, field), $"The array at +{field} is malformed.");
        return ((int)I64(bytes, field), header + 16);
    }

    private static string Text(byte[] bytes, int at) => Encoding.ASCII.GetString(bytes, at, System.Array.IndexOf(bytes, (byte)0, at) - at);
    private static int Target(byte[] bytes, int at) => (int)(at + I64(bytes, at));
    private static long I64(byte[] bytes, int at) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at));
    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
}
