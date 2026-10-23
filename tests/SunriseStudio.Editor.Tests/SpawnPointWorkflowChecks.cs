using System.Diagnostics;
using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Editor;
using SunriseStudio.Scene;

internal static class SpawnPointWorkflowChecks
{
    /** Add, duplicate, drag, remove and undo all pass through the normal workspace and mission reload. */
    internal static void Run(string root)
    {
        uint bank = WorldFixture.Base + 17;
        string install = Path.Combine(root, "spawn-install");
        WorldFixture.Create(install);
        // Fixture packages are unencrypted; the editor still requires a syntactically valid key export.
        string keys = Path.Combine(root, "synthetic-spawn-keys.bin");
        byte[] keyExport = new byte[60];
        "SRPKKEYS"u8.CopyTo(keyExport);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(keyExport.AsSpan(8), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(keyExport.AsSpan(12), 86657);
        File.WriteAllBytes(keys, keyExport);
        using var workspace = new StudioWorkspace { CacheOptions = new() { Enabled = false } };
        workspace.OpenInstallation(install, keys); Wait(workspace);
        workspace.CreateProject(Path.Combine(root, "spawn-project"), "Arrival workflow"); Wait(workspace);
        workspace.OpenMission(WorldFixture.Base, WorldFixture.Base + 4); Wait(workspace);
        var catalog = workspace.Catalog!;
        var project = workspace.Project!;
        var original = catalog.ReadOriginal(bank);
        var addition = SpawnPointAuthoring.Create(catalog, project.ObjectEdits, bank, new(30, 40, 50), Quaternion.Identity, 500);
        workspace.ApplyObjectEdit(addition); Wait(workspace);
        SceneMarker[] Points() => workspace.World!.Scene.Markers.Where(marker => marker.SourceTag == bank).ToArray();
        Require(Points().Length == 2 && Points()[1].Position == new Vector3(30, 40, 50), "Added marker did not reach the mission.");
        var duplicate = SpawnPointAuthoring.Create(catalog, project.ObjectEdits, bank, Points()[1].Position, Points()[1].Rotation, Points()[1].NameHash);
        workspace.ApplyObjectEdit(duplicate); Wait(workspace);
        byte[] both = catalog.Read(bank);
        Require(Points().Length == 3, "Duplicate did not reach the mission.");
        var point = Points()[2];
        var frame = TransformHandles.Read(catalog, point, workspace.World, out _, project.ObjectEdits)!;
        workspace.ApplyObjectEdit(TransformHandles.CreateEdit(catalog, point, frame, frame.Pivot,
            new(new(2, 3, 4), Quaternion.Identity, Vector3.One), workspace.World, "Moved arrival", project.ObjectEdits));
        Wait(workspace);
        Require(Points()[2].Position == new Vector3(32, 43, 54) && project.ObjectEdits.Count == 2,
            "Gizmo edit did not update the existing addition.");
        workspace.Undo(); Wait(workspace);
        Require(catalog.Read(bank).SequenceEqual(both), "Gizmo undo changed the bank.");
        workspace.RevertObjectEdit(addition.Key); Wait(workspace);
        Require(Points().Length == 2 && SpawnPointAuthoring.Find(catalog, Points()[1], project.ObjectEdits)?.Key == duplicate.Key,
            "Remove did not preserve the duplicate's stable identity.");
        workspace.Undo(); Wait(workspace);
        Require(catalog.Read(bank).SequenceEqual(both) && Points().Length == 3, "Remove undo did not restore both rows.");
        workspace.Redo(); Wait(workspace);
        Require(Points().Length == 2, "Remove redo did not remove the row.");
        workspace.RevertObjectEdit(duplicate.Key); Wait(workspace);
        Require(Points().Length == 1 && catalog.Read(bank).SequenceEqual(original), "Removing all additions did not restore the mission.");
        Console.WriteLine("PASS spawn-workflow checks=8 mission=reload markers=same gizmo=edited add/duplicate/remove/undo/redo=exact");
    }

    private static void Wait(StudioWorkspace workspace)
    {
        var watch = Stopwatch.StartNew();
        while (workspace.IsBusy && watch.Elapsed < TimeSpan.FromSeconds(60)) { workspace.Poll(); Thread.Sleep(2); }
        Require(!workspace.IsBusy && workspace.Error.Length == 0, "Spawn workflow failed: " + workspace.Error);
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidDataException(message); }
}
