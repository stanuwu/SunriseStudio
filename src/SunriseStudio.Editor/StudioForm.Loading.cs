using System.Numerics;
using ImGuiNET;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** StudioForm part: loading backdrop, load progress card and cancel rules. */
public sealed partial class StudioForm
{
    private LoadingBackdrop? backdrop;
    private bool loadingCaptured;
    /** Set once the editor has drawn; later loads keep the docked layout on screen and show a toast instead. */
    private bool editorShown;

    /** Solforge: true while something loads after the editor has been shown; the layout stays usable. */
    private bool LoadOverlay => !EditorReady && editorShown && !setup && workspace.Catalog is not null;

    /** The stage the loading card and toast show. */
    private StudioLoadProgress CurrentLoadProgress() => presentationProgress ?? (workspace.IsBusy ? workspace.LoadingProgress
        : new StudioLoadProgress(viewport?.Animation?.IsLoading == true || assetViewport?.Animation?.IsLoading == true
            ? "Loading animation" : browserPreparing ? "Preparing asset browser" : "Preparing editor"));

    /** Loads and reads can be dropped safely (Poll skips their adopt); builds and writes cannot. */
    private bool LoadCancellable => workspace.IsBusy
        && (workspace.Status.StartsWith("Loading", StringComparison.Ordinal) || workspace.Status.StartsWith("Reading", StringComparison.Ordinal));

    /** A small corner toast: spinner, "Loading European Dead Zone… 42%", the stage and Cancel. It never takes focus. */
    private (StudioLoadProgress Progress, string Title, bool Cancellable)? lastLoadToast;

    /**
     * Called every editor frame: visible while a load runs. When the work ends the toast keeps its last title and
     * progress and fades out (about 250 ms, UiMotion.Toast) with Cancel disabled, instead of vanishing in one frame.
     */
    private void DrawLoadToast(bool visible)
    {
        // Hook H15: the toast fades and rises in and out (UiMotion.Toast); it is decoration only and never takes focus.
        var (alpha, rise) = UiMotion.Toast(0x10AD_70A5u, visible);
        if (visible)
        {
            var current = CurrentLoadProgress();
            lastLoadToast = (current, workspace.IsBusy && workspace.Status.Length > 0 ? workspace.Status : current.Stage, LoadCancellable);
        }
        else if (alpha <= .01f || lastLoadToast is null) { lastLoadToast = null; return; }
        var (progress, title, cancellable) = lastLoadToast!.Value;
        string percent = progress.Fraction is { } fraction ? $" {fraction * 100:0}%" : "";
        float status = StatusBarHeight, margin = 12 * scale;
        ImGui.SetNextWindowPos(new(ClientSize.Width - margin, ClientSize.Height - status - margin + rise), ImGuiCond.Always, Vector2.One);
        ImGui.SetNextWindowBgAlpha(.96f);
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, Math.Max(.05f, alpha) * ImGui.GetStyle().Alpha);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 4 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12 * scale, 8 * scale));
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.AlwaysAutoResize;
        if (ImGui.Begin("##load-toast", flags))
        {
            SolTheme.PopupShadow();
            float line = ImGui.GetTextLineHeight();
            DrawSpinner(ImGui.GetCursorScreenPos() + new Vector2(line / 2), line * .42f);
            ImGui.Dummy(new(line));
            ImGui.SameLine(0, 8 * scale);
            ImGui.TextUnformatted($"{title}…{percent}");
            if (cancellable)
            {
                ImGui.SameLine(0, 12 * scale);
                ImGui.BeginDisabled(!visible);
                if (UiAuto.SmallButton("Cancel##load-toast") && visible) workspace.CancelWork();
                ImGui.EndDisabled();
            }
            if (progress.Stage != title)
            {
                string count = progress.Total > 0 ? $"  {progress.Completed:N0} / {progress.Total:N0}" : "";
                ImGui.TextDisabled(progress.Stage + count);
            }
            DrawProgressBar(progress, Math.Max(220 * scale, ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X - 12 * scale), 4 * scale);
        }
        ImGui.End();
        ImGui.PopStyleVar(3);
    }

    /** Stands in for an editor area whose content is being replaced (the document during a load). */
    private void DrawAreaLoading(string id, Vector2 position, Vector2 size)
    {
        BeginPanel(id, position, size, ImGuiWindowFlags.NoScrollbar);
        float radius = 10 * scale;
        var centre = position + size / 2;
        DrawSpinner(centre - new Vector2(0, radius + 6 * scale), radius);
        string text = CurrentLoadProgress().Stage;
        ImGui.SetCursorScreenPos(new(centre.X - ImGui.CalcTextSize(text).X / 2, centre.Y + 6 * scale));
        ImGui.TextDisabled(text);
        ImGui.End();
    }

    /** Windows' animation setting or Settings > Interface > Reduce motion: clock-driven decorations stand still. */
    private static bool MotionStill => LoadingBackdrop.ReducedMotion || UiMotion.ReduceMotion;

    /** A three-quarter arc turning once a second; still under reduced motion. */
    private void DrawSpinner(Vector2 centre, float radius)
    {
        var draw = ImGui.GetWindowDrawList();
        bool still = MotionStill;
        if (!still) UiMotion.KeepAnimating();
        float start = still ? 0 : (float)(clock.Elapsed.TotalSeconds % 1 * Math.PI * 2);
        draw.PathArcTo(centre, radius, start, start + MathF.PI * 1.5f, 24);
        draw.PathStroke(SolTheme.U32(SolTheme.Accent), ImDrawFlags.None, Math.Max(1.5f, 2 * scale));
    }

    /** Resource upload callbacks may present loading frames only outside an active ImGui frame. */
    private void ReportPreparation(string stage, int completed, int total)
    {
        presentationProgress = new(stage, completed, total);
        EditorReady = false;
        // Solforge: once the editor has been shown, a synchronous preparation keeps the last editor frame on screen
        // instead of painting the full-screen loading card over the docked layout; the toast reports it next frame.
        if (editorShown) return;
        if (closed || renderer is null || gui is null || guiFrameActive || nativeDialog
            || clock.Elapsed.TotalSeconds - lastLoadingFrame < 1d / 30) return;
        lastLoadingFrame = clock.Elapsed.TotalSeconds;
        renderer.Resize(ClientSize.Width, ClientSize.Height);
        guiFrameActive = true;
        try
        {
            gui.NewFrame((float)Math.Clamp(lastLoadingFrame - lastFrame, .001, .1));
            UiMotion.BeginFrame();
            lastFrame = lastLoadingFrame;
            renderer.BeginFrame(StudioTheme.Canvas);
            DrawLoading();
            UiMotion.EndFrame();
            gui.Render();
            CaptureLoadingFrame();
            renderer.EndFrame(false);
        }
        finally { guiFrameActive = false; }
    }

    /** Capture a counted loading stage without changing the worker's completion barrier. */
    private void CaptureLoadingFrame()
    {
        if (loadingCaptured || EditorReady || options.CaptureLoading is not { } path
            || (presentationProgress ?? workspace.LoadingProgress) is not { Fraction: >= .15f and <= .9f }) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        renderer!.CapturePng(path);
        loadingCaptured = true;
    }

    /** Editor controls stay absent until the scene and its graphics resources are ready. */
    private void DrawLoading()
    {
        var client = new Vector2(ClientSize.Width, ClientSize.Height);
        BeginPanel("Loading", Vector2.Zero, client, ImGuiWindowFlags.NoScrollbar);
        double now = clock.Elapsed.TotalSeconds;
        var draw = ImGui.GetWindowDrawList();
        backdrop?.Draw(draw, client, scale, now);
        // Without a menu bar the top strip is the caption, so the window can move and close while loading.
        chrome.Scale = scale;
        chrome.CaptionHeight = 30 * scale;
        chrome.DrawButtons(30 * scale);
        DrawLoadingCard(draw, client, now);
        ImGui.End();
        if (setup || settingsDialog) DrawDialogs();
    }

    /** The launcher's hero side: the Sol mark and wordmark, the stage and count and the bar, in a card left of the emblem. */
    private void DrawLoadingCard(ImDrawListPtr draw, Vector2 client, double now)
    {
        bool failed = workspace.Error.Length != 0, working = !setup && !failed;
        var progress = CurrentLoadProgress();
        float pad = 24 * scale, width = Math.Max(200 * scale, Math.Min(400 * scale, client.X - 128 * scale - pad * 2));
        bool font = SolTheme.PushFont(SolTheme.TitleFont);
        float title = ImGui.GetTextLineHeight(), tracking = ImGui.GetFontSize() * .2f;
        if (font) ImGui.PopFont();
        // Centred on the emblem's line for the usual card; an error grows it downwards.
        float height = title + 14 * scale + ImGui.GetTextLineHeight() * 2 + 4 * scale;
        var origin = new Vector2(64 * scale + pad, MathF.Round((client.Y - height) / 2));
        draw.ChannelsSplit(2);
        draw.ChannelsSetCurrent(1);
        ImGui.SetCursorScreenPos(origin);
        ImGui.BeginGroup();
        float markEdge = backdrop is null ? 0 : MathF.Round(title);
        backdrop?.DrawMark(draw, origin, markEdge);
        font = SolTheme.PushFont(SolTheme.TitleFont);
        SolTheme.ShimmerText(draw, origin + new Vector2(markEdge > 0 ? markEdge + 10 * scale : 0, 0), "SOLFORGE", tracking,
            MotionStill ? 0 : (float)(now % 6 / 6));
        if (!MotionStill) UiMotion.KeepAnimating();
        if (font) ImGui.PopFont();
        ImGui.Dummy(new Vector2(width, title + 10 * scale));
        if (failed) ImGui.TextColored(SolTheme.Bad, "Unable to load");
        else ImGui.TextUnformatted(setup ? "Sunrise location" : progress.Stage);
        if (working)
        {
            string count = progress.Total > 0 ? $"{progress.Completed:N0} / {progress.Total:N0}"
                : progress.Completed > 0 ? $"{progress.Completed:N0}" : "";
            if (count.Length > 0)
            {
                float countWidth = ImGui.CalcTextSize(count).X;
                ImGui.SameLine(); ImGui.SetCursorScreenPos(new(origin.X + width - countWidth, ImGui.GetCursorScreenPos().Y));
                ImGui.TextDisabled(count);
            }
            ImGui.Dummy(new Vector2(0, 4 * scale));
            // The loading card keeps the brand orange; every other bar is Accent.
            DrawProgressBar(progress, width, 6 * scale, brand: true);
        }
        if (failed)
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
            ImGui.TextWrapped(workspace.Error);
            ImGui.PopTextWrapPos();
            // A drive that dropped out comes back, so the same location can open again.
            bool retry = !workspace.IsBusy && workspace.Catalog is null && location.Length != 0;
            if (retry && SolTheme.PrimaryButton("Retry"))
                workspace.Run(() => workspace.OpenInstallation(location, keySource));
            if (retry) ImGui.SameLine();
            if (!workspace.IsBusy && (retry ? UiAuto.Button("Choose Sunrise location") : SolTheme.PrimaryButton("Choose Sunrise location")))
                setup = true;
        }
        ImGui.EndGroup();
        draw.ChannelsSetCurrent(0);
        SolTheme.ChamferFrame(draw, ImGui.GetItemRectMin() - new Vector2(pad), ImGui.GetItemRectMax() + new Vector2(pad),
            SolTheme.Cut, SolTheme.U32(SolTheme.Alpha(SolTheme.Panel, .72f)), SolTheme.U32(SolTheme.Line));
        draw.ChannelsMerge();
    }

    /** The launcher's .dl-progress: a sun-grad pill on a faint track with a sheen sweeping it; an unknown total moves a short segment. */
    private void DrawProgressBar(StudioLoadProgress progress, float width, float height, bool brand = false)
    {
        var bar = ImGui.GetCursorScreenPos();
        if (height < ImGui.GetTextLineHeight()) bar.Y += (ImGui.GetTextLineHeight() - height) / 2;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(bar, bar + new Vector2(width, height), SolTheme.U32(SolTheme.Surface2), height / 2);
        bool still = MotionStill;
        if (!still) UiMotion.KeepAnimating();
        double now = clock.Elapsed.TotalSeconds;
        if (progress.Fraction is { } fraction)
            SolTheme.ProgressFill(draw, bar, bar + new Vector2(width * Math.Clamp(fraction, 0, 1), height),
                still ? -1 : (float)(now % 2.4 / 2.4), brand);
        else
        {
            float segment = width * .22f;
            float start = still ? (width - segment) / 2 : (float)(now % 1.6 / 1.6) * (width + segment) - segment;
            SolTheme.ProgressFill(draw, bar + new Vector2(Math.Max(0, start), 0), bar + new Vector2(Math.Min(width, start + segment), height), brand: brand);
        }
        ImGui.Dummy(new Vector2(width, MathF.Max(height, ImGui.GetTextLineHeight())));
    }
}
