# Solforge architecture (as of upstream 007dd7a)

This describes the code that exists in the repository today, audited from `src/`, `tests/`, the
PowerShell scripts, `Directory.Build.props` and `Sunrise Studio.sln`. The target design lives in
`sol\08-docs\Studio.md` §3; the mapping is at the end. Projects, assemblies and namespaces keep
their `SunriseStudio.*` names to keep upstream merges cheap. Only the exe (`Solforge.exe`) and the
user-visible strings are rebranded (Phase 03).

## Toolchain

| Item | Actual |
|---|---|
| Language | C# 13 (`LangVersion` 13.0), nullable + implicit usings on, `AllowUnsafeBlocks` on |
| Frameworks | `net9.0` (portable libraries) and `net9.0-windows` (UI, renderer, audio, live link) |
| Platform | x64 only (`PlatformTarget` x64), deterministic builds (`ContinuousIntegrationBuild` on CI), warnings are errors in `src/` (not tests, not NuGet audit), version 0.1.0 |
| Native code in repo | None. No C++ or Rust projects; the solution is 100% C# |
| UI stack | WinForms host window (`UseWindowsForms`) + Dear ImGui (ImGui.NET) drawn by the D3D11 renderer. No WPF. Layout is Studio's own split tree (`StudioLayout`, `StudioForm.Layout.cs`): draggable splitters, panel show/hide, maximize, pop-out windows, saved per user. ImGui's docking flag is off on purpose (see Phase 03 below) |
| Renderer | Direct3D 11 (feature level 11_0) via Vortice.Direct3D11 / DXGI / D3DCompiler 3.8.3. No D3D12, SharpDX or Veldrid |
| Lua parsing | Loretta.CodeAnalysis.Lua 0.2.13 with `LuaSyntaxOptions.Lua54` |
| Texture encode | BCnEncoder.Net 2.3.0 |
| Package restore | `.nuget/packages` inside the repo (`RestorePackagesPath`) |
| Tests | 35 console check programs (`OutputType` Exe), no xUnit/NUnit. 33 are in the .sln; the two weapon checks build from `verify.ps1` |
| CI | `.github/workflows/build.yml`: windows-latest, .NET 9.0.x, `build.ps1`, `verify.ps1 -SkipBuild` (no game, no Blender), then `publish.ps1` |
| Blender add-on | `blender/` (Python, Blender 5.2+, `sunrise_blender_plugin` 1.13.19, GPLv3), git subtree |

## Scripts

| Script | Does |
|---|---|
| `build.ps1` | `dotnet build "Sunrise Studio.sln"` (Release by default, serial, no shared compiler) |
| `publish.ps1` | Self-contained single-file `win-x64` publish of `SunriseStudio.App` to `publish\Solforge.exe`; notices are embedded |
| `run.ps1` | Starts the developer build; `-Game`, `-Project`, `-Tag`, `-Warp` pass through |
| `verify.ps1` | Builds and runs every check program; `-Game <install>` adds checks that read real packages; `-Blender`/`-Plugin` add Blender round-trip checks |

## Projects and dependencies

`App -> Editor -> {Packages, Projects, Formats, Authoring, Interchange, Missions, Live, Audio, Viewport}`

| Project | TFM | References | Packages | .cs / LOC | Holds |
|---|---|---|---|---|---|
| Packages | net9.0 | - | - | 38 / 5.5k | Package header/entry/block reading (`PackageFile`, `PackageCatalog` + disk cache), AES keys (`PackageKeys`), Oodle (`OodleDecoder`), `PackageWriter`, string banks, name caches |
| Animation | net9.0 | Packages | - | 25 / 3.5k | Skeletons, clips, skinning |
| Projects | net9.0 | Packages | - | 23 / 4.4k | `sunrise.project.json` (`StudioProject`), binary deltas, object edits, `ProjectBuilder`, `ModInstaller` (`mod.json`, writes only under `bin/x64/Sunrise/mods`) |
| Scene | net9.0 | Packages, Animation | - | 82 / 14.4k | Map/world/scenario decoding, static and entity meshes, terrain, volumes, collision, spawns, squads |
| Formats | net9.0 | Packages, Scene, Animation | - | 81 / 14.9k | Typed tag readers/editors: investment (items, sockets, stats, vendors, lore), perk behavior graphs, CUI, Tome, glTF/OBJ/DDS/PNG |
| Authoring | net9.0 | Projects, Formats, Packages | - | 70 / 13.6k | Importers and edit rules; weapon asset compilers (model, material, animation, particle, reticle, tracer, tuning) |
| Missions | net9.0 | Projects | Loretta | 22 / 4.8k | Mission script graphs, Lua <-> graph conversion, mission SDK index/API schema, validation |
| Rendering | net9.0-windows | Packages | Vortice x3, ImGui.NET | 60 / 11.3k | D3D11 renderer, game shaders, ImGui backend, `Data/NativeRenderStates86657.json` |
| Viewport | net9.0-windows | Packages, Scene, Animation, Rendering | - | 56 / 15.9k | 3D/2D views, cameras, selection, gizmos, shortcuts |
| Interchange | net9.0-windows | Authoring, Rendering, Formats, Viewport | BCnEncoder.Net | 34 / 8.2k | Blender bundles, world scene export |
| Audio | net9.0-windows | Packages | - | 27 / 5.7k | Wwise/WEM reading, C# Vorbis decoder port, playback/export |
| Live | net9.0-windows | - | - | 14 / 1.6k | Link to a running game over shared memory |
| Editor | net9.0-windows (WinForms) | 9 projects above | - | 155 / 27.7k | `StudioForm` partials, pages, creation dialogs (`Creation/`: items, exotic weapons, perks, vendors, materials, textures, static props) |
| App | net9.0-windows WinExe | Editor | - | 1 / 74 | Entry point and command line |
| Solforge.Weapons | net9.0 | Formats, Authoring | - | 9 / 0.9k | Phase 20: `.weapon.json` recipe schema and validation, C# rewrite of Parhelion's weapon domain (GPL-3.0-only, see `THIRD_PARTY.md`, `docs/PARHELION_AUDIT.md`); not referenced by Editor yet. Check: `tests/Solforge.Weapons.Tests` |
| Solforge.Graphs | net9.0 | - | - | - | Mission/behaviour graph editor core: `GraphDocument` (saved model, JSON), `GraphEditor` and `GraphCommands` (edit operations, undo), node libraries, validation, `GraphCompilers` (graph to Lua/tag output), `GraphEvaluator` |

## Test projects

Every `tests/*` project is a console program of assertions (see Tests in the toolchain table). Most build a
synthetic package fixture in a temp folder, so they run without a game install. `tests/Fixtures` holds shared
fixture source, not a project.

| Project | Checks |
|---|---|
| Solforge.Content.Tests, Solforge.Graphs.Tests, Solforge.Weapons.Tests | Content store, graph editor/compiler, and weapon recipe code of the matching `Solforge.*` project |
| SunriseStudio.Packages.Tests, PackageAudit.Tests, PackageReplacement.Tests, Overlay.Tests, RootManifest.Tests | Package reading, audit of installed packages, replacing entries, the overlay package, the root manifest |
| SunriseStudio.Projects.Tests, ProjectSystem.Tests | Project file, dependency table, merge sidecars, mod install, integrity checks |
| SunriseStudio.Formats.Tests, Investment.Tests, Names.Tests, Cui.Tests, Cui.Gpu.Tests | Tag readers/editors: behavior expressions, glTF, investment items and collections, name caches, CUI binding/override/visual (Gpu variant renders) |
| SunriseStudio.Authoring.Tests, MeshAuthoring.Tests, MaterialInterchange.Tests, Creation.Tests, Sandbox.Tests, AssetSurface.Tests, OriginalAsset.Tests | Importers, compatible mesh/skin authoring, material round trips, the creation dialogs' workflows, perk kinds, asset surface and original-asset normals |
| SunriseStudio.Animation.Tests, AnimationAuthoring.Tests, Audio.Tests | Skeleton/clip/IK/cloth, native clip authoring; Wwise decode, cues, composer |
| SunriseStudio.World.Tests, Collision.Tests, CollisionView.Tests, Selection.Tests | World loading and placement updates, collision parts and their authoring audit, view and selection behavior |
| SunriseStudio.Viewport.Tests, ViewportPipeline.Tests, Shading.Tests, Shortcuts.Tests, WeaponParticleBinding.Tests, WeaponPresentation.Tests | Camera/geometry/probe pipeline, decorators, effects, shader overrides, shortcut input, weapon tracer and presentation |
| SunriseStudio.Live.Tests, LiveBinding.Tests, LivePose.Tests | Link to a running game: capture, dynamic bindings, owned pose |
| SunriseStudio.Missions.Tests, Missions.UiTests | Mission graph nodes, flow creation, legacy and Lua round trips; the mission UI |
| SunriseStudio.Interchange.Tests | Blender bundle assembly, mesh codec, skin and effect export |
| SunriseStudio.Editor.Tests | Browser, navigation and other editor-level checks |
| SunriseStudio.Architecture.Tests | Dependency direction between projects |
| SunriseStudio.Mcp.Tests, LocalServer.Tests | MCP shim round trips and tools; the local server tab |

## Phase 03 notes (shell)

- **Branding:** window, pop-out and toolbar titles, `--help`, error boxes and two project messages say
  Solforge, as do the loading screen and the empty workspace title (SOLFORGE); the exe is `Solforge.exe`. Help menu and the toolbar version tooltip credit Sunrise Studio
  by stan; `THIRD_PARTY.md` opens with the same credit. Kept on purpose: AppData folders
  (`%LOCALAPPDATA%\Sunrise Studio\...` for settings, cache, live reports, so existing users keep them),
  glTF/OBJ `generator` strings and bundle readmes (the Blender add-on checks them), the "Sunrise Studio"
  shortcut preset name (tests and saved settings use it), and the Blender add-on.
- **Docking:** not switched to ImGui docking. Studio already has a docking-style layout (split tree,
  splitters, pop-outs, saved layouts) that the 3D viewport and every page depend on; replacing it would be
  a large rewrite of upstream code with no new capability.
- **Status bar:** `StudioForm.Solforge.cs` adds a right-aligned block to the existing status bar:
  `D2 86657 | packages: N | keys: export/exe scan | cache: <name cache state> | mem: N MB`, refreshed
  once a second; hover for details.
- **UI-thread I/O audit:** long work already runs through `StudioWorkspace.Run` / background tasks
  (load, build, install, import, export). The per-frame `Directory.Exists` in the status bar notice was
  the only clear offender and is now cached per folder. Left as is (short, user-triggered, one file):
  icon/asset `File.ReadAllBytes` in creation dialogs and Import delta, settings/layout saves, and the
  `Directory.Exists` in the mission script combo (runs only while the combo is open with no scripts).

## Phase 03B notes (layout presets)

- **Presets** (`Editor/StudioLayout.Presets.cs`, new partial): `Solforge` (the default: Asset browser left,
  Workspace centre, Object tree over Inspector right, Project changes | Mission Graph bottom row),
  `Solforge (classic)` and the upstream `Sunrise Studio` tree. `Remove`/`Reinsert` take an area out and put it
  back beside the same neighbour (the Ctrl+Space Asset browser drawer). Reset layout gives `Solforge`.
- **First run:** `Solforge` applies only when there is no settings file and no saved layout, and never in smoke
  runs: the UI checks were written against the upstream default and have not been re-baselined.
- **Viewport** (`Viewport/AssetViewport.Fly.cs`, new partial): right drag + W/A/S/D/Q/E flies the orbit camera,
  the wheel sets fly speed meanwhile; an unbound right drag is a fly look. Q/W/E/R pick the transform tool,
  never while flying, dragging or in the player camera (which owns W/A/S/D, Space and Ctrl).

## Phase 03C notes (Sol theme)

- **SolTheme** (`Viewport/SolTheme.cs`, new file): the website token set (site.css night theme), sizes (title 17, heading 14, body 13,
  label 11, button 12.5 px; 28 px controls) and draw helpers: `ChamferFrame`, `ChamferGradient`/`PrimaryButton`
  (flat brand, as the site), `SectionHeader`/`CollapsingSection`, `PanelHeader` (notch + uppercase title), `ModeButton`
  (sun underline), `LeftBar`, `Chip`, `IconTint`. Lives in Viewport so the viewport and `ImGuiIcons` can use it.
- **Fonts:** Chakra Petch 600/700 (headings, buttons) and Inter 400/600 (body), the website's faces, are embedded (`Solforge.Fonts.*`) and added through
  `ImGuiController.HostFonts`, which the editor sets before creating its controller; the controller's 16 px
  body size gives the DPI scale. Without the hook (tests, other hosts) the controller keeps Segoe UI.
- **Upstream files** keep small edits: `StudioTheme` constants and `Apply` forward to SolTheme, `BeginPanel`
  draws the panel gradient, `StudioUi.Heading` draws a section header, and each restyled call site is one line.
  Viewport selection outlines and bubble boxes keep their colours.

## Runtime and native dependencies

- **Oodle**: `oo2core_3_win64.dll` is loaded with `NativeLibrary.Load` from the user's game install
  (`<game>\bin\x64\`). It is never shipped or committed.
- **Package keys**: `<game>\bin\x64\Sunrise\exports\package_keys.bin` written by the Sunrise client
  (`client.dump_package_keys`), with a fallback that signature-scans the installed executable. Keys
  never enter project files.
- **cimgui**: native library inside the ImGui.NET NuGet package.
- **Win32 P/Invoke**: user32, kernel32, dwmapi, dwrite, winmm. D3D compiler comes from Windows.
- **Live link**: memory-mapped files `Local\SunriseStudio.{Live,Command,Cui,Object,Pose,World}.<pid>`
  shared with the Sunrise client DLL. No sockets.
- **Mods**: a build installs to `<game>\bin\x64\Sunrise\mods\<mod>` with `mod.json`, loaded by the
  Sunrise 0.6 mod loader.

## Package format facts in code

- Header read at fixed offsets: build `0x18`, variant `0x1A`, patch `0x20`, entry count `0xB4`,
  entry offset `0xB8` (or `0x110`+96 for variant 1), block count `0xD0`, block offset `0xD4`.
- Tag id = `0x80800000 + (packageId << 13) + entryIndex` (13-bit index field).
- Entry block info: start block = low 14 bits, start offset = next 14 bits << 4.

## Studio.md §3 mapping

| Studio.md subsystem | Exists today | To build |
|---|---|---|
| Solforge.App / Editor | `App`, `Editor` (WinForms + ImGui; UE5-style chrome on branch `ue5-ui`) | Rebrand; the AI terminal pane was removed for the separate Solforge AI app |
| Solforge.Projects | `Projects`: `sunrise.project.json`, deltas, object edits, transactions | `.solproj` naming, `overlay/tags/*.tagpatch` layout, base-tag SHA256 snapshots |
| Solforge.Packages | `Packages`: reader, catalog cache, Oodle, AES keys, writer | Memory-mapped indexer, entry-limit sharder into `w64_sol_custom_*` packages |
| Solforge.Scene / Viewport | `Scene`, `Viewport`, `Rendering` (D3D11, gizmos, selection) | D3D12 (if wanted), PBR/cubemap/terrain tessellation per spec, spawn/flight-path markers beyond current set |
| Solforge.Formats | `Formats` (+ `Animation`, `Audio`) | FourCC table catalog as spec describes |
| Solforge.Missions / Graphs | `Missions` (graphs + Lua conversion, Lua 5.4 parser) | Separate Graphs lib (optional), Tiger VM limit validator, Lua language server |
| Solforge.Weapons | No project. Weapon creation is spread over `Authoring` (Weapon*), `Formats` (Investment*, WeaponConfigEdits, NativePerkBehavior) and `Editor/Creation` (ExoticWeaponWorkflow, PerkBehaviorWorkflow) | Parhelion port, `.weapon.json` recipe DSL, stat/TTK validator, consolidation into one library |
| Solforge.Content | Partial: investment items/vendors/sockets/stats, CUI, Tome editors in `Formats` | Armor 2.0, subclass trees, combatant/AI squads, raid encounters, seasonal artifact |
| Solforge.Build (`solc`) | `ProjectBuilder` + `ModInstaller` inside the GUI | CLI compiler, `.solmod` container, rollback snapshots |
| Solforge.Live | `Live` over shared memory (CUI, pose, world, object, commands/reload) | Teleport/spawn actions, telemetry HUD; spec's TCP `127.0.0.1:30978` does not exist |
| Solforge.AI | `src/Solforge.Mcp`, `src/Solforge.Mcp.Shim`, Editor `StudioForm.Ai*.cs` + `Ai/` | Phase 47B (see notes below) |
| (not in §3) | `Interchange` (Blender), `Authoring`, `Animation`, `Audio`, `Rendering` | Keep as-is; add to §3 |

Real count is 14 libraries (incl. App), not 20.

## Phase 47B notes (AI workspace and MCP)

- **Projects:** `Solforge.Mcp` (net9.0, no Windows/ASP.NET deps): JSON-RPC 2.0 dispatch (`McpDispatcher`), streamable HTTP on
  `127.0.0.1` with a random port over a plain `TcpListener` (no http.sys URL ACL), bearer token, `Mcp-Session-Id`, Origin check,
  GET SSE keep-alive; `AttentionLog`, `ObjectIndex` (fuzzy names; numbers exact; map-scoped), `ProjectFiles` (reads, search,
  overlay-only edit paths), `SolforgeTools` (11 read tools, 5 approve-gated) over `ISolforgeHost`. `Solforge.Mcp.Shim` builds
  `solforge-mcp.exe`: `--sol` serves the one client-facing `sol` aggregate (Solforge plus sol-testkit, tiger-mcp and the
  optional Gemini/ClinePass delegates), merging tools, resources, resource templates and prompts; its upstream list is
  written to `%LOCALAPPDATA%\Solforge\mcp\sol-upstreams-<hash>.json`.
  The `--project` mode remains the stdio bridge to the server found in `server-<pid>.json`. `publish.ps1` publishes the shim
  beside `Solforge.exe`.
- **Editor (superseded by "UE5 UI notes" below: the console is removed):** `StudioPanelKind.AI` is the AI area in the editor dropdown (Window > AI workspace, Ctrl+Shift+A).
  `Ai/ConPtyTerminal` (P/Invoke pseudo console), `Ai/VtScreen` (VT subset), `Ai/TerminalView` (ImGui draw; chars from the form
  KeyPress, terminal focus sets `WantTextInput` so shortcuts stay quiet). `StudioForm.Ai.cs` = header, Context panel, attention
  tracking; `StudioForm.Ai.Host.cs` = the tool host (UI-thread marshalling via `BeginInvoke`, context snapshot every 250 ms,
  visible list every 1 s, screenshots from `FrameRendered` + `CapturePng`, approvals, AI edits with Undo, mission-script hot reload).
  `AssetViewport.Ai.cs` adds `ScreenOrigin`/`ScreenSize`/`ScreenRect`; `LivePage.Ai.cs` adds `AiState`.
  The AI workspace's only engine is now the custom Pi harness (`pi-cust.cmd`); Solforge writes one `sol` MCP entry to the project's `.pi/mcp.json` and starts it with `--append-system-prompt` plus `--continue` when Resume is on. The old Claude/Cline/agy launch helpers stay in `EngineLaunch.cs` and `EngineProviders.cs` for the remaining MCP delegate tools.
  Start/Restart passes `--continue` when `AiResumeSessions` is on (default; New session starts fresh). The old per-engine session lookups remain for the MCP delegates.
- **Configs:** written when a project is open: `<project>/.mcp.json` (Claude Code), `<project>/.pi/mcp.json` (Pi harness),
  `~/.claude.json` (Claude Code user and matching project entries, when present), `~/.cline/data/settings/cline_mcp_settings.json`
  (+ the VS Code Cline extension settings if installed), `~/.gemini/antigravity/mcp_config.json` (Antigravity desktop) and
  `~/.gemini/config/mcp_config.json` (the `agy` CLI, which also gets the open folders as `--add-dir`). Each holds one
  `sol` entry with `--sol --project <folder> --upstreams <local list>`; the old separate `solforge`, `gemini`, `clinepass`,
  `sol-testkit` and `tiger-mcp` entries are removed. Tokens never go into config files.
- **Checks:** `tests/SunriseStudio.Mcp.Tests` (in `verify.ps1`): stdio round trip through the shim, find_object fixture, attention
  log, project path rules, config generation.

## UE5 UI notes (branch `ue5-ui`, docs/UI-UE5.md)

Status: source only. It builds; nothing here has been launched or looked at. Compiling is not visual acceptance.

- **Chrome:** `DrawEditor` draws a 30 px menu bar (`StudioForm.MenuBar.cs`: 16 px brand mark, File to Help, the project
  name centred in muted text, the window buttons; the caption hit test covers row 1 only), a 40 px main toolbar
  (`StudioForm.Toolbar.cs`: Back/Forward, Select/Move/Rotate/Scale, the Place dropdown, Build, Render, Shaders, Quality,
  Detail, Lighting, Settings, and the right-aligned Solforge AI pill), the areas and a 24 px status bar (the busy stage over
  a 3 px Accent line, the notice, the status text, the facts plus an "MCP" dot and call count). Each row keeps the "»"
  overflow; the `TitleGroup` names are unchanged and `Tools`, `Place` and `SolforgeAi` are appended.
- **Layout:** `StudioPanelKind` gains `Place`, `Output` and `Workflows` (appended). A leaf may hold several panels as tabs
  (`StudioLayoutNode.Tabs`, `ActiveTab`; null keeps old saved layouts byte-identical). `Find`, `Show`, `SetPanel`, `Close`
  and focus requests see tab members; `AddTab`, `CloseTab`, `SplitWith`, `MoveTabToNewArea` and `MergeIntoNeighbour` move
  them. `LargestPanel` no longer protects the AI area. The new preset `Solforge (UE5)` (`DefaultPreset`) is the first-run
  layout and what Reset layout restores; every older preset is unchanged, and `Default()` stays the fallback for a corrupt
  saved layout.
- **Area headers** (`StudioForm.PanelTabs.cs`): a 22 px strip of tabs (icon, 11 px semibold title, close "x" on hover in a
  tabbed area) with the editor-type combo, collapse and the "..." Area menu (which gains "Move tab to new area" and "Merge
  into neighbour") as compact icon buttons. Tabs drag with an ImGui payload typed `SOLFORGE_PANEL` (the kind name): onto a
  header joins that area, onto a body's outer 25 % splits it; the zone under the pointer shows an Accent 20 % overlay.
  Content fades in on first show and on a tab switch. Display titles are UE5 names (Viewport, Content Browser, Outliner,
  Details, Output Log, Solforge AI); the persisted and MCP-facing names (`PanelTitle`) are unchanged.
- **Content Browser drawer** (Ctrl+Space, `StudioForm.Layouts.cs`): slides with a UiMotion spring. A lone browser area is
  removed and reinserted beside its old neighbour as before; a tabbed bottom dock holding it folds to a strip. The layout
  changes only when the slide ends; Reduce motion makes it instant.
- **Console removed:** the ConPTY terminal, the engine picker, Start/Stop, install cards, auto-allow (`aiAllowTimer`),
  quota hand-off, terminal drag/drop and `ClaimAiKeyboard` are gone from the editor. The `Ai/` terminal classes,
  `Solforge.Mcp/ApprovalPrompts.cs` and `AiConsoleAdapters.cs` are no longer compiled into the shipped assemblies
  (`<Compile Remove>`); the Editor and Mcp test projects compile them for their checks. The engine helpers the delegates
  use (`EngineLaunch`, `EngineCommands`, `EngineProviders`, `ClinePassProxy`) stay. `/automatic` stays registered; with no
  watchdog all six tools refuse with `SolforgeTools.AutomaticUnavailable` ("Automatic nudges needed the removed in-editor
  console. Use model workflows (start_model_workflow) instead."), and neither the server instructions nor SESSION.md
  advertise it any more. `AiAutoAllow` is folded once into `AiAutoApproveRequests` on load; `get_editor_state` reports
  `autoApproveRequests`. Screenshots from the UI go to the clipboard as a path; "Ask about this" copies a prompt.
- **Standalone Solforge AI and the MCP** (`Solforge.Mcp/StandaloneRegistration.cs`): solforge-next's standalone overlay
  never reads `solforge-ai-mcp.json` and drops rows named `solforge`; it only imports the `~/.claude.json` servers listed in
  `importClaudeServers` of `<LOCALAPPDATA>\SolforgeNext\AiApp\_standalone\electron\solforge-standalone.json`. Settings >
  Solforge AI > STANDALONE MCP > Register (confirmation; blocked for UI automation) writes `mcpServers.solforge-standalone`
  (the shim run copy, `--sol --upstreams <sol-upstreams-default.json>`, no env) and adds that name to `importClaudeServers`.
  `PublishStandaloneFiles` is serialised by a lock, writes every temp file under a unique name, and re-points an existing
  registration at the current run copy. Editor launches with no project use `ProjectKey.EditorNoProject` (64 hex), never
  the `_standalone` profile.
- **Motion fixes:** area tabs ease the active fill and draw one sprung Accent top line per area; ImGui tab bars
  (`TabUnderline`) draw the same top line, clipped to the bar; the fold drawer slides between the strip and the full share;
  the status notice, load toast and pill badge fade out with their last content; the approval toast keeps its (disabled)
  buttons while fading; spinner, progress sheen and shimmer honour Settings > Reduce motion and call
  `UiMotion.KeepAnimating()` so frame pacing stays smooth; `SmoothScroll` handles tilt-wheel and Shift+wheel sideways.
- **Solforge AI:** the toolbar pill (UiAuto-blocked, so MCP UI automation cannot launch or stop it) and Ctrl+Shift+A call
  agent C's `LaunchOrFocusSolforgeAi`; its chevron menu offers Launch/Focus, Stop (Cancel while launching), the panel,
  Re-check and Settings. `window.ai_workspace` / `show_panel ai_workspace` only show the AI panel. The AI panel
  (`StudioForm.SolforgeAiPanel.cs`) shows C's status card, the approval cards, the last AI LOG lines, AI edits with Undo,
  connected sessions and the context controls. `ApproveAsync` no longer opens a workspace: it raises the pill's badge and a
  toast, and docks the AI panel as a bottom tab when the layout has none (worker sessions are never auto-approved).
- **MCP from startup:** `OnShown` calls `EnsureAiService()` while `AiMcpEnabled`, then `SolforgeAiStartup()`, so a
  standalone `solforge-mcp --sol` and Solforge AI find the editor with or without a project. `StartAiForProject` passes a
  project switch to `SolforgeAiProjectChanged` (a closed project only after 2 s, since reopening the install adopts
  Project = null for a moment) and rewrites the session files. `FormClosed` calls `StopFramePacing`, `ShutdownSolforgeAi`
  and then `StopAiService` (the console used to stop the service from its own FormClosed hook).
- **Output Log** (`StudioForm.OutputLog.cs`): status, notices, busy stages, errors and the MCP call log (the AI LOG plus
  `AppendAiLog` lines from agent C), with a filter box, category toggles, Copy and Clear view (hides only).
- **Place** (`StudioForm.PlacePanel.cs`): tiles over `CreationTypes` (New) and `StudioCategories`; a tile runs the same
  `StartCreation` or opens the category in the Content Browser.
- **Known test conflicts (tests were not edited or run):** the layout UI check expects Reset layout to equal the old
  `Solforge` preset and the one-row title bar geometry; the shortcut check expects the AI terminal's Help rows, which were
  removed with the console.