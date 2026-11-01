/**
 * File: StudioWorkspace partial: registers a standalone sandbox perk and edits a project perk's display text and icon (the
 * MCP create_perk tool and edit_perk_behavior display op; uses Creation/StandalonePerkAuthoring.cs).
 */
using SunriseStudio.Editor.Creation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

/** Workspace operations for standalone perks (part of StudioWorkspace). */
public sealed partial class StudioWorkspace
{
    /** The last perk create_perk registered, for its follow-up reply. */
    public PreparedStandalonePerk? LastStandalonePerk { get; private set; }

    /** Plans the perk off-thread, then commits one undoable transaction, as CreateCustomPerks does. */
    public void CreateStandalonePerk(string name, string description, PerkBehaviorGraph behavior, ushort packageId,
        ushort icon = ushort.MaxValue) =>
        CommitPerkPlan("Registering perk", (project, catalog) =>
            StandalonePerkAuthoring.Prepare(project, catalog, name, description, behavior, packageId, icon),
            plan => $"Perk registered: {plan.Name} (sandbox perk {plan.PerkIndex})");

    /** New display text or icon for a project perk (see StandalonePerkAuthoring.PrepareDisplay). */
    public void EditStandalonePerkDisplay(int perkIndex, string? name, string? description, ushort? icon, ushort packageId) =>
        CommitPerkPlan("Saving perk display", (project, catalog) =>
            StandalonePerkAuthoring.PrepareDisplay(project, catalog, perkIndex, name, description, icon, packageId),
            plan => $"Perk display saved: sandbox perk {plan.PerkIndex}");

    /** The last copies clone_tags made, for its follow-up reply. */
    public PreparedTagClone? LastTagClone { get; private set; }

    /** Copies installed tags into the project (see TagCloneAuthoring). */
    public void CloneTags(IReadOnlyList<uint> tags, string name, IReadOnlyDictionary<uint, uint> replace,
        IReadOnlyDictionary<uint, string> strings, ushort packageId)
    {
        LastTagClone = null;
        CommitPlan("Copying tags", (project, catalog) =>
            TagCloneAuthoring.Prepare(project, catalog, tags, name, replace, strings, packageId),
            plan => plan.Assets, plan => plan.Changes, plan =>
            {
                LastTagClone = plan;
                return $"Copied {plan.Tags.Count} tag(s) as {plan.Name}";
            });
    }

    private void CommitPerkPlan(string work, Func<StudioProject, PackageCatalog, PreparedStandalonePerk> prepare,
        Func<PreparedStandalonePerk, string> status) =>
        CommitPlan(work, prepare, plan => plan.Assets, plan => plan.Changes, plan =>
        {
            LastStandalonePerk = plan;
            return status(plan);
        });

    /**
     * Plans off-thread, then commits the plan's added assets and table edits as one undoable transaction, refusing when
     * the project or installation changed meanwhile.
     */
    private void CommitPlan<T>(string work, Func<StudioProject, PackageCatalog, T> prepare,
        Func<T, IReadOnlyList<ProjectAssetAddition>> assets, Func<T, IReadOnlyDictionary<uint, byte[]>> changes,
        Func<T, string> done)
    {
        RequireEditable();
        var project = Project!; var catalog = Catalog!; var selected = Document;
        var manifest = File.ReadAllBytes(project.ManifestPath);
        int revision = ProjectRevision;
        catalog.InvalidateReadCache();
        long generation = catalog.ReadGeneration, overlay = catalog.OverlayRevision;
        string source = catalog.GetPackageFileIdentity();
        if (typeof(T) == typeof(PreparedStandalonePerk)) LastStandalonePerk = null;
        BeforeAssetMutation?.Invoke();
        BeginWork(work, () =>
        {
            var token = OperationToken;
            token.ThrowIfCancellationRequested();
            var plan = prepare(project, catalog);
            token.ThrowIfCancellationRequested();
            return () =>
            {
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(Project, project) || !ReferenceEquals(Catalog, catalog)
                    || ProjectRevision != revision || catalog.ReadGeneration != generation || catalog.OverlayRevision != overlay
                    || !File.ReadAllBytes(project.ManifestPath).AsSpan().SequenceEqual(manifest)
                    || catalog.GetPackageFileIdentity() != source)
                    throw new InvalidDataException("The project or installation changed. Try again.");
                var planChanges = changes(plan);
                var previous = planChanges.Keys.ToDictionary(tag => tag, ReadRawBytes);
                var edits = planChanges.ToDictionary(pair => pair.Key, pair => RebaseOnRaw(pair.Key, pair.Value, previous[pair.Key]));
                var split = SplitAssetEdits(edits);
                IReadOnlyList<AddedProjectAsset> created = [];
                MutateProject(() => created = project.ApplyTransaction(assets(plan), _ => split, catalog));
                PushUndo(new(selected?.Entry.TagHash ?? 0, previous, selected?.AddedAssetId, selected?.OpenContext,
                    created.ToDictionary(asset => asset.Id, _ => (AddedAssetSnapshot?)null)));
                ProjectRevision++;
                Status = done(plan);
            };
        });
    }
}
