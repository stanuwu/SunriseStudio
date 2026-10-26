# solforge-next-v2: status

Branch `ue5-ui`, a copy of 09-solforge. Nothing here is committed. The full spec is [UI-UE5.md](UI-UE5.md).

## What was done

- **UE5-style UI**: a two-row chrome (menu bar and main toolbar), tabbed dock areas with drag to join or split, the
  `Solforge (UE5)` default layout, the Content Browser drawer, a status bar with an MCP fact, new Place, Output Log and
  Workflows panels, and `UiMotion` easing (hover, tab lines, drawer, fades, toasts, smooth scroll). Reduce motion turns it off.
- **AI console removed**. The toolbar's Solforge AI pill and Ctrl+Shift+A launch, focus or stop the separate Solforge AI
  app. The Solforge AI panel keeps the status card, approvals, AI edits with Undo, sessions and context publishing. The
  terminal classes and console-only MCP helpers no longer ship in the assemblies; the test projects compile them.
- **Ported from solforge-next**: the Solforge AI launcher, model role routing, the credential rules, the shared-resources
  preflight, the workflow runtime with its panel, and Settings > Solforge AI and Models & workflows.
- **Standalone MCP** (the user's request). The shim serves its headless tools with no editor running, and the editor's
  tools attach once Solforge runs. Solforge AI started by itself only gets the MCP after Settings > Solforge AI >
  STANDALONE MCP > Register, which writes `~/.claude.json` `mcpServers.solforge-standalone` and adds that name to
  Solforge AI's `importClaudeServers`. solforge-next never reads `solforge-ai-mcp.json`, so the earlier note that said
  it did was wrong and has been corrected.
- **Review fixes in this pass**:
  - Standalone registration was added, as above.
  - `PublishStandaloneFiles` now runs one call at a time, and every temp file gets a unique name.
  - (Superseded by the shared profile below.) With no project open, editor launches used their own profile, not `_standalone`.
  - Automatic mode is no longer advertised. Its six tools still exist and refuse, pointing to `start_model_workflow`.
  - `AiAutoAllow` is folded into `AiAutoApproveRequests` when settings load, and `get_editor_state` reports
    `autoApproveRequests`.
  - The area tabs slide their top line. The page, World and Settings tab bars use the same top line, clipped to the bar.
  - The fold drawer no longer pops at either end.
  - The notice, the load toast and the pill badge now fade out. The approval toast keeps its size while it fades.
  - Spinners, progress lines and the wordmark shimmer obey the app's Reduce motion setting and keep the frame pacing steady.
  - Tilt-wheel and Shift+wheel scroll sideways again.
  - The viewport toolbar's combo and slider widths scale with the UI.
  - The status card warns when Solforge AI has not called the editor's MCP.

## Solforge AI app v3: panel move and project follow (UI-UE5.md §4.2.1)

- AI REQUESTS, AI LOG, SESSIONS and CONTEXT moved to Solforge AI › Editor over the new `/editor/*` channel
  (`EditorUiApi.cs`, `EditorUiContract.cs`).
  - The channel uses its own in-memory UiToken and a peer-pid check.
  - Pairing uses an Allow/Deny modal for apps the editor did not launch.
- The AI panel is now:
  - the status card;
  - a pointer to the app;
  - the compact context row;
  - a fallback block (request cards, last log lines, AI edits, sessions) while no paired app is attached.
- The approval toast has Approve and Deny. The edit review and workflow start modals stay in the editor.
- Project changes publish discovery (null on close), emit `project.changed` and write `last-project.json`.
- Solforge AI is no longer quit on a project switch. It uses one shared profile, `AiApp\shared`.
- The shim row has no `--project`. The `DEEPSEEK_API_KEY` allowance is gone.
- Not verified: no editor or app was launched, so no pairing, no event stream, no approve/deny round trip, no fallback
  switch and no project follow were seen working. `dotnet build` of SunriseStudio.Editor passes; compiling is not acceptance.

## Accepted deviations (recorded in UI-UE5.md)

- The viewport toolbar sits above the image and keeps its icon toggles. Turning them into the Perspective, Lit and Show
  dropdowns from §1.4 would rename UI automation ids that MCP clients use.
- The solforge launch row keeps `failOnStartupError: false`, so a broken shim cannot stop Solforge AI from opening.
- `EngineProviders`, `EngineLaunch`, `EngineCommands` and `ClinePassProxy` stay: the delegates and the settings type use them.
- solforge-next was not changed, because it is read-only for this task. A cleaner fix would be for its standalone
  overlay to read `solforge-ai-mcp.json` itself, and that needs the user's go-ahead.

## What was NOT verified

- **Only the build was checked.** `dotnet build "Sunrise Studio.sln" -c Release` succeeds with 0 warnings and 0 errors.
  Compiling is not visual acceptance: the editor was not launched, and no animation, layout, tab line, toast or drawer
  slide has been looked at.
- No tests were run, and no tests were added. Known conflicts: the layout UI check expects the old Reset-layout preset
  and the one-row title bar, and the shortcut check expects the removed terminal's Help rows.
- Standalone registration was not exercised: no file was written to `~/.claude.json` or `solforge-standalone.json`.
  Whether Solforge AI then lists the `solforge-standalone` tools is unverified. So is whether its client honours
  `tools/list_changed` when the editor starts or stops.
- No Solforge AI build exists yet: discovery skips the only staging copy. Launch, focus, stop, the control pipe and the
  new no-project profile are therefore all untested against a real app.
- The status card's "has not called the editor's MCP" hint is a heuristic based on the request count. It can miss a
  failed shim if another client keeps calling the editor.
- The tab-line clipping estimates the width of ImGui's scroll arrows and list button from the font size; check it with
  many page tabs open.

## Review fixes (2026-10-24, AI panel move and pairing)

- `StudioForm.SolforgeAiPanel.cs`: the panel is the launcher plus a pointer. While a paired Solforge AI is attached it
  draws the pending count, the Publish context privacy switch and an optional "Show requests here too" (approval
  cards only). With no app attached it draws the minimal fallback: the approval cards and the context row (chip and
  popup with AI edits and Undo, Publish context, Folders, Ask about this, screenshot path). The SESSIONS list and the
  AI log are no longer drawn in the editor; they are in Solforge AI › Editor. This is the intended panel move, not a
  parity loss.
- `Solforge.Mcp/EditorUiApi.cs`: pairing accepts any peer image named `Solforge AI.exe`. A build that the settings path
  or discovery does not recognize is never accepted silently, even when this editor launched it, and goes to the
  Allow/Deny modal. `IEditorUiHost.ConfirmPairAsync` gained a `recognized` flag, and the modal
  (`StudioForm.EditorUi.cs`) warns for an unrecognized build. The packaged app has the Electron RunAsNode fuse off
  (`solforge-next/ai/package-desktop.ps1` step 1c), so the image check now identifies the browser process.
- The app releases the event stream while its window is hidden or its Editor plugin is off. `AppAttached` then goes
  false within about 5 seconds and the fallback cards appear.
- Build: `dotnet build "Sunrise Studio.sln" -c Debug`: 0 warnings, 0 errors. The editor was not launched, and the panel
  and modal were not looked at. No tests were run or added.

## G1-G5 integration (2026-10-24)

- Editor: `ConfiguredModelDiscovery` + `AppCredentialStore` feed a "YOUR CONFIGURED MODELS" section on the Models &
  workflows page (Frontier/Mid/Low/List only edit the page buffer; Save writes). `AiProviders` is never written.
- `SubscriptionModelWorker`: Google AI Pro through the Gemini CLI (Google login) or `agy.exe`; no token copying, no API
  fallback. Gemini CLI path and flags unverified (not installed here).
- `StandaloneDescriptor.RefreshIfStale` rewrites a stale `%LOCALAPPDATA%/Solforge/mcp/solforge-ai-mcp.json` on start.
- `LaunchOverlay.cs`: solforge and destiny-modder rows use `awaitStartup: false`, `failOnStartupError: false`.
- Build: `dotnet build "Sunrise Studio.sln" -c Release`: 0 warnings, 0 errors. Editor not launched; nothing accepted.

## Review fixes (2026-10-24, evening)

- Credentials: `StudioForm.Workflows.cs` resolves keys from the editor credential file only; `AppCredentialStore` is a
  no-op stub (the old Solforge AI profile `.credentials.yaml` is never read, pending explicit user approval). The Models &
  workflows page note now says workflows read the credential file in memory per request.
- `ConfiguredModelDiscovery`: `claude-api-key`, `claude-gateway`, `cline-saved` and unknown AiProviders choices are listed
  as "Not offered" with the reason (no paid fallback); no tier buttons, `AddDiscoveredModel` refuses them.
- `ProviderProcesses.NpmEntry`: npm `.cmd` wrappers resolve from package.json `bin`, the shim's script path, then known
  layouts (Gemini CLI `bundle/gemini.js`); `SubscriptionModelWorker` falls back to agy.exe when gemini.cmd cannot be resolved.
- `SolforgeMcpService`: a null descriptor write/refresh now reports "solforge-ai-mcp.json could not be written/refreshed".
- Build: `dotnet build "Sunrise Studio.sln" -c Release`: 0 warnings, 0 errors. Published
  `solforge-builds/Solforge-Next-v2-ue5-2026-10-24-4` (v2 publish + solforge-ai/ai-runtime from AI build `-23-8`). Packaged
  Solforge AI cold start: first window 0.76-1.8 s, main window 4.4-7.1 s (was 38.7 s first window on `-23-5`); see
  solforge-next/HANDOFF.md. The editor was not launched; nothing here is accepted.
