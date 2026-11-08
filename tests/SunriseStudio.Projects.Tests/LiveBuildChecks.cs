using System.Buffers.Binary;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.World.Sdk;

/**
 * Live updates write each changed package as its next generation in the installed mod, keep the
 * earlier generations untouched, and serve exactly the bytes a full build would.
 */
static class LiveBuildChecks
{
    private const uint SpawnClass = 0x80809162, StaticClass = 0x808071A7, AddedClass = 0x80800003, LightClass = 0x80800010,
        LampClass = 0x80800011;

    public static void Run(string parent)
    {
        string install = Path.Combine(parent, "live-install");
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        byte[] spawn = Bytes(16, 1), mesh = Bytes(32, 2);
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_test_0100_0.pkg"), 0x100,
            [new(SpawnClass, 0x100A, spawn, "test:spawn"), new(StaticClass, 0x100A, mesh, "test:static"), new(LightClass, 0x100A, Light(1, 0), "test:light"),
                new(LampClass, 0x100A, Light(1, 0), "test:lamp")]);
        uint spawnTag = TagHashes.Make(0x100, 0), lightTag = spawnTag + 2, lampTag = spawnTag + 3;
        using var catalog = PackageCatalog.Open(install);
        var schema = Schema();

        var project = StudioProject.Create(Path.Combine(parent, "live-project"), "Live project");
        project.SetMod(new ModMetadata("test.live", "1.0.0", "Live test"));
        project.ApplyEdit(spawnTag, Bytes(16, 3), catalog);
        var added = project.AddAsset("added.bin", Bytes(24, 4), new(0x0C00, AddedClass, 0x100A, "test:added"));
        uint addedTag = TagHashes.Make(0x0C00, added.Native!.EntryIndex!.Value);
        ModInstaller.BuildAndInstall(project, catalog, []);
        string mod = Path.Combine(ModInstaller.ModsDirectory(install), "test.live");
        Require(File.Exists(Path.Combine(mod, LiveModState.FileName)) && File.Exists(Path.Combine(mod, "patches", "w64_test_0100_1.pkg"))
            && File.Exists(Path.Combine(mod, "packages", "w64_sunrise_0c00_0.pkg")), "The install did not write its files and live state.");
        string firstPatch = Hash(Path.Combine(mod, "patches", "w64_test_0100_1.pkg"));

        // A spawn edit and an added-asset edit become one new generation each.
        project.ApplyEdit(spawnTag, Bytes(16, 5), catalog);
        project.UpdateAddedAsset(added.Id, Bytes(24, 6));
        var first = ModInstaller.BuildAndPushLive(project, catalog, [], schema);
        Require(first.WrittenFiles.Order(StringComparer.Ordinal).SequenceEqual(["packages/w64_sunrise_0c00_1.pkg", "patches/w64_test_0100_2.pkg"])
            && first.Revision == 1 && Hash(Path.Combine(mod, "patches", "w64_test_0100_1.pkg")) == firstPatch,
            "A live update did not write the next generations, or it touched an earlier one: " + string.Join(", ", first.WrittenFiles));
        var spawnChange = first.Tags.Single(x => x.Tag == spawnTag);
        Require(spawnChange is { ClassId: SpawnClass, Tier: 1, LayoutPreserving: true, Removed: false }
            && first.Tags.Single(x => x.Tag == addedTag) is { Tier: 4, NewSize: 24 }, "The live tags are wrong.");
        Served(install, mod, spawnTag, Bytes(16, 5), addedTag, Bytes(24, 6));
        Require(ModInstaller.BuildAndPushLive(project, catalog, [], schema) is { WrittenFiles.Count: 0, Revision: 1 },
            "An unchanged project wrote a generation.");

        // Reverting the edit serves the installed bytes again, through one more generation.
        project.Revert(spawnTag);
        var reverted = ModInstaller.BuildAndPushLive(project, catalog, [], schema);
        Require(reverted.WrittenFiles.SequenceEqual(["patches/w64_test_0100_3.pkg"]), "A revert did not write one generation.");
        Served(install, mod, spawnTag, spawn, addedTag, Bytes(24, 6));
        var full = project.Build(catalog, Path.Combine(parent, "live-full-build"), tagSchema: schema);
        using (var served = Chain(install, mod, "live-check-full"))
            Require(full.Contents.NewEntries.All(entry => served.Read(TagHashes.Make(entry.PackageId, entry.EntryIndex))
                .AsSpan().SequenceEqual(entry.Data)), "The live chain does not serve what a full build writes.");

        Poke(project, catalog, schema, lightTag);

        // A scalar outside every poke-safe struct is a tier 4 patch the game applies at the next load.
        project.ApplyEdit(lampTag, Light(2.5f, 0), catalog);
        var lamp = ModInstaller.BuildAndPushLive(project, catalog, [], schema).Tags.Single(x => x.Tag == lampTag);
        Require(lamp is { Tier: 4, LayoutPreserving: true } && Matches(lamp.Ranges, Light(1, 0), Light(2.5f, 0)),
            "A changed scalar outside a poke-safe struct did not become a load patch.");

        // An activity SDK pack that differs from the build needs a restart.
        string pack = Path.Combine(mod, ModActivitySdk.FileName);
        File.WriteAllBytes(pack, [1, 2, 3]);
        try
        {
            var stale = Throws<InvalidOperationException>(() => ModInstaller.BuildAndPushLive(project, catalog, [], schema));
            Require(stale.Message.Contains("activity SDK pack", StringComparison.Ordinal), "A changed activity SDK pack was not refused by name.");
        }
        finally { File.Delete(pack); }

        // A new entry needs a restart, and the refusal writes nothing.
        project.AddAsset("second.bin", Bytes(8, 7), new(0x0C00, AddedClass, 0x100A, "test:second"));
        int before = Directory.GetFiles(mod, "*.pkg", SearchOption.AllDirectories).Length;
        Throws<InvalidOperationException>(() => ModInstaller.BuildAndPushLive(project, catalog, [], schema));
        Require(Directory.GetFiles(mod, "*.pkg", SearchOption.AllDirectories).Length == before, "A refused live update wrote a file.");
        Console.WriteLine("live_build generations=next earlier=untouched tiers=registry unchanged=no_write revert=installed full=identical poke=scalar_only guard=served load=scalar sdk_pack=restart new_entry=restart");
    }

    /** A scalar in a poke-safe struct becomes a guarded patch of the served bytes; a reference does not. */
    private static void Poke(StudioProject project, PackageCatalog catalog, RegistryIndex schema, uint lightTag)
    {
        project.ApplyEdit(lightTag, Light(2.5f, 0), catalog);
        var first = ModInstaller.BuildAndPushLive(project, catalog, [], schema).Tags.Single(x => x.Tag == lightTag);
        Require(first is { Tier: 2, SpawnTime: false, LayoutPreserving: true } && Matches(first.Ranges, Light(1, 0), Light(2.5f, 0)),
            "A changed scalar did not become a tier 2 patch of the installed bytes.");
        project.ApplyEdit(lightTag, Light(3.5f, 0), catalog);
        var second = ModInstaller.BuildAndPushLive(project, catalog, [], schema).Tags.Single(x => x.Tag == lightTag);
        Require(second.Tier == 2 && Matches(second.Ranges, Light(2.5f, 0), Light(3.5f, 0)),
            "A second patch does not guard the bytes the first one served.");
        project.ApplyEdit(lightTag, Light(3.5f, 0x80800001), catalog);
        Require(ModInstaller.BuildAndPushLive(project, catalog, [], schema).Tags.Single(x => x.Tag == lightTag) is { Tier: 4, Ranges.Count: 0 },
            "A changed reference was offered as a memory patch.");
    }

    /** Every range covers changed bytes only, and together they turn before into after. */
    private static bool Matches(IReadOnlyList<LiveRange> ranges, byte[] before, byte[] after)
    {
        byte[] patched = before.ToArray();
        foreach (var range in ranges)
        {
            if (!range.Old.AsSpan().SequenceEqual(before.AsSpan(range.Offset, range.Old.Length))) return false;
            range.Bytes.CopyTo(patched, range.Offset);
        }
        return ranges.Count > 0 && patched.SequenceEqual(after);
    }

    /** A 16-byte light: the tag header, a float intensity at 8, a texture reference at 12. */
    private static byte[] Light(float intensity, uint texture)
    {
        byte[] bytes = new byte[16];
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8), intensity);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), texture);
        return bytes;
    }

    /** The installed packages plus every generation the mod serves, read as one install. */
    private static PackageCatalog Chain(string install, string mod, string name)
    {
        string root = Path.Combine(Path.GetDirectoryName(install)!, name + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(root, "packages"));
        foreach (string file in Directory.GetFiles(Path.Combine(install, "packages"))
            .Concat(Directory.GetFiles(mod, "*.pkg", SearchOption.AllDirectories)))
            File.Copy(file, Path.Combine(root, "packages", Path.GetFileName(file)));
        return PackageCatalog.Open(root);
    }

    private static void Served(string install, string mod, uint firstTag, byte[] firstBytes, uint secondTag, byte[] secondBytes)
    {
        using var served = Chain(install, mod, "live-check");
        Require(served.Read(firstTag).AsSpan().SequenceEqual(firstBytes) && served.Read(secondTag).AsSpan().SequenceEqual(secondBytes),
            "The newest generations do not serve the edited bytes.");
    }

    /** The spawn class is tier 1, the light is poke-safe and the lamp is not; the rest default to 4. */
    private static RegistryIndex Schema()
    {
        var registry = new TagRegistry { BuildId = "test" };
        registry.FieldTypes.Add(new(0, "u32", 4, 4, "scalar"));
        registry.FieldTypes.Add(new(1, "f32", 4, 4, "scalar"));
        registry.FieldTypes.Add(new(2, "tagref", 4, 4, "reference"));
        registry.Classes.Add(new()
        {
            Class = LightClass, Name = "light", Kind = "tag", Size = 16, Source = "game", PokeSafe = true, LiveTier = 2,
            Fields = [new() { Name = "intensity", Offset = 8, Type = 1, Confidence = "test" }, new() { Name = "texture", Offset = 12, Type = 2, Confidence = "test" }],
        });
        registry.Classes.Add(new()
        {
            Class = LampClass, Name = "lamp", Kind = "tag", Size = 16, Source = "game",
            Fields = [new() { Name = "intensity", Offset = 8, Type = 1, Confidence = "test" }, new() { Name = "texture", Offset = 12, Type = 2, Confidence = "test" }],
        });
        registry.Classes.Add(new() { Class = SpawnClass, Name = "spawn", Kind = "tag", Size = 16, Source = "game", LiveTier = 1 });
        registry.Classes.Add(new() { Class = StaticClass, Name = "static", Kind = "tag", Size = 32, Source = "game" });
        return new RegistryIndex(registry);
    }

    private static byte[] Bytes(int length, byte fill) => Enumerable.Repeat(fill, length).ToArray();

    private static string Hash(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }

    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
