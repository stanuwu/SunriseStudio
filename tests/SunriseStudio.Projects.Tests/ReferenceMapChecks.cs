using System.Buffers.Binary;
using System.Text;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.Tags;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Tft;

/**
 * A mod with a new package, a patch of a stock tag and a script lists exactly the locations of its
 * own tags; moving its package with the map keeps every reference resolving.
 */
static class ReferenceMapChecks
{
    private const uint Holder = 0x80800600;

    public static void Run(string parent)
    {
        string install = Path.Combine(parent, "refmap-install");
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_test_0100_0.pkg"), 0x100, [new(Holder, 0x101C, new byte[24], "stock:holder")]);
        uint stock = TagHashes.Make(0x100, 0);
        using var catalog = PackageCatalog.Open(install);
        var schema = Schema(tagRef: true);

        var project = StudioProject.Create(Path.Combine(parent, "refmap-project"), "Reference map");
        project.SetMod(new ModMetadata("test.refmap", "1.0.0", "Reference map"));
        var allocator = project.EnsureTagAllocator(catalog);
        uint a = allocator.Allocate("hoenn/a.holder"), b = allocator.Allocate("hoenn/b.holder");
        project.SaveTagAllocator(allocator);
        ushort own = TagHashes.Split(a).PackageId;
        Put(project, Tag(schema, "hoenn/a.holder", "hoenn/b.holder"));
        Put(project, Tag(schema, "hoenn/b.holder", "#0x80A00000"));
        byte[] patched = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(patched.AsSpan(8), b);
        project.ApplyEdit(stock, patched, catalog);
        byte[] script = Encoding.UTF8.GetBytes($"-- spawn the second holder\nlocal target = 0x{b:X8}\n");
        ModInstaller.BuildAndInstall(project, catalog, [new("refmap/refmap.lua", script)], tagSchema: schema);
        string mod = Path.Combine(ModInstaller.ModsDirectory(install), "test.refmap");

        var map = ReferenceMap.Read(File.ReadAllBytes(Path.Combine(mod, ReferenceMap.FileName)));
        var entries = map.Rows.Where(row => row.Kind == ReferenceKind.TagRef && row.Entry < ReferenceMap.SidecarEntry)
            .Select(row => (row.Package, row.Entry, row.Offset)).Order().ToArray();
        Require(map.ModId == "test.refmap" && map.Packages.SequenceEqual([own])
            && entries.SequenceEqual(new[] { (0x100u, 0u, 8u), ((uint)own, (uint)TagHashes.Split(a).EntryIndex, 8u) }.Order()),
            "The map does not list exactly the patched stock reference and the new tag's reference: " + string.Join(", ", entries));
        Require(map.Rows.Single(row => row.Kind == ReferenceKind.LuaConstant) is { Entry: 2, Offset: 15 } lua
            && map.Files[(int)lua.Package] == "scripts/refmap/refmap.lua", "The script literal was not listed by file, line and column.");
        Require(File.ReadAllText(Path.Combine(mod, ModManifest.FileName)).Contains($"\"0x{own:X4}\"", StringComparison.Ordinal),
            "mod.json does not list the own package.");

        // Move the own package with the map and read everything back.
        ushort target = TagAllocator.PreferredPackage("test.refmap#remap", id => id == own || catalog.ContainsInstalledPackage(id));
        uint shift = (uint)((target - own) << TagAllocator.EntryBits);
        string moved = Path.Combine(parent, "refmap-moved");
        var files = Directory.GetFiles(mod, "*.pkg", SearchOption.AllDirectories);
        var copies = PackageRemap.Rewrite(files, map.Locations, new Dictionary<ushort, ushort> { [own] = target }, moved);
        string verify = Path.Combine(parent, "refmap-verify");
        Directory.CreateDirectory(Path.Combine(verify, "packages"));
        foreach (string file in Directory.GetFiles(Path.Combine(install, "packages")).Concat(copies.Values))
            File.Copy(file, Path.Combine(verify, "packages", Path.GetFileName(file)));
        using (var remapped = PackageCatalog.Open(verify))
        {
            Require(!remapped.ContainsInstalledPackage(own) && remapped.ContainsInstalledPackage(target),
                "The moved copy kept its old package id.");
            Require(U32(remapped.Read(a + shift), 8) == b + shift && U32(remapped.Read(b + shift), 8) == 0x80A00000
                && U32(remapped.Read(stock), 8) == b + shift, "A reference does not name the moved tag.");
            Require(remapped.GetName(b + shift) == "hoenn/b.holder" && remapped.GetName(b) is null,
                "The moved package's named-tag rows still name the old tags.");
        }

        // A reference the schema does not type fails the build.
        var untyped = StudioProject.Create(Path.Combine(parent, "refmap-untyped"), "Untyped");
        var second = untyped.AddAsset("second.bin", new byte[24], new(0x0C00, Holder, 0x101C, "untyped:second"));
        byte[] first = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(first.AsSpan(8), TagHashes.Make(0x0C00, second.Native!.EntryIndex!.Value));
        untyped.AddAsset("first.bin", first, new(0x0C00, Holder, 0x101C, "untyped:first"));
        Throws<InvalidDataException>(() => untyped.Build(catalog, Path.Combine(parent, "refmap-untyped-build"), tagSchema: Schema(tagRef: false)),
            "Unresolved own-hash occurrence");
        untyped.Build(catalog, Path.Combine(parent, "refmap-typed-build"), tagSchema: Schema(tagRef: true));
        Console.WriteLine("reference_map rows=exact lua=file_line_column manifest=packages remap=resolves untyped=refused");
    }

    private static void Put(StudioProject project, TftFile file)
    {
        string path = Path.Combine(project.ContentDirectory, file.Name.Replace('/', Path.DirectorySeparatorChar) + TftNames.FileExtension);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, file.Write());
    }

    /** Holder {size, link, label string}; the link is a tag reference, or an untyped u32. */
    private static RegistryIndex Schema(bool tagRef)
    {
        var registry = new TagRegistry { BuildId = "test" };
        foreach (var (name, size) in new[] { ("u64", 8), (tagRef ? "tagref" : "u32", 4), ("string_ptr", 8) })
            registry.FieldTypes.Add(new(registry.FieldTypes.Count, name, size, Math.Min(size, 8), "scalar"));
        registry.Classes.Add(new()
        {
            Class = Holder, Name = "holder", Kind = "tag", Size = 24, Align = 8, Source = "game",
            Fields = [new() { Name = "size", Offset = 0, Type = 0, Confidence = "game" }, new() { Name = "link", Offset = 8, Type = 1, Confidence = "game" },
                new() { Name = "label", Offset = 16, Type = 2, Confidence = "game" }],
        });
        return new RegistryIndex(registry);
    }

    private static TftFile Tag(RegistryIndex schema, string name, string link)
    {
        var root = new TagBlueprint { ClassId = Holder, Bytes = new byte[24] };
        BinaryPrimitives.WriteUInt32LittleEndian(root.Bytes.AsSpan(8), TftFile.TagRefPlaceholder);
        byte[] data = CanonicalTagWriter.Write(root, schema);
        return new TftFile
        {
            Name = name, TagType = "holder", ClassId = Holder, TypeInfo = 0x101C, BuildHash = TftFile.HashBuildId("test"),
            Layout = TftLayout.Extract(schema, data, Holder), Data = data, References = [new(8, TftReferenceKind.TagRef, 0, link)],
        };
    }

    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));

    private static void Throws<T>(Action action, string text) where T : Exception
    {
        try { action(); }
        catch (T error) when (error.Message.Contains(text, StringComparison.Ordinal)) { return; }
        throw new Exception($"Expected {typeof(T).Name} with \"{text}\".");
    }

    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
