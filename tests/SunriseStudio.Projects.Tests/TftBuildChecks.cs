using System.Buffers.Binary;
using System.Text;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.Tags;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Tft;

/**
 * A project with two `.tft` files builds one new package whose entries read back as the compiled tags.
 * A game name becomes the package name, and a wide reference to a project tag resolves through its alias.
 */
static class TftBuildChecks
{
    private const uint Holder = 0x80800600;

    public static void Run(string parent)
    {
        string install = Path.Combine(parent, "tft-install");
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_test_0100_0.pkg"), 0x100, [new(0x80800001, 0x100A, [1, 2, 3, 4])]);
        using var catalog = PackageCatalog.Open(install);
        var schema = Schema();

        var project = StudioProject.Create(Path.Combine(parent, "tft-project"), "Loose tags");
        Put(project, Tag(schema, "hoenn/a.holder", "hoenn/b.holder", "first", wide: "hoenn/b.holder", gameName: "map:hoenn:root"));
        Put(project, Tag(schema, "hoenn/b.holder", "#0x80A00000", "second"));
        var build = project.Build(catalog, tagSchema: schema);
        var allocator = TagAllocator.Parse(File.ReadAllBytes(Path.Combine(project.DirectoryPath, StudioProject.AllocationFileName)));
        uint a = allocator.Tags["hoenn/a.holder"], b = allocator.Tags["hoenn/b.holder"];
        Require(build.TagFiles.Count == 2 && b == a + 1 && !catalog.ContainsInstalledPackage(TagHashes.Split(a).PackageId),
            "The build did not compile both files into one new package.");

        string verify = Path.Combine(parent, "tft-verify");
        Directory.CreateDirectory(Path.Combine(verify, "packages"));
        foreach (string file in Directory.GetFiles(Path.Combine(install, "packages")).Concat(build.PackageFiles))
            File.Copy(file, Path.Combine(verify, "packages", Path.GetFileName(file)));
        using (var built = PackageCatalog.Open(verify))
        {
            byte[] first = built.Read(a), second = built.Read(b);
            Require(built.TryGetEntry(a, out var entry) && entry.Reference == Holder && entry.TypeInfo == 0x101C,
                "The built entry lost its class or type word.");
            Require(BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(8)) == b && BinaryPrimitives.ReadUInt32LittleEndian(second.AsSpan(8)) == 0x80A00000
                && TagDocument.Parse(first, Holder, schema).Problems.Count == 0, "A built reference does not name its target.");
            Require(built.Resolve64(BinaryPrimitives.ReadUInt64LittleEndian(first.AsSpan(WideField))) == b,
                "A wide reference to a project tag does not resolve through the built alias.");
            Require(built.GetName(a) == "map:hoenn:root" && built.GetName(b) == "hoenn/b.holder", "The built package names are wrong.");
        }

        // A second build keeps every hash, and the allocation table does not change.
        string table = File.ReadAllText(Path.Combine(project.DirectoryPath, StudioProject.AllocationFileName));
        var again = project.Build(catalog, tagSchema: schema);
        Require(again.TagFiles.Select(x => x.TagHash).Order().SequenceEqual([a, b])
            && File.ReadAllText(Path.Combine(project.DirectoryPath, StudioProject.AllocationFileName)) == table, "A rebuild moved a hash.");
        Console.WriteLine("tft_build files=2 package=new references=project,installed,wide game_name=written rebuild=stable");
    }

    private static void Put(StudioProject project, TftFile file)
    {
        string path = Path.Combine(project.ContentDirectory, file.Name.Replace('/', Path.DirectorySeparatorChar) + TftNames.FileExtension);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, file.Write());
    }

    private const int WideField = 24;

    /** Holder {size, link tagref, label string, wide wideref}. */
    private static RegistryIndex Schema()
    {
        var registry = new TagRegistry { BuildId = "test" };
        foreach (var (name, size) in new[] { ("u64", 8), ("tagref", 4), ("string_ptr", 8), ("wideref", 8) })
            registry.FieldTypes.Add(new(registry.FieldTypes.Count, name, size, Math.Min(size, 8), "scalar"));
        registry.Classes.Add(new()
        {
            Class = Holder, Name = "holder", Kind = "tag", Size = 32, Align = 8, Source = "game",
            Fields = [new() { Name = "size", Offset = 0, Type = 0, Confidence = "game" }, new() { Name = "link", Offset = 8, Type = 1, Confidence = "game" },
                new() { Name = "label", Offset = 16, Type = 2, Confidence = "game" }, new() { Name = "wide", Offset = WideField, Type = 3, Confidence = "game" }],
        });
        return new RegistryIndex(registry);
    }

    private static TftFile Tag(RegistryIndex schema, string name, string link, string label, string? wide = null, string? gameName = null)
    {
        var root = new TagBlueprint { ClassId = Holder, Bytes = new byte[32] };
        BinaryPrimitives.WriteUInt32LittleEndian(root.Bytes.AsSpan(8), TftFile.TagRefPlaceholder);
        root.Links.Add(new(16, TagLinkKind.String, 0, [], Encoding.UTF8.GetBytes(label + "\0")));
        byte[] data = CanonicalTagWriter.Write(root, schema);
        return new TftFile
        {
            Name = name, TagType = "holder", GameName = gameName, ClassId = Holder, TypeInfo = 0x101C, BuildHash = TftFile.HashBuildId("test"),
            Layout = TftLayout.Extract(schema, data, Holder), Data = data,
            References = wide is null ? [new(8, TftReferenceKind.TagRef, 0, link)]
                : [new(8, TftReferenceKind.TagRef, 0, link), new(WideField, TftReferenceKind.WideRef, 0, wide)],
        };
    }

    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
