using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags;

/** Package ids come from the mod id, the same on every machine, and spill along the same probe. */
static class TagAllocationChecks
{
    public static void Run(string parent)
    {
        // Two machines with the same installed set pick the same ids; a different install only moves past what it has.
        bool Installed(ushort id) => id is 0x0100 or 0x0200;
        ushort first = TagAllocator.PreferredPackage("dev.example.alpha", Installed);
        Require(first == TagAllocator.PreferredPackage("dev.example.alpha", Installed)
            && first == TagAllocator.Probe(TagAllocator.ModSeed + "dev.example.alpha").First(id => !Installed(id)),
            "The same mod id picked two package ids.");

        // Two mods share an id only when the probe forces it: taking one's id moves it to its next.
        ushort other = TagAllocator.PreferredPackage("dev.example.beta", Installed);
        Require(other != first, "Two mod ids picked the same first package.");
        ushort forced = TagAllocator.PreferredPackage("dev.example.alpha", id => Installed(id) || id == first);
        Require(forced == TagAllocator.Probe(TagAllocator.ModSeed + "dev.example.alpha").Where(id => !Installed(id)).Skip(1).First(),
            "A taken id did not move the mod to the next id of its probe.");

        // A full package spills into the next probe id.
        var allocator = new TagAllocator([first]);
        allocator.Grow = () => allocator.NextPackage("dev.example.alpha", Installed);
        for (int index = 0; index < TagHashes.EntriesPerPackage; index++) allocator.Allocate("t" + index);
        uint spilled = allocator.Allocate("overflow");
        Require(allocator.Packages.Count == 2 && TagHashes.Split(spilled) == (forced, 0),
            "A full package did not spill to the next id of the probe.");
        var reread = TagAllocator.Parse(allocator.ToJson());
        Require(reread.Packages.SequenceEqual(allocator.Packages) && reread.Tags["overflow"] == spilled, "The spilled table did not round-trip.");

        // A project meeting this version without object edits takes the mod probe; an older one keeps its seed.
        string install = Path.Combine(parent, "allocation-install");
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_test_0100_0.pkg"), 0x100, [new(0x80800001, 0x100A, [1, 2, 3, 4])]);
        using var catalog = PackageCatalog.Open(install);
        var project = StudioProject.Create(Path.Combine(parent, "allocation-project"), "Allocation");
        project.SetMod(new ModMetadata("dev.example.alpha", "1.0.0", "Allocation"));
        Require(project.EnsureTagAllocator(catalog) is { IdentityGeneratedPackages: false } fresh
            && fresh.Packages.Single() == TagAllocator.PreferredPackage("dev.example.alpha", catalog.ContainsInstalledPackage),
            "A new project did not start on its mod probe.");
        var legacy = TagAllocator.Parse(Encoding.UTF8.GetBytes("{\"generated\":\"identity\",\"packages\":[\"0x0412\"],\"retired\":{},\"tags\":{}}"));
        Require(legacy.IdentityGeneratedPackages && Encoding.UTF8.GetString(legacy.ToJson()).Contains("\"identity\"", StringComparison.Ordinal),
            "The legacy flag did not round-trip.");
        // The legacy seed probes exactly as generated data did before the mod probe.
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes("Sunrise object edits\ndev.example.alpha"));
        int count = PackageWriter.LastNewPackageId - PackageWriter.FirstNewPackageId + 1;
        ushort old = (ushort)(PackageWriter.FirstNewPackageId + BinaryPrimitives.ReadUInt32LittleEndian(digest) % count);
        Require(TagAllocator.Probe("Sunrise object edits\ndev.example.alpha").First() == old, "The legacy seed no longer reproduces old ids.");
        Console.WriteLine("tag_allocation mod_id=deterministic clash=forced_only spill=probe legacy=kept");
    }

    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
