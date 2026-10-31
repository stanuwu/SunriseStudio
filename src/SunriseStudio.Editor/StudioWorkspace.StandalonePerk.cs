/**
 * File: StudioWorkspace partial: registers a standalone sandbox perk (the MCP create_perk tool; uses
 * Creation/StandalonePerkAuthoring.cs).
 */
using SunriseStudio.Editor.Creation;
using SunriseStudio.Formats;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

/** Workspace operations for standalone perks (part of StudioWorkspace). */
public sealed partial class StudioWorkspace
{
    /** The last perk create_perk registered, for its follow-up reply. */
    public PreparedStandalonePerk? LastStandalonePerk { get; private set; }

    /** Plans the perk off-thread, then commits one undoable transaction, as CreateCustomPerks does. */
    public void CreateStandalonePerk(string name, string description, PerkBehaviorGraph behavior, ushort packageId)
    {
        RequireEditable();
        var project = Project!; var catalog = Catalog!; var selected = Document;
        var manifest = File.ReadAllBytes(project.ManifestPath);
        int revision = ProjectRevision;
        catalog.InvalidateReadCache();
        long generation = catalog.ReadGeneration, overlay = catalog.OverlayRevision;
        string source = catalog.GetPackageFileIdentity();
        LastStandalonePerk = null;
        BeforeAssetMutation?.Invoke();
        BeginWork("Registering perk", () =>
        {
            var token = OperationToken;
            token.ThrowIfCancellationRequested();
            var plan = StandalonePerkAuthoring.Prepare(project, catalog, name, description, behavior, packageId);
            token.ThrowIfCancellationRequested();
            return () =>
            {
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(Project, project) || !ReferenceEquals(Catalog, catalog)
                    || ProjectRevision != revision || catalog.ReadGeneration != generation || catalog.OverlayRevision != overlay
                    || !File.ReadAllBytes(project.ManifestPath).AsSpan().SequenceEqual(manifest)
                    || catalog.GetPackageFileIdentity() != source)
                    throw new InvalidDataException("The project or installation changed. Create the perk again.");
                var previous = plan.Changes.Keys.ToDictionary(tag => tag, ReadRawBytes);
                var edits = plan.Changes.ToDictionary(pair => pair.Key, pair => RebaseOnRaw(pair.Key, pair.Value, previous[pair.Key]));
                var split = SplitAssetEdits(edits);
                IReadOnlyList<AddedProjectAsset> created = [];
                MutateProject(() => created = project.ApplyTransaction(plan.Assets, _ => split, catalog));
                PushUndo(new(selected?.Entry.TagHash ?? 0, previous, selected?.AddedAssetId, selected?.OpenContext,
                    created.ToDictionary(asset => asset.Id, _ => (AddedAssetSnapshot?)null)));
                ProjectRevision++;
                LastStandalonePerk = plan;
                Status = $"Perk registered: {plan.Name} (sandbox perk {plan.PerkIndex})";
            };
        });
    }
}
