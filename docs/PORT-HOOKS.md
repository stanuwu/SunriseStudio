# Port hooks: what agent B must call or wire (agent C, UI-UE5 §4.1, §4.4, §5, §6)

Status (2026-10-24): source only. Nothing below has been launched, clicked or looked at. "Compiles" means: the new and
changed projects build on their own (`Solforge.Harness`, `Solforge.Harness.Runtime`, `Solforge.AiLauncher`, `Solforge.Mcp`,
`Solforge.Mcp.Shim`, and the MCP/retail/local-server/provenance check projects, built but not run), and
`SunriseStudio.Editor` compiles in a scratch copy where `StudioSettings` is declared `partial`. In the real tree the editor
does **not** compile until H1 lands (CS0260, `StudioSettings.Ai.cs` is a partial part). A trial `dotnet publish` of the shim
with publish.ps1's trimming flags (into a scratch folder) succeeded; the exe was not run.

## 0. Already done by C: do not add these again

C implements these members in its own partial parts of the nested `AiHost` class. Adding them in
`StudioForm.Ai.Host.cs` too is a duplicate-member compile error.

| `ISolforgeHost` member | Where (C) | What |
| --- | --- | --- |
| `ApproveEditAsync(summary, path, before, after, expiresIn, cancel)` | `StudioForm.DiffReview.cs` | Queues the side-by-side diff review (`QueueDiffReview`), never auto-approved. |
| `EditorScreenshotAsync(cancel)` | `StudioForm.DiffReview.cs` | Uncropped back buffer through `renderer.CapturePng` on the next `FrameRendered`. |
| `CredentialDocumentVisible` | `StudioForm.DiffReview.cs` | True when the Graph panel's mission script or node graph file is the credential file. |
| `Workflows` | `StudioForm.Workflows.cs` | The `IModelWorkflows` bridge (start always opens the native confirm). |

These parts use `Owner` (the `private StudioForm Owner => form;` property in B's part of `AiHost`). Keep it.

## 1. Partial methods B implements

Both are declared (without a body) in `StudioForm.SolforgeAi.cs`. Implement them as `partial void` in a B file; if B
does not, the calls compile away.

```csharp
// H16: the Output Log / AI LOG line list. Called from ANY thread (launcher and workflow callbacks): lock or queue.
partial void AppendAiLog(string text) { /* add a timestamped "MCP"/"Solforge AI" line to the Output Log */ }

// The pill's NotFound click and the chevron's "Settings...": open Preferences on the "Solforge AI" tab.
partial void OpenSolforgeAiSettings() { /* show Preferences, select the Solforge AI tab */ }
```

## 2. The hooks

| # | B's file | Exactly what to call |
| --- | --- | --- |
| H1 | `StudioSettings.cs` | `public sealed partial class StudioSettings` (first edit). |
| H4 | `StudioForm.Preferences.cs` | New tab "Solforge AI": `DrawSolforgeAiSettings();`. New tab "Models & workflows": `DrawModelsWorkflowsSettings();`. Both draw their own `UiAuto.PushBlocked/PopBlocked`. In `DrawAiSettingsRows` remove only the console rows (consoles text, Pi harness path, Resume); keep the MCP switch, auto-approve and the automatic settings. |
| H5 | `StudioForm.Toolbar.cs` | See "H5 in detail" below. |
| H6 | `StudioForm.SolforgeAiPanel.cs` | `DrawSolforgeAiStatusCard();` first thing in the AI panel, then Requests, Edits, Presence, Context. |
| H7 | `StudioLayout.cs`, `StudioForm.Layout.cs` | Append `Workflows` to `StudioPanelKind` (after `Place, Output`); in `DrawPanelArea`: `case StudioPanelKind.Workflows: DrawWorkflowStatusPanel(); break;`. Title "Workflows"; add it to Pickable / Window > Panels; not in the default layout. |
| H8 | `StudioForm.cs` `OnShown` | `if (settings.AiMcpEnabled) EnsureAiService();` unconditionally (no project or AI panel needed), then `SolforgeAiStartup();` (arms the credential guard, first discovery, writes the standalone MCP files; launches nothing). |
| H9 | `StudioForm.Ai.Host.cs` | (a) `ApproveAsync`: remove `form.OpenAiWorkspace()`; raise the toolbar badge (it reads `aiRequests.Count` through `SolforgeAiButtonState.PendingApprovals`), show the approval toast, dock the AI panel as a tab if missing (UI-UE5 §4.2). (b) `BuildAiContext` and `BuildAiOpenDocuments`: when `CredentialGuard.IsCredentialPath(missionGraph.ScriptPath)` (or `nodeGraph.FilePath`) is true, leave that path and its contents out. (c) `ScreenshotAsync`: `if (form.CredentialDocumentShown()) return null;` before capturing (the tools already refuse through `CredentialDocumentVisible`; this covers the UI path). Do **not** add ApproveEditAsync / EditorScreenshotAsync (section 0). Optional: in `ApproveAsync`, ignore auto-approve when `McpSessionRegistry.Current?.IsWorker == true`. |
| H10 | `StudioForm.cs` `DrawEditor`, after the layout and next to `DrawDialogs` | `DrawDiffReviewModal(); DrawStartWorkflowDialog();` every frame (each returns at once when nothing waits). |
| H11 | `StudioForm.Ai.Host.cs` `StartAiForProject` | When the open project changes (not every frame): `SolforgeAiProjectChanged(workspace.Project?.DirectoryPath);` (C quits a Solforge AI started for another project with quit-force and stops the old project's workflows). Also call it with `null` when the project closes. `AiWriteSession(project)` runs here on project open (B). |
| H12 | `StudioForm.cs` `FormClosed` | `ShutdownSolforgeAi();` **before** `StopAiService()`. Blocks at most about 30 s (quit-force, 25 s wait, then a tree kill of the process C started, never any other). |
| H16 | `StudioForm.OutputLog.cs` | Implement `partial void AppendAiLog(string text)` (section 1); show those lines under the "MCP" filter. |

### H5 in detail (the Solforge AI pill)

```csharp
var pill = SolforgeAiButtonState;            // read once per frame (it also keeps the badge count and credential guard current)
// pill.State: NotFound | Stopped | Launching | Running | Stopping | Failed (Solforge.AiLauncher.SolforgeAiState)
// dot: NotFound Dim hollow; Stopped Muted; Launching Warn pulsing; Running Warn (backend starting) or Ok (pill.BackendReady);
//      Stopping Warn pulsing; Failed Bad.  pill.Pulsing is true for Launching and Stopping.
// tooltip: pill.Tooltip (already the §4.1 text; Failed shows the curated last error, never environment or credential text).
UiAuto.PushBlocked();                        // MCP UI automation must never launch or stop a GUI app
if (main clicked && pill.Enabled) LaunchOrFocusSolforgeAi();   // NotFound opens Settings (OpenSolforgeAiSettings)
// chevron menu: "Launch"/"Focus" -> LaunchOrFocusSolforgeAi(); "Stop" (and "Cancel" while Launching) -> StopSolforgeAi();
//               "Open Solforge AI panel" -> B's panel focus; "Re-check" -> RecheckSolforgeAi(); "Settings..." -> OpenSolforgeAiSettings()
UiAuto.PopBlocked();
// badge: pill.PendingApprovals > 0 -> Warn count badge; its click opens the AI Requests tab (B).
```

Ctrl+Shift+A (`OpenAiWorkspace`, same id and binding) now calls `LaunchOrFocusSolforgeAi()`. The MCP commands
`window.ai_workspace` / `show_panel ai_workspace` only show the AI panel and never call any of these.

## 3. Editor members C relies on (keep names and signatures)

`settings`, `SaveSettings()`, `workspace`, `scale`, `gui` (`MonospaceFont`), `renderer` (`CapturePng`), `FrameRendered`,
`missionGraph.ScriptPath`, `nodeGraph.FilePath`, `aiService`, `aiHost`, `aiPaused`, `aiRequests` (+ `AiRequest`),
`RecordAiRequest(tool, summary, AiRequestOutcome)`, `EnsureAiService()`, `PrepareAiEngine(folder)`, `AiWriteSession(folder)`,
`AiServiceStatus(ref string)`, `DrawAiMcpWarning()`, `OnUi<T>(Func<T>)`, `PickFile(filter, action)`, `PickFolder(title, action)`.
From agent A: `SolTheme.Warn/Ok/Bad/Muted/Dim/Text`, `SolTheme.SectionHeader(text, tooltip)`, `SolTheme.PrimaryButton`,
`SolTheme.Alpha`, `SolTheme.U32`, `UiMotion.Dot(draw, center, radius, id, color, pulsing, hollow)`, `UiMotion.SmoothScroll()`.

## 4. Public surface C provides (for B's code)

| Member (StudioForm) | Use |
| --- | --- |
| `SolforgeAiButton SolforgeAiButtonState` | H5 pill state (State, Label, Tooltip, Enabled, Pulsing, BackendReady, PendingApprovals). |
| `void LaunchOrFocusSolforgeAi()` / `void StopSolforgeAi()` / `void RecheckSolforgeAi()` | H5 and the status card. |
| `void DrawSolforgeAiStatusCard()` | H6. |
| `void SolforgeAiStartup()` | H8. |
| `void SolforgeAiProjectChanged(string? newProject)` | H11. |
| `void ShutdownSolforgeAi()` | H12. |
| `void DrawSolforgeAiSettings()` / `void DrawModelsWorkflowsSettings()` | H4. |
| `void DrawWorkflowStatusPanel()` | H7. |
| `void DrawDiffReviewModal()` / `void DrawStartWorkflowDialog()` | H10. |
| `bool CredentialDocumentShown()` (private) | H9 (c). |

## 5. Things B should know

- **Tool list changed.** New tools: `get_recent_activity` (alias of `get_attention_log`), `get_editor_screenshot`,
  `start_model_workflow`, `get_model_workflow`, `cancel_model_workflow`. `apply_project_edit` gained `expected_sha256`.
  `tests/SunriseStudio.Mcp.Tests` (`StdioRoundTrip`) compares the exact tool-name sequence, so that check will fail until
  its list is updated; tests may not be edited under this task, so it was left as is.
- `--project` with no editor running now serves headless instead of answering "Solforge is not running...", so the AI
  panel's MCP probe (`CheckAiMcp`) passes without an editor too.
- `RequiresEditor` is metadata, not a new `McpTool` field (`McpProtocol.cs` is outside C's files): `SolforgeTools.RequiresEditor(tool)`,
  `SolforgeTools.HeadlessTools`, and `_meta["solforge/requiresEditor"]` in `tools-cache.json` and the shim's `tools/list`.
- Not done: feeding the destiny-modder definition into `SolMcpServers.Collect` (UI-UE5 §5.2 "optional"); Solforge AI already
  gets destiny-modder as its own overlay row, and adding it to `sol` too would start it twice there.
- "Running (not started by Solforge)" for a Solforge AI the user started directly is not shown: its control pipe name and
  token are random per launch, so the editor cannot ask it. It is never stopped or killed by the editor.

## 6. Not verified (recorded honestly)

- The standalone MCP acceptance (UI-UE5 §4.4): editor closed, `solforge-mcp --sol` lists the union and a headless tool
  answers; then the editor starts and an editor tool answers through the same shim session. Not run.
- `notifications/tools/list_changed` handling in the Solforge AI client: unknown.
- Solforge AI launch, focus, stop, quit confirmation, project switch and editor-exit shutdown: never launched (no build
  folder holds a complete `solforge-ai\Solforge AI.exe`; NotFound is the expected state).
- The diff review modal, the start-workflow dialog, the Workflows panel, the settings pages and the status card: never
  drawn or looked at. Drag and drop in the role ladders: untested.
- Model workflows: no provider was called; the OpenAI-compatible and subscription workers were never exercised.
- The trimmed single-file shim: published to a scratch folder only, never executed.
