using System.Buffers.Binary;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

internal static class FixtureChecks
{
    internal static void Run(string scratch, GeneratedNavigation generated, Action<bool, string> check)
    {
        string game = Path.Combine(scratch, "fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(game, "packages"));
        const uint tag = 0x80A00000;
        byte[] root = generated.Root.ToArray(); U32(root, 8, tag + 1); U32(root, 12, tag + 2);
        byte[] container = Array(0x38, 0x28, 0x80808BB0, 4, out int slot); U32(container, slot, tag + 3); container[8] = 1;
        byte[] branch = Array(24, 8, 0x808084C1, 16, out int branchRow); U32(branch, branchRow, tag + 4);
        byte[] bubble = new byte[32]; I64(bubble, 0, 32); U32(bubble, 8, tag + 5); U32(bubble, 24, 0x11223344);
        byte[] manifest = Array(48, 16, 0x80809EFB, 40, out int group); U32(manifest, group, 0x100);
        U32(manifest, 8, tag + 7); U32(manifest, 12, tag + 4);
        manifest = PackageManifest.AddEntries(manifest, 0x100, [3, 4, 7]);
        AuthoredPackageEntry[] entries =
        [
            new(0x80808CB6, 0x1000, root), new(0x80808CB8, 0x1000, generated.Mesh), new(0x80808CBA, 0x1000, generated.Graph),
            new(0x808099D6, 0x1000, NavigationPlacement.Table(tag)), new(0x80808A54, 0x2000, container),
            new(0x808091E0, 0x1000, branch), new(0x80807DAE, 0x1000, bubble), new(PackageManifest.ClassId, 0x1000, manifest)
        ];
        PackageWriter.WriteNew(Path.Combine(game, "packages", "w64_navigation_0100_0.pkg"), 0x100, entries);
        // Synthetic entity/config/schema entries exercise the installed constructor dependency closure.
        var globals = Enumerable.Range(0, 0xD02).Select(_ => new AuthoredPackageEntry(0, 0x1000, new byte[8])).ToArray();
        byte[] entity = Array(32, 16, 0x80809C04, 12, out int construction); U32(entity, construction, 0x80C70D00);
        byte[] config = new byte[80]; I64(config, 0, 80); U32(config, 0x44, 0x80C70B90);
        globals[0xD01] = new(0x80809C0F, 0x1000, entity); globals[0xD00] = new(0x80809C36, 0x1000, config);
        globals[0xB90] = new(0x80809BBB, 0x1000, new byte[8]);
        PackageWriter.WriteNew(Path.Combine(game, "packages", "w64_navigation_globals_0238_0.pkg"), 0x238, globals);
        using var catalog = PackageCatalog.Open(game, cacheOptions: new() { Enabled = false });
        var scene = new AssetSceneLoader(catalog).LoadBubble(tag + 6);
        check(scene.Navigation.Count == 1 && scene.DecodeErrorCount == 0, "Scene loader follows ownership and decodes navigation");
        check(scene.Navigation[0].Mesh.Layers[0].Faces.Length > 0, "Scene overlay has faces");
        var plan = NavigationPlacement.Plan(catalog, tag + 6);
        check(PackageNativeDependencies.Read(catalog, tag, 0x80808CB6, 0x1000, root).Order().SequenceEqual(new[] { tag + 1, tag + 2 }), "Native manifest closure includes both navigation children");
        check(PackageNativeDependencies.Read(catalog, tag + 3, 0x808099D6, 0x1000, NavigationPlacement.Table(tag)).Contains(0x80C70D01), "Native manifest closure includes the installed constructor entity");
        check(plan.TableField == slot, "Attachment replaces the original navigation slot");
        var project = StudioProject.Create(Path.Combine(scratch, "project-" + Guid.NewGuid().ToString("N")), "Navigation fixture");
        var added = project.ApplyTransaction(NavigationPlacement.Additions(generated, 0x101), allocated => NavigationPlacement.Bind(plan, generated, allocated), catalog);
        check(added.Count == 4 && project.Changes.Count == 1, "One transaction creates the group and retargets its owner");
        byte[] attached = project.ReadAsset(tag + 4, catalog);
        check(BinaryPrimitives.ReadUInt32LittleEndian(attached.AsSpan(slot)) == 0x80A02003, "Container names the allocated placement");
        var bound = NavigationDecoder.Decode(project.ReadAddedAsset(added[0].Id, catalog), project.ReadAddedAsset(added[1].Id, catalog), project.ReadAddedAsset(added[2].Id, catalog));
        check(bound.MeshTag == 0x80A02001 && bound.GraphTag == 0x80A02002, "Allocated CB6 children decode");
        var built = project.Build(catalog, Path.Combine(scratch, "build-" + Guid.NewGuid().ToString("N")));
        check(built.PackageFiles.Count >= 2, "Project builds a new package and an owner patch");
        string output = Path.Combine(scratch, "readback-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(output, "packages"));
        foreach (string file in built.PackageFiles) File.Copy(file, Path.Combine(output, "packages", Path.GetFileName(file)));
        // A patch reads unchanged blocks from its predecessor.
        File.Copy(Path.Combine(game, "packages", "w64_navigation_0100_0.pkg"), Path.Combine(output, "packages", "w64_navigation_0100_0.pkg"));
        File.Copy(Path.Combine(game, "packages", "w64_navigation_globals_0238_0.pkg"), Path.Combine(output, "packages", "w64_navigation_globals_0238_0.pkg"));
        using var readback = PackageCatalog.Open(output, cacheOptions: new() { Enabled = false });
        for (int i = 0; i < 4; i++)
            check(readback.Read(0x80A02000u + (uint)i).SequenceEqual(project.ReadAddedAsset(added[i].Id, catalog)), "Built entry bytes match the published group");
        var builtManifest = PackageManifest.Read(readback.Read(tag + 7));
        check(Enumerable.Range(0, 4).All(i => builtManifest.Lists(0x80A02000u + (uint)i)), "A manifest without navigation classes receives the whole generated group");
        check(new uint[] { 0x80C70D01, 0x80C70D00, 0x80C70B90 }.All(builtManifest.Lists), "Manifest includes the installed entity/config/schema closure");
        var rebuilt = new AssetSceneLoader(readback).LoadBubble(tag + 6);
        check(rebuilt.Navigation.Count == 1 && rebuilt.Navigation[0].Tag == 0x80A02000, "PackageFile and scene reader re-decode the built ownership graph");
        check(rebuilt.DecodeErrorCount == 0, "Built scene has no decode errors");
        var noNav = plan with { TableField = -1 };
        byte[] appended = noNav.Attach(0x80A02003);
        check(BinaryPrimitives.ReadInt64LittleEndian(appended.AsSpan(0x28)) == 2, "Nav-less owner append preserves existing slots");
    }

    private static byte[] Array(int size, int field, uint type, int stride, out int row)
    {
        int header = (size + 19) & ~15; row = header + 16; byte[] b = new byte[row + stride];
        I64(b, 0, b.Length); I64(b, field, 1); I64(b, field + 8, header - field - 8);
        U32(b, header - 4, 0x80809FBD); I64(b, header, 1); U32(b, header + 8, type); return b;
    }
    private static void U32(byte[] b, int p, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p), v);
    private static void I64(byte[] b, int p, long v) => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(p), v);
}
