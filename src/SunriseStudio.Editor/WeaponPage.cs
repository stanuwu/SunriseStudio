using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using SunriseStudio.Animation;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** A named record the Weapon page opens. */
public sealed record WeaponPageLink(string Name, uint Tag);

/**
 * What the Weapon page shows for one weapon at one overlay revision.
 * Receipt marks a weapon built from a Blender export; Owned marks one added by this project.
 */
public sealed record WeaponPageState(uint Weapon, long Overlay)
{
    public WeaponArchetypeSource? Art { get; init; }
    public IReadOnlyList<WeaponPageLink> Behaviors { get; init; } = [];
    public InvestmentItem? Catalyst { get; init; }
    public uint AnimationDefinition { get; init; }
    public uint FireClip { get; init; }
    public uint ReloadClip { get; init; }
    public IReadOnlyList<WeaponPageLink> Animations { get; init; } = [];
    public IReadOnlyList<WeaponEffectPreviewLink> ClipBursts { get; init; } = [];
    public IReadOnlyList<WeaponSoundSlot> Sounds { get; init; } = [];
    public bool Owned { get; init; }
    public bool Receipt { get; init; }
    public bool CustomMuzzle { get; init; }
    public bool CustomReload { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];

    public static WeaponPageState Failed(uint weapon, long overlay, string error) => new(weapon, overlay) { Errors = [error] };

    /** Runs on a worker. A failed part is kept as an error; the parts that did read still show. */
    public static WeaponPageState Read(StudioProject? project, PackageCatalog catalog, InvestmentCatalog investment,
        StudioDocument document, long overlay, CancellationToken token)
    {
        uint weapon = document.Entry.TagHash;
        var errors = new List<string>();
        void Step(Action step)
        {
            token.ThrowIfCancellationRequested();
            try { step(); }
            catch (Exception failure) when (failure is not OperationCanceledException) { errors.Add(failure.Message); }
        }

        WeaponArchetypeSource? art = null;
        IReadOnlyList<WeaponPageLink> behaviors = [], animations = [];
        IReadOnlyList<WeaponEffectPreviewLink> bursts = [];
        IReadOnlyList<WeaponSoundSlot> sounds = [];
        InvestmentItem? catalyst = null;
        uint definition = 0, fire = 0, reload = 0;
        Step(() => art = WeaponArchetypeAuthoring.Read(catalog, document.Bytes));
        Step(() => (behaviors, catalyst) = ReadPerks(catalog, investment, document));
        if (art is { } source)
            Step(() =>
            {
                uint owner = WeaponPresentationReader.Read(catalog, source.EntityTag, WeaponArchetypeAuthoring.GearKey(catalog, source));
                var clips = new EntityAnimationCatalog(catalog).Read(owner, resolveNames: true);
                animations = clips.GroupBy(clip => clip.ClipTag).Select(group => new WeaponPageLink(
                    AssetSceneLoader.ClipName(catalog, group.Key, group.First().Index,
                        recoveredName: EntityAnimationCatalog.UniqueName(group)), group.Key)).ToArray();
                uint[] definitions = clips.Select(clip => clip.DefinitionTag).Distinct().ToArray();
                if (definitions.Length == 1)
                {
                    definition = definitions[0];
                    fire = clips.FirstOrDefault(clip => clip.Index == WeaponPresentationAuthoring.FireSlot)?.ClipTag ?? 0;
                    reload = clips.FirstOrDefault(clip => clip.Index == WeaponPresentationAuthoring.ReloadSlot)?.ClipTag ?? 0;
                }
                // A name used by several clips gets its list position.
                var counts = animations.GroupBy(clip => clip.Name).ToDictionary(group => group.Key, group => group.Count());
                bursts = WeaponEffectPreviewSource.Discover(catalog, animations.Select((clip, index) => (clip, index))
                    .ToDictionary(row => counts[row.clip.Name] == 1 ? row.clip.Name : $"{row.clip.Name} ({row.index + 1})",
                        row => row.clip.Tag));
            });

        var owned = project?.AddedAssets.Where(asset => asset.Native is not null)
            .Select(asset => asset.Native!.Tag()).ToHashSet() ?? [];
        bool receipt = false;
        if (project is not null && ReadReceipt(project, catalog, weapon, errors) is { } found)
        {
            receipt = true;
            if (found != 0) catalyst = investment.FindItem(found);
        }
        bool isOwned = owned.Contains(weapon);
        if (isOwned) Step(() => sounds = WeaponSoundAuthoring.Find(project!, catalog, weapon));
        bool muzzle = false, reloadEffect = false;
        if (receipt)
            try
            {
                var row = WeaponFiringRow.Find(project!, catalog, catalog.Read, weapon);
                byte[] hud = catalog.Read(row.Hud);
                ref readonly var firing = ref TagView.At<SWeaponFiringRow>(hud, row.Row);
                muzzle = owned.Contains(firing.MuzzleSlotEntityRestartsEachShot);
                reloadEffect = owned.Contains(firing.ReloadSlotEntityNeverPaused);
            }
            // A weapon that still shares its firing row has no effects of its own.
            catch (InvalidDataException) { }
            catch (Exception failure) when (failure is not OperationCanceledException) { errors.Add(failure.Message); }

        return new(weapon, overlay)
        {
            Art = art, Behaviors = behaviors, Catalyst = catalyst, AnimationDefinition = definition,
            FireClip = fire, ReloadClip = reload, Animations = animations, ClipBursts = bursts, Sounds = sounds,
            Owned = isOwned, Receipt = receipt, CustomMuzzle = muzzle, CustomReload = reloadEffect, Errors = errors
        };
    }

    /** Perk behaviors of the initial plugs; a plug named Catalyst that is not Empty is the catalyst. */
    private static (IReadOnlyList<WeaponPageLink> Links, InvestmentItem? Catalyst) ReadPerks(PackageCatalog catalog,
        InvestmentCatalog investment, StudioDocument document)
    {
        var links = new List<WeaponPageLink>();
        InvestmentItem? catalyst = null;
        foreach (var socket in investment.ReadItem(document.Entry.TagHash, document.Bytes).Sockets)
        {
            if (socket.InitialPlug >= investment.Items.Count) continue;
            var plug = investment.Items[socket.InitialPlug];
            if (plug.Name.Contains("Catalyst", StringComparison.OrdinalIgnoreCase)
                && !plug.Name.Contains("Empty", StringComparison.OrdinalIgnoreCase)) catalyst = plug;
            foreach (var perk in investment.ReadItem(plug.Tag, catalog.Read(plug.Tag)).Perks)
            {
                try { links.Add(new(investment.PerkName(perk.Index), InvestmentSandboxPerkAuthoring.Read(catalog, perk.Index).GameplayTag)); }
                catch (InvalidDataException) { }
            }
        }
        return (links.DistinctBy(link => link.Tag).ToArray(), catalyst);
    }

    /**
     * Finds the creation receipt that marks a Blender-built weapon.
     * @return Null when there is none; else the catalyst tag it names, or 0.
     */
    private static uint? ReadReceipt(StudioProject project, PackageCatalog catalog, uint weapon, List<string> errors)
    {
        foreach (var asset in project.AddedAssets.Where(asset => asset.Native is null
            && asset.Size <= 1024 * 1024 && asset.Name.EndsWith(".creation.json", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var json = JsonDocument.Parse(project.ReadAddedAsset(asset.Id, catalog));
                var root = json.RootElement;
                if (root.GetProperty("format").GetString() != "sunrise-exotic-creation"
                    || root.GetProperty("weaponTag").GetUInt32() != weapon) continue;
                return root.TryGetProperty("catalystTag", out var value) && value.TryGetUInt32(out uint tag) ? tag : 0;
            }
            catch (Exception failure) when (failure is JsonException or KeyNotFoundException or InvalidOperationException
                or FormatException or OverflowException or InvalidDataException or NotSupportedException)
            { errors.Add("Creation receipt: " + failure.Message); }
        }
        return null;
    }
}

/** Weapon navigation follows the current item, art tables and socketed perk definitions. */
public sealed class WeaponPage : IStudioPage
{
    private PackageCatalog? requestedCatalog;
    private long requestedOverlay = -1;
    private WeaponTracerState? lastTracer;
    private Vector4 tracerStart, tracerEnd;
    public string Id => "sunrise.weapon";
    public string Title => "Weapon";
    public bool CanOpen(StudioDocument document) => document.Entry.Reference == InvestmentCatalog.ItemClass
        && document.Entry.Type is 8 or 16 && document.Bytes.Length > ItemFields.Bucket && document.Bytes[ItemFields.Bucket] <= 2;

    /** Draw reads no package records; a worker reads them once per weapon and overlay revision. */
    public void Draw(StudioPageContext context)
    {
        var workspace = context.Workspace;
        if (workspace.Document is not { } document || workspace.Catalog is not { } packages) return;
        if (workspace.Investment is not { } investment)
        {
            // One request per catalog and overlay; a failed read waits for Retry.
            if (!workspace.IsBusy && (!ReferenceEquals(requestedCatalog, packages) || requestedOverlay != packages.OverlayRevision))
            {
                requestedCatalog = packages; requestedOverlay = packages.OverlayRevision;
                workspace.Run(workspace.LoadInvestment);
            }
            if (workspace.IsBusy) ImGui.TextDisabled("Reading weapon...");
            else
            {
                if (workspace.Error.Length != 0) ImGui.TextWrapped(workspace.Error);
                if (ImGui.SmallButton("Retry")) requestedCatalog = null;
            }
            return;
        }
        uint weapon = document.Entry.TagHash;
        if (!workspace.IsBusy && workspace.CanEdit && workspace.TakePendingOwnership(weapon))
            workspace.Run(() => workspace.OwnWeaponRecords(weapon, afterCreation: true));
        if (workspace.LoadedWeapon is not { } state || state.Weapon != weapon || state.Overlay != packages.OverlayRevision)
        {
            if (!workspace.IsBusy) workspace.Run(workspace.LoadWeaponPage);
            ImGui.TextDisabled("Reading weapon...");
            return;
        }
        bool idle = !workspace.IsBusy;
        // Sections follow the project; busy work only disables their buttons, so the layout holds still.
        bool editable = workspace.Project is not null;
        var item = investment.FindItem(weapon);
        ImGui.TextUnformatted(item?.Name ?? document.Name);
        if (item?.Description is { Length: > 0 } description) StudioUi.Tip(description);
        ImGui.SameLine();
        ImGui.TextDisabled(item?.TypeName ?? "Weapon");
        if (state.Errors.Count != 0)
        {
            ImGui.TextColored(StudioTheme.Accent, "Some parts could not be read.");
            StudioUi.Tip(string.Join("\n", state.Errors));
        }

        if (state.Art is { } art)
        {
            StudioUi.Heading("Model");
            // The weapon view already shows this model, or its failure note, beside the tabs.
            bool modelShown = document.OpenContext?.WeaponItemTag == weapon;
            if (!modelShown && StudioUi.IconButton("weapon-scene", StudioIcon.Cube, "Open the weapon scene", idle))
                workspace.Run(() => workspace.OpenWeaponModel(weapon));
            if (editable && state.Receipt)
            {
                if (!modelShown) ImGui.SameLine();
                if (StudioUi.IconButton("weapon-reimport", StudioIcon.Import, "Reimport the Blender export", idle))
                    workspace.Run(() => workspace.RequestWeaponAssetReimport(weapon));
                ImGui.SameLine();
                if (StudioUi.IconButton("weapon-own", StudioIcon.Build, "Copy shared base records into this project", idle))
                    workspace.Run(() => workspace.OwnWeaponRecords(weapon));
                ImGui.SameLine();
                if (StudioUi.IconButton("weapon-firing", StudioIcon.Spark, "Apply Muzzle, Eject and Reload emitters", idle))
                    PickFiringSource(workspace, weapon);
            }
            if (ImGui.CollapsingHeader("Model parts")) DrawParts(workspace, art);
        }

        if (state.AnimationDefinition != 0 && state.Animations.Count != 0)
        {
            StudioUi.Heading("Animation");
            ImGui.BeginDisabled(!workspace.CanEdit || !idle);
            DrawBinding(workspace, state, "Fire", WeaponPresentationAuthoring.FireSlot, state.FireClip);
            DrawBinding(workspace, state, "Reload", WeaponPresentationAuthoring.ReloadSlot, state.ReloadClip);
            ImGui.EndDisabled();
            if (StudioUi.IconButton("weapon-arms", StudioIcon.Export,
                "Export first-person arms for Blender. A reimport applies them.\n" + StudioUi.MayNotWork, idle && state.FireClip != 0 && state.ReloadClip != 0))
                workspace.RequestWeaponArmsExport(state.FireClip, state.ReloadClip);
        }

        bool authored = editable && state.Receipt;
        if (authored || state.ClipBursts.Count != 0)
        {
            StudioUi.Heading("Effects");
            if (authored)
            {
                ImGui.TextUnformatted("Muzzle and Eject");
                ImGui.SameLine();
                ImGui.TextDisabled(state.CustomMuzzle ? "From Blender" : "Base weapon");
                if (state.CustomReload)
                {
                    ImGui.TextUnformatted("Reload");
                    StudioUi.Tip(StudioUi.MayNotWork);
                    ImGui.SameLine();
                    ImGui.TextDisabled("From Blender");
                }
                DrawTracer(workspace, state);
            }
            if (state.ClipBursts.Count != 0) DrawClipBursts(workspace, state);
        }

        if (editable && state.Owned && ImGui.CollapsingHeader("Sounds")) DrawSounds(workspace, state);

        StudioUi.Heading("Perks");
        if (state.Behaviors.Count == 0) ImGui.TextDisabled("No perk behavior linked.");
        ImGui.BeginDisabled(!idle);
        foreach (var behavior in state.Behaviors)
        {
            if (ImGui.Button(behavior.Name + "##" + behavior.Tag)) workspace.Run(() => workspace.Select(behavior.Tag));
            StudioUi.Tip("Open the perk's behavior graph");
        }
        ImGui.EndDisabled();
        if (state.Catalyst is { } catalyst)
        {
            StudioUi.Heading("Catalyst");
            if (StudioUi.IconButton("weapon-catalyst", StudioIcon.Open, "Open catalyst", idle))
                workspace.Run(() => workspace.Select(catalyst.Tag));
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(catalyst.Name);
        }
    }

    private static void PickFiringSource(StudioWorkspace workspace, uint weapon)
    {
        using var dialog = new OpenFileDialog { Filter = "Blender weapon export|*.glb", CheckFileExists = true };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        string path = dialog.FileName;
        workspace.Run(() => workspace.ApplyFiringEffects(weapon, path));
    }

    /** Part buttons wrap to the next line when the row is full. */
    private static void DrawParts(StudioWorkspace workspace, WeaponArchetypeSource art)
    {
        var style = ImGui.GetStyle();
        float right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        ImGui.BeginDisabled(workspace.IsBusy);
        for (int index = 0; index < art.Variants.Count; index++)
        {
            uint entity = art.Variants[index].EntityTag;
            string label = $"Model part {index + 1}";
            float width = ImGui.CalcTextSize(label).X + style.FramePadding.X * 2;
            if (index > 0 && ImGui.GetItemRectMax().X + style.ItemSpacing.X + width <= right) ImGui.SameLine();
            if (ImGui.Button(label)) workspace.Run(() => workspace.Select(entity));
            StudioUi.Tip($"Entity {entity:X8}");
        }
        ImGui.EndDisabled();
    }

    /** Tracer colour edits the trail of a copy of the base projectile; other bases show the refusal. */
    private void DrawTracer(StudioWorkspace workspace, WeaponPageState state)
    {
        uint weapon = state.Weapon;
        var tracer = workspace.LoadedTracer;
        if (tracer is null || tracer.Weapon != weapon || tracer.Overlay != state.Overlay)
        {
            if (!workspace.IsBusy) workspace.Run(() => workspace.LoadTracer(weapon));
            ImGui.TextDisabled("Reading tracer...");
            return;
        }
        // Swatches take the read colours once per read, then keep the user's edits.
        if (!ReferenceEquals(tracer, lastTracer))
        {
            lastTracer = tracer;
            tracerStart = tracer.Start; tracerEnd = tracer.End;
        }
        // Apply stays off: in game the copy kept the base colour and broke tracers while moving.
        const string Broken = "Off: in game the tracers kept their colour and broke while moving.";
        ImGui.TextUnformatted("Tracer colour");
        StudioUi.Wip(Broken);
        bool usable = tracer.Refusal is null;
        const ImGuiColorEditFlags flags = ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoTooltip;
        ImGui.BeginDisabled();
        ImGui.ColorEdit4("Start##tracer", ref tracerStart, flags);
        StudioUi.Tip(tracer.Refusal ?? Broken);
        ImGui.SameLine();
        ImGui.ColorEdit4("End##tracer", ref tracerEnd, flags);
        StudioUi.Tip(tracer.Refusal ?? Broken);
        ImGui.EndDisabled();
        ImGui.SameLine();
        StudioUi.IconButton("tracer-apply", StudioIcon.Check, tracer.Refusal ?? Broken, false);
        ImGui.SameLine();
        if (StudioUi.IconButton("tracer-reset", StudioIcon.Reset, tracer.Refusal ?? "Use the base weapon's tracer",
            usable && tracer.Custom && !workspace.IsBusy))
            workspace.Run(() => workspace.ResetTracerColour(weapon));
    }

    /** Clip-event bursts; the game emits nothing for them on a weapon point. */
    private static void DrawClipBursts(StudioWorkspace workspace, WeaponPageState state)
    {
        ImGui.TextUnformatted("Clip bursts");
        StudioUi.Wip(StudioUi.NotShown);
        foreach (var effect in state.ClipBursts)
        {
            if (StudioUi.IconButton($"burst-{effect.ClipTag:X8}-{effect.Frame}-{effect.EffectEntityTag:X8}", StudioIcon.Play,
                "Preview with the weapon", !workspace.IsBusy))
                workspace.Run(() => workspace.OpenWeaponEffect(state.Weapon, effect));
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled($"{effect.Name}, {effect.ClipName} frame {effect.Frame}");
        }
    }

    /** Sound events reached through the weapon's records; a 16-bit WAV replaces one. */
    private static void DrawSounds(StudioWorkspace workspace, WeaponPageState state)
    {
        if (state.Sounds.Count == 0) ImGui.TextDisabled("No sound events found.");
        foreach (var slot in state.Sounds.DistinctBy(slot => slot.EventTag))
        {
            if (StudioUi.IconButton($"sound-{slot.EventTag:X8}", StudioIcon.Import,
                "Replace with a 16-bit WAV. A NAME.release.wav beside it plays on release.", !workspace.IsBusy))
            {
                using var dialog = new OpenFileDialog { Filter = "Wave audio|*.wav", CheckFileExists = true };
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    string path = dialog.FileName;
                    workspace.Run(() => workspace.ReplaceWeaponSounds(state.Weapon, new Dictionary<uint, string> { [slot.EventTag] = path }));
                }
            }
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(slot.Label);
            StudioUi.Tip(slot.Detail);
            if (!slot.Owned) continue;
            ImGui.SameLine();
            ImGui.TextDisabled("custom");
        }
    }

    /** Imported clips share one rig and may replace either gameplay binding. */
    private static void DrawBinding(StudioWorkspace workspace, WeaponPageState state, string label, int slot, uint current)
    {
        string selected = state.Animations.FirstOrDefault(clip => clip.Tag == current)?.Name ?? "Existing clip";
        ImGui.SetNextItemWidth(230);
        if (!ImGui.BeginCombo(label, selected)) { StudioUi.Tip("Clip this action plays."); return; }
        foreach (var clip in state.Animations)
            if (ImGui.Selectable(clip.Name, clip.Tag == current))
                workspace.Run(() => workspace.BindWeaponAnimation(state.AnimationDefinition, slot, clip.Tag));
        ImGui.EndCombo();
    }
}
