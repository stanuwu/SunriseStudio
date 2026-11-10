using System.ComponentModel;
using System.Diagnostics;
using ImGuiNET;
using SunriseStudio.Live;
using SunriseStudio.Rendering;

namespace SunriseStudio.Editor;

/**
 * The Live tab. It lists the installation's running games, holds the one attachment every section shares, draws the section
 * tabs and watches for the game's exit. Games are listed on first draw, on Refresh and after the game closes, never every frame.
 */
internal sealed class LiveTab : IDisposable
{
    private const string DetachedText = "Not attached. Choose the game and press Attach.";
    private const string AttachTip = "Attach every Live section to this game. " + LiveGate.AttachHelp;
    private readonly LiveInvestmentSession investment;
    private readonly IReadOnlyList<ILiveSection> sections;
    private IReadOnlyList<LiveProcess> processes = [];
    private int selected;
    private string? root;
    private bool listed, closed;
    private LiveProcess? attached;
    private Func<bool>? exited;
    // The attached game's process handle. Holding it keeps Windows from reusing the id while attached.
    private Process? game;
    // Why the last Attach failed, shown in place of the detached line until the next attempt.
    private string failure = "";
    private string? requested;
    // Whether a Show named a row, which history records as a link rather than a choice made in the section.
    private bool linkedRow;
    // The section the last Draw showed.
    private ILiveSection? drawn;

    public LiveTab(LiveInvestmentSession investment, IReadOnlyList<ILiveSection> sections)
    {
        this.investment = investment;
        this.sections = sections;
    }

    /** True from Attach until Detach or the game's exit. Meanwhile Studio shares the GPU with the game. */
    public bool Attached => attached is not null;

    /** Attaches every section to the game. A game that already exited or changed stays detached, with the reason shown. */
    public void Attach(LiveProcess process)
    {
        Detach(gameClosed: false);
        try { exited = OpenExitProbe(process); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
        {
            failure = error is ArgumentException ? "The game is not running. Refresh the list." : error.Message;
            return;
        }
        attached = process;
        failure = ""; closed = false;
        foreach (var section in sections) section.Attach(process);
    }

    /** gameClosed says the game exited. The game list is then read again, so the closed game leaves it. */
    public void Detach(bool gameClosed)
    {
        if (attached is null) return;
        foreach (var section in sections) section.Detach(gameClosed);
        game?.Dispose(); game = null;
        exited = null; attached = null;
        closed = gameClosed;
        if (gameClosed && root is { } gameRoot) Refresh(gameRoot);
    }

    /** The shown section's chosen row, which history records with its page. */
    public string? Row => drawn?.Row;

    /** True while a section Show asked for has not been drawn yet. */
    public bool Requested => requested is not null;

    /** Shows a section, and a subtab and row of it, the next time the Live tab draws. */
    public void Show(string section, string view, string? row)
    {
        if (sections.FirstOrDefault(item => item.Id == section) is not { } target) return;
        requested = section;
        linkedRow |= row is not null;
        target.Show(view, row);
    }

    /** True once after a Show that named a row, so history keeps the place it left. */
    public bool TakeLinkedRow()
    {
        bool linked = linkedRow;
        linkedRow = false;
        return linked;
    }

    /** Runs each frame. A new installation ends the attachment, a closed game detaches all sections, then each section polls. */
    public void Poll(LiveSectionContext context)
    {
        string? gameRoot = context.Workspace.Catalog?.GameRoot;
        if (gameRoot != root)
        {
            Detach(gameClosed: false);
            root = gameRoot; processes = []; selected = 0; listed = false; failure = ""; closed = false;
        }
        if (exited?.Invoke() == true) Detach(gameClosed: true);
        foreach (var section in sections) section.Poll(context);
    }

    /**
     * The shared toolbar, the section tabs and the shown section.
     * @return The shown page id history records: "live:<section>" and ":<subtab>" when the section has subtabs.
     */
    public string Draw(LiveSectionContext context)
    {
        if (root is { } gameRoot && !listed) Refresh(gameRoot);
        DrawToolbar();
        string page = "live";
        drawn = null;
        if (!ImGui.BeginTabBar("live-sections")) return page;
        foreach (var section in sections)
        {
            bool focus = requested == section.Id;
            if (!StudioForm.BeginEditorTab(section.Title, focus)) continue;
            if (focus) requested = null;
            drawn = section;
            section.Draw(context);
            page = "live:" + section.Id + (section.View.Length != 0 ? ":" + section.View : "");
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
        return page;
    }

    /** Runs between frames so sections that draw textures can upload them, as pages do. */
    public void PrepareFrame(StudioWorkspace workspace, Dx11Renderer renderer)
    {
        foreach (var section in sections.OfType<IFramePreparedPage>()) section.PrepareFrame(workspace, renderer);
    }

    /** Draws the status bar's indicator, which opens Live when clicked. @return True when it was clicked. */
    public bool DrawStatus() => InvestmentLinkStatus.Draw(investment, StatusWidth(), opensLive: true);

    public float StatusWidth() => InvestmentLinkStatus.Width(investment);

    /** The game list, Refresh, and Attach or Detach, then the attachment's state, which always has a slot so nothing moves. */
    private void DrawToolbar()
    {
        string locked = root is null ? "Open a Sunrise installation first." : Attached ? "Detach to choose another game." : "";
        ImGui.BeginDisabled(locked.Length != 0);
        // Wide enough for the longest label a process id can give, plus the combo's arrow.
        ImGui.SetNextItemWidth(ImGui.CalcTextSize($"Destiny 2 ({uint.MaxValue})").X + ImGui.GetFrameHeight() + 2 * ImGui.GetStyle().FramePadding.X);
        string shown = attached is { } chosen ? Label(chosen) : processes.Count == 0 ? "No running game" : Label(processes[selected]);
        if (ImGui.BeginCombo("##live-process", shown))
        {
            for (int i = 0; i < processes.Count; i++)
                if (ImGui.Selectable($"{Label(processes[i])}##{processes[i].Id}", selected == i)) selected = i;
            ImGui.EndCombo();
        }
        StudioUi.Tip(locked);
        ImGui.SameLine();
        if (ImGui.Button("Refresh")) Refresh(root!);
        StudioUi.Tip(locked);
        ImGui.EndDisabled();
        ImGui.SameLine();
        var style = ImGui.GetStyle();
        // One button of the wider label's width, so the status after it never moves on attach.
        float width = Math.Max(ImGui.CalcTextSize("Attach").X, ImGui.CalcTextSize("Detach").X) + 2 * style.FramePadding.X;
        if (attached is not null)
        {
            if (ImGui.Button("Detach", new(width, 0))) Detach(gameClosed: false);
            StudioUi.Tip("Stop reading this game in every Live section.");
        }
        else
        {
            string refusal = root is null ? "Open a Sunrise installation first."
                : processes.Count == 0 ? "Start the game from this installation, then Refresh." : "";
            ImGui.BeginDisabled(refusal.Length != 0);
            if (ImGui.Button("Attach", new(width, 0))) Attach(processes[selected]);
            ImGui.EndDisabled();
            StudioUi.Tip(refusal.Length != 0 ? refusal : AttachTip);
        }
        ImGui.SameLine();
        if (attached is not null) { InvestmentLinkStatus.Draw(investment, ImGui.GetContentRegionAvail().X, opensLive: false); return; }
        if (failure.Length != 0) ImGui.TextColored(StudioTheme.Caution, failure);
        else ImGui.TextDisabled(DetachedText);
        StudioUi.Tip(closed ? LiveInvestmentSession.GameClosed : "");
    }

    private static string Label(LiveProcess process) => $"Destiny 2 ({process.Id})";

    private void Refresh(string gameRoot)
    {
        listed = true;
        processes = LiveConnection.FindProcesses(gameRoot);
        selected = 0;
    }

    /**
     * Opens the chosen game and returns a probe that reports its exit. Like LiveConnection.Attach, it refuses an id whose
     * process is not the listed executable.
     */
    private Func<bool> OpenExitProbe(LiveProcess target)
    {
        var process = Process.GetProcessById(target.Id);
        try
        {
            if (!string.Equals(process.MainModule?.FileName, target.Executable, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected game process changed. Refresh the process list.");
        }
        catch { process.Dispose(); throw; }
        game = process;
        return () => process.HasExited;
    }

    public void Dispose()
    {
        Detach(gameClosed: false);
        foreach (var section in sections) section.Dispose();
    }
}
