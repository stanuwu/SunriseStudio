/**
 * File: the 30 px menu bar, row 1 of the UE5 chrome (docs/UI-UE5.md §1.3): the 16 px brand mark, the menus File to
 * Help, the project name centred in muted text and the window buttons. Its empty part is the window caption
 * (WindowChrome hit test). Menus that do not fit fold into a "»" menu; the toolbar row (StudioForm.Toolbar.cs) does
 * the same for its groups. The TitleGroup names stay as they were so the overflow, MCP UI automation names and the
 * layout checks still resolve.
 */
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    /**
     * Title bar groups. The menus are row 1; the others are the main toolbar's groups (row 2), left to right, with
     * Tools, Place and SolforgeAi appended so the old values keep their numbers. Groups that do not fit their row move
     * into that row's "»" menu from the right end, so nothing ever overlaps.
     */
    internal enum TitleGroup
    {
        File, Edit, Assets, Build, View, Window, Help,
        Navigation, Project, Render, Shaders, Quality, Detail, Lighting, Settings,
        Tools, Place, SolforgeAi
    }

    private static readonly TitleGroup[] MenuGroups =
        [TitleGroup.File, TitleGroup.Edit, TitleGroup.Assets, TitleGroup.Build, TitleGroup.View, TitleGroup.Window, TitleGroup.Help];
    /** Row 2 order; the Solforge AI pill is drawn apart, right-aligned, and never folds. */
    private static readonly TitleGroup[] ToolGroups =
    [
        TitleGroup.Navigation, TitleGroup.Tools, TitleGroup.Place, TitleGroup.Project, TitleGroup.Render, TitleGroup.Shaders,
        TitleGroup.Quality, TitleGroup.Detail, TitleGroup.Lighting, TitleGroup.Settings
    ];

    // Width each group took when last drawn on its row (0 until then), used to decide what fits; indexed by group.
    private readonly float[] titleWidths = new float[Enum.GetValues<TitleGroup>().Length];

    private readonly HashSet<string> titleReachable = [];
    private readonly List<string> titleOverflow = [];

    /** Groups (and "WindowButtons") the last frame offered, on a row or in a "»" menu. */
    internal IReadOnlyCollection<string> TitleBarReachable => titleReachable;

    /** Groups the last frame moved into a "»" menu. */
    internal IReadOnlyList<string> TitleBarOverflow => titleOverflow;

    /** One title bar group (or "Overflow") as last drawn, relative to its row's window: its x span, and its last item's y span. */
    internal readonly record struct TitleRect(string Name, bool Menu, Vector2 Min, Vector2 Max);
    private readonly List<TitleRect> titleRects = [];

    /** Where each group and the "»" buttons were last drawn. */
    internal IReadOnlyList<TitleRect> TitleBarRects => titleRects;

    /** The menu bar's height (row 1, which holds the window buttons) and its controls' frame height, as last drawn. */
    internal float TitleBarHeight { get; private set; }
    internal float TitleBarFrameHeight { get; private set; }

    /** Minimize, maximize and close as last drawn, window-relative. */
    internal IReadOnlyList<(Vector2 Min, Vector2 Max)> TitleBarButtonRects => chrome.ButtonRects;

    /**
     * Row 1: brand, the menus, the centred project name and the window buttons, in one window's menu bar (no overlay
     * window). Wherever no control sits it is the window's caption; the project name is drawn, not an item, so it drags too.
     */
    private void DrawTitleBar(float height)
    {
        var style = ImGui.GetStyle();
        float normalPad = style.FramePadding.Y;
        // The menu bar is as tall as the frame padding at Begin and BeginMenuBar makes it; controls use the normal padding.
        float barPad = Math.Max(normalPad, (height - ImGui.GetFontSize()) / 2);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(style.FramePadding.X + SolTheme.Space1, barPad));
        ImGui.PushStyleColor(ImGuiCol.MenuBarBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, SolTheme.Bar);
        BeginPanel("Toolbar", Vector2.Zero, new(ClientSize.Width, height),
            ImGuiWindowFlags.MenuBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        // A frame whose bar fails to open offers nothing, so the layout checks see it.
        titleReachable.Clear();
        titleOverflow.Clear();
        titleRects.Clear();
        bool bar = ImGui.BeginMenuBar();
        ImGui.PopStyleVar();
        if (!bar)
        {
            ImGui.End(); ImGui.PopStyleColor(2); ImGui.PopStyleVar();
            return;
        }
        float row = (height - ImGui.GetFrameHeight()) / 2;
        float textTop = row + normalPad - barPad;
        TitleBarHeight = height;
        TitleBarFrameHeight = ImGui.GetFrameHeight();
        var barMin = ImGui.GetWindowPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddLine(barMin + new Vector2(0, height - .5f), barMin + new Vector2(ClientSize.Width, height - .5f), SolTheme.U32(SolTheme.Border), 1);
        float x = DrawTitleBrand(height);
        float right = ClientSize.Width - chrome.ButtonsWidth - 8 * scale;

        // The menus that fit, from last frame's widths; the rest go to the "»" menu, the right-most first.
        float overflowWidth = ImGui.CalcTextSize("»").X + ImGui.GetStyle().FramePadding.X * 2 + style.ItemSpacing.X;
        int fit = FitGroups(MenuGroups, x, right, overflowWidth);
        for (int i = 0; i < fit; i++)
        {
            var group = MenuGroups[i];
            float start = x;
            ImGui.SetCursorPos(new(x, textTop));
            DrawTitleGroup(group, row, textTop);
            // On a menu bar row each item's ItemSize steps the cursor past it (and the item spacing).
            x = ImGui.GetCursorPosX();
            titleWidths[(int)group] = x - start;
            titleReachable.Add(group.ToString());
            RecordTitleRect(group.ToString(), true, start, barMin);
        }
        if (fit < MenuGroups.Length) x = DrawOverflowButton("title-overflow", MenuGroups, fit, x, row, barMin, height);

        // The project name, centred over the whole window when it clears the menus and the window buttons.
        string project = workspace.Project?.Name ?? "View only";
        float nameWidth = ImGui.CalcTextSize(project).X;
        float centred = (ClientSize.Width - nameWidth) / 2;
        float nameX = centred > x + 16 * scale && centred + nameWidth < right ? centred
            : x + 16 * scale + nameWidth < right ? x + 16 * scale : -1;
        if (nameX >= 0)
            draw.AddText(barMin + new Vector2(nameX, (height - ImGui.GetTextLineHeight()) / 2), SolTheme.U32(SolTheme.Muted), project);
        titleReachable.Add(TitleGroup.Project.ToString());
        chrome.DrawButtons(height);
        titleReachable.Add("WindowButtons");
        ImGui.EndMenuBar();
        ImGui.End(); ImGui.PopStyleColor(2); ImGui.PopStyleVar();
    }

    /** How many groups of a row fit before right, from last frame's widths (the "»" button takes overflowWidth). */
    private int FitGroups(TitleGroup[] groups, float x, float right, float overflowWidth)
    {
        if (x + groups.Sum(TitleWidth) <= right) return groups.Length;
        float used = x + overflowWidth;
        int fit = 0;
        while (fit < groups.Length && used + TitleWidth(groups[fit]) <= right) used += TitleWidth(groups[fit++]);
        return fit;
    }

    /** The "»" button of a row and its popup listing the folded groups; returns the x after it. */
    private float DrawOverflowButton(string id, TitleGroup[] groups, int fit, float x, float row, Vector2 barMin, float height)
    {
        ImGui.SetCursorPos(new(x, row));
        if (UiAuto.Button("»##" + id)) ImGui.OpenPopup(id);
        Vector2 overflowMin = ImGui.GetItemRectMin();
        RecordTitleRect("Overflow", false, x, barMin);
        x = ImGui.GetItemRectMax().X - barMin.X + ImGui.GetStyle().ItemSpacing.X;
        ImGui.SetNextWindowPos(new(overflowMin.X, barMin.Y + height));
        if (ImGui.BeginPopup(id))
        {
            SolTheme.PopupShadow();
            for (int i = fit; i < groups.Length; i++) DrawTitleOverflowItem(groups[i]);
            ImGui.EndPopup();
        }
        for (int i = fit; i < groups.Length; i++)
        {
            titleReachable.Add(groups[i].ToString());
            titleOverflow.Add(groups[i].ToString());
        }
        return x;
    }

    private static bool IsMenu(TitleGroup group) => group <= TitleGroup.Help;

    /** Records a group from its first x to its last item, relative to its row's window, for the layout checks. */
    private void RecordTitleRect(string name, bool menu, float startX, Vector2 barMin)
    {
        Vector2 min = ImGui.GetItemRectMin() - barMin, max = ImGui.GetItemRectMax() - barMin;
        titleRects.Add(new(name, menu, new(Math.Min(startX, min.X), min.Y), max));
    }

    // Extra room before a toolbar section, with a hairline in it: navigation | tools | place | build | view controls.
    // Controls inside a section are ItemSpacing.X apart.
    private float TitleLead(TitleGroup group) => group is TitleGroup.Tools or TitleGroup.Place or TitleGroup.Project or TitleGroup.Render
        ? 14 * scale : 0;

    /** A BorderHi hairline one control tall, centred at x between two toolbar sections. */
    private static void TitleDivider(float x, float row)
    {
        var origin = ImGui.GetWindowPos();
        float top = row + ImGui.GetFrameHeight() * .15f, bottom = row + ImGui.GetFrameHeight() * .85f;
        x = MathF.Round(x) + .5f;
        ImGui.GetWindowDrawList().AddLine(origin + new Vector2(x, top), origin + new Vector2(x, bottom), SolTheme.U32(SolTheme.BorderHi), 1);
    }

    /** A group's width when last drawn, or a guess before its first frame on the row. */
    private float TitleWidth(TitleGroup group)
    {
        float width = titleWidths[(int)group];
        if (width > 0) return width;
        var style = ImGui.GetStyle();
        return IsMenu(group) ? ImGui.CalcTextSize(group.ToString()).X + style.ItemSpacing.X * 2 + style.FramePadding.X * 2
            : group is TitleGroup.Quality or TitleGroup.Lighting ? 150 * scale
            : group is TitleGroup.Tools ? (ImGui.GetFrameHeight() + style.ItemSpacing.X) * 4 + TitleLead(group)
            : group is TitleGroup.Place ? 90 * scale + TitleLead(group)
            : group is TitleGroup.Project ? 80 * scale + TitleLead(group)
            : ImGui.GetFrameHeight() * 2 + style.ItemSpacing.X + TitleLead(group);
    }

    /** The 16 px brand mark, drawn without a clickable item so it stays part of the drag caption; its tooltip names the version. */
    private float DrawTitleBrand(float height)
    {
        Vector2 origin = ImGui.GetWindowPos();
        float x = SolTheme.Space4;
        int mark = (int)MathF.Round(16 * scale);
        if (toolbarLogo?.Width != mark && renderer is not null)
        {
            toolbarLogo?.Dispose();
            toolbarLogo = StudioBrand.CreateTexture(renderer, mark);
        }
        var markMin = Vector2.Round(origin + new Vector2(x, (height - mark) / 2));
        if (toolbarLogo is { } logo)
            // Whole pixels keep the 1:1 logo from being resampled again.
            ImGui.GetWindowDrawList().AddImage(logo.ImGuiId, markMin, markMin + new Vector2(mark));
        else StudioBrand.DrawMark(ImGui.GetWindowDrawList(), markMin, mark);
        if (ImGui.IsMouseHoveringRect(markMin, markMin + new Vector2(mark)) && ImGui.IsWindowHovered())
            ImGui.SetTooltip($"Solforge {StudioVersion.Text}\n{AboutCredit}");
        return x + mark + SolTheme.Space4;
    }

    /** One group on its row; the cursor is at its start, its top at row (textTop for text). */
    private void DrawTitleGroup(TitleGroup group, float row, float textTop)
    {
        switch (group)
        {
            case TitleGroup.File: DrawFileMenu(); break;
            case TitleGroup.Edit: DrawEditMenu(); break;
            case TitleGroup.Assets: DrawAssetsMenu(); break;
            case TitleGroup.Build: DrawBuildMenu(); break;
            case TitleGroup.View: DrawViewMenu(); break;
            case TitleGroup.Window: DrawWindowMenu(); break;
            case TitleGroup.Help: DrawHelpMenu(); break;
            default: DrawToolbarGroup(group, row, textTop); break;
        }
    }

    /** One group inside a "»" menu: menus become submenus, buttons become menu items. */
    private void DrawTitleOverflowItem(TitleGroup group)
    {
        switch (group)
        {
            case TitleGroup.File: DrawFileMenu(); break;
            case TitleGroup.Edit: DrawEditMenu(); break;
            case TitleGroup.Assets: DrawAssetsMenu(); break;
            case TitleGroup.Build: DrawBuildMenu(); break;
            case TitleGroup.View: DrawViewMenu(); break;
            case TitleGroup.Window: DrawWindowMenu(); break;
            case TitleGroup.Help: DrawHelpMenu(); break;
            case TitleGroup.Navigation:
                if (UiAuto.MenuItem("Back", ShortcutInput.Text(ShortcutActions.NavigateBack), false, workspace.CanNavigateBack)) NavigateBack();
                if (UiAuto.MenuItem("Forward", ShortcutInput.Text(ShortcutActions.NavigateForward), false, workspace.CanNavigateForward))
                    NavigateForward();
                break;
            case TitleGroup.Tools:
                if (UiAuto.BeginMenu("Transform tool", viewport is not null))
                {
                    foreach (var (tool, _, tip) in ToolbarTools)
                        if (UiAuto.MenuItem(tip, "", viewport?.Tool == tool)) viewport!.Tool = tool;
                    UiAuto.EndMenu();
                }
                break;
            case TitleGroup.Place:
                if (UiAuto.BeginMenu("Place"))
                {
                    DrawPlaceMenuItems();
                    UiAuto.EndMenu();
                }
                break;
            case TitleGroup.Project:
                ImGui.Separator();
                ImGui.TextDisabled(workspace.Project?.Name ?? "View only");
                if (workspace.Project is null)
                {
                    if (UiAuto.MenuItem("New project")) projectDialog = true;
                }
                else
                {
                    if (UiAuto.MenuItem("Add asset", "", false, workspace.CanEdit)) addDialog = true;
                    if (UiAuto.MenuItem("Build packages", ShortcutInput.Text(ShortcutActions.BuildPackages), false, workspace.CanEdit))
                        workspace.Run(() => workspace.Build());
                }
                break;
            case TitleGroup.Render: DrawRenderingMenuItem(); break;
            case TitleGroup.Shaders:
            {
                bool shading = settings.GameShaders;
                if (UiAuto.MenuItem("Game shaders", "", ref shading)) SetGameShaders(shading);
                break;
            }
            case TitleGroup.Quality:
                if (UiAuto.BeginMenu("Quality"))
                {
                    for (int i = 0; i < QualityNames.Length; i++)
                        if (UiAuto.MenuItem(QualityNames[i], "", i == (int)settings.ViewportQuality)) SetViewportQuality((ViewportQuality)i);
                    UiAuto.EndMenu();
                }
                break;
            case TitleGroup.Detail:
            {
                bool fullDetail = settings.FullDetail;
                if (UiAuto.MenuItem("Full detail", "", ref fullDetail)) SetFullDetail(fullDetail);
                break;
            }
            case TitleGroup.Lighting:
                if (UiAuto.BeginMenu("Lighting"))
                {
                    int lighting = Math.Clamp((int)settings.LightingMode, 0, LightingNames.Length - 1);
                    for (int i = 0; i < LightingNames.Length; i++)
                        if (UiAuto.MenuItem(LightingNames[i], "", i == lighting) && i != lighting) SetLighting(i);
                    UiAuto.EndMenu();
                }
                break;
            case TitleGroup.Settings:
                if (UiAuto.MenuItem("Settings...", ShortcutInput.Text(ShortcutActions.OpenSettings))) preferencesOpen = true;
                break;
        }
    }

    private void SetFullDetail(bool fullDetail)
    {
        settings.FullDetail = fullDetail;
        if (viewport is not null) viewport.FullDetail = fullDetail;
        if (assetViewport is not null) assetViewport.FullDetail = fullDetail;
        if (fullDetail && settings.ViewportQuality < ViewportQuality.High) SetViewportQuality(ViewportQuality.High);
        else SaveSettings();
    }

    private void SetLighting(int lighting)
    {
        settings.LightingMode = (SunriseStudio.Rendering.NativeLightingMode)lighting;
        if (viewport?.NativeRenderer is { } scene) scene.LightingMode = settings.LightingMode;
        if (assetViewport?.NativeRenderer is { } asset) asset.LightingMode = settings.LightingMode;
        // Lighting is part of game shading; picking a mode turns it on so the choice shows.
        if (!settings.GameShaders || settings.ViewportQuality < ViewportQuality.High) SetGameShaders(true);
        else SaveSettings();
    }

    /** Pixels from a text line's top to its baseline in the current font. */
    private static float FontAscent() => ImGui.GetFont().Ascent * ImGui.GetFontSize() / ImGui.GetFont().FontSize;

    /** The next toolbar control on this row, its top at row (the centre line less half a frame). */
    private static void ToolbarNext(float row)
    {
        ImGui.SameLine();
        ImGui.SetCursorPosY(row);
    }
}
