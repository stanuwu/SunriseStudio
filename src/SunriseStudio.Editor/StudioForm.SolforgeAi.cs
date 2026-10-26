/**
 * File: The separate Solforge AI app inside the editor (UI-UE5 §4.1, §5.1): owns the Solforge.AiLauncher.SolforgeAiHost,
 * prepares a launch (MCP service, session files, client configs, standalone files), and exposes what agent B's chrome
 * calls: LaunchOrFocusSolforgeAi, StopSolforgeAi, RecheckSolforgeAi, SolforgeAiButtonState (the toolbar pill),
 * DrawSolforgeAiStatusCard (top of the AI panel), SolforgeAiProjectChanged (project switch: workflows stop; the app follows) and
 * ShutdownSolforgeAi (FormClosed: quit-force, 25 s, tree kill of the started process only). See docs/PORT-HOOKS.md.
 * Launching is a user action: MCP tools never call these, and the buttons are blocked for MCP UI automation.
 */
using System.Numerics;
using ImGuiNET;
using Solforge.AiLauncher;
using Solforge.Harness;
using Solforge.Harness.Credentials;
using Solforge.Mcp;
using SunriseStudio.Rendering;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** What the toolbar's Solforge AI pill shows (UI-UE5 §4.1 state table). */
public readonly record struct SolforgeAiButton(SolforgeAiState State, string Label, string Tooltip, bool Enabled, bool Pulsing,
    bool BackendReady, int PendingApprovals);

public sealed partial class StudioForm
{
    private SolforgeAiHost? solforgeAi;
    private bool solforgeAiChecked, solforgeAiFilesPublished;
    private string? solforgeAiShim;
    /** The MCP request count when Solforge AI was last launched (the status card's shim-not-connected hint). */
    private int solforgeAiLaunchRequests;
    private HarnessSettings? harnessSettings;
    private string? harnessSettingsError;

    /** Agent B's Output Log / AI LOG append (hook H16); without B's implementation the call compiles away. */
    partial void AppendAiLog(string text);
    /** Agent B opens Settings > Solforge AI (the NotFound pill click, the chevron's "Settings..."). */
    partial void OpenSolforgeAiSettings();

    /** The launcher, created on first use. */
    private SolforgeAiHost SolforgeAi => solforgeAi ??= CreateSolforgeAi();

    private SolforgeAiHost CreateSolforgeAi()
    {
        var host = new SolforgeAiHost(SolforgeAiOptions);
        host.StateChanged += () =>
        {
            // The state is read every frame; only make sure a frame comes soon while the editor is idle.
            try { if (IsHandleCreated) BeginInvoke((Action)Invalidate); }
            catch (InvalidOperationException) { }
        };
        return host;
    }

    /** The launch options for a project, from the current settings (never contacts a provider). */
    private SolforgeAiLaunchOptions SolforgeAiOptions(string? project)
    {
        var harness = (LoadHarnessSettings() ?? new HarnessSettings()).Clone();
        harness.SharedResourcesPath = settings.SharedResourcesRoot ?? "";
        harness.WorkbenchRoot = string.IsNullOrWhiteSpace(settings.WorkbenchRoot) ? null : settings.WorkbenchRoot;
        harness.CredentialFilePath = string.IsNullOrWhiteSpace(settings.CredentialFile) ? null : settings.CredentialFile;
        return new SolforgeAiLaunchOptions
        {
            AppPathSetting = settings.SolforgeAiPath,
            BuildsRoot = string.IsNullOrWhiteSpace(settings.SolforgeBuildsRoot) ? AppDiscovery.DefaultBuildsRoot() : settings.SolforgeBuildsRoot,
            SkipWelcome = settings.SolforgeAiSkipWelcome,
            ShimPath = solforgeAiShim ?? Path.Combine(AppContext.BaseDirectory, "solforge-mcp.exe"),
            ProjectUpstreamsFile = project is null ? null : SolMcpServers.UpstreamFile(project),
            DefaultUpstreamsFile = McpDiscovery.DefaultUpstreamsFile,
            CredentialFile = harness.CredentialFilePath,
            CredentialNames = harness.Models.Where(model => model.Enabled && !string.IsNullOrWhiteSpace(model.ApiKeyVariable))
                .Select(model => model.ApiKeyVariable!).Distinct(StringComparer.Ordinal).ToArray(),
            Harness = harness,
            BundledSkillsDirectory = Path.Combine(AppContext.BaseDirectory, "Integrations", "skills")
        };
    }

    /** The model team settings (harness-settings.json); null with harnessSettingsError when the file is invalid. File read only. */
    private HarnessSettings? LoadHarnessSettings(bool reload = false)
    {
        if (harnessSettings is not null && !reload) return harnessSettings;
        try
        {
            harnessSettings = Task.Run(() => new HarnessSettingsStore().LoadAsync(HarnessSettingsStore.DefaultPath)).GetAwaiter().GetResult();
            harnessSettingsError = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            or InvalidDataException or HarnessSettingsValidationException)
        {
            harnessSettingsError = "Could not load " + HarnessSettingsStore.DefaultPath + ": " + error.Message;
            harnessSettings ??= new HarnessSettings();
        }
        return harnessSettings;
    }

    /**
     * Hook H8, right after the unconditional EnsureAiService() in OnShown: arms the credential guard from the settings, runs
     * the first Solforge AI discovery and writes the standalone MCP files. File inspection only; nothing is launched.
     */
    public void SolforgeAiStartup()
    {
        CredentialGuard.CredentialFile = settings.CredentialFile;
        solforgeAiChecked = true;
        SolforgeAi.Recheck(SolforgeAiProject);
        // Follow harness-settings.json changes made by Solforge AI even if the settings pages are never opened.
        EnsureHarnessWatch();
        if (aiService is not null && !solforgeAiFilesPublished) { solforgeAiFilesPublished = true; PublishStandaloneMcpFiles(); }
    }

    /** The open project folder, or null. */
    private string? SolforgeAiProject => workspace.Project?.DirectoryPath;

    /**
     * The toolbar pill's state (UI-UE5 §4.1). Read every frame by agent B's toolbar (hook H5). Also keeps the credential
     * guard and the pending-approval badge current, and publishes the standalone MCP files once the service runs.
     */
    public SolforgeAiButton SolforgeAiButtonState
    {
        get
        {
            CredentialGuard.CredentialFile = settings.CredentialFile;
            var host = SolforgeAi;
            int pending;
            lock (aiRequests) pending = aiRequests.Count;
            host.PendingApprovals = pending;
            if (!solforgeAiChecked) { solforgeAiChecked = true; host.Recheck(SolforgeAiProject); }
            if (!solforgeAiFilesPublished && aiService is not null) { solforgeAiFilesPublished = true; PublishStandaloneMcpFiles(); }
            string project = SolforgeAiProject is { } open ? Path.GetFileName(open) : "no project";
            var status = host.Status;
            return host.State switch
            {
                SolforgeAiState.NotFound => new(host.State, "Solforge AI", "Solforge AI not found. Set its folder in Settings.", true, false, false, pending),
                SolforgeAiState.Launching => new(host.State, "Solforge AI", "Starting...", false, true, false, pending),
                SolforgeAiState.Running when status is { BackendReady: true } =>
                    new(host.State, "Solforge AI", $"Running · MCP {aiService?.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "off"} · {aiService?.Requests ?? 0} calls", true, false, true, pending),
                SolforgeAiState.Running => new(host.State, "Solforge AI", "Running, backend starting", true, false, false, pending),
                SolforgeAiState.Stopping => new(host.State, "Solforge AI", "Waiting for Solforge AI to quit (it may be asking you to confirm)", false, true, false, pending),
                SolforgeAiState.Failed => new(host.State, "Solforge AI", host.LastError ?? "Solforge AI failed to start.", true, false, false, pending),
                _ => new(host.State, "Solforge AI", "Launch Solforge AI for " + project, true, false, false, pending)
            };
        }
    }

    /**
     * The pill's main click and Ctrl+Shift+A (OpenAiWorkspace): launch for the open project, or focus the running app.
     * NotFound opens Settings > Solforge AI instead. A user action only: never reached from an MCP tool.
     */
    public void LaunchOrFocusSolforgeAi()
    {
        var host = SolforgeAi;
        if (host.State == SolforgeAiState.NotFound)
        {
            host.Recheck(SolforgeAiProject);
            if (host.State == SolforgeAiState.NotFound) { OpenSolforgeAiSettings(); return; }
        }
        if (host.IsRunning) { _ = host.LaunchOrFocusAsync(host.Project); return; }
        string? project = SolforgeAiProject;
        PrepareSolforgeAiLaunch(project);
        solforgeAiLaunchRequests = aiService?.Requests ?? 0;
        AppendAiLog("Solforge AI: launching (shared profile) with " + (project ?? "no project open") + ".");
        _ = host.LaunchOrFocusAsync(project).ContinueWith(_ => AppendAiLog("Solforge AI: " + SolforgeAiStateText(host) + "."), TaskScheduler.Default);
    }

    /** The chevron's Stop: quit (honouring the app's confirmation), 25 s, then the tree kill of the started process only. */
    public void StopSolforgeAi()
    {
        if (solforgeAi is not { } host) return;
        AppendAiLog("Solforge AI: asked to quit.");
        _ = host.StopAsync(force: false).ContinueWith(task => AppendAiLog("Solforge AI: " + (task.IsCompletedSuccessfully ? task.Result switch
        {
            SolforgeAiStopResult.Declined => "quit cancelled in Solforge AI; still running",
            SolforgeAiStopResult.StillConfirming => "still asking to confirm quitting",
            _ => "stopped"
        } : "stop failed") + "."), TaskScheduler.Default);
    }

    /** The chevron's Re-check: discovery, the shared-resources preflight and one status request. */
    public void RecheckSolforgeAi()
    {
        LoadHarnessSettings(reload: true);
        SolforgeAi.Recheck(SolforgeAiProject);
        PublishStandaloneMcpFiles();
    }

    /**
     * Hook H11: the editor opened another project (or closed it). Workflows of the old project are stopped. Solforge AI is not
     * quit any more (SOLFORGE-AI-APP-SPEC §6.4): it uses one shared profile and follows the editor's project through
     * project.changed on the editor UI channel and the discovery file.
     */
    public void SolforgeAiProjectChanged(string? newProject)
    {
        CloseModelWorkflows(newProject);
        if (solforgeAi is not { } host || !host.IsRunning || SameFolder(host.Project, newProject)) return;
        AppendAiLog("Solforge AI: following the editor to " + (newProject ?? "no project") + ".");
    }

    /** Hook H12: FormClosed, before StopAiService. Blocks at most about 30 s (quit-force, 25 s grace, tree kill). */
    public void ShutdownSolforgeAi()
    {
        CloseModelWorkflows(null);
        if (solforgeAi is { } host) { solforgeAi = null; host.Shutdown(); host.Dispose(); }
    }

    /** Before a launch (UI-UE5 §4.1): the MCP service, discovery and client configs, the session files, the standalone files. */
    private void PrepareSolforgeAiLaunch(string? project)
    {
        EnsureAiService();
        if (SolforgeMcpService.FindShim() is { } published)
            try { solforgeAiShim = SolMcpServers.RunCopy(published, Path.Combine(McpDiscovery.Folder, "bin")); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { solforgeAiShim = published; }
        if (project is not null)
        {
            PrepareAiEngine(project);
            AiWriteSession(project);
        }
        PublishStandaloneMcpFiles();
    }

    /** UI-UE5 §4.4.7: the per-user default upstreams and the standalone descriptor (with destiny-modder and skill folders). */
    private void PublishStandaloneMcpFiles()
    {
        var tools = aiService?.Dispatcher.Tools;
        string version = typeof(StudioForm).Assembly.GetName().Version?.ToString(3) ?? "0";
        var options = SolforgeAiOptions(SolforgeAiProject);
        var node = SolforgeAi.Layout?.Node;
        _ = Task.Run(() =>
        {
            Solforge.Harness.Shared.SharedHarnessResourceSnapshot? shared = null;
            try { shared = Solforge.Harness.Shared.SharedHarnessResources.Inspect(options.Harness, null, node, options.BundledSkillsDirectory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
            SolforgeMcpService.PublishStandaloneFiles(tools, version, shared);
        });
    }

    private (StandaloneRegistration.State State, DateTime At)? standaloneRegistration;
    private string? standaloneRegistrationMessage;

    /** The standalone registration, re-inspected at most every 2 s (file reads only). */
    private StandaloneRegistration.State StandaloneRegistrationState()
    {
        if (standaloneRegistration is { } cached && DateTime.UtcNow - cached.At < TimeSpan.FromSeconds(2)) return cached.State;
        var state = StandaloneRegistration.Inspect(SolforgeMcpService.StandaloneShim, McpDiscovery.DefaultUpstreamsFile);
        standaloneRegistration = (state, DateTime.UtcNow);
        return state;
    }

    /**
     * The user's confirmed Register (Settings > Solforge AI > STANDALONE MCP): publishes the standalone files, then adds the
     * solforge-standalone entry to ~/.claude.json and importClaudeServers so Solforge AI started by itself runs the MCP.
     */
    private void RegisterStandaloneMcp()
    {
        EnsureAiService();
        var tools = aiService?.Dispatcher.Tools;
        string version = typeof(StudioForm).Assembly.GetName().Version?.ToString(3) ?? "0";
        var options = SolforgeAiOptions(SolforgeAiProject);
        var node = SolforgeAi.Layout?.Node;
        standaloneRegistrationMessage = "Registering...";
        _ = Task.Run(() =>
        {
            Solforge.Harness.Shared.SharedHarnessResourceSnapshot? shared = null;
            try { shared = Solforge.Harness.Shared.SharedHarnessResources.Inspect(options.Harness, null, node, options.BundledSkillsDirectory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
            try
            {
                var state = SolforgeMcpService.RegisterStandalone(tools, version, shared);
                standaloneRegistrationMessage = state.Registered ? "Registered. Start Solforge AI by itself to use it (an open one picks it up at its next start)." : state.Summary;
                AppendAiLog("Solforge AI: standalone MCP registered as " + StandaloneRegistration.ServerName + ".");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
                or System.Text.Json.JsonException or ArgumentException)
            {
                standaloneRegistrationMessage = "Not registered: " + error.Message;
            }
            standaloneRegistration = null;
        });
    }

    /** The user's Unregister: removes the entry and the import name again. */
    private void UnregisterStandaloneMcp()
    {
        try
        {
            var state = StandaloneRegistration.Unregister();
            standaloneRegistrationMessage = "Unregistered. " + state.Summary;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            standaloneRegistrationMessage = "Not unregistered: " + error.Message;
        }
        standaloneRegistration = null;
    }

    private static bool SameFolder(string? a, string? b)
    {
        if (a is null || b is null) return a is null && b is null;
        try { return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static string SolforgeAiStateText(SolforgeAiHost host) => host.State switch
    {
        SolforgeAiState.NotFound => "not found",
        SolforgeAiState.Launching => "starting",
        SolforgeAiState.Running => "running" + (host.ProcessId is { } pid ? " (PID " + pid + ")" : ""),
        SolforgeAiState.Stopping => "stopping",
        SolforgeAiState.Failed => "failed: " + (host.LastError ?? "unknown error"),
        _ => "not running"
    };

    /** The dot colour of a state (UI-UE5 §4.1 table). */
    private static Vector4 SolforgeAiDot(SolforgeAiState state, bool ready) => state switch
    {
        SolforgeAiState.NotFound => SolTheme.Dim,
        SolforgeAiState.Launching or SolforgeAiState.Stopping => SolTheme.Warn,
        SolforgeAiState.Running => ready ? SolTheme.Ok : SolTheme.Warn,
        SolforgeAiState.Failed => SolTheme.Bad,
        _ => SolTheme.Muted
    };

    /**
     * Hook H6: the status card at the top of the AI panel: Launch/Focus, Stop and Re-check; process and status reply; the app
     * path or the NotFound hint; MCP port and calls and the MCP warning; the shared-resources preflight lines; the workflow
     * summary line.
     */
    public void DrawSolforgeAiStatusCard()
    {
        var button = SolforgeAiButtonState;
        var host = SolforgeAi;
        ImGui.PushID("solforge-ai-card");
        SolTheme.SectionHeader("SOLFORGE AI", "The separate Solforge AI app: launched, focused and stopped from here.");
        var draw = ImGui.GetWindowDrawList();
        float line = ImGui.GetTextLineHeight();
        var at = ImGui.GetCursorScreenPos();
        UiMotion.Dot(draw, at + new Vector2(line * .5f, line * .5f), line * .3f, ImGui.GetID("state-dot"), SolforgeAiDot(button.State, button.BackendReady),
            button.Pulsing, hollow: button.State == SolforgeAiState.NotFound);
        ImGui.Dummy(new Vector2(line, line));
        ImGui.SameLine();
        string since = host.StartedAt is { } started && host.IsRunning ? " · since " + started.ToString("HH:mm:ss") : "";
        ImGui.TextUnformatted(char.ToUpperInvariant(SolforgeAiStateText(host)[0]) + SolforgeAiStateText(host)[1..] + since);

        // MCP UI automation must never launch or stop a GUI app: these controls are blocked for it.
        UiAuto.PushBlocked();
        try
        {
            ImGui.BeginDisabled(!button.Enabled);
            string main = host.IsRunning ? "Focus" : button.State == SolforgeAiState.NotFound ? "Settings..." : button.State == SolforgeAiState.Failed ? "Retry" : "Launch";
            if (SolTheme.PrimaryButton(main + "##solforge-ai-main")) LaunchOrFocusSolforgeAi();
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(button.Tooltip);
            ImGui.SameLine();
            ImGui.BeginDisabled(!(host.IsRunning || button.State == SolforgeAiState.Launching) || button.State == SolforgeAiState.Stopping);
            if (UiAuto.Button("Stop##solforge-ai-stop")) StopSolforgeAi();
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (UiAuto.Button("Re-check##solforge-ai-recheck")) RecheckSolforgeAi();
        }
        finally { UiAuto.PopBlocked(); }

        if (host.Status is { } status && host.IsRunning)
            CardLine(SolTheme.Muted, $"Backend {status.Backend} · ready {(status.BackendReady ? "yes" : "no")} · workspace {(status.EnteredWorkspace ? "entered" : "not entered")}"
                + $" · window {(status.WindowVisible ? "visible" : "hidden")}" + (status.Quitting ? " · quitting" : status.ConfirmingQuit ? " · asking to confirm quit" : ""));
        if (host.Note is { Length: > 0 } note) CardLine(SolTheme.Muted, note);
        if (button.State == SolforgeAiState.Failed && host.LastError is { Length: > 0 } error) CardLine(SolTheme.Bad, error);
        CardLine(SolTheme.Muted, host.Layout is { } layout ? "App: " + layout.AppExecutable + " (" + host.DiscoveryDetail + ")" : host.DiscoveryDetail);
        if (host.IsRunning)
            CardLine(SolTheme.Muted, "Profile: shared · launched with " + (host.Project ?? "no project") + (host.ProcessId is { } pid ? " · PID " + pid : "")
                + (host.OverlayInjected ? " · overlay: solforge" + (host.SharedOverlay?.ServerAdded == true ? ", destiny-modder" : "")
                    + (host.SharedOverlay?.SkillsAdded == true ? ", shared skills" : "") : ""));

        string mcp = "MCP not started";
        AiServiceStatus(ref mcp);
        CardLine(SolTheme.Muted, mcp + " · " + SolforgeMcpService.StandaloneStatus + " Standalone Solforge AI: "
            + (StandaloneRegistrationState().Registered ? "MCP registered." : "no Solforge MCP (Settings > Solforge AI > Register)."));
        // The solforge row uses failOnStartupError false (docs/UI-UE5.md §4.1): a shim that fails to start does not stop
        // Solforge AI, so say so here when the shim has not called this editor 30 s after the backend became ready.
        if (host.IsRunning && host.OverlayInjected && host.Status is { BackendReady: true } && host.StartedAt is { } launched
            && DateTimeOffset.Now - launched > TimeSpan.FromSeconds(30) && aiService is { } service && service.Requests == solforgeAiLaunchRequests)
            CardLine(SolTheme.Warn, "Solforge AI has not called this editor's MCP since it started: its solforge server may have failed to start "
                + "(Solforge AI then shows 'editor tools not loaded'). Check solforge-mcp.exe and " + McpDiscovery.DefaultUpstreamsFile + ".");
        DrawAiMcpWarning();

        if (host.SharedResources is { } shared && ImGui.TreeNodeEx("Shared resources##solforge-ai-shared", ImGuiTreeNodeFlags.SpanAvailWidth))
        {
            foreach (var check in shared.Checks) CheckLine(check.Ok, check.Name + ": " + check.Text);
            ImGui.TreePop();
        }
        DrawWorkflowSummaryLine();
        ImGui.PopID();
    }

    /** One wrapped line of the status card in a colour. */
    private static void CardLine(Vector4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    /** A preflight line: an Ok or Bad dot and its text. */
    private static void CheckLine(bool ok, string text)
    {
        float line = ImGui.GetTextLineHeight();
        var at = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddCircleFilled(at + new Vector2(line * .5f, line * .5f), line * .22f, SolTheme.U32(ok ? SolTheme.Ok : SolTheme.Bad));
        ImGui.Dummy(new Vector2(line, line));
        ImGui.SameLine();
        CardLine(ok ? SolTheme.Text : SolTheme.Muted, text);
    }
}
