using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

public sealed partial class StudioWorkspace
{
    private ObjectEditCompilation compiledObjects = ObjectEditCompilation.Empty;

    /** The project's object edits as the overlay shows them; empty while they fail to compile. */
    public ObjectEditCompilation CompiledObjects => compiledObjects;

    /** Why the object edits do not compile against this installation; empty when they do. */
    public string ObjectEditError { get; private set; } = "";

    /** Every asset emitted by a project build shares the browser's changed state. */
    public bool IsModified(uint tag) =>
        nativeAdditions.ContainsKey(tag) || Project?.ContainsEdit(tag) == true || compiledObjects.Assets.ContainsKey(tag)
        || Project?.ConfigBindings.Any(binding => binding.TagHash == tag) == true;

    /** Records one object edit as one undo step. A null edit means nothing changed. */
    public void ApplyObjectEdit(ObjectEdit? edit, string action = "Saved object edit") =>
        ApplyObjectEdits(edit is null ? [] : [edit], action);

    /** Records these object edits as one undo step; they all save or none do. */
    public void ApplyObjectEdits(IReadOnlyList<ObjectEdit> edits, string action = "Saved object edit")
    {
        RequireEditable();
        if (edits.Count == 0) { Status = "No changes"; return; }
        var project = Project!;
        var previous = new Dictionary<string, ObjectEdit?>(StringComparer.Ordinal);
        foreach (var edit in edits) previous[edit.Key] = project.FindObjectEdit(edit.Key);
        bool changed = false;
        MutateProject(() => changed = project.RecordObjectEdits(edits, Catalog!), []);
        if (!changed) { Status = "No changes"; return; }
        FinishObjectStates(new Dictionary<uint, byte[]>(), previous, action);
    }

    /** Labels one scene object in this project; a blank name removes the label. It is not an undo step. */
    public void SetObjectName(string key, string? name)
    {
        RequireEditable();
        Project!.SetObjectName(key, name);
        ProjectRevision++;
        Status = string.IsNullOrWhiteSpace(name) ? "Object name removed" : "Object name saved";
        Changed?.Invoke();
    }

    /** Reverting needs no compile, so a stale edit can always be removed. */
    public void RevertObjectEdit(string key)
    {
        RequireEditable();
        var project = Project!;
        if (project.FindObjectEdit(key) is not { } previous) return;
        MutateProject(() => project.RevertObjectEdit(key), []);
        FinishObjectEdit(key, previous, "Reverted object edit");
    }

    /**
     * Replaces placement-only raw deltas with object edits that compile to the same bytes, as one undo step.
     * @return What converted and why each other delta stayed; also shown as the notice.
     */
    public DeltaConversion ConvertPlacementDeltas()
    {
        RequireEditable();
        var project = Project!;
        var catalog = Catalog!;
        var plan = DeltaConverter.Plan(project, catalog);
        Notice = new(plan.Summary(), false, Details: plan.Kept.Count > 0 ? plan.KeptDetails() : null);
        if (plan.Edits.Count == 0) { Status = "No changes"; Changed?.Invoke(); return plan; }
        var previousBytes = plan.Converted.ToDictionary(tag => tag, ReadRawBytes);
        var previousObjects = plan.Edits.ToDictionary(edit => edit.Key, edit => project.FindObjectEdit(edit.Key),
            StringComparer.Ordinal);
        var states = plan.Edits.ToDictionary(edit => edit.Key, edit => (ObjectEdit?)edit, StringComparer.Ordinal);
        // A payload equal to the original removes its raw delta.
        var removed = plan.Converted.ToDictionary(tag => tag, catalog.ReadOriginal);
        MutateProject(() => project.RestoreTransaction(new(removed), new Dictionary<string, AddedAssetSnapshot?>(),
            catalog, states), plan.Converted);
        FinishObjectStates(previousBytes, previousObjects, "Converted placement deltas");
        return plan;
    }

    private void FinishObjectEdit(string key, ObjectEdit? previous, string action) =>
        FinishObjectStates(new Dictionary<uint, byte[]>(),
            new Dictionary<string, ObjectEdit?>(StringComparer.Ordinal) { [key] = previous }, action);

    /** The undo frame restores these raw payloads, object edit states and added assets together. */
    private void FinishObjectStates(IReadOnlyDictionary<uint, byte[]> previousBytes,
        IReadOnlyDictionary<string, ObjectEdit?> previousObjects, string action,
        IReadOnlyDictionary<string, AddedAssetSnapshot?>? previousAdded = null)
    {
        var document = Document;
        PushUndo(new(document?.AddedAssetId is null ? document?.Entry.TagHash ?? 0 : 0, previousBytes,
            document?.AddedAssetId, document?.OpenContext, previousAdded is { Count: > 0 } ? previousAdded : null,
            ObjectStates: previousObjects));
        ProjectRevision++;
        if (World is not null) RefreshWorld();
        else if (document?.AddedAssetId is { } addedId) SelectAdded(addedId, document.OpenContext);
        else if (document is not null) Select(document.Entry.TagHash, true);
        Status = action;
        Changed?.Invoke();
    }

    /** A failed compile keeps the project open with raw deltas only; builds still refuse it. */
    private ObjectEditCompilation CompileObjects(PackageCatalog catalog)
    {
        ObjectEditError = "";
        if (Project is not { } project) return ObjectEditCompilation.Empty;
        try { return project.CompileObjectEdits(catalog); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException
            or InvalidOperationException or KeyNotFoundException)
        {
            ObjectEditError = error.Message;
            return ObjectEditCompilation.Empty;
        }
    }

    private static IEnumerable<uint> ChangedObjectTags(ObjectEditCompilation before, ObjectEditCompilation after)
    {
        if (ReferenceEquals(before, after)) return [];
        var modified = before.Assets.Keys.Union(after.Assets.Keys).Where(tag =>
            !before.Assets.TryGetValue(tag, out var old) || !after.Assets.TryGetValue(tag, out var next)
            || !old.AsSpan().SequenceEqual(next));
        var priorGenerated = before.GeneratedAssets.ToDictionary(asset => asset.TagHash);
        var nextGenerated = after.GeneratedAssets.ToDictionary(asset => asset.TagHash);
        var generated = priorGenerated.Keys.Union(nextGenerated.Keys).Where(tag =>
            !priorGenerated.TryGetValue(tag, out var old) || !nextGenerated.TryGetValue(tag, out var next)
            || !SameGeneratedMetadata(old, next) || !old.Data.AsSpan().SequenceEqual(next.Data));
        return modified.Concat(generated).Distinct().ToArray();
    }

    /** Stable membership permits the normal byte-range checks for in-place placement updates. */
    private static bool SameGeneratedMembership(ObjectEditCompilation before, ObjectEditCompilation after)
    {
        if (ReferenceEquals(before, after)) return true;
        if (before.GeneratedAssets.Count != after.GeneratedAssets.Count) return false;
        var old = before.GeneratedAssets.ToDictionary(asset => asset.TagHash);
        foreach (var next in after.GeneratedAssets)
            if (!old.TryGetValue(next.TagHash, out var prior) || !SameGeneratedMetadata(prior, next)) return false;
        return true;
    }

    private static bool SameGeneratedMetadata(GeneratedObjectAsset prior, GeneratedObjectAsset next) =>
        prior.Native == next.Native && prior.SourceTag == next.SourceTag
        && prior.InstanceOrigins.OrderBy(pair => pair.Key).SequenceEqual(next.InstanceOrigins.OrderBy(pair => pair.Key))
        && prior.PlacementOrigins.OrderBy(pair => pair.Key).SequenceEqual(next.PlacementOrigins.OrderBy(pair => pair.Key))
        && prior.Replaces.ToHashSet().SetEquals(next.Replaces)
        && prior.AddedInstances.OrderBy(pair => pair.Key).SequenceEqual(next.AddedInstances.OrderBy(pair => pair.Key));

    /** Bytes without object edits. Raw deltas and undo frames store these, so object values never enter a delta. */
    private byte[] ReadRawBytes(uint tag) =>
        compiledObjects.Assets.ContainsKey(tag) ? Project!.ReadAsset(tag, Catalog!) : Catalog!.Read(tag);

    /**
     * Editors change the compiled bytes. A byte they left as compiled keeps its raw value,
     * so an object edit stays out of the raw delta and still wins where both write.
     */
    private byte[] RebaseOnRaw(uint tag, byte[] edited, byte[] raw)
    {
        if (!compiledObjects.Assets.TryGetValue(tag, out var compiled) || compiled.Length != edited.Length
            || raw.Length != edited.Length) return edited;
        byte[] result = edited.ToArray();
        for (int i = 0; i < result.Length; i++)
            if (edited[i] == compiled[i]) result[i] = raw[i];
        return result;
    }
}
