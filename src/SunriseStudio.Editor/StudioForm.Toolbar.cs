/**
 * File: the 40 px main toolbar, row 2 of the UE5 chrome (docs/UI-UE5.md §1.3): Back/Forward, the transform tools
 * (Select, Move, Rotate, Scale, promoted from the viewport toolbar, which keeps a compact copy), the Place dropdown,
 * Build (the primary Accent button), the view controls (Render, Shaders, Quality, Detail, Lighting, Settings) and,
 * right-aligned, the Solforge AI pill (§4.1, hook H5). Groups that do not fit fold into the row's "»" menu.
 *
 * The pill launches or focuses the separate Solforge AI app through agent C's SolforgeAiHost. It is drawn between
 * UiAuto.PushBlocked and PopBlocked, so MCP UI automation can never launch or stop a GUI app (this replaces the
 * protection the removed console's ClaimAiKeyboard gave). Pending MCP approvals show as a Warn badge on it.
 */
using System.Numerics;
using ImGuiNET;
using Solforge.AiLauncher;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    private static readonly (TransformTool Tool, StudioIcon Icon, string Tip)[] ToolbarTools =
    [
        (TransformTool.Select, StudioIcon.Select, "Select"),
        (TransformTool.Move, StudioIcon.Move, "Move along an axis"),
        (TransformTool.Rotate, StudioIcon.Rotate, "Rotate around an axis"),
        (TransformTool.Scale, StudioIcon.Scale, "Scale (the game stores one uniform scale)")
    ];

    /** The pill's size at 100 %: about 132 px wide, one 26 px control tall, its chevron part 22 px. */
    private const float PillWidth = 132, PillHeight = 26, PillChevron = 22;

    /** Row 2: the toolbar groups that fit, a "»" for the rest, and the Solforge AI pill at the right end. */
    private void DrawMainToolbar(float top, float height)
    {
        var style = ImGui.GetStyle();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(SolTheme.Space4, 0));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, SolTheme.Panel);
        BeginPanel("Main toolbar", new(0, top), new(ClientSize.Width, height), ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleColor();
        var barMin = ImGui.GetWindowPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(barMin, barMin + new Vector2(ClientSize.Width, height), SolTheme.U32(SolTheme.Panel));
        draw.AddLine(barMin + new Vector2(0, height - .5f), barMin + new Vector2(ClientSize.Width, height - .5f), SolTheme.U32(SolTheme.Border), 1);
        float row = MathF.Round((height - ImGui.GetFrameHeight()) / 2);
        float textTop = row + style.FramePadding.Y;
        float pill = MathF.Round(PillWidth * scale);
        float right = ClientSize.Width - pill - SolTheme.Space6 - SolTheme.Space4;
        float x = SolTheme.Space4;
        float overflowWidth = ImGui.CalcTextSize("»").X + style.FramePadding.X * 2 + style.ItemSpacing.X;
        int fit = FitGroups(ToolGroups, x, right, overflowWidth);
        for (int i = 0; i < fit; i++)
        {
            var group = ToolGroups[i];
            float start = x;
            x += TitleLead(group);
            if (TitleLead(group) > 0) TitleDivider(start + TitleLead(group) / 2 - style.ItemSpacing.X / 2, row);
            ImGui.SetCursorPos(new(x, row));
            DrawToolbarGroup(group, row, textTop);
            x = ImGui.GetItemRectMax().X - barMin.X + style.ItemSpacing.X;
            titleWidths[(int)group] = x - start;
            titleReachable.Add(group.ToString());
            RecordTitleRect(group.ToString(), false, start + TitleLead(group), barMin);
        }
        if (fit < ToolGroups.Length) DrawOverflowButton("toolbar-overflow", ToolGroups, fit, x, row, barMin, height);
        ImGui.SetCursorPos(new(ClientSize.Width - pill - SolTheme.Space4, MathF.Round((height - PillHeight * scale) / 2)));
        DrawSolforgeAiPill();
        titleReachable.Add(TitleGroup.SolforgeAi.ToString());
        RecordTitleRect(TitleGroup.SolforgeAi.ToString(), false, ClientSize.Width - pill - SolTheme.Space4, barMin);
        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    /** One toolbar group; the cursor is at its start with its top at row (textTop for text). */
    private void DrawToolbarGroup(TitleGroup group, float row, float textTop)
    {
        switch (group)
        {
            case TitleGroup.Navigation: DrawNavigationButtons(row); break;
            case TitleGroup.Tools:
            {
                var view = viewport;
                string unavailable = view?.GizmoUnavailableReason is { Length: > 0 } reason ? "\n" + reason : "";
                ImGui.BeginDisabled(view is null);
                for (int i = 0; i < ToolbarTools.Length; i++)
                {
                    var (tool, icon, tip) = ToolbarTools[i];
                    if (i > 0) ToolbarNext(row);
                    if (ImGuiIcons.Button("tb-tool-" + tool, icon, tip + (tool == TransformTool.Select ? "" : unavailable), view?.Tool == tool) && view is not null)
                        view.Tool = tool;
                }
                ImGui.EndDisabled();
                break;
            }
            case TitleGroup.Place:
            {
                ImGui.SetNextItemWidth(ImGui.CalcTextSize("Place").X + ImGui.GetFrameHeight() * 2 + ImGui.GetStyle().FramePadding.X * 2);
                var at = ImGui.GetCursorScreenPos();
                if (UiAuto.BeginCombo("##tb-place", "      Place", ImGuiComboFlags.HeightLarge))
                {
                    SolTheme.PopupShadow();
                    DrawPlaceMenuItems();
                    UiAuto.EndCombo();
                }
                float size = ImGui.GetFrameHeight() * .6f;
                ImGuiIcons.Draw(ImGui.GetWindowDrawList(), StudioIcon.Place, at + new Vector2(ImGui.GetStyle().FramePadding.X, (ImGui.GetFrameHeight() - size) / 2),
                    size, SolTheme.U32(SolTheme.Text));
                StudioUi.Tip("Place: create a new asset or open a category (the same catalog as the Place panel)");
                break;
            }
            case TitleGroup.Project:
                if (workspace.Project is null)
                {
                    if (ImGuiIcons.Button("tb-new", StudioIcon.Plus, "New project")) projectDialog = true;
                }
                else
                {
                    ImGui.BeginDisabled(!workspace.CanEdit);
                    string build = ShortcutInput.Text(ShortcutActions.BuildPackages);
                    if (SolTheme.PrimaryButton("Build##tb-build")) workspace.Run(() => workspace.Build());
                    StudioUi.Tip(build.Length == 0 ? "Build packages" : $"Build packages ({build})");
                    ImGui.EndDisabled();
                }
                break;
            case TitleGroup.Render: DrawRenderingToolbarButton(); break;
            case TitleGroup.Shaders:
            {
                bool shading = settings.GameShaders;
                string? shaderProblem = GameShaderProblem();
                if (ImGuiIcons.Toggle("tb-shaders", StudioIcon.Shader,
                    "Game shaders: the game's materials and lighting (Quality High or Full)" + (shaderProblem is null ? "" : "\n" + shaderProblem),
                    ref shading))
                    SetGameShaders(shading);
                break;
            }
            case TitleGroup.Quality: DrawQualityToolbarCombo(); break;
            case TitleGroup.Detail:
            {
                bool fullDetail = settings.FullDetail;
                if (ImGuiIcons.Toggle("tb-detail", StudioIcon.Detail,
                    "Full detail: draw static objects at full detail at every distance, with no far cull.\nOff switches detail and culls with distance, as the game does."
                    + "\nIn the map viewport this applies at Quality High; Full always draws full detail, Low and Medium never do.",
                    ref fullDetail))
                    SetFullDetail(fullDetail);
                break;
            }
            case TitleGroup.Lighting:
            {
                int lighting = Math.Clamp((int)settings.LightingMode, 0, LightingNames.Length - 1);
                // The preview names the setting so a bare "Studio" doesn't read as the app or layout name.
                ImGui.SetNextItemWidth(ImGui.CalcTextSize("Lighting: Ambient").X + ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.X * 2);
                if (UiAuto.BeginCombo("##lighting", "Lighting: " + LightingNames[lighting]))
                {
                    SolTheme.PopupShadow();
                    for (int i = 0; i < LightingNames.Length; i++)
                        if (UiAuto.Selectable(LightingNames[i], i == lighting) && i != lighting) SetLighting(i);
                    UiAuto.EndCombo();
                }
                string? shaderProblem = GameShaderProblem();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip((lighting == 1
                    ? "Lighting: atmosphere preview\nLocal probes and shadows are not drawn"
                    : "Lighting: editor lights with game materials") + (shaderProblem is null ? "" : "\n" + shaderProblem));
                break;
            }
            case TitleGroup.Settings:
            {
                string settingsKeys = ShortcutInput.Text(ShortcutActions.OpenSettings);
                if (ImGuiIcons.Button("tb-settings", StudioIcon.Gear, settingsKeys.Length > 0 ? $"Settings ({settingsKeys})" : "Settings"))
                    preferencesOpen = true;
                break;
            }
        }
    }

    /**
     * The Solforge AI pill (hook H5): the Sparkle glyph in Brand, the label, a status dot and a split chevron menu. Its
     * main click launches, focuses, retries, or (not found) opens Settings > Solforge AI; it is disabled while
     * launching or stopping. A Warn badge counts pending MCP approvals; clicking it opens the AI Requests tab.
     */
    private void DrawSolforgeAiPill()
    {
        var pill = SolforgeAiButtonState;
        var draw = ImGui.GetWindowDrawList();
        float width = MathF.Round(PillWidth * scale), height = MathF.Round(PillHeight * scale), chevron = MathF.Round(PillChevron * scale);
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(width, height);
        float radius = height / 2;
        UiAuto.PushBlocked();
        try
        {
            // The badge sits over the pill's top-right corner and takes the click before the button under it.
            var badgeCentre = new Vector2(max.X - chevron - 2 * scale, min.Y + 3 * scale);
            float badgeRadius = MathF.Round(7 * scale);
            bool badge = pill.PendingApprovals > 0;
            bool overBadge = badge && Vector2.DistanceSquared(ImGui.GetMousePos(), badgeCentre) <= badgeRadius * badgeRadius
                && ImGui.IsWindowHovered();

            ImGui.SetCursorScreenPos(min);
            ImGui.BeginDisabled(!pill.Enabled);
            bool main = ImGui.InvisibleButton("##solforge-ai-pill", new(width - chevron, height));
            ImGui.EndDisabled();
            uint mainId = ImGui.GetItemID();
            bool mainHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !overBadge;
            bool mainHeld = ImGui.IsItemActive();
            if (mainHovered) ImGui.SetTooltip(pill.Tooltip + (badge ? $"\n{pill.PendingApprovals} MCP request(s) wait for approval: click the badge." : ""));
            ImGui.SetCursorScreenPos(new(max.X - chevron, min.Y));
            bool menu = ImGui.InvisibleButton("##solforge-ai-chevron", new(chevron, height));
            uint chevronId = ImGui.GetItemID();
            bool chevronHovered = ImGui.IsItemHovered();
            if (chevronHovered) ImGui.SetTooltip("Solforge AI options");

            // Fills ease between rest, hover and pressed; the whole pill is one rounded shape split by a hairline.
            var mainFill = UiMotion.EaseColor(mainId, !pill.Enabled ? SolTheme.Surface : mainHeld ? SolTheme.Pressed : mainHovered ? SolTheme.Hover : SolTheme.Surface, 35f);
            var chevronFill = UiMotion.EaseColor(chevronId, chevronHovered ? SolTheme.Hover : SolTheme.Surface, 35f);
            draw.AddRectFilled(min, new(max.X - chevron, max.Y), SolTheme.U32(mainFill), radius, ImDrawFlags.RoundCornersLeft);
            draw.AddRectFilled(new(max.X - chevron, min.Y), max, SolTheme.U32(chevronFill), radius, ImDrawFlags.RoundCornersRight);
            draw.AddRect(min, max, SolTheme.U32(pill.State == SolforgeAiState.Running && pill.BackendReady ? SolTheme.Alpha(SolTheme.Brand, .55f) : SolTheme.BorderHi),
                radius, ImDrawFlags.None, 1);
            draw.AddLine(new(max.X - chevron, min.Y + 5 * scale), new(max.X - chevron, max.Y - 5 * scale), SolTheme.U32(SolTheme.BorderHi), 1);
            float icon = MathF.Round(14 * scale);
            var iconAt = new Vector2(min.X + 10 * scale, min.Y + (height - icon) / 2);
            ImGuiIcons.Draw(draw, StudioIcon.Sparkle, iconAt, icon, SolTheme.U32(pill.Enabled ? SolTheme.Brand : SolTheme.Alpha(SolTheme.Brand, .5f)));
            bool font = SolTheme.PushFont(SolTheme.ButtonFont);
            var textAt = new Vector2(iconAt.X + icon + 6 * scale, min.Y + (height - ImGui.GetTextLineHeight()) / 2);
            draw.AddText(textAt, SolTheme.U32(pill.Enabled ? SolTheme.TextStrong : SolTheme.Muted), pill.Label);
            if (font) ImGui.PopFont();
            var dotCentre = new Vector2(max.X - chevron - 10 * scale, min.Y + height / 2);
            UiMotion.Dot(draw, dotCentre, 3.5f * scale, mainId, SolforgeAiDot(pill.State, pill.BackendReady), pill.Pulsing,
                hollow: pill.State == SolforgeAiState.NotFound);
            float chevronSize = 5 * scale;
            UiMotion.DrawChevron(draw, new(max.X - chevron / 2 - scale, min.Y + height / 2), chevronSize,
                // Points down; turns to point up while the menu is open.
                MathF.PI / 2 + UiMotion.Chevron(chevronId, ImGui.IsPopupOpen("solforge-ai-menu")) * 2, SolTheme.U32(SolTheme.Text));

            // The badge fades in and, once the last request is answered, out with its last count (UiMotion.Toast).
            var (badgeAlpha, _) = UiMotion.Toast(UiMotion.Mix(mainId, 0xBAD6u), badge);
            if (badge) badgeLastCount = pill.PendingApprovals;
            if (badge || badgeAlpha > .01f && badgeLastCount > 0)
            {
                float alpha = badgeAlpha;
                int shownCount = badge ? pill.PendingApprovals : badgeLastCount;
                string count = shownCount > 9 ? "9+" : shownCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
                draw.AddCircleFilled(badgeCentre, badgeRadius, SolTheme.U32(SolTheme.Alpha(SolTheme.Warn, alpha)), 16);
                draw.AddCircle(badgeCentre, badgeRadius, SolTheme.U32(SolTheme.Alpha(SolTheme.Panel, alpha)), 16, Math.Max(1, 1.5f * scale));
                var size = ImGui.CalcTextSize(count);
                draw.AddText(badgeCentre - size / 2, SolTheme.U32(SolTheme.Alpha(SolTheme.Bg, alpha)), count);
                if (overBadge)
                {
                    ImGui.SetTooltip($"{pill.PendingApprovals} MCP request(s) wait for your approval. Click to review them.");
                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) { OpenAiWorkspace(); main = false; }
                }
            }
            if (main && pill.Enabled) LaunchOrFocusSolforgeAi();
            if (menu) ImGui.OpenPopup("solforge-ai-menu");
            ImGui.SetNextWindowPos(new(max.X, max.Y + 2 * scale), ImGuiCond.Always, new(1, 0));
            if (ImGui.BeginPopup("solforge-ai-menu"))
            {
                SolTheme.PopupShadow();
                bool running = pill.State == SolforgeAiState.Running;
                bool busy = pill.State is SolforgeAiState.Launching or SolforgeAiState.Stopping;
                if (ImGui.MenuItem(running ? "Focus" : pill.State == SolforgeAiState.Failed ? "Launch (retry)" : "Launch", "",
                    false, pill.Enabled && !busy && pill.State != SolforgeAiState.NotFound))
                    LaunchOrFocusSolforgeAi();
                if (pill.State == SolforgeAiState.Launching)
                {
                    if (ImGui.MenuItem("Cancel")) StopSolforgeAi();
                }
                else if (ImGui.MenuItem("Stop", "", false, running)) StopSolforgeAi();
                ImGui.Separator();
                if (ImGui.MenuItem("Open Solforge AI panel")) OpenAiWorkspace();
                if (ImGui.MenuItem("Re-check")) RecheckSolforgeAi();
                if (ImGui.MenuItem("Settings...")) OpenSolforgeAiSettings();
                ImGui.EndPopup();
            }
            ImGui.SetCursorScreenPos(min);
            ImGui.Dummy(new(width, height));
        }
        finally { UiAuto.PopBlocked(); }
    }

    /** The newest approval the toast showed and the one the user dismissed (a new request shows the toast again). */
    private object? approvalToastRequest, approvalToastDismissed;
    private int approvalToastCount, badgeLastCount;

    /**
     * "Solforge AI wants to: <summary>" under the toolbar's right end while an MCP request waits (UI-UE5 §4.2), with
     * Approve and Deny (the fallback answer when Solforge AI is closed; whichever side answers first wins), Review (opens the
     * AI panel or its tab) and Dismiss. It also draws the Solforge AI pairing modal. It fades and rises in; it never takes keyboard focus.
     */
    private void DrawApprovalToast()
    {
        DrawInfoToast();
        DrawEditorPairModal();
        AiRequest? newest;
        int count;
        lock (aiRequests) { newest = aiRequests.LastOrDefault(); count = aiRequests.Count; }
        bool visible = newest is not null && !ReferenceEquals(newest, approvalToastDismissed);
        if (newest is not null) approvalToastRequest = newest;
        // The "(+N more)" line keeps its last count while the toast fades out.
        if (visible) approvalToastCount = count; else count = approvalToastCount;
        var (alpha, rise) = UiMotion.Toast(0xA9_70A5u, visible);
        if (alpha <= .01f || approvalToastRequest is not AiRequest shown) return;
        float top = MathF.Round(70 * scale) + SolTheme.Space4;
        ImGui.SetNextWindowPos(new(ClientSize.Width - SolTheme.Space4, top - rise), ImGuiCond.Always, new(1, 0));
        ImGui.SetNextWindowSizeConstraints(new(220 * scale, 0), new(420 * scale, 400 * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, alpha * ImGui.GetStyle().Alpha);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(SolTheme.Space5, SolTheme.Space4));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 4 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, SolTheme.Popup);
        ImGui.PushStyleColor(ImGuiCol.Border, SolTheme.Alpha(SolTheme.Warn, .6f));
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.AlwaysAutoResize;
        UiAuto.PushBlocked();
        try
        {
            if (ImGui.Begin("##approval-toast", flags))
            {
                SolTheme.PopupShadow();
                var draw = ImGui.GetWindowDrawList();
                float icon = ImGui.GetTextLineHeight();
                ImGuiIcons.Draw(draw, StudioIcon.Sparkle, ImGui.GetCursorScreenPos(), icon, SolTheme.U32(SolTheme.Brand));
                ImGui.Dummy(new(icon));
                ImGui.SameLine();
                ImGui.TextColored(SolTheme.TextStrong, "Solforge AI wants to:");
                if (count > 1) { ImGui.SameLine(); ImGui.TextColored(SolTheme.Warn, $"(+{count - 1} more)"); }
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 380 * scale);
                ImGui.TextWrapped($"{shown.Tool}: {shown.Summary}");
                ImGui.PopTextWrapPos();
                // The buttons stay (disabled) through the fade-out, so the auto-sized toast does not snap shorter.
                // Approve and Deny answer the request here (SOLFORGE-AI-APP-SPEC §6.6), so it never waits unseen when Solforge AI
                // is closed; Solforge AI may answer it too, and whichever side answers first wins.
                ImGui.BeginDisabled(!visible);
                if (SolTheme.PrimaryButton("Approve##approval-toast") && visible) Answer(shown, true);
                ImGui.SameLine();
                if (UiAuto.Button("Deny##approval-toast") && visible) Answer(shown, false);
                ImGui.SameLine();
                if (UiAuto.Button("Review##approval-toast") && visible) OpenAiWorkspace();
                ImGui.SameLine();
                if (UiAuto.Button("Dismiss##approval-toast") && visible) approvalToastDismissed = newest;
                ImGui.EndDisabled();
            }
            ImGui.End();
        }
        finally { UiAuto.PopBlocked(); }
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(4);
        if (!visible && alpha <= .02f) approvalToastRequest = null;
    }
}
