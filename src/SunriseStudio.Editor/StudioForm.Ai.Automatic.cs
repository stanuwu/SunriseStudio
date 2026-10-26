/**
 * File: what is left of /automatic in the editor (docs/UI-UE5.md §4.3). Automatic nudges typed their prompts into the
 * in-editor AI console, which is removed, and the Solforge AI control pipe has no "send prompt" command, so the
 * feature went with the console. The MCP tools in Solforge.Mcp/SolforgeTools.Automatic.cs stay registered (the tool
 * surface does not change): with no watchdog the host side refuses them, and get_editor_state reports the state as
 * off with the reason. Model workflows (start_model_workflow) are the replacement. The saved limits stay in the
 * settings file so an old file loads unchanged.
 */
using System.Text.Json.Nodes;
using ImGuiNET;
using Solforge.Mcp;

namespace SunriseStudio.Editor;

/** StudioForm part: the removed /automatic mode's remaining glue (see file header). */
public sealed partial class StudioForm
{
    /** Why automatic mode is unavailable; shown in Settings and returned in get_editor_state. */
    private const string AutomaticRemoved = SolforgeTools.AutomaticUnavailable;

    /** Settings > AI: automatic mode is gone with the console; the note says what replaces it. */
    private void DrawAiAutomaticSettings()
    {
        ImGui.TextDisabled("Automatic mode (/automatic <goal>)");
        SettingNote(AutomaticRemoved);
    }

    /** get_editor_state and the remote AI state: automatic mode is always off now, with the reason. */
    private JsonObject AiAutomaticJson() => new() { ["state"] = "off", ["note"] = AutomaticRemoved };

    private sealed partial class AiHost
    {
        /**
         * ISolforgeHost: no /automatic watchdog. The automatic_* and goal_* tools stay listed and answer that this
         * Solforge has no automatic mode (their wording lives in Solforge.Mcp); get_editor_state carries AutomaticRemoved.
         */
        public AutomaticWatchdog? Automatic => null;
    }
}
