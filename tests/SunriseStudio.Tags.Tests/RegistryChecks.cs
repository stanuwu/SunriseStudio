using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;

/** The registry model: byte-exact round trip, build check on load, the checked-in registry and the names and categories it ships. */
static class RegistryChecks
{
    public static void Run()
    {
        var registry = new TagRegistry { BuildId = "test" };
        registry.FieldTypes.Add(new(0, "u32", 4, 4, "scalar"));
        registry.FieldTypes.Add(new(1, "array", 16, 8, "container"));
        registry.Classes.Add(new() { Class = 0x80800010, Name = "s_owner", Kind = "tag", Size = 24, Source = "game", Category = "Map",
            Fields = [new() { Name = "slots", Offset = 8, Type = 1, Element = 0x80800011, Confidence = "game", Refs = ["a.cs:1"] }] });
        registry.Classes.Add(new() { Class = 0x80800011, Name = "slot", Kind = "element", Size = 4, Source = "game",
            Fields = [new() { Name = "value", Offset = 0, Type = 0, Confidence = "verified" }] });
        registry.Names[0xE7C9EDE0] = "slots";

        byte[] first = registry.ToJson();
        var parsed = TagRegistry.Parse(first);
        Check(parsed.ToJson().AsSpan().SequenceEqual(first), "A registry does not round trip byte for byte.");
        Check(!first.Contains((byte)'\r'), "The registry is not LF-only.");
        Check(parsed.Names[0xE7C9EDE0] == "slots" && parsed.Classes[0].Fields.Single() is { Element: 0x80800011, Refs: ["a.cs:1"] },
            "Names or field details were lost.");
        Check(parsed.Classes[0].Category == "Map" && parsed.Classes[1].Category is null, "A class category was lost or invented.");
        string file = Path.Combine(Path.GetTempPath(), "sunrise-tags-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllBytes(file, first);
            Check(TagRegistry.Load(file, "test").BuildId == "test", "The matching build was refused.");
            bool refused = false;
            try { TagRegistry.Load(file, "86657"); }
            catch (InvalidDataException) { refused = true; }
            Check(refused, "A registry for another build was accepted.");
        }
        finally { File.Delete(file); }
    }

    /** A generated registry reads back to the same bytes, and every field type id it uses exists. */
    public static void CheckedIn(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var registry = TagRegistry.Parse(bytes);
        Check(registry.ToJson().AsSpan().SequenceEqual(bytes), "The registry does not round trip byte for byte; regenerate it.");
        Check(registry.FieldTypes.Select((x, i) => x.Id == i).All(x => x), "Field type ids are not their table positions.");
        var badType = registry.Classes.SelectMany(c => c.Fields.Where(f => f.Type < 0 || f.Type >= registry.FieldTypes.Count)
            .Select(f => $"0x{c.Class:X8} {f.Name}")).FirstOrDefault();
        Check(badType is null, "A field names a type the table does not have: " + badType);
        Console.WriteLine($"registry build={registry.BuildId} classes={registry.Classes.Count} "
            + $"fields={registry.Classes.Sum(x => x.Fields.Count)} names={registry.Names.Count}");
    }

    /** Browser categories and class names come from the shipped registry; package types keep their own. */
    public static void Shipped()
    {
        static PackageEntry Tag(uint reference) => new(0x80A00000, reference, 8, 0, 64, "test", 0x0100, 0);
        Check(AssetCategories.CategoryOf(Tag(0x80809994)) == "Activity", "s_scenario lost its registry category.");
        Check(AssetCategories.CategoryOf(Tag(0x8080496A)) == "CUI / Screen", "A category type was not read from the registry.");
        Check(AssetCategories.CategoryOf(Tag(0x80800010)) == "Other", "A class with no category is not Other.");
        Check(AssetCategories.CategoryOf(new(0x80A00001, 0, 26, 5, 64, "test", 0x0100, 1)) == "Audio", "A Wwise bank lost its package type category.");
        Check(ClassNames.Get(0x80807193) == "s_static_special_mesh" && ClassNames.Words(0x808071A7) == "static mesh",
            "Class names do not come from the registry.");
        var unnamed = StudioRegistry.Index.Registry.Classes.First(x => x.NameSource is null).Class;
        Check(ClassNames.Get(unnamed) is null && ClassNames.Describe(unnamed) == $"class_{unnamed:X8}", "An unnamed class got a name.");
        Check(ClassNames.SearchText(0x80807BEA).Contains("s_investment_item", StringComparison.Ordinal), "Other names are not searchable.");
        var malformed = StudioRegistry.Index.Registry.Classes.Where(x => x.Category is { } category
            && category.Split(" / ").Any(part => part.Length == 0 || part.Contains('/'))).Select(x => $"0x{x.Class:X8}").FirstOrDefault();
        Check(malformed is null, "A category is not \"Category\" or \"Category / Type\": " + malformed);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
