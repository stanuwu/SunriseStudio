using System.Diagnostics;
using SunriseStudio.Editor;
using SunriseStudio.Editor.Creation;
using static Expect;

/** The New map workflow in a headless workspace: its fields, one undoable import, and the map opening as a world. */
internal static class WorkflowChecks
{
    internal static void Run(string installation, string temporary)
    {
        using var workspace = new StudioWorkspace { CacheOptions = new() { Enabled = false } };
        workspace.OpenInstallation(installation); Wait(workspace);
        workspace.CreateProject(Path.Combine(temporary, "workflow-project"), "Workflow"); Wait(workspace);
        var type = CreationTypes.For(CreationTypes.MapKey, CreationMode.New);
        Check(type.Available && type.Summary.Contains(StudioUi.MayNotWork, StringComparison.Ordinal)
            && CreationTypes.Blocked.All(blocked => blocked.Name != "Map"), "New map is not offered with its warning.");
        var workflow = (NewMapWorkflow)type.Factory!(workspace);
        Check(workflow.Limits.Contains(StudioUi.MayNotWork), "The workflow does not say it may not work in game.");
        CreationField Field(string label) => workflow.Fields.Single(field => field.Label == label);
        Check(Field("Bubbles").Text == "1" && workflow.Problems(workspace).SequenceEqual(["Name is required."]),
            "The defaults need more than a name.");
        Field("Name").Text = "Upper";
        Check(workflow.Problems(workspace).Count == 1, "An upper-case name was accepted.");
        Field("Name").Text = "workflow_world";
        foreach (var (count, valid) in new[] { ("0", false), ("65", false), ("2", true) })
        {
            Field("Bubbles").Text = count;
            Check(workflow.Problems(workspace).Count == 0 == valid, $"Bubble count {count} was judged wrongly.");
        }
        Field("Ground size").Text = "0.01";
        Check(workflow.Problems(workspace).Count != 0, "A ground size off the 1/64 grid was accepted.");
        Field("Ground size").Text = "24";
        Field("Ground colour").Text = "3";
        Check(workflow.Problems(workspace).Count == 0, string.Join(" ", workflow.Problems(workspace)));
        var next = workflow.Create(workspace);
        Wait(workspace);
        var project = workspace.Project!;
        var created = project.AddedAssets.Where(asset => asset.Group == "workflow_world").ToArray();
        var root = created.Single(asset => asset.Native?.Name == "map:workflow_world:root");
        Check(next is { Background: true, MapGroup: "workflow_world" } && created.Length == project.AddedAssets.Count && created.Length == 180,
            "The workflow did not add the whole map as one group.");
        Check(workspace.Document?.AddedAssetId == root.Id, "The map root is not selected after the import.");
        // The editor opens what the follow-up names; the map loads as a world from the project overlay.
        uint tag = 0x80800000u + ((uint)root.Native!.PackageId << 13) + (uint)root.Native.EntryIndex!.Value;
        workspace.OpenMap(tag); Wait(workspace);
        Check(workspace.World is { } world && world.Map.Tag == tag && world.MapScene.Bubbles.Count == 2
            && world.MapScene.Markers.Count(marker => marker.Layer == SunriseStudio.Scene.SceneLayer.Spawns) == 2,
            "The new map does not open with its bubbles and arrivals.");
        var again = new NewMapWorkflow(workspace);
        again.Fields.Single(field => field.Label == "Name").Text = "workflow_world";
        Check(again.Problems(workspace).Any(problem => problem.Contains("exists", StringComparison.Ordinal)),
            "A second map with the same name was accepted.");
        var ids = created.Select(asset => (asset.Id, asset.Native)).OrderBy(pair => pair.Id, StringComparer.Ordinal).ToArray();
        workspace.Undo(); Wait(workspace);
        Check(workspace.Project!.AddedAssets.Count == 0, "Undo left part of the map.");
        workspace.Redo(); Wait(workspace);
        Check(workspace.Project!.AddedAssets.Select(asset => (asset.Id, asset.Native)).OrderBy(pair => pair.Id, StringComparer.Ordinal).SequenceEqual(ids),
            "Redo did not restore the same ids and entry indices.");
        Console.WriteLine($"PASS new-world-workflow assets={created.Length} opened={tag:X8} bubbles=2 undo=removed redo=same-ids "
            + "refusals=name,count,size,duplicate");
    }

    private static void Wait(StudioWorkspace workspace)
    {
        var watch = Stopwatch.StartNew();
        while (workspace.IsBusy && watch.Elapsed < TimeSpan.FromSeconds(120)) { workspace.Poll(); Thread.Sleep(2); }
        Check(!workspace.IsBusy, "The workspace timed out.");
        Check(workspace.Error.Length == 0, workspace.Error);
    }
}
