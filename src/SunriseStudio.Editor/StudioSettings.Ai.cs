/**
 * File: Solforge AI settings (UI-UE5 §4.1, §5.1-5.3): where the separate Solforge AI app is, the welcome-page switch, the
 * shared skills/MCP collection, the Workbench and the credential file. Drawn by StudioForm.SolforgeAiSettings.cs. The
 * credential file setting is a PATH only; its values are never stored here, shown or logged. Needs StudioSettings to be
 * partial (agent B's hook H1).
 */
namespace SunriseStudio.Editor;

public sealed partial class StudioSettings
{
    /** A folder holding solforge-ai\Solforge AI.exe, or the exe itself; blank: beside Solforge, then the newest build. */
    public string SolforgeAiPath { get; set; } = "";
    /** Builds searched for the newest complete solforge-ai; blank: the first solforge-builds folder above Solforge. */
    public string SolforgeBuildsRoot { get; set; } = "";
    /** Open Solforge AI's main window directly instead of its API-key welcome page (SOLFORGE_AI_SKIP_WELCOME=1). */
    public bool SolforgeAiSkipWelcome { get; set; }
    /** The consolidated skills and MCP collection (skills\, mcp\destiny-modder-mcp); blank disables it. */
    public string SharedResourcesRoot { get; set; } = Solforge.Harness.Shared.SharedHarnessResources.DefaultSharedResourcesRoot;
    /** The DestinyWorkbench root (config, memory, artifacts) destiny-modder works on. */
    public string WorkbenchRoot { get; set; } = Solforge.Harness.Shared.SharedHarnessResources.DefaultWorkbenchRoot;
    /** The credential file (flat JSON or KEY=value, at most 64 KB) for Solforge AI and API-model workflows; blank: none. */
    public string CredentialFile { get; set; } = "";
}
