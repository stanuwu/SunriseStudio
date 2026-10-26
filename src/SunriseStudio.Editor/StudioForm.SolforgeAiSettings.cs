/**
 * File: Settings > Solforge AI and Settings > Models & workflows (UI-UE5 §5.1-5.4, hook H4). Solforge AI: the app path and
 * builds root, Skip welcome, the resolved path and Re-check; the shared skills/MCP collection and Workbench with one
 * preflight line per check; the credential file with its validation (names and counts only, never values); the standalone
 * MCP files. Models & workflows: the model table, the models configured elsewhere (Settings > AI providers, Solforge AI
 * profiles, the subscription CLIs: ConfiguredModelDiscovery) to assign to a ladder, the role ladders (drag to reorder,
 * payload "SOLFORGE_MODEL"), the parallel and depth limits and the validator's errors. Opening or editing these pages never contacts a provider: every
 * check is file inspection. Both pages are blocked for MCP UI automation, like Settings > AI.
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

public sealed partial class StudioForm
{
    private string? solforgeAiPathEdit, solforgeBuildsEdit, sharedRootEdit, workbenchEdit, credentialEdit;
    private (string? Path, DateTime Stamp, CredentialFileReport Report)? credentialReport;
    private HarnessSettings? harnessEdit;
    private string? harnessStatus;
    /** Hash of harness-settings.json when harnessEdit was loaded: the compare token for saving (null: file absent). */
    private string? harnessEditHash;
    /** harnessEdit serialized right after loading; a different serialization means the page has unsaved edits. */
    private string? harnessEditBaseline;
    /** Watches harness-settings.json for the other writer (Solforge AI); created on first use. */
    private HarnessSettingsWatcher? harnessWatcher;
    /** The file changed under unsaved edits: the page asks "reload?". */
    private bool harnessExternalPending;
    /** The last save hit a conflict: the page offers Reload or Overwrite. */
    private bool harnessSaveConflict;
    private static readonly System.Text.Json.JsonSerializerOptions HarnessCompareJson = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
    private static readonly string[] TransportNames = Enum.GetNames<HarnessTransport>();
    /** Models configured elsewhere (null: not scanned yet) and the subscription CLIs found with them. */
    private IReadOnlyList<ConfiguredModelCandidate>? discoveredModels;
    private SubscriptionClis? discoveredClis;
    private string discoveredFilter = "";
    private static readonly string[] TierNames = Enum.GetNames<HarnessModelTier>();

    /** Hook H4: the "Solforge AI" Preferences tab. */
    public void DrawSolforgeAiSettings()
    {
        UiAuto.PushBlocked();
        try { DrawSolforgeAiSettingsRows(); }
        finally { UiAuto.PopBlocked(); }
    }

    private void DrawSolforgeAiSettingsRows()
    {
        EnsureHarnessWatch();
        var host = SolforgeAi;
        SolTheme.SectionHeader("SOLFORGE AI APP");
        bool changed = false;
        changed |= FolderSetting("Solforge AI folder or exe", ref solforgeAiPathEdit, settings.SolforgeAiPath, value => settings.SolforgeAiPath = value,
            "A folder holding solforge-ai\\Solforge AI.exe, or the exe itself. Blank: beside Solforge, then the newest build.", file: true);
        changed |= FolderSetting("Builds folder", ref solforgeBuildsEdit, settings.SolforgeBuildsRoot, value => settings.SolforgeBuildsRoot = value,
            "Searched for the newest complete build (folders starting with '.' or containing -staging- are skipped). Blank: "
            + AppDiscovery.DefaultBuildsRoot() + ".");
        bool skip = settings.SolforgeAiSkipWelcome;
        if (ImGui.Checkbox("Skip the welcome page", ref skip)) { settings.SolforgeAiSkipWelcome = skip; SaveSettings(); }
        SolforgeAiNote("Opens Solforge AI's main window directly instead of its API-key welcome page (SOLFORGE_AI_SKIP_WELCOME=1). Applies at the next launch.");
        if (changed) host.Recheck(SolforgeAiProject);
        if (host.Layout is { } layout) { CheckLine(true, "Resolved: " + layout.AppExecutable); SolforgeAiNote(host.DiscoveryDetail); }
        else CheckLine(false, host.DiscoveryDetail);
        if (UiAuto.Button("Re-check##solforge-ai-settings-recheck")) RecheckSolforgeAi();
        ImGui.SameLine();
        ImGui.TextColored(SolTheme.Muted, SolforgeAiStateText(host));

        ImGui.Spacing();
        SolTheme.SectionHeader("SHARED RESOURCES");
        bool shared = FolderSetting("Skills and MCP collection", ref sharedRootEdit, settings.SharedResourcesRoot, value => settings.SharedResourcesRoot = value,
            "The consolidated collection: skills\\<name>\\SKILL.md and mcp\\destiny-modder-mcp. Blank disables both.");
        shared |= FolderSetting("Workbench", ref workbenchEdit, settings.WorkbenchRoot, value => settings.WorkbenchRoot = value,
            "The DestinyWorkbench root (config, memory, artifacts) destiny-modder works on.");
        if (shared) host.Recheck(SolforgeAiProject);
        if (host.SharedResources is { } snapshot)
            foreach (var check in snapshot.Checks) CheckLine(check.Ok, check.Name + ": " + check.Text);
        else SolforgeAiNote("Not inspected yet: press Re-check.");
        if (UiAuto.Button("Re-check##shared-recheck")) RecheckSolforgeAi();
        SolforgeAiNote("File inspection only: nothing is started or installed. Node's version is read from node.exe's version resource.");

        ImGui.Spacing();
        SolTheme.SectionHeader("CREDENTIAL FILE");
        bool credentials = FolderSetting("Credential file", ref credentialEdit, settings.CredentialFile, value => settings.CredentialFile = value,
            "A flat JSON object or KEY=value lines, at most 64 KB. Names must contain KEY, TOKEN, SECRET or PASSWORD. Values are never shown, logged or shared with models.",
            file: true, filter: "Credential file|*.json;*.env;*.txt|All files|*.*");
        if (credentials) credentialReport = null;
        string? path = string.IsNullOrWhiteSpace(settings.CredentialFile) ? null : settings.CredentialFile;
        if (path is not null)
        {
            DateTime stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (credentialReport is not { } cached || cached.Path != path || cached.Stamp != stamp)
                credentialReport = (path, stamp, CredentialEnvironment.Inspect(path));
            var report = credentialReport.Value.Report;
            CheckLine(report.Readable && report.Refused.Count == 0, report.Summary);
            if (report.Readable && report.Accepted.Count > 0) SolforgeAiNote("Accepted names: " + string.Join(", ", report.Accepted));
        }
        else SolforgeAiNote("No credential file: Solforge AI starts with no provider keys.");
        CredentialGuard.CredentialFile = settings.CredentialFile;
        DrawSharedCredentialPath(path);

        ImGui.Spacing();
        SolTheme.SectionHeader("STANDALONE MCP");
        SolforgeAiNote("When the editor launches Solforge AI it always gets the Solforge MCP. A Solforge AI you start by itself only gets it "
            + "once it is registered here: Register adds a '" + StandaloneRegistration.ServerName + "' stdio entry (solforge-mcp --sol --upstreams "
            + "<per-user default>) to ~/.claude.json and lists that name in importClaudeServers of Solforge AI's standalone settings. Its "
            + "headless tools answer at once; the editor's tools attach while Solforge runs. Claude Code also sees the ~/.claude.json entry.");
        var registration = StandaloneRegistrationState();
        CheckLine(registration.Registered, registration.Summary);
        // The whole page is already blocked for MCP UI automation (DrawSolforgeAiSettings): only the user registers.
        if (SolTheme.PrimaryButton((registration.Registered ? "Register again" : "Register for standalone Solforge AI") + "##standalone-register"))
            ImGui.OpenPopup("Register standalone MCP##confirm");
        if (registration.ClaudeEntry || registration.Imported)
        {
            ImGui.SameLine();
            if (ImGui.Button("Unregister##standalone-unregister")) UnregisterStandaloneMcp();
        }
        ImGui.SameLine();
        if (ImGui.Button("Write standalone MCP files")) PublishStandaloneMcpFiles();
        DrawStandaloneRegisterConfirm();
        if (standaloneRegistrationMessage is { } message) SolforgeAiNote(message);
        SolforgeAiNote("Files: " + SolforgeMcpService.StandaloneStatus + " " + McpDiscovery.StandaloneDescriptorFile
            + " describes the same launch for clients that read it; Solforge AI itself does not. A Solforge AI that is already open loads the change at its next start.");
    }

    /** The confirmation for Register: it edits two files that belong to other apps. */
    private void DrawStandaloneRegisterConfirm()
    {
        bool open = true;
        if (!ImGui.BeginPopupModal("Register standalone MCP##confirm", ref open, ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 520 * scale);
        ImGui.TextWrapped("This changes two files outside Solforge:");
        ImGui.BulletText(StandaloneRegistration.ClaudeConfigFile() + ": adds mcpServers." + StandaloneRegistration.ServerName
            + " (every other key is kept; a one-time copy is saved as .claude.json.solforge.bak).");
        ImGui.BulletText(StandaloneRegistration.StandaloneSettingsFile() + ": adds the name to importClaudeServers (created with Solforge AI's defaults if missing).");
        ImGui.TextWrapped("No credential is written. Claude Code will also start this MCP in every project. Unregister removes both again.");
        ImGui.PopTextWrapPos();
        if (SolTheme.PrimaryButton("Register##standalone-confirm")) { RegisterStandaloneMcp(); ImGui.CloseCurrentPopup(); }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##standalone-cancel")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    /**
     * One path setting: a text field (saved when it loses focus or Enter), Browse and Clear. True when the value changed.
     * @param file pick a file (with filter) instead of a folder.
     */
    private bool FolderSetting(string label, ref string? edit, string? current, Action<string> apply, string note, bool file = false, string? filter = null)
    {
        current ??= "";
        edit ??= current;
        bool changed = false;
        ImGui.PushID(label);
        ImGui.TextDisabled(label);
        ImGui.SetNextItemWidth(Math.Max(120 * scale, ImGui.GetContentRegionAvail().X - 170 * scale));
        string value = edit;
        bool entered = ImGui.InputText("##value", ref value, 2048, ImGuiInputTextFlags.EnterReturnsTrue);
        edit = value;
        if ((entered || ImGui.IsItemDeactivatedAfterEdit()) && edit.Trim() != current) { apply(edit.Trim()); SaveSettings(); changed = true; }
        ImGui.SameLine();
        if (ImGui.Button("Browse..."))
        {
            string shown = label;
            Action<string> picked = chosen => { apply(chosen); SaveSettings(); ResetSolforgeAiEdits(); SolforgeAi.Recheck(SolforgeAiProject); };
            if (file) PickFile(filter ?? "All files|*.*", picked);
            else PickFolder(shown, picked);
        }
        ImGui.SameLine();
        if (ImGui.Button("Clear") && current.Length > 0) { apply(""); SaveSettings(); edit = ""; changed = true; }
        SolforgeAiNote(note);
        ImGui.PopID();
        return changed;
    }

    /** The edit buffers follow the settings again (after a picker wrote one). */
    private void ResetSolforgeAiEdits() { solforgeAiPathEdit = solforgeBuildsEdit = sharedRootEdit = workbenchEdit = credentialEdit = null; credentialReport = null; }

    /** A muted wrapped note under a setting. */
    private static void SolforgeAiNote(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, SolTheme.Muted);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    /** Hook H4: the "Models & workflows" Preferences tab. */
    public void DrawModelsWorkflowsSettings()
    {
        UiAuto.PushBlocked();
        try { DrawModelsWorkflowsRows(); }
        finally { UiAuto.PopBlocked(); }
    }

    private void DrawModelsWorkflowsRows()
    {
        EnsureHarnessWatch();
        if (harnessEdit is null) BeginHarnessEdit();
        var team = harnessEdit!;
        SolforgeAiNote("Your model team for start_model_workflow, the Workflows panel and Solforge AI's tiered models (Frontier = Planner, "
            + "Mid = Executor, Low = SmallFix). Each role tries its models top to bottom; only a recognised quota or credit failure moves "
            + "to the next one. API keys are referenced by variable NAME in the credential file (Settings > Solforge AI). Solforge AI "
            + "edits the same file. Nothing here calls a model.");
        if (harnessSettingsError is { } loadError) ImGui.TextColored(SolTheme.Bad, loadError);
        if (harnessExternalPending)
        {
            ImGui.TextColored(SolTheme.Warn, "Changed by Solforge AI — reload? Your unsaved edits on this page would be discarded.");
            if (SolTheme.PrimaryButton("Reload##harness-external-reload")) { BeginHarnessEdit(); harnessStatus = "Reloaded the file changed by Solforge AI."; }
            ImGui.SameLine();
            if (ImGui.Button("Keep my edits##harness-external-keep")) harnessExternalPending = false;
        }

        SolTheme.SectionHeader("MODELS");
        if (ImGui.BeginTable("models", 9, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 28 * scale);
            ImGui.TableSetupColumn("Name");
            ImGui.TableSetupColumn("Transport");
            ImGui.TableSetupColumn("Provider");
            ImGui.TableSetupColumn("Model id");
            ImGui.TableSetupColumn("Base URL");
            ImGui.TableSetupColumn("API key variable");
            ImGui.TableSetupColumn("Tier");
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 24 * scale);
            ImGui.TableHeadersRow();
            for (int i = 0; i < team.Models.Count; i++)
            {
                var model = team.Models[i];
                ImGui.PushID(i);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                bool enabled = model.Enabled;
                if (ImGui.Checkbox("##on", ref enabled)) model.Enabled = enabled;
                ImGui.TableNextColumn();
                string name = model.DisplayName;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##name", ref name, 128)) model.DisplayName = name;
                if (ProviderCatalogs.IsClineUsageBilled(model) && ImGui.IsItemHovered())
                    ImGui.SetTooltip("Cline usage-billed (not ClinePass): this model id is from Cline's pay-per-token catalog. Use a ClinePass model instead.");
                // Drag a model from here onto a role ladder.
                if (ImGui.BeginDragDropSource())
                {
                    draggedModel = model.Id;
                    ImGui.SetDragDropPayload("SOLFORGE_MODEL", IntPtr.Zero, 0);
                    ImGui.TextUnformatted(model.DisplayName.Length > 0 ? model.DisplayName : model.Id);
                    ImGui.EndDragDropSource();
                }
                ImGui.TableNextColumn();
                int transport = (int)model.Transport;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.Combo("##transport", ref transport, TransportNames, TransportNames.Length)) model.Transport = (HarnessTransport)transport;
                ImGui.TableNextColumn();
                string provider = model.Provider;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##provider", ref provider, 128)) model.Provider = provider;
                ImGui.TableNextColumn();
                string id = model.ModelId;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##model", ref id, 512)) model.ModelId = id;
                ImGui.TableNextColumn();
                string url = model.BaseUrl ?? "";
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##url", ref url, 2048)) model.BaseUrl = url.Length == 0 ? null : url;
                ImGui.TableNextColumn();
                string key = model.ApiKeyVariable ?? "";
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##key", ref key, 256)) model.ApiKeyVariable = key.Length == 0 ? null : key;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("The variable NAME in the credential file (for example DEEPSEEK_API_KEY), never the key itself.");
                ImGui.TableNextColumn();
                int tier = (int)model.Tier;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.Combo("##tier", ref tier, TierNames, TierNames.Length)) model.Tier = (HarnessModelTier)tier;
                ImGui.TableNextColumn();
                if (ImGui.SmallButton("x"))
                {
                    foreach (var route in team.Routes) route.ModelIds.RemoveAll(entry => entry == model.Id);
                    team.Models.RemoveAt(i);
                    ImGui.PopID();
                    break;
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove this model (and its places in the ladders).");
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        if (ImGui.Button("Add model"))
        {
            int next = team.Models.Count + 1;
            while (team.Models.Any(model => model.Id == "model-" + next)) next++;
            team.Models.Add(new HarnessModel { Id = "model-" + next, DisplayName = "Model " + next, Provider = "openai-compatible", ModelId = "default",
                Transport = HarnessTransport.OpenAiCompatible });
        }
        SolforgeAiNote("Ids are fixed once created. Subscription transports use the official client's own sign-in: Codex (codex exec), Claude Code "
            + "(claude -p) and Google AI Pro (the Gemini CLI's Google-account login, else the Antigravity agy CLI). No token is copied and no API key is used for them.");

        ImGui.Spacing();
        DrawDiscoveredModels(team);

        ImGui.Spacing();
        SolTheme.SectionHeader("ROLE LADDERS");
        SolforgeAiNote("Drag models from the table or within a ladder to reorder. The three Subagent roles serve bounded children and the parallel review.");
        foreach (var role in Enum.GetValues<HarnessRole>()) DrawRoleLadder(team, role);

        ImGui.Spacing();
        SolTheme.SectionHeader("LIMITS");
        int parallel = team.MaxParallelSubagents, depth = team.MaxSubagentDepth;
        ImGui.SetNextItemWidth(160 * scale);
        if (ImGui.SliderInt("Parallel subagents", ref parallel, 1, HarnessSettingsValidator.MaximumParallelSubagents)) team.MaxParallelSubagents = parallel;
        ImGui.SetNextItemWidth(160 * scale);
        if (ImGui.SliderInt("Subagent depth (0 disables)", ref depth, 0, HarnessSettingsValidator.MaximumSubagentDepth)) team.MaxSubagentDepth = depth;

        ImGui.Spacing();
        var errors = HarnessSettingsValidator.Validate(team);
        foreach (string error in errors) ImGui.TextColored(SolTheme.Bad, error);
        ImGui.BeginDisabled(errors.Count > 0);
        if (SolTheme.PrimaryButton("Save##harness-save")) SaveHarnessSettings(team);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Revert##harness-revert")) { BeginHarnessEdit(); harnessStatus = "Reverted to the saved file."; }
        if (harnessStatus is { } status) { ImGui.SameLine(); ImGui.TextColored(SolTheme.Muted, status); }
        if (harnessSaveConflict)
        {
            ImGui.TextColored(SolTheme.Warn, "harness-settings.json was changed by Solforge AI after this page loaded. Reload it (your edits "
                + "here are discarded), or overwrite Solforge AI's change with this page.");
            if (SolTheme.PrimaryButton("Reload##harness-conflict-reload")) { BeginHarnessEdit(); harnessStatus = "Reloaded."; }
            ImGui.SameLine();
            if (ImGui.Button("Overwrite##harness-conflict-overwrite")) SaveHarnessSettings(team, overwrite: true);
        }
        if (CredentialPathDiffers(team) is { } other)
        {
            SolforgeAiNote("Solforge AI reads its provider keys from " + other + " (CredentialFilePath in this file); the editor's credential "
                + "file (Settings > Solforge AI) is " + (string.IsNullOrWhiteSpace(settings.CredentialFile) ? "not set" : settings.CredentialFile)
                + ". Saving keeps Solforge AI's path.");
            if (ImGui.Button("Use the editor's credential file for Solforge AI##harness-credential-adopt"))
            {
                team.CredentialFilePath = string.IsNullOrWhiteSpace(settings.CredentialFile) ? null : settings.CredentialFile;
                SaveHarnessSettings(team, adoptCredential: true);
            }
        }
    }

    /** Tier ladders a discovered model can be assigned to from the list (the Advanced roles use the ladder editors). */
    private static readonly (HarnessRole Role, string Label, HarnessModelTier Tier)[] DiscoveredTargets =
    [
        (HarnessRole.Planner, "Frontier", HarnessModelTier.Frontier), (HarnessRole.Executor, "Mid", HarnessModelTier.Executor),
        (HarnessRole.SmallFix, "Low", HarnessModelTier.Small)
    ];

    /**
     * YOUR CONFIGURED MODELS: what the user configured in Settings > AI providers (AiProviders), in Solforge AI profiles and
     * the subscription CLIs on this machine, with buttons that add a model to the table and to a tier ladder in this page's
     * edit buffer (Save writes it). File inspection only; AiProviders and the profile files are never changed.
     */
    private void DrawDiscoveredModels(HarnessSettings team)
    {
        SolTheme.SectionHeader("YOUR CONFIGURED MODELS");
        if (discoveredModels is null) RescanConfiguredModels();
        var all = discoveredModels ?? [];
        var clis = discoveredClis;
        SolforgeAiNote("Found in Settings > AI providers, your Solforge AI profiles and the subscription CLIs on this machine. Adding copies the "
            + "model id, endpoint and key NAME into this page (Save writes it); nothing else is changed and no key is read here. "
            + "Model workflows read the key a model names from your credential file (Settings > AI) in memory per request; "
            + "keys saved inside old Solforge AI profiles are not read.");
        if (clis is not null)
            SolforgeAiNote("Subscription CLIs: claude " + (clis.Claude is null ? "not installed" : "found") + ", codex " + (clis.Codex is null ? "not installed" : "found")
                + ", Google " + (clis.Google is { } google ? google.Cli + " found" : "not installed (gemini or agy)") + ".");
        var tiers = new HashSet<string>(DiscoveredTargets.SelectMany(target => team.Route(target.Role).ModelIds), StringComparer.Ordinal);
        var open = all.Where(candidate => team.Models.FirstOrDefault(candidate.Matches) is not { } existing || !tiers.Contains(existing.Id)).ToList();
        if (UiAuto.Button("Re-scan##discovered-rescan")) RescanConfiguredModels();
        ImGui.SameLine();
        ImGui.TextColored(SolTheme.Muted, $"{open.Count} not in a tier ladder, {all.Count} found");
        if (open.Count > 20)
        {
            ImGui.SetNextItemWidth(Math.Min(320 * scale, ImGui.GetContentRegionAvail().X));
            ImGui.InputTextWithHint("##discovered-filter", "Filter by name, provider or source", ref discoveredFilter, 200);
        }
        string needle = discoveredFilter.Trim();
        var shown = (needle.Length == 0 ? open : open.Where(candidate => (candidate.DisplayName + " " + candidate.ModelId + " " + candidate.Provider + " "
            + candidate.SourceLabel + " " + candidate.Transport).Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList()).ToList();
        if (all.Count == 0) { SolforgeAiNote("No configured models were found outside this page."); return; }
        if (!ImGui.BeginTable("discovered", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupColumn("Model");
        ImGui.TableSetupColumn("Kind");
        ImGui.TableSetupColumn("Source");
        ImGui.TableSetupColumn("Add to", ImGuiTableColumnFlags.WidthFixed, 230 * scale);
        ImGui.TableHeadersRow();
        foreach (var candidate in shown.Take(60))
        {
            ImGui.PushID(candidate.Key);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(candidate.DisplayName);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(candidate.ModelId + " - " + candidate.Note);
            ImGui.TableNextColumn();
            ImGui.TextColored(SolTheme.Muted, candidate.Unavailable ? "Not offered" : candidate.Transport switch
            {
                HarnessTransport.ClaudeSubscription => "Claude subscription",
                HarnessTransport.CodexSubscription => "Codex subscription",
                HarnessTransport.GoogleSubscription => "Google AI Pro subscription",
                _ => "API (" + candidate.ApiKeyVariable + ")"
            });
            ImGui.TableNextColumn();
            ImGui.TextColored(SolTheme.Muted, candidate.SourceLabel);
            ImGui.TableNextColumn();
            if (candidate.Unavailable)
            {
                // Listed so every configured entry is visible; it has no tier route (no paid fallback).
                ImGui.TextColored(SolTheme.Muted, candidate.Note);
                ImGui.PopID();
                continue;
            }
            foreach (var target in DiscoveredTargets)
            {
                if (ImGui.SmallButton(target.Label)) AddDiscoveredModel(team, candidate, target.Role, target.Tier);
                ImGui.SameLine();
            }
            if (ImGui.SmallButton("List only")) AddDiscoveredModel(team, candidate, null, HarnessModelTier.Executor);
            ImGui.PopID();
        }
        ImGui.EndTable();
        if (shown.Count > 60) SolforgeAiNote($"{shown.Count - 60} more: type in the filter to find them.");
    }

    /** Scans the configured-model sources again (file inspection only). */
    private void RescanConfiguredModels()
    {
        var choices = (settings.AiProviders ?? []).Where(entry => entry.Value is not null)
            .Select(entry => new EngineProviderChoice(entry.Key, entry.Value.Provider ?? "", entry.Value.Model ?? "", entry.Value.BaseUrl ?? "")).ToList();
        try
        {
            discoveredModels = ConfiguredModelDiscovery.Discover(choices);
            discoveredClis = SubscriptionClis.Find();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            discoveredModels = [];
            harnessStatus = "Could not scan configured models: " + error.Message;
        }
    }

    /** Adds a discovered model to the table (once) and to a ladder; the page's Save writes it. */
    private void AddDiscoveredModel(HarnessSettings team, ConfiguredModelCandidate candidate, HarnessRole? role, HarnessModelTier tier)
    {
        if (candidate.Unavailable) { harnessStatus = candidate.DisplayName + ": " + candidate.Note; return; }
        var model = team.Models.FirstOrDefault(candidate.Matches);
        if (model is null)
        {
            model = candidate.ToModel(team.Models.Select(existing => existing.Id).ToHashSet(StringComparer.Ordinal), tier);
            team.Models.Add(model);
        }
        if (role is { } target && !team.Route(target).ModelIds.Contains(model.Id)) team.Route(target).ModelIds.Add(model.Id);
        harnessStatus = "Added " + model.DisplayName + (role is { } added ? " to " + TierRoleLabel(added) : "") + ". Save to keep it.";
    }

    /** Load the file into a fresh edit buffer and remember its hash and serialization (for compare and dirtiness). */
    private void BeginHarnessEdit()
    {
        harnessEdit = (LoadHarnessSettings(reload: true) ?? new HarnessSettings()).Clone();
        foreach (var role in Enum.GetValues<HarnessRole>()) harnessEdit.Route(role);
        try { harnessEditHash = HarnessSettingsStore.FileHash(HarnessSettingsStore.DefaultPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { harnessEditHash = null; }
        harnessEditBaseline = SerializeHarness(harnessEdit);
        harnessExternalPending = false;
        harnessSaveConflict = false;
    }

    private static string SerializeHarness(HarnessSettings team) => System.Text.Json.JsonSerializer.Serialize(team, HarnessCompareJson);

    /** Whether the page holds edits that are not in the file it loaded. */
    private bool HarnessEditDirty => harnessEdit is not null && SerializeHarness(harnessEdit) != harnessEditBaseline;

    /**
     * Start watching harness-settings.json (idempotent). Solforge AI writes the same file; a change the editor did not make
     * reloads the cached settings, and reloads this page too unless it has unsaved edits (then the page asks). P4: call this
     * from SolforgeAiStartup so the cache follows the file even when the settings page is never opened.
     */
    public void EnsureHarnessWatch()
    {
        if (harnessWatcher is not null) return;
        try
        {
            harnessWatcher = new HarnessSettingsWatcher(HarnessSettingsStore.DefaultPath);
            harnessWatcher.Changed += () =>
            {
                try { if (IsHandleCreated) BeginInvoke((Action)OnHarnessFileChanged); }
                catch (InvalidOperationException) { }
            };
            FormClosed += (_, _) => { harnessWatcher?.Dispose(); harnessWatcher = null; };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            harnessStatus = "Not watching harness-settings.json: " + error.Message;
        }
    }

    /** UI thread: the file changed on disk. */
    private void OnHarnessFileChanged()
    {
        string? hash;
        try { hash = HarnessSettingsStore.FileHash(HarnessSettingsStore.DefaultPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        LoadHarnessSettings(reload: true);
        if (harnessEdit is null || hash == harnessEditHash) { Invalidate(); return; }
        if (HarnessEditDirty) harnessExternalPending = true;
        else { BeginHarnessEdit(); harnessStatus = "Reloaded: changed by Solforge AI."; }
        Invalidate();
    }

    /**
     * Solforge AI's credential path when it is set and differs from the editor's credential file, else null. The editor fills
     * an empty CredentialFilePath with its own file on save; it never replaces a different value without the user's click.
     */
    private string? CredentialPathDiffers(HarnessSettings team)
    {
        string? mine = string.IsNullOrWhiteSpace(settings.CredentialFile) ? null : settings.CredentialFile;
        string? shared = string.IsNullOrWhiteSpace(team.CredentialFilePath) ? null : team.CredentialFilePath;
        return shared is not null && !SameCredentialPath(shared, mine) ? shared : null;
    }

    /** "Planner" -> "Frontier (Planner)" etc.: the tier names Solforge AI shows for the three tier ladders. */
    private static string TierRoleLabel(HarnessRole role) => role switch
    {
        HarnessRole.Planner => "Frontier (Planner)",
        HarnessRole.Executor => "Mid (Executor)",
        HarnessRole.SmallFix => "Low (SmallFix)",
        _ => RoleLabel(role)
    };

    private string? draggedModel;

    /** One role's ordered ladder with drag and drop (payload SOLFORGE_MODEL), remove and add. */
    private void DrawRoleLadder(HarnessSettings team, HarnessRole role)
    {
        var route = team.Route(role);
        ImGui.PushID((int)role);
        ImGui.TextUnformatted(TierRoleLabel(role));
        ImGui.Indent(12 * scale);
        if (route.ModelIds.Count == 0) ImGui.TextColored(SolTheme.Muted, "(no models: this role pauses a run)");
        int? dropAt = null;
        for (int i = 0; i < route.ModelIds.Count; i++)
        {
            string id = route.ModelIds[i];
            var model = team.Models.FirstOrDefault(item => item.Id == id);
            ImGui.PushID(i);
            string label = $"{i + 1}. {model?.DisplayName ?? id}" + (model is { Enabled: false } ? " (disabled)" : "") + (model is null ? " (missing)" : "")
                + (model is not null && ProviderCatalogs.IsClineUsageBilled(model) ? " [Cline usage-billed (not ClinePass)]" : "");
            ImGui.Selectable(label, false, ImGuiSelectableFlags.None, new Vector2(Math.Max(80 * scale, ImGui.GetContentRegionAvail().X - 30 * scale), 0));
            if (ImGui.BeginDragDropSource())
            {
                draggedModel = id;
                ImGui.SetDragDropPayload("SOLFORGE_MODEL", IntPtr.Zero, 0);
                ImGui.TextUnformatted(label);
                ImGui.EndDragDropSource();
            }
            if (ImGui.BeginDragDropTarget())
            {
                if (Accepted(ImGui.AcceptDragDropPayload("SOLFORGE_MODEL"))) dropAt = i;
                ImGui.EndDragDropTarget();
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("x")) { route.ModelIds.RemoveAt(i); ImGui.PopID(); break; }
            ImGui.PopID();
        }
        // Dropping on the role's add row appends.
        string[] choices = team.Models.Where(model => !route.ModelIds.Contains(model.Id)).Select(model => model.Id).ToArray();
        ImGui.SetNextItemWidth(Math.Min(220 * scale, ImGui.GetContentRegionAvail().X));
        if (ImGui.BeginCombo("##add", "+ add a model"))
        {
            foreach (string id in choices)
                if (ImGui.Selectable(team.Models.First(model => model.Id == id).DisplayName + "##" + id)) route.ModelIds.Add(id);
            ImGui.EndCombo();
        }
        if (ImGui.BeginDragDropTarget())
        {
            if (Accepted(ImGui.AcceptDragDropPayload("SOLFORGE_MODEL"))) dropAt = route.ModelIds.Count;
            ImGui.EndDragDropTarget();
        }
        if (dropAt is { } at && draggedModel is { } dragged)
        {
            // Moving within this ladder, from another ladder (copied there) or from the table.
            int existing = route.ModelIds.IndexOf(dragged);
            if (existing >= 0)
            {
                route.ModelIds.RemoveAt(existing);
                if (existing < at) at--;
            }
            route.ModelIds.Insert(Math.Clamp(at, 0, route.ModelIds.Count), dragged);
            draggedModel = null;
        }
        ImGui.Unindent(12 * scale);
        ImGui.PopID();
    }

    private static unsafe bool Accepted(ImGuiPayloadPtr payload) => payload.NativePtr != null;

    private string? sharedCredentialStatus;

    /**
     * The credential path Solforge AI reads (CredentialFilePath in harness-settings.json) next to the editor's own. Share
     * writes only that field (read-modify-write, compare-before-replace) and asks first when the file holds a different path.
     */
    private void DrawSharedCredentialPath(string? editorPath)
    {
        var shared = LoadHarnessSettings()?.CredentialFilePath;
        bool same = SameCredentialPath(string.IsNullOrWhiteSpace(shared) ? null : shared, editorPath);
        if (same && editorPath is not null) CheckLine(true, "Solforge AI uses the same credential file (harness-settings.json).");
        else if (editorPath is not null)
        {
            SolforgeAiNote(string.IsNullOrWhiteSpace(shared)
                ? "Solforge AI has no credential file yet. Share this one so a Solforge AI started by itself finds the keys."
                : "Solforge AI reads its keys from " + shared + ".");
            if (ImGui.Button("Share with Solforge AI##credential-share"))
            {
                if (string.IsNullOrWhiteSpace(shared)) ShareCredentialPathWithApp(editorPath);
                else ImGui.OpenPopup("Replace Solforge AI's credential file?##credential-share-confirm");
            }
        }
        bool open = true;
        if (ImGui.BeginPopupModal("Replace Solforge AI's credential file?##credential-share-confirm", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted("Solforge AI currently reads " + shared + ".");
            ImGui.TextUnformatted("Use " + editorPath + " instead? Only the path is written; no key is read or copied.");
            if (SolTheme.PrimaryButton("Replace##credential-share-yes") && editorPath is not null) { ShareCredentialPathWithApp(editorPath); ImGui.CloseCurrentPopup(); }
            ImGui.SameLine();
            if (ImGui.Button("Cancel##credential-share-no")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        if (sharedCredentialStatus is { } status) SolforgeAiNote(status);
    }

    /** Write CredentialFilePath alone: latest file, one field changed, replaced only if unchanged since that read. */
    private void ShareCredentialPathWithApp(string path)
    {
        try
        {
            var store = new HarnessSettingsStore();
            string file = HarnessSettingsStore.DefaultPath;
            var (current, hash) = Task.Run(() => store.LoadWithHashAsync(file)).GetAwaiter().GetResult();
            current.CredentialFilePath = path;
            Task.Run(() => store.SaveAsync(file, current, hash)).GetAwaiter().GetResult();
            LoadHarnessSettings(reload: true);
            if (harnessEdit is not null && !HarnessEditDirty) BeginHarnessEdit();
            sharedCredentialStatus = "Shared. Solforge AI uses it from its next AI host start.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HarnessSettingsValidationException
            or InvalidDataException or System.Text.Json.JsonException)
        {
            sharedCredentialStatus = "Not shared: " + error.Message;
        }
    }

    /** Case-insensitive full-path equality (both null counts as equal). */
    private static bool SameCredentialPath(string? a, string? b)
    {
        if (a is null || b is null) return a is null && b is null;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /**
     * Validates and writes harness-settings.json atomically; running workflows keep their snapshot. The replace happens only
     * while the file still has the hash this page loaded (Solforge AI writes the same file), unless the user chose Overwrite.
     * CredentialFilePath takes the editor's credential file when the file's value is empty or already equal, so a standalone
     * Solforge AI finds the keys; a different value is kept unless the user adopted the editor's file explicitly.
     */
    private void SaveHarnessSettings(HarnessSettings team, bool overwrite = false, bool adoptCredential = false)
    {
        try
        {
            var saved = team.Clone();
            string? mine = string.IsNullOrWhiteSpace(settings.CredentialFile) ? null : settings.CredentialFile;
            if (!adoptCredential && mine is not null && (string.IsNullOrWhiteSpace(saved.CredentialFilePath) || SameCredentialPath(saved.CredentialFilePath, mine)))
                saved.CredentialFilePath = mine;
            var store = new HarnessSettingsStore();
            string path = HarnessSettingsStore.DefaultPath;
            string? expected = harnessEditHash;
            if (overwrite) Task.Run(() => store.SaveAsync(path, saved)).GetAwaiter().GetResult();
            else Task.Run(() => store.SaveAsync(path, saved, expected)).GetAwaiter().GetResult();
            harnessSettings = saved.Clone();
            harnessSettingsError = null;
            harnessEdit = saved.Clone();
            foreach (var role in Enum.GetValues<HarnessRole>()) harnessEdit.Route(role);
            harnessEditHash = HarnessSettingsStore.FileHash(path);
            harnessEditBaseline = SerializeHarness(harnessEdit);
            harnessExternalPending = false;
            harnessSaveConflict = false;
            harnessStatus = "Saved. New workflows and Solforge AI's tiers use it (Solforge AI loads new models and keys when its AI host restarts).";
        }
        catch (HarnessSettingsConflictException)
        {
            harnessSaveConflict = true;
            harnessStatus = "Not saved: changed by Solforge AI.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HarnessSettingsValidationException or InvalidDataException)
        {
            harnessStatus = "Not saved: " + error.Message;
        }
    }
}
