using ImGuiNET;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    /** Back and Forward live on the toolbar; a document with no view picks its entity owner here. */
    private void DrawAssetHeader(StudioDocument document)
    {
        ImGui.TextUnformatted(document.Name);
        if (document.Scene is null && document.Texture is null) DrawPreviewChoice(document, -1);
    }

    /**
     * The model the preview draws, above the preview: the entity owner, which related assets retain, an item's art
     * arrangement or one entity of it, or the guardian an item is shown on.
     */
    private void DrawPreviewChoice(StudioDocument document, float width)
    {
        var context = document.OpenContext;
        // A weapon shows in a guardian's hands too: its own view has the owner picker, the guardian view the wearer picker.
        bool weapon = context is { WeaponItemTag: > 0 } && context.WeaponItemTag == document.Entry.TagHash;
        float toggle = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X;
        void GuardianToggle(bool sameLine)
        {
            if (sameLine) ImGui.SameLine();
            bool shown = workspace.ShowGuardian;
            if (!ImGuiIcons.Toggle("show-guardian", StudioIcon.Wearer, "Show Guardian", ref shown)) return;
            // The model grows or shrinks, so the view frames it again. A weapon's own view reads EntityTag as its
            // animation owner, so leaving the guardian drops the wearer.
            workspace.ShowGuardian = shown;
            reframeView = true;
            workspace.Select(document.Entry.TagHash,
                context: weapon && !shown ? context! with { EntityTag = 0, WearerFemale = false } : context);
        }
        if (document.Wearers.Count != 0)
        {
            var current = document.Wearers.FirstOrDefault(value => value.Class.BodyTag == context?.EntityTag
                && value.Female == context.WearerFemale) ?? document.Wearers[0];
            // Armor names its model entity, like the weapon owners, with the guardian it is worn by; an item with no
            // model of its own, a shader, names the guardian alone and always draws it.
            bool piece = current.EntityTag != 0;
            string Label(ItemWearer wearer) => piece ? $"{workspace.DisplayName(wearer.EntityTag)} - {wearer.Name}" : wearer.Name;
            ImGui.SetNextItemWidth(piece || weapon ? MathF.Max(1, width - toggle) : width);
            if (ImGui.BeginCombo("##item-wearer", Label(current)))
            {
                foreach (var wearer in document.Wearers)
                    if (ImGui.Selectable(Label(wearer), wearer == current) && wearer != current)
                        workspace.Select(document.Entry.TagHash,
                            context: (context ?? new AssetOpenContext { ItemModelTag = document.Entry.TagHash })
                                with { EntityTag = wearer.Class.BodyTag, WearerFemale = wearer.Female });
                ImGui.EndCombo();
            }
            if (piece || weapon) GuardianToggle(true);
            return;
        }
        if (document.Connections is not { EntityCandidates.Count: > 0 } connections
            || connections.EntityCandidates is [var only] && only == document.Entry.TagHash)
        {
            if (weapon) GuardianToggle(false);
            return;
        }
        uint owner = connections.AnimationEntityTag != 0 ? connections.AnimationEntityTag
            : document.OpenContext?.EntityTag ?? 0;
        // An item model lists its whole art arrangement first, as entity 0: its row in the arrangement table.
        bool art = document.ArtArrangement >= 0;
        string Name(uint entity) => entity != 0 ? workspace.DisplayName(entity)
            : $"{workspace.DisplayName(GuardianAppearance.ArrangementTable)} / Row {document.ArtArrangement}";
        ImGui.SetNextItemWidth(weapon ? MathF.Max(1, width - toggle) : width);
        if (ImGui.BeginCombo("##animation-owner", owner != 0 || art ? Name(owner) : "Choose entity owner"))
        {
            foreach (uint candidate in art ? connections.EntityCandidates.Prepend(0u) : connections.EntityCandidates)
                if (ImGui.Selectable(Name(candidate) + "##" + candidate, candidate == owner))
                {
                    var nextContext = document.OpenContext is { } held && (held.WeaponItemTag > 0 || held.ItemModelTag > 0)
                        ? held with { EntityTag = candidate } : new AssetOpenContext(EntityTag: candidate);
                    workspace.Select(document.Entry.TagHash, context: nextContext);
                    ShowAssetPage("sunrise.preview");
                }
            ImGui.EndCombo();
        }
        if (weapon) GuardianToggle(true);
    }
}
