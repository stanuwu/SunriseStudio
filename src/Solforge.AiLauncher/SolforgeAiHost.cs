/**
 * File: Starts and owns the separate Solforge AI desktop app (UI-UE5 §4.1, §5.1), ported from solforge-next
 * (Solforge.Next/SolforgeAiHost.cs) and its MainWindow.Harness launcher logic. It follows README-runtime's launch contract:
 *  - the environment is built from an empty block: the inherited allowlist, then the credential file's accepted names, then
 *    the launch variables (so the credential file can never override them);
 *  - the overlay has an .owner marker and the rows solforge (solforge-mcp.exe --sol, failOnStartupError false),
 *    destiny-modder and the shared skills;
 *  - the control pipe client is CurrentUserOnly and checks the server PID before sending the token;
 *  - user Stop is quit, then (while it reports confirmingQuit) waiting, then a 25 s exit wait and a tree kill of the
 *    started process only; editor exit and Stop during launch use quit-force. A project switch never stops the app: it uses
 *    the shared profile (ProjectKey.AppRoot) and follows the editor itself (SOLFORGE-AI-APP-SPEC 6.4).
 * Only the process started here is ever focused, stopped or killed. stdout/stderr are drained and never logged.
 */
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Solforge.Harness;
using Solforge.Harness.Credentials;
using Solforge.Harness.Shared;

namespace Solforge.AiLauncher;

/** What the toolbar pill and the status card show. */
public enum SolforgeAiState { NotFound, Stopped, Launching, Running, Stopping, Failed }

/** Outcome of a user Stop. */
public enum SolforgeAiStopResult
{
    /** The owned process exited (on its own or after the grace period and a tree kill). */
    Stopped,
    /** The user cancelled the app's quit confirmation; the process is kept and still owned. */
    Declined,
    /** The quit confirmation is still open after the wait; the process is kept and still owned. */
    StillConfirming
}

/** The control pipe's status reply. */
public sealed record AiAppStatus(string Backend, bool BackendReady, bool EnteredWorkspace, bool WindowVisible, bool Quitting, bool ConfirmingQuit, DateTimeOffset At)
{
    public static AiAppStatus From(JsonElement body)
    {
        static bool Flag(JsonElement body, string name) => body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        string backend = body.TryGetProperty("backend", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "unknown" : "unknown";
        return new(backend, Flag(body, "backendReady"), Flag(body, "enteredWorkspace"), Flag(body, "windowVisible"), Flag(body, "quitting"),
            Flag(body, "confirmingQuit"), DateTimeOffset.Now);
    }
}

/** Everything one launch needs; built by the editor from its settings right before the launch. */
public sealed class SolforgeAiLaunchOptions
{
    /** StudioSettings.SolforgeAiPath (folder or exe) and SolforgeBuildsRoot. */
    public string? AppPathSetting { get; init; }
    public string? BuildsRoot { get; init; }
    /** SOLFORGE_AI_SKIP_WELCOME=1. */
    public bool SkipWelcome { get; init; }
    /** The editor's folder (discovery step 2, the bundled skills). */
    public string EditorDirectory { get; init; } = AppContext.BaseDirectory;
    /** This editor's solforge-mcp.exe (the overlay's solforge row). */
    public required string ShimPath { get; init; }
    /** SolMcpServers.UpstreamFile(project), already written; null or missing falls back to DefaultUpstreamsFile. */
    public string? ProjectUpstreamsFile { get; init; }
    /** The per-user sol-upstreams-default.json. */
    public required string DefaultUpstreamsFile { get; init; }
    /** StudioSettings.CredentialFile and the variable names enabled models use; only these names are passed. */
    public string? CredentialFile { get; init; }
    public IReadOnlyList<string> CredentialNames { get; init; } = [];
    /** The preflight inputs (SharedResourcesPath and WorkbenchRoot already copied from StudioSettings). */
    public required HarnessSettings Harness { get; init; }
    public string? BundledSkillsDirectory { get; init; }
}

/** The owned Solforge AI process and its state machine. Methods may be called from the UI thread; work runs off it. */
public sealed class SolforgeAiHost : IDisposable
{
    private const string ControlPipePrefix = "solforge-ai-";
    private static readonly string[] InheritedEnvironment =
    [
        "SystemRoot", "SystemDrive", "windir", "TEMP", "TMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "HOMEDRIVE", "HOMEPATH",
        "PATH", "PATHEXT", "COMSPEC", "OS", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS", "ProgramData", "ProgramFiles",
        "ProgramFiles(x86)", "ProgramW6432", "ComputerName", "USERNAME", "USERDOMAIN", "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY",
        "SSL_CERT_FILE", "NODE_EXTRA_CA_CERTS"
    ];
    private readonly Func<string?, SolforgeAiLaunchOptions> options;
    private readonly object sync = new();
    private Process? process;
    private string? overlayPath, controlPipe, controlToken;
    private Task? outputTask, errorTask, launchTask;
    private CancellationTokenSource? launch, poll;

    /** @param options builds the launch options for a project (null: no project) from the editor's current settings. */
    public SolforgeAiHost(Func<string?, SolforgeAiLaunchOptions> options) => this.options = options;

    public SolforgeAiState State { get; private set; } = SolforgeAiState.Stopped;
    /** The last failure, curated: never environment or credential text. */
    public string? LastError { get; private set; }
    /** The last status reply while running. */
    public AiAppStatus? Status { get; private set; }
    /** A one-line note for the status card (control channel, quit confirmation, early exit). */
    public string? Note { get; private set; }
    /** The project open when the app was launched (null: none). The app follows later project changes itself (shared profile). */
    public string? Project { get; private set; }
    public int? ProcessId { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public int? LastExitCode { get; private set; }
    /** The discovered app, or null; DiscoveryDetail says where it came from or why none was found. */
    public SolforgeAiLayout? Layout { get; private set; }
    public string DiscoveryDetail { get; private set; } = "Not checked yet.";
    /** The overlay rows of the last launch (null before one). */
    public SharedOverlay? SharedOverlay { get; private set; }
    public SharedHarnessResourceSnapshot? SharedResources { get; private set; }
    /** True once an overlay was written and passed through SOLFORGE_AI_PATCH. Not proof the host applied it. */
    public bool OverlayInjected { get; private set; }
    /** Pending MCP approval requests (set by the editor every frame), for the toolbar badge. */
    public int PendingApprovals { get; set; }
    public bool IsRunning { get { lock (sync) return process is { HasExited: false }; } }
    /** Raised after any state change, on any thread. */
    public event Action? StateChanged;

    /** Re-runs discovery and the shared-resources preflight; while running, asks for status once. Launches nothing. */
    public void Recheck(string? project = null)
    {
        var current = options(project);
        var found = AppDiscovery.Find(current.AppPathSetting, current.EditorDirectory, current.BuildsRoot);
        Layout = found.Layout;
        DiscoveryDetail = found.Detail;
        try
        {
            SharedResources = SharedHarnessResources.Inspect(WithProject(current.Harness), project ?? Project,
                found.Layout?.Node, current.BundledSkillsDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { }
        if (!IsRunning && launchTask is not { IsCompleted: false })
        {
            if (Layout is null) State = SolforgeAiState.NotFound;
            else if (State is SolforgeAiState.NotFound or SolforgeAiState.Running or SolforgeAiState.Stopping) State = SolforgeAiState.Stopped;
        }
        if (IsRunning) _ = RefreshStatusAsync();
        Raise();
    }

    /** Launch for the project, or focus the app when this editor already runs it. */
    public Task LaunchOrFocusAsync(string? project)
    {
        if (launchTask is { IsCompleted: false }) return launchTask;
        if (IsRunning) return FocusOrNoteAsync();
        var cancellation = new CancellationTokenSource();
        launch = cancellation;
        launchTask = Task.Run(() => LaunchAsync(project, cancellation));
        return launchTask;
    }

    private async Task FocusOrNoteAsync()
    {
        Note = await FocusAsync().ConfigureAwait(false) ? "Solforge AI focused." : "Solforge AI is running, but its window could not be brought forward.";
        Raise();
    }

    private async Task LaunchAsync(string? project, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            var current = options(project);
            var found = AppDiscovery.Find(current.AppPathSetting, current.EditorDirectory, current.BuildsRoot);
            Layout = found.Layout;
            DiscoveryDetail = found.Detail;
            if (found.Layout is not { } layout)
            {
                State = SolforgeAiState.NotFound;
                LastError = found.Detail;
                return;
            }
            State = SolforgeAiState.Launching; LastError = null; Note = null; Status = null;
            Raise();
            await StartAsync(layout, project, current, token).ConfigureAwait(false);
            Raise();
            bool answered = await WaitForControlAsync(TimeSpan.FromSeconds(60), token).ConfigureAwait(false);
            if (IsRunning)
            {
                State = SolforgeAiState.Running;
                Note = answered ? "Control channel answered." : "Control channel did not answer within 60 s; the window may still be opening.";
                StartPolling();
            }
            else if (State == SolforgeAiState.Launching)
            {
                State = SolforgeAiState.Stopped;
                Note = "Exited right after launch. If Solforge AI was already open for this profile (one instance per profile), that window was focused instead; otherwise check the build's solforge-ai and ai-runtime folders.";
            }
        }
        catch (OperationCanceledException)
        {
            // Stop, a project switch or editor close during launch: the window may already be up, so use the owned stop
            // contract (quit-force, 25 s grace, then a tree kill of the started process) rather than an immediate kill.
            State = SolforgeAiState.Stopping; Raise();
            if (IsRunning) await StopOwnedAsync(force: true).ConfigureAwait(false); else DisposeOwned();
            State = Layout is null ? SolforgeAiState.NotFound : SolforgeAiState.Stopped;
            Note = null;
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException
            or InvalidDataException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            // Messages are curated (layout, credential names, start failure); credential contents never reach them.
            DisposeOwned();
            State = SolforgeAiState.Failed;
            LastError = error.Message;
        }
        finally
        {
            if (ReferenceEquals(launch, cancellation)) launch = null;
            cancellation.Dispose();
            Raise();
        }
    }

    private async Task StartAsync(SolforgeAiLayout layout, string? project, SolforgeAiLaunchOptions current, CancellationToken token)
    {
        string appRoot = ProjectKey.AppRoot(project);
        string home = Path.Combine(appRoot, "home"), userData = Path.Combine(appRoot, "electron");
        Directory.CreateDirectory(home); Directory.CreateDirectory(userData);
        if (!File.Exists(current.ShimPath)) throw new InvalidOperationException("solforge-mcp.exe is missing beside the editor; publish Solforge again.");
        foreach (var name in current.CredentialNames)
            if (CredentialEnvironment.RefusalReason(name) is { } reason)
                throw new InvalidOperationException("Credential variable '" + name + "' cannot be passed to Solforge AI (" + reason + "). " + HarnessSettingsValidator.CredentialNameRule);
        LaunchOverlay.DeleteStale(home);
        string overlay = LaunchOverlay.NewPath(home);
        string upstreams = project is not null && current.ProjectUpstreamsFile is { } projectFile && File.Exists(projectFile) ? projectFile : current.DefaultUpstreamsFile;
        // SOLFORGE-AI-APP-SPEC §6.4: no --project; the shim follows MCP discovery to whichever project the editor has open.
        List<string> args = ["--sol", "--upstreams", upstreams];
        string workingDirectory = project is not null && Directory.Exists(project) ? project : home;
        string patch = LaunchOverlay.SolforgeRow(current.ShimPath, args, workingDirectory);
        var snapshot = SharedHarnessResources.Inspect(WithProject(current.Harness), project, layout.Node, current.BundledSkillsDirectory);
        var shared = LaunchOverlay.SharedRows(snapshot, layout.RuntimeDirectory);
        SharedResources = snapshot; SharedOverlay = shared;
        patch += shared.Entries;
        await File.WriteAllTextAsync(overlay, patch, token).ConfigureAwait(false);
        lock (sync) overlayPath = overlay;
        token.ThrowIfCancellationRequested();

        var start = new ProcessStartInfo(layout.AppExecutable)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--user-data-dir=" + userData);
        // The app and its children get a minimal allowlisted environment, not the editor's.
        start.Environment.Clear();
        foreach (string name in InheritedEnvironment)
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } inheritedValue) start.Environment[name] = inheritedValue;
        if (!string.IsNullOrWhiteSpace(current.CredentialFile) && File.Exists(current.CredentialFile))
        {
            var credentials = CredentialEnvironment.Read(current.CredentialFile);
            var allowed = current.CredentialNames.ToHashSet(StringComparer.Ordinal);
            foreach (var (name, value) in credentials)
                if (allowed.Contains(name) && CredentialEnvironment.RefusalReason(name) is null) start.Environment[name] = value;
        }
        string pipe = ControlPipePrefix + Guid.NewGuid().ToString("N");
        string controlSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        // Launch settings are written after credentials so a credential file can never override them.
        start.Environment["DSH_HOME"] = home;
        start.Environment["DSH_TELEMETRY_DISABLED"] = "1";
        start.Environment["DSH_DESKTOP_OPEN_DEVTOOLS"] = "0";
        start.Environment["SOLFORGE_AI"] = "1";
        start.Environment["SOLFORGE_AI_RUNTIME_DIR"] = layout.RuntimeDirectory;
        start.Environment["SOLFORGE_AI_NODE"] = layout.Node;
        start.Environment["SOLFORGE_AI_PATCH"] = overlay;
        start.Environment["SOLFORGE_AI_USER_DATA"] = userData;
        start.Environment["SOLFORGE_AI_CONTROL_PIPE"] = @"\\.\pipe\" + pipe;
        start.Environment["SOLFORGE_AI_CONTROL_TOKEN"] = controlSecret;
        start.Environment["SOLFORGE_AI_DISABLE_OFFICE"] = "1";
        // Non-secret: which editor launched the app (its link prefers this editor when pairing) and the project open at launch.
        start.Environment["SOLFORGE_EDITOR_PID"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (project is not null) start.Environment["SOLFORGE_AI_PROJECT_DIR"] = project;
        if (current.SkipWelcome) start.Environment["SOLFORGE_AI_SKIP_WELCOME"] = "1";

        var owned = new Process { StartInfo = start, EnableRaisingEvents = true };
        owned.Exited += (_, _) => OnExited(owned);
        try
        {
            if (!owned.Start()) throw new InvalidOperationException("Solforge AI did not start.");
        }
        catch
        {
            owned.Dispose();
            LaunchOverlay.Delete(overlay);
            lock (sync) overlayPath = null;
            throw;
        }
        lock (sync) { process = owned; controlPipe = pipe; controlToken = controlSecret; }
        Project = project; OverlayInjected = true; StartedAt = DateTimeOffset.Now; LastExitCode = null;
        try { ProcessId = owned.Id; } catch (InvalidOperationException) { }
        LaunchOverlay.WriteOwner(overlay, owned);
        // Drain only: output may echo environment details, so it is never mirrored into editor logs.
        outputTask = Task.Run(async () =>
        {
            try { while (await owned.StandardOutput.ReadLineAsync().ConfigureAwait(false) is not null) { } }
            catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
        });
        errorTask = Task.Run(async () =>
        {
            try { while (await owned.StandardError.ReadLineAsync().ConfigureAwait(false) is not null) { } }
            catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
        });
    }

    private void OnExited(Process owned)
    {
        try { LastExitCode = owned.ExitCode; } catch (InvalidOperationException) { }
        bool current;
        lock (sync) current = ReferenceEquals(process, owned);
        if (!current) return;
        LaunchOverlay.Delete(overlayPath);
        if (State is not (SolforgeAiState.Stopping or SolforgeAiState.Launching))
        {
            State = SolforgeAiState.Stopped;
            Note = StartedAt is { } at && DateTimeOffset.Now - at < TimeSpan.FromSeconds(15)
                ? "Exited right after launch. If Solforge AI was already open for this profile (one instance per profile), that window was focused instead; otherwise check the build's solforge-ai and ai-runtime folders."
                : "Solforge AI exited" + (LastExitCode is { } code ? " (exit " + code + ")." : ".");
        }
        poll?.Cancel();
        Raise();
    }

    /** User Stop (force false) or quit-force (editor exit, project switch). Only the started process is ever stopped. */
    public async Task<SolforgeAiStopResult> StopAsync(bool force)
    {
        launch?.Cancel();
        if (launchTask is { IsCompleted: false } pending)
            try { await pending.ConfigureAwait(false); } catch (Exception) { }
        if (!IsRunning) { DisposeOwned(); if (State is SolforgeAiState.Running or SolforgeAiState.Stopping) State = SolforgeAiState.Stopped; Raise(); return SolforgeAiStopResult.Stopped; }
        State = SolforgeAiState.Stopping;
        Note = force ? null : "Waiting for Solforge AI to quit (it may be asking you to confirm in its window).";
        Raise();
        var result = SolforgeAiStopResult.Stopped;
        try { result = await StopOwnedAsync(force).ConfigureAwait(false); }
        finally
        {
            if (result != SolforgeAiStopResult.Stopped && IsRunning)
            {
                // The user's answer in Solforge AI wins: the process is kept and still owned by this editor.
                State = SolforgeAiState.Running;
                Note = result == SolforgeAiStopResult.Declined
                    ? "Stop cancelled in Solforge AI. It is still running."
                    : "Solforge AI is still asking to confirm quitting. Answer it there, or press Stop again.";
            }
            else
            {
                State = Layout is null ? SolforgeAiState.NotFound : SolforgeAiState.Stopped;
                Note = "Solforge AI stopped. Its sessions are kept.";
                Status = null;
            }
            Raise();
        }
        return result;
    }

    /** Synchronous quit-force for the editor's FormClosed (25 s grace, then the tree kill of the started process). */
    public void Shutdown()
    {
        try { StopAsync(force: true).Wait(TimeSpan.FromSeconds(30)); }
        catch (AggregateException) { }
        DisposeOwned();
    }

    private async Task<SolforgeAiStopResult> StopOwnedAsync(bool force)
    {
        Process? owned;
        lock (sync) owned = process;
        if (owned is null) return SolforgeAiStopResult.Stopped;
        try
        {
            if (!owned.HasExited)
            {
                var sent = await SendControlAsync(force ? "quit-force" : "quit").ConfigureAwait(false);
                if (!force && sent.Ok)
                {
                    var decision = await WaitForQuitDecisionAsync(owned).ConfigureAwait(false);
                    if (decision != SolforgeAiStopResult.Stopped) return decision;
                }
                await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25)).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException) { }
        DisposeOwned();
        return SolforgeAiStopResult.Stopped;
    }

    /**
     * After a non-force "quit": Stopped when the app exits, starts quitting, stops answering, or does not report
     * confirmingQuit (older build); Declined when it answers idle twice; StillConfirming after 10 minutes.
     */
    private async Task<SolforgeAiStopResult> WaitForQuitDecisionAsync(Process owned)
    {
        var started = DateTimeOffset.UtcNow;
        int idleReplies = 0;
        while (DateTimeOffset.UtcNow - started < TimeSpan.FromMinutes(10))
        {
            await Task.Delay(500).ConfigureAwait(false);
            if (owned.HasExited) return SolforgeAiStopResult.Stopped;
            var reply = await SendControlAsync("status").ConfigureAwait(false);
            if (reply is not { Ok: true, Body: { ValueKind: JsonValueKind.Object } body }) return SolforgeAiStopResult.Stopped;
            Status = AiAppStatus.From(body);
            if (body.TryGetProperty("quitting", out var quitting) && quitting.ValueKind == JsonValueKind.True) return SolforgeAiStopResult.Stopped;
            if (!body.TryGetProperty("confirmingQuit", out var confirming)) return SolforgeAiStopResult.Stopped;
            if (confirming.ValueKind == JsonValueKind.True) { idleReplies = 0; continue; }
            // "quit" is dispatched after the reply; two idle answers at least 1 s in mean the user cancelled.
            if (++idleReplies >= 2 && DateTimeOffset.UtcNow - started > TimeSpan.FromSeconds(1)) return SolforgeAiStopResult.Declined;
        }
        return SolforgeAiStopResult.StillConfirming;
    }

    /** Sends one control command to the started app. */
    public Task<ControlReply> SendControlAsync(string command, CancellationToken cancellationToken = default)
    {
        string? pipe, token;
        int? pid;
        lock (sync) { pipe = controlPipe; token = controlToken; pid = ProcessId; }
        if (pipe is null || token is null || pid is null || !IsRunning) return Task.FromResult(new ControlReply(false, null, "not running"));
        return ControlPipeClient.SendAsync(pipe, token, pid.Value, command, cancellationToken);
    }

    /** Polls "status" until the app answers or exits. True only when it answered. */
    private async Task<bool> WaitForControlAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var until = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < until && IsRunning)
        {
            var reply = await SendControlAsync("status", cancellationToken).ConfigureAwait(false);
            if (reply is { Ok: true, Body: { ValueKind: JsonValueKind.Object } body }) { Status = AiAppStatus.From(body); return true; }
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    /** One status refresh (Re-check). */
    private async Task RefreshStatusAsync()
    {
        var reply = await SendControlAsync("status").ConfigureAwait(false);
        if (reply is { Ok: true, Body: { ValueKind: JsonValueKind.Object } body })
        {
            Status = AiAppStatus.From(body);
            if (State == SolforgeAiState.Launching) State = SolforgeAiState.Running;
        }
        else Note = "Control channel: " + (reply.Error ?? "no answer");
        Raise();
    }

    /** status every 2 s while running, off the UI thread. */
    private void StartPolling()
    {
        poll?.Cancel();
        var stop = poll = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested && IsRunning)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stop.Token).ConfigureAwait(false);
                    if (State is SolforgeAiState.Stopping) continue;
                    var reply = await SendControlAsync("status", stop.Token).ConfigureAwait(false);
                    if (reply is { Ok: true, Body: { ValueKind: JsonValueKind.Object } body }) Status = AiAppStatus.From(body);
                    Raise();
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    /** Control-pipe focus first, then the owned process's main window, then the single-instance relaunch fallback. */
    private async Task<bool> FocusAsync()
    {
        Process? owned;
        lock (sync) owned = process;
        if (owned is null || owned.HasExited) return false;
        try { AllowSetForegroundWindow(owned.Id); } catch (InvalidOperationException) { }
        if ((await SendControlAsync("focus").ConfigureAwait(false)).Ok) return true;
        try
        {
            owned.Refresh();
            IntPtr window = owned.MainWindowHandle;
            if (window != IntPtr.Zero)
            {
                if (IsIconic(window)) ShowWindow(window, 9); // SW_RESTORE
                if (SetForegroundWindow(window)) return true;
            }
        }
        catch (InvalidOperationException) { }
        // README-runtime: the same exe with the same --user-data-dir hits the single-instance lock, which focuses the
        // running window; the second process exits by itself and is not kept.
        if (Layout is { } layout)
            try
            {
                var again = new ProcessStartInfo(layout.AppExecutable) { UseShellExecute = false, CreateNoWindow = true };
                again.ArgumentList.Add("--user-data-dir=" + Path.Combine(ProjectKey.AppRoot(Project), "electron"));
                again.Environment.Clear();
                foreach (string name in InheritedEnvironment)
                    if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value) again.Environment[name] = value;
                Process.Start(again)?.Dispose();
                return true;
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { }
        return false;
    }

    /** The settings copy the preflight reads (it never edits the editor's instance). */
    private static HarnessSettings WithProject(HarnessSettings settings) => settings.Clone();

    private void Raise()
    {
        if (StateChanged is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
            try { handler(); } catch (Exception) { }
    }

    /** Forgets the owned process; kills its tree only if it is still running (last resort after the grace period). */
    private void DisposeOwned()
    {
        Process? owned;
        string? overlay;
        lock (sync)
        {
            owned = process; process = null;
            overlay = overlayPath; overlayPath = null;
            controlToken = null; controlPipe = null;
        }
        poll?.Cancel();
        if (owned is not null)
        {
            try { if (!owned.HasExited) owned.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            var drains = Task.WhenAll(outputTask ?? Task.CompletedTask, errorTask ?? Task.CompletedTask);
            _ = drains.ContinueWith(_ => owned.Dispose(), TaskScheduler.Default);
        }
        LaunchOverlay.Delete(overlay);
        ProcessId = null;
    }

    public void Dispose()
    {
        launch?.Cancel();
        DisposeOwned();
        poll?.Dispose();
    }

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
}
