/**
 * File: the Place panel (StudioPanelKind.Place, docs/UI-UE5.md §1.1) and the toolbar's Place dropdown: a tile view
 * over the existing creation catalog (Creation/CreationTypes) and the browser categories (StudioCategories). A
 * Create tile runs the same StartCreation the creation bar and the browser's New icons run; a Browse tile opens that
 * category in the Content Browser. There is no new creation logic here.
 */
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    private string placeSearch = "";

    /** The New types of the catalog, by key (Duplicate and other clone-only routes are not "place" actions). */
    private static IEnumerable<(string Key, CreationType Type)> PlaceTypes() =>
        CreationTypes.All.Where(item => item.Mode == CreationMode.New && item.Type.Name != CreationTypes.Duplicate)
            .Select(item => (item.Key, item.Type));

    private void DrawPlacePanel(Vector2 position, Vector2 size)
    {
        BeginPanel("Place", position, size, ImGuiWindowFlags.NoScrollbar);
        TrackAreaFocus();
        ImGui.SetNextItemWidth(-1);
        UiAuto.InputTextWithHint("##place-search", "Search the catalog", ref placeSearch, 128);
        ImGui.BeginChild("place-tiles", Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollWithMouse);
        UiMotion.SmoothScroll();
        string filter = placeSearch.Trim();
        bool Matches(string text) => filter.Length == 0 || text.Contains(filter, StringComparison.OrdinalIgnoreCase);

        SolTheme.SectionHeader("Create", workspace.Project is null ? "Create or open a project first." : "New assets in the open project");
        BeginTiles();
        if (Matches("Mission script")) PlaceTile("place-mission", StudioIcon.Edit, "Mission script", "A new Lua mission script in the Graph panel",
            workspace.Catalog is null ? "Open the game installation first." : workspace.IsBusy ? "Working" : null, NewMissionScript);
        if (Matches("Add asset")) PlaceTile("place-add", StudioIcon.Import, "Add asset", "Add a file as a source asset or a native package entry",
            workspace.CanEdit ? null : "Create or open a project first.", () => addDialog = true);
        foreach (var (key, type) in PlaceTypes())
        {
            if (!Matches(key) && !Matches(type.Name)) continue;
            var start = type;
            PlaceTile("place-new-" + key, CategoryIcon(key), key, type.Summary.Length > 0 ? type.Summary : "New " + type.Name,
                CreationRefusal(type), () => StartCreation(start));
        }
        EndTiles();
        foreach (var group in StudioCategories.All.GroupBy(item => item.Group))
        {
            var rows = group.Where(item => Matches(item.Label) || Matches(item.Name)).ToArray();
            if (rows.Length == 0) continue;
            SolTheme.SectionHeader("Browse " + group.Key, "Open a category in the Content Browser");
            BeginTiles();
            foreach (var item in rows)
            {
                string name = item.Name;
                PlaceTile("place-open-" + name, item.Icon, item.Label, item.Tooltip, workspace.Catalog is null ? "Open the game installation first." : null,
                    () => OpenPlaceCategory(name));
            }
            EndTiles();
        }
        ImGui.EndChild();
        ImGui.End();
    }

    /** The toolbar's Place dropdown: the same catalog as menu items. */
    private void DrawPlaceMenuItems()
    {
        ImGui.TextDisabled("Create");
        string? missionReason = workspace.Catalog is null ? "Open the game installation first." : null;
        if (UiAuto.MenuItem("Mission script...", ShortcutInput.Text(ShortcutActions.CreateMissionScript), false, missionReason is null)) NewMissionScript();
        if (UiAuto.MenuItem("Add asset...", "", false, workspace.CanEdit)) addDialog = true;
        foreach (var (key, type) in PlaceTypes())
        {
            var start = type;
            CreationMenuItem(key, type, () => StartCreation(start));
        }
        ImGui.Separator();
        foreach (var group in StudioCategories.All.GroupBy(item => item.Group))
        {
            if (!UiAuto.BeginMenu("Browse " + group.Key, workspace.Catalog is not null)) continue;
            SolTheme.PopupShadow();
            foreach (var item in group)
                if (UiAuto.MenuItem(item.Label)) OpenPlaceCategory(item.Name);
            UiAuto.EndMenu();
        }
        if (UiAuto.MenuItem("Show the Place panel")) ShowPlacePanel();
    }

    /** Opens a category's list in the Content Browser and shows the browser. */
    private void OpenPlaceCategory(string name)
    {
        focusAssetBrowser = true;
        OpenBrowserCategory(name);
        if (!ShowPopout(StudioPanelKind.Browser, true))
        {
            var node = layout.Show(StudioPanelKind.Browser);
            if (node.Collapsed == true) OpenFoldedArea(node);
            layoutDirty = true;
        }
    }

    private void ShowPlacePanel()
    {
        if (ShowPopout(StudioPanelKind.Place, true)) return;
        var node = layout.Show(StudioPanelKind.Place);
        if (node.Collapsed == true) node.Collapsed = false;
        maximizedPanel = null;
        layoutDirty = true;
    }

    /** The icon of a creation key: its browser category's icon when it has one. */
    private static StudioIcon CategoryIcon(string key) => StudioCategories.Find(key)?.Icon ?? key switch
    {
        CreationTypes.TextureKey => StudioIcon.Frame,
        CreationTypes.MaterialKey => StudioIcon.Shader,
        CreationTypes.StaticPropKey => StudioIcon.Cube,
        _ => StudioIcon.Plus
    };

    private float tileRowX;

    private void BeginTiles() => tileRowX = 0;

    private void EndTiles()
    {
        if (tileRowX > 0) ImGui.NewLine();
        ImGui.Dummy(new(0, SolTheme.Space2));
    }

    /**
     * One tile: icon over a label, wrapping into rows; disabled (with the reason as its tooltip) when refusal is set.
     * The hover fill eases (UiMotion).
     */
    private void PlaceTile(string id, StudioIcon icon, string label, string tooltip, string? refusal, Action run)
    {
        var tile = new Vector2(MathF.Round(92 * scale), MathF.Round(70 * scale));
        float spacing = SolTheme.Space2;
        if (tileRowX > 0)
        {
            float edge = ImGui.GetWindowPos().X + ImGui.GetWindowWidth() - ImGui.GetStyle().WindowPadding.X - ImGui.GetStyle().ScrollbarSize;
            if (tileRowX + spacing + tile.X <= edge) ImGui.SameLine(0, spacing);
            else tileRowX = 0;
        }
        ImGui.BeginDisabled(refusal is not null);
        bool clicked = UiAuto.InvisibleButton(id, label, tile);
        ImGui.EndDisabled();
        bool hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        tileRowX = max.X;
        var draw = ImGui.GetWindowDrawList();
        uint key = ImGui.GetItemID();
        var fill = UiMotion.EaseColor(key, refusal is not null ? SolTheme.Surface : ImGui.IsItemActive() ? SolTheme.Pressed : hovered ? SolTheme.Hover : SolTheme.Surface, 35f);
        SolTheme.ChamferFrame(draw, min, max, 0, SolTheme.U32(fill), SolTheme.U32(hovered && refusal is null ? SolTheme.Accent : SolTheme.BorderHi));
        float iconSize = MathF.Round(22 * scale);
        var tint = refusal is not null ? SolTheme.Dim : hovered ? SolTheme.TextStrong : SolTheme.Text;
        ImGuiIcons.Draw(draw, icon, new(min.X + (tile.X - iconSize) / 2, min.Y + 10 * scale), iconSize, SolTheme.U32(tint));
        bool font = SolTheme.PushFont(SolTheme.LabelFont);
        string shown = StudioUi.Fit(label, tile.X - SolTheme.Space3 * 2);
        float width = ImGui.CalcTextSize(shown).X;
        draw.AddText(new(min.X + (tile.X - width) / 2, max.Y - ImGui.GetTextLineHeight() - 9 * scale), SolTheme.U32(tint), shown);
        if (font) ImGui.PopFont();
        if (hovered) ImGui.SetTooltip(refusal is null ? $"{label}\n{tooltip}" : $"{label}\n{refusal}");
        if (clicked && refusal is null) run();
    }
}
