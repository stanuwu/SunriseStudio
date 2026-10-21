using System.Text.Json;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using static Expect;

/** The map as a project group: manifest version 8, undo and redo with the same ids, build and package readback. */
internal static class ProjectChecks
{
    internal static void Run(PackageCatalog catalog, string installation, IReadOnlyList<NewWorldBuild> builds, string temporary)
    {
        var build = builds[1];
        var recipe = CompileChecks.Two;
        var project = StudioProject.Create(Path.Combine(temporary, "project"), "New world");
        project.ObjectEditCompiler = ObjectEditCompiler.Default;
        var created = project.ApplyTransaction(build.Additions, catalog: catalog);
        Check(created.Count == build.Additions.Count && created.All(asset => asset.Group == recipe.Name)
            && created.Skip(1).Select(asset => asset.Native!.EntryIndex).SequenceEqual(build.Allocation.Entries.Select(entry => (int?)entry.EntryIndex)),
            "The project did not keep the group or the planned entry indices.");
        Check(created.Count(asset => asset.BaseTagHash is not null) == 7 * recipe.Bubbles.Count + 3, "Derived copies lost their originals.");
        using (var manifest = JsonDocument.Parse(File.ReadAllBytes(project.ManifestPath)))
            Check(manifest.RootElement.GetProperty("version").GetInt32() == StudioProject.ManifestVersion && StudioProject.ManifestVersion == 8,
                "A grouped project is not written as manifest version 8.");
        var reopened = StudioProject.Open(project.ManifestPath);
        Check(reopened.AddedAssets.Select(asset => (asset.Id, asset.Group)).SequenceEqual(created.Select(asset => (asset.Id, asset.Group))),
            "Groups did not survive a reopen.");
        foreach (var (asset, addition) in created.Zip(build.Additions))
            Check(reopened.ReadAddedAsset(asset.Id, catalog).AsSpan().SequenceEqual(addition.Data), $"{asset.Name} does not read back from the project.");
        // Undo removes the group in one transaction; redo restores the same ids and entry indices.
        var snapshots = created.ToDictionary(asset => asset.Id, asset => (AddedAssetSnapshot?)project.CaptureAddedAsset(asset.Id, catalog));
        project.RestoreTransaction(new(new Dictionary<uint, byte[]>()), created.ToDictionary(asset => asset.Id, _ => (AddedAssetSnapshot?)null), catalog);
        Check(project.AddedAssets.Count == 0, "Undo left part of the map.");
        project.RestoreTransaction(new(new Dictionary<uint, byte[]>()), snapshots, catalog);
        Check(project.AddedAssets.OrderBy(asset => asset.Id).Select(asset => (asset.Id, asset.Native, asset.Group, asset.BaseTagHash))
            .SequenceEqual(created.OrderBy(asset => asset.Id).Select(asset => (asset.Id, asset.Native, asset.Group, asset.BaseTagHash))),
            "Redo did not restore the same ids, entry indices and groups.");
        Throws<InvalidDataException>(() => project.ApplyTransaction([new("bad.bin", [1]) { Group = "a/b" }], catalog: catalog),
            "A group with a path separator was accepted.");
        // Build into a new package and read it back beside the installation.
        var result = ProjectBuilder.Build(project, catalog, Path.Combine(temporary, "build"));
        string install = Path.Combine(temporary, "built-install");
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        foreach (string path in Directory.EnumerateFiles(Path.Combine(installation, "packages")).Concat(result.PackageFiles))
            File.Copy(path, Path.Combine(install, "packages", Path.GetFileName(path)));
        int manifests;
        using (var built = PackageCatalog.Open(install))
        {
            foreach (var entry in build.Allocation.Entries)
            {
                Check(built.TryGetEntry(entry.Tag, out var row) && row.Reference == entry.Reference && row.TypeInfo == entry.TypeInfo,
                    $"The build does not hold {entry.Key} with its class and type.");
                Check(built.Read(entry.Tag).AsSpan().SequenceEqual(entry.Data), $"The build changed the bytes of {entry.Key}.");
            }
            Check(built.GetName(build.MapTag) == recipe.MapName, "The built root is not named map:NAME:root.");
            manifests = built.Entries.Count(entry => entry.PackageId == CompileChecks.Package && entry.Reference == PackageManifest.ClassId);
            var read = ReaderChecks.Decode(built, build, recipe, "package", collision: false);
            Check(read.Count > build.Allocation.Entries.Count / 2, "The built package did not decode.");
        }
        // A new package alone has no installed patch whose dependency record could list it; the receipt says so.
        int unlisted, planned;
        using (var receipt = JsonDocument.Parse(File.ReadAllBytes(result.ReceiptPath)))
        {
            unlisted = receipt.RootElement.GetProperty("addedAssetsInNoRecord").GetArrayLength();
            planned = receipt.RootElement.GetProperty("rootManifests").GetArrayLength();
        }
        Check(planned == 0, "The build replaced the map's own root manifests.");
        Console.WriteLine($"PASS new-world-project group={recipe.Name} assets={created.Count} manifest=8 undo-redo=same-ids "
            + $"unlisted-in-records={unlisted} planned-root-manifests={planned} "
            + $"packages={string.Join(",", result.PackageFiles.Select(Path.GetFileName))} entries={build.Allocation.Entries.Count} "
            + $"read-back=equal root-manifests={manifests} notes={result.Notes.Count}");
        foreach (string note in result.Notes) Console.WriteLine("  note " + note);
    }
}
