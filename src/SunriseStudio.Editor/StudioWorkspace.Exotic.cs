using SunriseStudio.Authoring;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using System.Text.Json;

namespace SunriseStudio.Editor;

public sealed partial class StudioWorkspace
{
    public PreparedExoticWeapon? LastExoticWeapon { get; private set; }
    private uint? pendingOwnership;
    private string? pendingFiringSource;
    private (uint Weapon, string Path)? pendingFiring;

    /** The form applies a queued per-shot step once the workspace is idle. */
    internal (uint Weapon, string Path)? TakePendingFiringEffects()
    {
        var result = pendingFiring;
        pendingFiring = null;
        return result;
    }

    /** Queues the firing step; its worker skips an export with no Muzzle, Eject or Reload emitter. */
    internal void QueueFiringEffects(uint weaponTag, string? path)
    {
        if (path is not null && File.Exists(path)) pendingFiring = (weaponTag, path);
    }

    /** The Weapon page runs the ownership step once the new weapon is in the investment index. */
    internal bool TakePendingOwnership(uint weaponTag)
    {
        if (pendingOwnership != weaponTag) return false;
        pendingOwnership = null;
        return true;
    }

    /** The per-shot bursts of the weapon just created, once it owns its firing row. */
    internal void QueuePendingFiringEffects(uint weaponTag)
    {
        QueueFiringEffects(weaponTag, pendingFiringSource);
        pendingFiringSource = null;
    }

    private WeaponPageState? loadedWeapon;
    private PackageCatalog? weaponSource;

    /** The Weapon page's records; null until a read from the open catalog lands. */
    public WeaponPageState? LoadedWeapon => ReferenceEquals(weaponSource, Catalog) ? loadedWeapon : null;

    /** Reads the selected weapon's page records on a worker; a result for an older overlay is dropped. */
    public void LoadWeaponPage()
    {
        RequireIdle();
        var document = Document ?? throw new InvalidOperationException("Choose a weapon.");
        var catalog = Catalog ?? throw new InvalidOperationException("Open your Sunrise installation first.");
        var investment = Investment ?? throw new InvalidOperationException("The investment catalog is not loaded.");
        var project = Project;
        long overlay = catalog.OverlayRevision;
        string status = Status;
        BeginWork("Reading weapon", () =>
        {
            WeaponPageState state;
            // A failed read still publishes, so the page does not retry every frame.
            try { state = WeaponPageState.Read(project, catalog, investment, document, overlay, OperationToken); }
            catch (Exception error) when (error is not OperationCanceledException)
            { state = WeaponPageState.Failed(document.Entry.TagHash, overlay, error.Message); }
            return () =>
            {
                Status = status;
                if (ReferenceEquals(Catalog, catalog) && catalog.OverlayRevision == overlay)
                { loadedWeapon = state; weaponSource = catalog; }
            };
        }, blocking: false, keepNotice: true);
    }

    /**
     * Muzzle, Eject and Reload emitters of a Blender export become the weapon's effects.
     * One undo step.
     * @param onlyIfEmitters Does nothing for an export with no such emitter, instead of failing.
     */
    public void ApplyFiringEffects(uint weaponTag, string path, bool onlyIfEmitters = false)
    {
        RequireEditable();
        var project = Project!; var catalog = Catalog!;
        var (package, next) = WeaponEntryCursor(project, weaponTag);
        int revision = ProjectRevision; long overlay = catalog.OverlayRevision;
        string status = Status;
        BeginWork("Applying firing effects", () =>
        {
            // An export with no emitters still removes the effects this weapon already has.
            if (onlyIfEmitters && !WeaponFiringEffectAuthoring.HasEmitters(path) && !OwnsFiringEffects(project, catalog, weaponTag))
                return () => Status = status;
            var plan = WeaponFiringEffectAuthoring.Prepare(project, catalog, weaponTag, path, package, next);
            return () =>
            {
                RequireSameProjectState(project, catalog, revision, overlay);
                // Bursts the rebuild no longer uses go in the same undo step.
                ApplyAssetReplacement(plan.Assets, new ProjectAssetEdits(plan.Changes, null), plan.Removals, "Firing effects applied");
                Notice = new("Firing effects applied", false,
                    Details: plan.Summary.Count != 0 ? string.Join("\n", plan.Summary) : null);
            };
        }, blocking: false);
    }

    /** True when the weapon's own firing row holds a project muzzle or reload entity. */
    private static bool OwnsFiringEffects(StudioProject project, PackageCatalog catalog, uint weaponTag)
    {
        var owned = project.AddedAssets.Where(asset => asset.Native is not null).Select(asset => asset.Native!.Tag()).ToHashSet();
        try
        {
            var row = WeaponFiringRow.Find(project, catalog, catalog.Read, weaponTag);
            byte[] hud = catalog.Read(row.Hud);
            ref readonly var firing = ref TagView.At<SWeaponFiringRow>(hud, row.Row);
            return owned.Contains(firing.MuzzleSlotEntityRestartsEachShot) || owned.Contains(firing.ReloadSlotEntityNeverPaused);
        }
        // A weapon that still shares its firing row has no effects of its own.
        catch (InvalidDataException) { return false; }
    }

    /**
     * Copies the records only the base weapon uses, so this weapon stops sharing them. One undo step.
     * @param afterCreation Queues the new weapon's firing step once the copy lands.
     */
    public void OwnWeaponRecords(uint weaponTag, bool afterCreation = false)
    {
        RequireEditable();
        var investment = Investment ?? throw new InvalidOperationException("The investment catalog is not loaded.");
        var weapon = investment.FindItem(weaponTag) ?? throw new InvalidDataException("The weapon is not in the investment index.");
        var project = Project!; var catalog = Catalog!;
        var (package, next) = WeaponEntryCursor(project, weaponTag);
        int revision = ProjectRevision; long overlay = catalog.OverlayRevision;
        BeginWork("Copying base records", () =>
        {
            var plan = WeaponOwnershipAuthoring.Prepare(project, catalog, investment, weapon, package, next);
            return () =>
            {
                RequireSameProjectState(project, catalog, revision, overlay);
                if (plan.Assets.Count == 0 && plan.Changes.Count == 0) Status = $"{weapon.Name} already owns its records";
                else
                {
                    ApplyAssetTransaction(plan.Assets, _ => new ProjectAssetEdits(plan.Changes, null), $"Gave {weapon.Name} its own records");
                    Status = $"{weapon.Name} now owns {plan.Assets.Count} copied records";
                }
                if (afterCreation) QueuePendingFiringEffects(weaponTag);
            };
        }, blocking: false);
    }

    /** The weapon's added package and the first entry after this project's assets in it. */
    private static (ushort Package, int Next) WeaponEntryCursor(StudioProject project, uint weaponTag)
    {
        var native = project.AddedAssets.Select(asset => asset.Native).FirstOrDefault(value => value is not null
            && value.Tag() == weaponTag)
            ?? throw new InvalidDataException("The weapon must be created in this project.");
        int next = project.AddedAssets.Where(asset => asset.Native?.PackageId == native.PackageId)
            .Select(asset => asset.Native!.EntryIndex!.Value + 1).DefaultIfEmpty(0).Max();
        return (native.PackageId, next);
    }

    /** A worker's plan applies only to the project and overlay it was prepared from. */
    private void RequireSameProjectState(StudioProject project, PackageCatalog catalog, int revision, long overlay)
    {
        if (!ReferenceEquals(Project, project) || !ReferenceEquals(Catalog, catalog)
            || ProjectRevision != revision || catalog.OverlayRevision != overlay)
            throw new InvalidDataException("The project changed while this was prepared. Try again.");
    }

    /** Preparation runs off-thread; one checked transaction publishes the complete weapon. */
    public void CreateExoticWeapon(ExoticWeaponDefinition definition, string blenderPath)
    {
        RequireEditable();
        definition = JsonSerializer.Deserialize<ExoticWeaponDefinition>(JsonSerializer.Serialize(definition))
            ?? throw new InvalidDataException("The exotic weapon draft is missing.");
        var project = Project!; var catalog = Catalog!; var selected = Document;
        var manifest = File.ReadAllBytes(project.ManifestPath);
        int revision = ProjectRevision;
        catalog.InvalidateReadCache();
        long generation = catalog.ReadGeneration, overlay = catalog.OverlayRevision;
        string source = catalog.GetPackageFileIdentity();
        LastExoticWeapon = null;
        BeforeAssetMutation?.Invoke();
        BeginWork("Creating exotic weapon", () =>
        {
            var token = OperationToken;
            token.ThrowIfCancellationRequested();
            var plan = ExoticWeaponAuthoring.Prepare(project, catalog, definition, (staged, variants, entry) =>
            {
                token.ThrowIfCancellationRequested();
                var assets = WeaponOriginalAssetImporter.Prepare(staged, blenderPath,
                    definition.PackageId, entry, definition.Name, variants);
                return new(assets.Assets, assets.VariantEntities, assets.SurfaceReport, assets.SourceHashes,
                    assets.GameplayEntityTag, assets.AnimationEntityTag, assets.AnimationClips);
            });
            token.ThrowIfCancellationRequested();
            return () =>
            {
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(Project, project) || !ReferenceEquals(Catalog, catalog)
                    || ProjectRevision != revision || catalog.ReadGeneration != generation || catalog.OverlayRevision != overlay
                    || !File.ReadAllBytes(project.ManifestPath).AsSpan().SequenceEqual(manifest)
                    || catalog.GetPackageFileIdentity() != source)
                    throw new InvalidDataException("The project or installation changed. Create the weapon again.");
                if (plan.SourceHashes is null)
                    throw new InvalidDataException("The Blender import has no source receipt.");
                WeaponOriginalAssetImporter.ValidateSources(plan.SourceHashes);
                var previous = plan.Changes.Keys.ToDictionary(tag => tag, ReadRawBytes);
                var edits = plan.Changes.ToDictionary(pair => pair.Key,
                    pair => RebaseOnRaw(pair.Key, pair.Value, previous[pair.Key]));
                var split = SplitAssetEdits(edits);
                IReadOnlyList<AddedProjectAsset> created = [];
                MutateProject(() => created = project.ApplyTransaction(plan.Assets, _ => split, catalog));
                string id = created.Single(asset => asset.Native is { } native && native.Tag() == plan.WeaponTag).Id;
                PushUndo(new(selected?.Entry.TagHash ?? 0, previous, selected?.AddedAssetId, selected?.OpenContext,
                    created.ToDictionary(asset => asset.Id, _ => (AddedAssetSnapshot?)null),
                    ReverseSelection: new(0, id, null)));
                ProjectRevision++;
                LastExoticWeapon = plan;
                Status = "Exotic weapon created";
                pendingOwnership = plan.WeaponTag;
                pendingFiringSource = blenderPath;
                SelectAdded(id);
            };
        });
    }
}
