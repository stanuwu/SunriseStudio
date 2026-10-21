using System.Diagnostics;
using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Editor;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

/** Adds a static copy through the workspace, moves it in place and walks undo, redo, close and reopen. */
internal static class AddedPlacementWorkflowChecks
{
    internal static void Run(string root)
    {
        string install = Path.Combine(root, "added-placement-install");
        StaticPlacementFixture.Write(install);
        // The fixture is unencrypted, so a synthetic key export only satisfies the opening check.
        string keys = Path.Combine(root, "added-placement-keys.bin");
        byte[] export = new byte[60];
        "SRPKKEYS"u8.CopyTo(export); export[8] = 1; BitConverter.TryWriteBytes(export.AsSpan(12), 86657u);
        File.WriteAllBytes(keys, export);
        using var workspace = new StudioWorkspace { CacheOptions = new() { Enabled = false } };
        workspace.OpenInstallation(install, keys); Wait(workspace);
        workspace.CreateProject(Path.Combine(root, "added-placement-project"), "Added placement"); Wait(workspace);
        var catalog = workspace.Catalog!;
        var project = workspace.Project!;
        var source = Preview(catalog, 3).Instances.Single(instance => instance.InstanceSourceTag == StaticPlacementFixture.Table
            && instance.InstanceSourceIndex == 1);
        var duplicate = StaticPlacements.Duplicate(catalog, source, project.ObjectEdits, "Copy");
        workspace.ApplyObjectEdit(duplicate); Wait(workspace);
        Check(workspace.ObjectEditError.Length == 0 && workspace.CompiledObjects.GeneratedAssets.Count == 4
            && project.ObjectEdits.Single().Kind == ObjectEditKind.AddedPlacement && project.AddedAssets.Count == 0,
            "The workspace did not publish the added placement.");
        var generatedTags = workspace.CompiledObjects.GeneratedAssets.Select(asset => asset.TagHash).ToHashSet();
        Check(catalog.GetOverlayEntries().Count(entry => entry.Generated && entry.AddedInstances.Count == 1) == 1
            && workspace.Assets.All(row => !generatedTags.Contains(row.Entry.TagHash)),
            "Added placement metadata did not reach the overlay or leaked into browser rows.");
        var copy = Copy(Preview(catalog, 4, workspace));
        var world = workspace.World!;
        var moved = SceneEdits.CreateTransformEdit(catalog, copy, new Vector3(15, 2, 0), null, 1)!;
        Check(moved.Kind == ObjectEditKind.AddedPlacement && moved.Target == duplicate.Target, "Moving the copy changed its target.");
        workspace.ApplyObjectEdit(moved); Wait(workspace);
        Check(project.ObjectEdits.Single().Position!.SequenceEqual([15f, 2, 0])
            && workspace.LastWorldRefresh is { Incremental: true } && ReferenceEquals(workspace.World, world)
            && copy.Transform.Translation == new Vector3(15, 2, 0), "Moving the copy rebuilt the world or missed the drawn row.");
        Check(workspace.CompiledObjects.GeneratedAssets.Select(asset => asset.TagHash).ToHashSet().SetEquals(generatedTags),
            "Moving the copy reallocated generated identities.");
        workspace.Undo(); Wait(workspace);
        Check(project.ObjectEdits.Single().Position!.SequenceEqual(duplicate.Position!) && workspace.CanRedo
            && workspace.LastWorldRefresh is { Incremental: true } && copy.Transform.Translation == new Vector3(10, 0, 0),
            "Undoing the move failed or rebuilt the world.");
        typeof(StudioWorkspace).GetProperty(nameof(StudioWorkspace.World))!.SetValue(workspace, null);
        workspace.Undo(); Wait(workspace);
        Check(project.ObjectEdits.Count == 0 && workspace.CompiledObjects.GeneratedAssets.Count == 0
            && generatedTags.All(tag => !catalog.ContainsTag(tag)), "Undoing the copy left private assets registered.");
        Preview(catalog, 3);
        workspace.Redo(); Wait(workspace);
        Check(project.ObjectEdits.Single().Target == duplicate.Target, "Redo lost the copy.");
        Preview(catalog, 4);
        string path = project.ManifestPath;
        workspace.CloseProject(); Wait(workspace);
        Check(generatedTags.All(tag => !catalog.ContainsTag(tag)), "Closing the project retained private assets.");
        workspace.OpenProject(path); Wait(workspace);
        Check(workspace.Project!.ObjectEdits.Single().Added == duplicate.Added
            && workspace.CompiledObjects.GeneratedAssets.Select(asset => asset.TagHash).ToHashSet().SetEquals(generatedTags),
            "Reopening changed the copy or its private namespace.");
        Preview(catalog, 4);
        Console.WriteLine("editor_added_placement overlay=pass move=in_place undo=pass redo=pass close=pass reopen=pass");
    }

    private static SceneDocument Preview(PackageCatalog catalog, int count, StudioWorkspace? workspace = null)
    {
        MeshData Mesh(uint tag) => new() { TagHash = tag, Vertices = [new(Vector3.Zero, Vector3.UnitZ, Vector2.Zero, Vector4.One)], Indices = [] };
        var loader = new AssetSceneLoader(catalog)
        {
            MeshCache = new Dictionary<uint, MeshData[]>
            { [StaticPlacementFixture.ModelA] = [Mesh(StaticPlacementFixture.ModelA)], [StaticPlacementFixture.ModelB] = [Mesh(StaticPlacementFixture.ModelB)] }
        };
        var scene = loader.LoadBubble(StaticPlacementFixture.Bubble);
        Check(scene.Instances.Count == count, $"The overlay drew {scene.Instances.Count} instances, not {count}.");
        if (workspace is null) return scene;
        typeof(StudioWorkspace).GetProperty(nameof(StudioWorkspace.World))!.SetValue(workspace, WorldDocument.FromScene(scene));
        typeof(StudioWorkspace).GetField("worldChanges", System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!.SetValue(workspace, new HashSet<uint>());
        return scene;
    }

    private static MeshInstance Copy(SceneDocument scene) => scene.Instances.Single(instance => instance.InstanceSourceTag != StaticPlacementFixture.Table);

    private static void Wait(StudioWorkspace workspace)
    {
        var timer = Stopwatch.StartNew();
        while (workspace.IsBusy && timer.Elapsed < TimeSpan.FromSeconds(30))
        { workspace.Poll(); Thread.Sleep(1); }
        Check(!workspace.IsBusy && workspace.Error.Length == 0, workspace.Error);
    }

    private static void Check(bool valid, string message)
    { if (!valid) throw new InvalidOperationException(message); }
}
