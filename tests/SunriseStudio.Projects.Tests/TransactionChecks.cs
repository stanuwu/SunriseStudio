using System.Buffers.Binary;
using System.Text;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

internal static class TransactionChecks
{
    /** A private clone and its owning reference must enter and leave the manifest together. */
    public static void Run(string parent, PackageCatalog catalog, uint tag)
    {
        var project = StudioProject.Create(Path.Combine(parent, "transactions"), "Transactions");
        byte[] original = catalog.ReadOriginal(tag);
        var label = project.AddAsset("labels.json", Encoding.UTF8.GetBytes("{}"));
        var oldLabel = project.CaptureAddedAsset(label.Id);
        var metadata = new NativeAssetMetadata(0x301, 0x80800001, 0x100A, "Private root");
        byte[] linked = original.ToArray();
        var created = project.ApplyTransaction([new("private-root", original, metadata, tag),
            new("new-labels.json", [1, 2, 3])], allocated =>
        {
            Check(project.AddedAssets.Count == 1 && StudioProject.Open(project.DirectoryPath).Changes.Count == 0,
                "Staged additions became visible before their linked edits.");
            uint target = TagHashes.Make(allocated[0].Native!.PackageId, allocated[0].Native!.EntryIndex!.Value);
            BinaryPrimitives.WriteUInt32LittleEndian(linked, target);
            return new(new Dictionary<uint, byte[]> { [tag] = linked }, new Dictionary<string, byte[]> { [label.Id] = [9, 8] });
        }, catalog);
        Check(created.Count == 2 && project.AddedAssets.Count == 3 && project.ReadAsset(tag, catalog).SequenceEqual(linked),
            "Coupled creation lost an addition or reference edit.");
        Check(project.ReadAddedAsset(created[0].Id, catalog).SequenceEqual(original)
            && created[0].BaseTagHash == tag && created[0].Path.StartsWith("deltas/", StringComparison.Ordinal),
            "Derived creation stored an original as an owned payload.");
        var committed = created.Append(project.AddedAssets.Single(asset => asset.Id == label.Id))
            .ToDictionary(asset => asset.Id, asset => (AddedAssetSnapshot?)project.CaptureAddedAsset(asset.Id, catalog));
        var undo = created.ToDictionary(asset => asset.Id, _ => (AddedAssetSnapshot?)null);
        undo[label.Id] = oldLabel;
        project.RestoreTransaction(new(new Dictionary<uint, byte[]> { [tag] = original }), undo, catalog);
        Check(project.Changes.Count == 0 && project.AddedAssets.Count == 1 && project.ReadAddedAsset(label.Id).SequenceEqual(oldLabel.Data),
            "Undo did not restore membership, reference and existing metadata together.");
        project.RestoreTransaction(new(new Dictionary<uint, byte[]> { [tag] = linked }), committed, catalog);
        var reopened = StudioProject.Open(project.DirectoryPath);
        Check(reopened.AddedAssets.Single(asset => asset.Id == created[0].Id) == created[0]
            && reopened.ReadAsset(tag, catalog).SequenceEqual(linked), "Redo changed the native identity or failed reopening.");
        Check(catalog.ReadOriginal(tag).SequenceEqual(original), "Transaction changed the installed source.");

        byte[] manifest = File.ReadAllBytes(project.ManifestPath);
        string[] files = Files();
        Reject(() => project.ApplyTransaction([new("never-published", [5])], _ =>
            new(new Dictionary<uint, byte[]> { [uint.MaxValue] = [7] }), catalog));
        Reject(() => project.ApplyTransaction([new("never-published", [5])], _ => throw new InvalidOperationException("Rejected factory"), catalog));
        Reject(() => project.ApplyTransaction([new("occupied-package", original, metadata with { PackageId = 0x100, EntryIndex = 7000 }, tag)],
            _ => new(new Dictionary<uint, byte[]> { [tag] = [6] }), catalog));
        Reject(() => project.ApplyTransaction([new("invalid\0name", [5], metadata)],
            _ => new(new Dictionary<uint, byte[]> { [tag] = [6] }), catalog));
        Reject(() => project.ApplyTransaction([], _ => { project.AddAsset("nested", [1]); return new(new Dictionary<uint, byte[]>()); }, catalog));
        Check(File.ReadAllBytes(project.ManifestPath).SequenceEqual(manifest) && Files().SequenceEqual(files),
            "Rejected planning published data or an orphan payload.");
        var saved = committed[created[0].Id]!;
        Reject(() => project.RestoreTransaction(new(new Dictionary<uint, byte[]>()),
            new Dictionary<string, AddedAssetSnapshot?> { [saved.Asset.Id] = saved with { Data = [0] } }, catalog));
        Check(File.ReadAllBytes(project.ManifestPath).SequenceEqual(manifest), "Invalid restore changed the manifest.");
        if (OperatingSystem.IsWindows())
        {
            using (var held = new FileStream(project.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject(() => project.ApplyTransaction([new("write-rollback", [4, 3, 2])], _ =>
                    new(new Dictionary<uint, byte[]> { [tag] = [6] }, new Dictionary<string, byte[]> { [label.Id] = [7] }), catalog));
            Check(File.ReadAllBytes(project.ManifestPath).SequenceEqual(manifest) && Files().SequenceEqual(files),
                "Failed manifest publication left a partial project or staged payloads.");
        }
        var build = project.Build(catalog);
        Check(build.AddedAssets.Single(asset => asset.Id == created[0].Id).TagHash
            == BinaryPrimitives.ReadUInt32LittleEndian(linked), "Built clone tag differs from the published reference.");
        Console.WriteLine("project_transaction allocation=private publish=atomic restore=stable failures=rolled_back derived=delta build=linked");

        string[] Files() => Directory.GetFiles(project.DirectoryPath, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
    }

    private static void Check(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or KeyNotFoundException
            or IOException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Rejected transaction unexpectedly succeeded.");
    }
}
