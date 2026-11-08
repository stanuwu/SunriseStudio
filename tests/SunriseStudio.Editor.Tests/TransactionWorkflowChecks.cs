using System.Buffers.Binary;
using System.Diagnostics;
using SunriseStudio.Editor;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

internal static class TransactionWorkflowChecks
{
    /** Undo and redo restore created native identities as well as all edited owners. */
    public static void Run(string parent)
    {
        const uint tag = 0x80A00000;
        string install = Path.Combine(parent, "transaction-install");
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        byte[] original = new byte[64]; original[20] = 7;
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_transaction_0100_0.pkg"), 0x100,
            [new(0x80800001, 8 << 9, original, "Transaction owner")]);
        using var workspace = new StudioWorkspace { CacheOptions = new() { Enabled = false } };
        workspace.OpenInstallation(install); Wait(workspace);
        workspace.Select(tag); Wait(workspace);
        workspace.CreateProject(Path.Combine(parent, "transaction-project"), "Transaction workflow"); Wait(workspace);
        byte[] manifestBefore = File.ReadAllBytes(workspace.Project!.ManifestPath);
        var documentBefore = workspace.Document;
        long overlayBefore = workspace.Catalog!.OverlayRevision;
        int documentRevision = workspace.DocumentRevision, sceneRevision = workspace.SceneRevision;
        Reject(() => workspace.ApplyAssetTransaction([new("occupied-package", original,
            new(0x100, 0x80800001, 8 << 9, EntryIndex: 7000), tag)],
            _ => new(new Dictionary<uint, byte[]> { [tag] = [9] })));
        Check(File.ReadAllBytes(workspace.Project.ManifestPath).SequenceEqual(manifestBefore)
            && workspace.Catalog.OverlayRevision == overlayBefore && !workspace.CanUndo
            && ReferenceEquals(workspace.Document, documentBefore), "Rejected allocation changed the manifest, overlay or history.");
        Check(workspace.DocumentRevision == documentRevision && workspace.SceneRevision == sceneRevision,
            "A failed save that changed nothing reloaded the views.");
        var label = workspace.Project!.AddAsset("labels.json", [1]);
        byte[] linked = original.ToArray(); uint addedTag = 0;
        var created = workspace.ApplyAssetTransaction([new("private-root", original,
            new(0x301, 0x80800001, 8 << 9), tag), new("owned-labels", [4])], allocated =>
        {
            var metadata = allocated[0].Native!;
            addedTag = TagHashes.Make(metadata.PackageId, metadata.EntryIndex!.Value);
            BinaryPrimitives.WriteUInt32LittleEndian(linked, addedTag);
            return new(new Dictionary<uint, byte[]> { [tag] = linked }, new Dictionary<string, byte[]> { [label.Id] = [2, 3] });
        });
        Wait(workspace);
        Check(workspace.Catalog!.ContainsTag(addedTag) && workspace.Document!.Bytes.SequenceEqual(linked)
            && workspace.CanUndo, "Creation was not published to the editor overlay.");
        for (int i = 0; i < 3; i++)
        {
            workspace.Undo(); Wait(workspace);
            Check(!workspace.Catalog.ContainsTag(addedTag) && workspace.Project.AddedAssets.Count == 1
                && workspace.Document!.Bytes.SequenceEqual(original) && workspace.Project.ReadAddedAsset(label.Id).SequenceEqual(new byte[] { 1 }),
                "Undo retained a clone, label, reference or virtual entry.");
            workspace.Redo(); Wait(workspace);
            Check(workspace.Catalog.ContainsTag(addedTag) && workspace.Project.AddedAssets.Any(asset => asset == created[0])
                && workspace.Document!.Bytes.SequenceEqual(linked) && workspace.Project.ReadAddedAsset(label.Id).SequenceEqual(new byte[] { 2, 3 }),
                "Redo changed an allocated identity or lost the linked payload.");
        }
        var owner = new AssetOpenContext(PlacementTag: tag, PlacementOffset: 24);
        workspace.Select(addedTag, context: owner); Wait(workspace);
        Check(workspace.Document!.AddedAssetId == created[0].Id, "Tag navigation lost the added-asset identity.");
        byte[] childEdit = original.ToArray(); childEdit[21] = 9;
        workspace.ApplyBytes(childEdit); Wait(workspace);
        workspace.Undo(); Wait(workspace);
        Check(workspace.Catalog.Read(addedTag).SequenceEqual(original), "Undo of a later derived edit failed.");
        workspace.Undo(); Wait(workspace);
        Check(!workspace.Catalog.ContainsTag(addedTag) && workspace.Document!.Entry.TagHash == tag,
            "Creation undo could not follow a derived-asset edit.");
        workspace.Redo(); Wait(workspace); workspace.Redo(); Wait(workspace);
        Check(workspace.Document!.Entry.TagHash == addedTag && workspace.Document.Bytes.SequenceEqual(childEdit)
            && workspace.Document.OpenContext == owner,
            "Redo chain failed to recreate and then edit the same native asset.");
        string manifest = workspace.Project.ManifestPath;
        workspace.CloseProject(); Wait(workspace); workspace.OpenProject(manifest); Wait(workspace);
        Check(workspace.Catalog.Read(addedTag).SequenceEqual(childEdit) && workspace.Catalog.ReadOriginal(tag).SequenceEqual(original),
            "Reopening lost the derived data or changed the source.");
        Console.WriteLine("editor_transaction undo=membership redo=stable chained_edits=pass overlay=coherent reopen=pass");
        WorldContext(parent);
    }

    /** World refreshes retain the owner that selected an added native resource. */
    private static void WorldContext(string parent)
    {
        string install = Path.Combine(parent, "transaction-world"); WorldFixture.Create(install);
        using var workspace = new StudioWorkspace { CacheOptions = new() { Enabled = false } };
        workspace.OpenInstallation(install); Wait(workspace);
        workspace.OpenMission(WorldFixture.Base, WorldFixture.Base + 4); Wait(workspace);
        workspace.CreateProject(Path.Combine(parent, "transaction-world-project"), "World transactions"); Wait(workspace);
        uint source = WorldFixture.Base + 17;
        byte[] original = workspace.Catalog!.Read(source);
        workspace.Catalog.TryGetEntry(source, out var entry);
        var created = workspace.ApplyAssetTransaction([new("private-spawn", original,
            new(0x301, entry.Reference, entry.TypeInfo), source)], _ => new(new Dictionary<uint, byte[]>())); Wait(workspace);
        uint addedTag = TagHashes.Make(created[0].Native!.PackageId, created[0].Native!.EntryIndex!.Value);
        var owner = new AssetOpenContext(EntityTag: WorldFixture.Base + 5, PlacementTag: WorldFixture.Base + 6, PlacementOffset: 32);
        workspace.Select(addedTag, context: owner); Wait(workspace);
        byte[] changed = original.ToArray(); changed[^1] ^= 1;
        workspace.ApplyAssetEdits(new Dictionary<uint, byte[]> { [source] = changed }); Wait(workspace);
        Check(workspace.World is not null && workspace.Document?.AddedAssetId == created[0].Id
            && workspace.Document.OpenContext == owner, "World reload lost an added resource's owner context.");
        workspace.Undo(); Wait(workspace);
        Check(workspace.Document?.OpenContext == owner && workspace.Catalog.Read(source).SequenceEqual(original),
            "World undo lost the selected added resource context.");
        workspace.Redo(); Wait(workspace); workspace.Revert(source); Wait(workspace);
        Check(workspace.Document?.OpenContext == owner, "Revert lost the selected added resource context.");
        var extra = workspace.Project!.AddAsset("temporary-label", [1]);
        workspace.RemoveAdded(extra.Id); Wait(workspace);
        Check(workspace.Document?.OpenContext == owner, "Removing another addition lost the selected resource context.");
    }

    private static void Wait(StudioWorkspace workspace)
    {
        var elapsed = Stopwatch.StartNew();
        while (workspace.IsBusy && elapsed.Elapsed < TimeSpan.FromSeconds(30)) { workspace.Poll(); Thread.Sleep(1); }
        workspace.Poll();
        Check(!workspace.IsBusy && workspace.Error.Length == 0, "Transaction workflow failed: " + workspace.Error);
    }

    private static void Check(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid allocation was accepted.");
    }
}
