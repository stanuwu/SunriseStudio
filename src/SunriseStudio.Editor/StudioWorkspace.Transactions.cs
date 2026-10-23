using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

public sealed partial class StudioWorkspace
{
    /** Creation and retargeting share one manifest publication and one reversible editor action. */
    public IReadOnlyList<AddedProjectAsset> ApplyAssetTransaction(IReadOnlyList<ProjectAssetAddition> additions,
        Func<IReadOnlyList<AddedProjectAsset>, ProjectAssetEdits> editFactory, string action = "Saved asset transaction")
    {
        RequireEditable();
        var project = Project!; var catalog = Catalog!;
        var document = Document ?? throw new InvalidOperationException("Choose an asset before editing its related resources.");
        var previous = new Dictionary<uint, byte[]>();
        var previousAdded = new Dictionary<string, AddedAssetSnapshot?>(StringComparer.Ordinal);
        IReadOnlyList<AddedProjectAsset> created = [];
        IReadOnlyList<AddedActivity>? previousActivities = null;
        MutateProject(() => created = project.ApplyTransaction(additions, allocated =>
        {
            var proposed = editFactory(allocated);
            if (proposed.AddedActivities is not null) previousActivities = project.AddedActivities.ToArray();
            var changed = new Dictionary<uint, byte[]>();
            foreach (var (tag, proposedBytes) in proposed.Assets)
            {
                byte[] prior = ReadRawBytes(tag);
                byte[] bytes = RebaseOnRaw(tag, proposedBytes, prior);
                if (prior.AsSpan().SequenceEqual(bytes)) continue;
                previous.Add(tag, prior); changed.Add(tag, bytes);
            }
            var split = SplitAssetEdits(changed);
            var addedEdits = split.AddedAssets!.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            foreach (var (id, bytes) in proposed.AddedAssets ?? new Dictionary<string, byte[]>())
            {
                var prior = project.CaptureAddedAsset(id, catalog);
                if (prior.Data.AsSpan().SequenceEqual(bytes)) continue;
                if (!addedEdits.TryAdd(id, bytes))
                    throw new InvalidDataException("An added asset was edited twice in one transaction.");
                previousAdded.Add(id, prior);
            }
            foreach (var asset in allocated) previousAdded.Add(asset.Id, null);
            return new(split.Assets, addedEdits) { AddedActivities = proposed.AddedActivities };
        }, catalog));
        if (previous.Count == 0 && previousAdded.Count == 0) { Status = "No changes"; return created; }
        ProjectRevision++;
        PushUndo(new(document.Entry.TagHash, previous, document.AddedAssetId, document.OpenContext, previousAdded)
        { ActivityStates = previousActivities });
        if (World is not null) RefreshWorld();
        else if (document.AddedAssetId is { } selectedId) SelectAdded(selectedId, document.OpenContext);
        else Select(document.Entry.TagHash, true, context: document.OpenContext);
        Status = action; Changed?.Invoke();
        return created;
    }

    /** Edits, new assets and removed assets share one manifest write and one undo step. */
    public IReadOnlyList<AddedProjectAsset> ApplyAssetReplacement(IReadOnlyList<ProjectAssetAddition> additions,
        ProjectAssetEdits edits, IReadOnlyCollection<string> removals, string action)
    {
        RequireEditable();
        var project = Project!; var catalog = Catalog!;
        var document = Document ?? throw new InvalidOperationException("Choose an asset before editing its related resources.");
        var previous = new Dictionary<uint, byte[]>();
        var previousAdded = new Dictionary<string, AddedAssetSnapshot?>(StringComparer.Ordinal);
        var changed = new Dictionary<uint, byte[]>();
        foreach (var (tag, proposed) in edits.Assets)
        {
            byte[] prior = ReadRawBytes(tag);
            byte[] bytes = RebaseOnRaw(tag, proposed, prior);
            if (prior.AsSpan().SequenceEqual(bytes)) continue;
            previous.Add(tag, prior); changed.Add(tag, bytes);
        }
        var split = SplitAssetEdits(changed);
        var addedEdits = split.AddedAssets!.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var (id, bytes) in edits.AddedAssets ?? new Dictionary<string, byte[]>())
        {
            var prior = project.CaptureAddedAsset(id, catalog);
            if (prior.Data.AsSpan().SequenceEqual(bytes)) continue;
            if (!addedEdits.TryAdd(id, bytes)) throw new InvalidDataException("An added asset was edited twice in one transaction.");
            previousAdded.Add(id, prior);
        }
        foreach (string id in removals) previousAdded.Add(id, project.CaptureAddedAsset(id, catalog));
        var states = removals.ToDictionary(id => id, _ => (AddedAssetSnapshot?)null, StringComparer.Ordinal);
        IReadOnlyList<AddedProjectAsset> created = [];
        MutateProject(() => created = project.RestoreTransaction(new(split.Assets, addedEdits), states, catalog, additions: additions));
        foreach (var asset in created) previousAdded.Add(asset.Id, null);
        if (previous.Count == 0 && previousAdded.Count == 0) { Status = "No changes"; return created; }
        ProjectRevision++;
        PushUndo(new(document.Entry.TagHash, previous, document.AddedAssetId, document.OpenContext, previousAdded));
        if (World is not null) RefreshWorld();
        else if (document.AddedAssetId is { } selectedId && project.AddedAssets.Any(asset => asset.Id == selectedId))
            SelectAdded(selectedId, document.OpenContext);
        else Select(document.Entry.TagHash, true, context: document.OpenContext);
        Status = action; Changed?.Invoke();
        return created;
    }
}
