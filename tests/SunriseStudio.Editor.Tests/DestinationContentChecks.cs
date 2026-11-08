using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using SunriseStudio.Authoring;
using SunriseStudio.Editor;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Tft;
using SunriseStudio.World;

/**
 * A destination the project made opens in the world view from its `content/` tree, and its bundle
 * static moves in its `.tft` source with one undo step. Needs an installation.
 */
static class DestinationContentChecks
{
    // Trostland, Widow's Walk and the Atrium of EDZ, as the testbed keeps them.
    private static readonly uint[] Kept = [0xB8459D59, 0xC0064AEF, 0xC0064AED];
    private const float Raise = 2f;

    public static void Owned(string game, string root)
    {
        string folder = Path.Combine(root, "destination-project");
        using var workspace = new StudioWorkspace();
        workspace.OpenInstallation(game); Wait(workspace, "open installation");
        workspace.CreateProject(folder, "destination"); Wait(workspace, "create project");
        workspace.SetModMetadata(new SunriseStudio.Projects.ModMetadata("check.destination", "1.0.0", "destination", "check"));
        var catalog = workspace.Catalog!;
        uint scenario = catalog.Names.Where(x => x.Value == "edz_freeroam:scenario_client").Select(x => x.Key)
            .OrderByDescending(x => catalog.Read(x).Length).ThenBy(x => x).First();
        var template = ActivityRows.FreeRoam(catalog.ReadOriginal(ActivityRows.ActivityTable)).Single(row => row.Name == "edz_freeroam");
        ushort package = ushort.Parse(workspace.DefaultPackageText, System.Globalization.NumberStyles.HexNumber);
        if (workspace.Document is null) { workspace.Select(ActivityRows.ActivityTable); Wait(workspace, "open anchor"); }
        workspace.CreateDestination(scenario, Kept.ToHashSet(), "testbed", new(template, "Testbed", "", package), TestFixtures.TestbedBubble("bubble_a"));
        Wait(workspace, "create destination", 1800);
        Check(workspace.Notice is not { Failed: true }, "The destination was not created: " + workspace.Notice?.Text);

        uint map = ContentTag(workspace, folder, SMapRoot.Class), mission = ContentTag(workspace, folder, SScenario.Class);
        Check(workspace.ProjectContentError.Length == 0 && workspace.IsProjectContent(map),
            "The project's content did not compile into the overlay: " + workspace.ProjectContentError);
        workspace.OpenMission(map, mission); Wait(workspace, "open world", 1800);
        var world = workspace.World ?? throw new InvalidOperationException("The project's map did not open.");
        var target = world.Objects.FirstOrDefault(row => row.Source is MeshInstance mesh && workspace.IsProjectContent(mesh.InstanceSourceTag)
            && catalog.TryGetEntry(mesh.InstanceSourceTag, out var entry) && entry.Reference == SStaticMeshInstances.Class)
            ?? throw new InvalidOperationException("The bundle static is not in the project's world.");
        var info = SceneEdits.Describe(catalog, target, world, workspace.IsProjectContent);
        Check(info.CanTransform && info.Target is { } row && workspace.IsProjectContent(row.SourceTag),
            "The project's static cannot be moved: " + info.Reason);
        uint table = info.Target!.SourceTag;
        string file = Directory.EnumerateFiles(workspace.Project!.ContentDirectory, "*.static_mesh_instances.tft", SearchOption.AllDirectories)
            .Single(path => ContentTag(workspace, folder, path) == table);
        string before = Hash(file);
        workspace.Run(() => workspace.MoveContentStatic(info.Target!.SourceTag, info.Target.Index,
            info.Position + new Vector3(0, 0, Raise), info.Rotation, info.Scale));
        Wait(workspace, "move");
        string moved = Hash(file);
        var after = SceneEdits.Describe(catalog, workspace.World!.Find(target.Id)!, workspace.World, workspace.IsProjectContent);
        Check(moved != before && Math.Abs(after.Position.Z - info.Position.Z - Raise) < 0.001f,
            $"The move did not reach the source or the world: {info.Position} -> {after.Position}.");
        workspace.Undo(); Wait(workspace, "undo");
        Check(Hash(file) == before, "Undo did not restore the static's source file.");
        workspace.Redo(); Wait(workspace, "redo");
        Check(Hash(file) == moved, "Redo did not restore the move.");
        Console.WriteLine($"destination_content map=0x{map:X8} objects={world.Objects.Count} moved=+{Raise}m undo=pass redo=pass");
    }

    /** The compiled tag of the one content file of a class, or of one path. */
    private static uint ContentTag(StudioWorkspace workspace, string folder, uint classId)
    {
        string content = workspace.Project!.ContentDirectory;
        string path = Directory.EnumerateFiles(content, "*.tft", SearchOption.AllDirectories)
            .Single(path => TftFile.Read(File.ReadAllBytes(path)).ClassId == classId);
        return ContentTag(workspace, folder, path);
    }

    private static uint ContentTag(StudioWorkspace workspace, string folder, string path)
    {
        string name = Path.GetRelativePath(workspace.Project!.ContentDirectory, path).Replace('\\', '/');
        name = name[..^TftNames.FileExtension.Length];
        var allocation = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(folder,
            SunriseStudio.Projects.StudioProject.AllocationFileName)))!;
        string text = (string)FindValue(allocation, name)!;
        return Convert.ToUInt32(text, 16);
    }

    private static System.Text.Json.Nodes.JsonNode? FindValue(System.Text.Json.Nodes.JsonNode node, string key) => node switch
    {
        System.Text.Json.Nodes.JsonObject o => o.TryGetPropertyValue(key, out var value) ? value
            : o.Select(pair => pair.Value is null ? null : FindValue(pair.Value, key)).FirstOrDefault(x => x is not null),
        _ => null,
    };

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void Wait(StudioWorkspace workspace, string step, int seconds = 900)
    {
        var watch = Stopwatch.StartNew();
        while (workspace.IsBusy && watch.Elapsed < TimeSpan.FromSeconds(seconds)) { workspace.Poll(); Thread.Sleep(5); }
        workspace.Poll();
        if (workspace.IsBusy) throw new TimeoutException($"{step} timed out.");
        if (workspace.Error.Length != 0) throw new InvalidOperationException($"{step}: {workspace.Error}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
