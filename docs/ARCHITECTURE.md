# Solforge: where everything is

A map for new developers. The root `ARCHITECTURE.md` audits every project, toolchain choice and phase note in detail; this
file answers two questions: how does data move through the program, and where is the code for a feature. Project and
namespace names keep the upstream `SunriseStudio.*` prefix; the Project Sol additions are `Solforge.*`.

Every source file starts with a header comment saying what it holds and which files it works with, and every type and
method has a `/** */` comment. Start from the header of the file you are in.

## Projects (src/)

| Project | Holds |
|---|---|
| `SunriseStudio.Packages` | Reads the game's `.pkg` files: headers, entries, AES keys, Oodle, the tag catalog and name caches. Everything else reads game data through its `PackageCatalog`. |
| `SunriseStudio.Formats`, `Scene`, `Animation` | Readers and editors for tag types: items and perks (`Formats`), maps, meshes, volumes and collision (`Scene`, `Scene/Collision`), skeletons and clips (`Animation`). |
| `SunriseStudio.Projects` | The Solforge project (`sunrise.project.json`), deltas that replace tag bytes, project build, mod installer. |
| `SunriseStudio.Authoring` | Importers and edit rules, including the weapon asset compilers. |
| `SunriseStudio.Missions` | Mission scripts as graphs, Lua conversion, the mission SDK index. |
| `SunriseStudio.Rendering`, `Viewport` | Direct3D 11 renderer, ImGui backend, 3D and 2D views, cameras, gizmos, shortcuts, help (`HelpCatalog`). `MaterialOverrides` (Rendering) plus `NativeSceneRenderer.Overrides.cs` (Viewport) draw live material edits. |
| `SunriseStudio.Audio` | Wwise/WEM audio, plus CRI movie support: `UsmContainer` (movie container), `CriUtfTable`, `AdxDecoder`, `UsmExport`. |
| `SunriseStudio.Live` | Link to a running game over shared memory. |
| `SunriseStudio.Interchange` | Blender bundles and scene export. |
| `Solforge.Content` | Data-only content families (abilities, enemies, activities, artifacts, subclasses): model, validation, JSON store, Lua export, `.solmod` packaging. |
| `Solforge.Graphs` | Node graphs: document model, editor model, node library, validation, compilers (see data flow below). |
| `Solforge.Weapons` | `.weapon.json` recipes and weapon domain code. |
| `Solforge.Mcp` | The MCP server and every tool (see below). |
| `Solforge.Mcp.Shim` | `solforge-mcp.exe`: `--project` bridges stdio to the running editor; `--sol` aggregates Solforge, sol-testkit, tiger-mcp and the optional delegates as the one `sol` server, including tools, resources, resource templates and prompts. |
| `SunriseStudio.Editor` | The editor window and pages. `StudioForm.*.cs` partials (UE5 chrome: `.MenuBar`, `.Toolbar`, `.PanelTabs`, `.PlacePanel`, `.OutputLog`, `.SolforgeAiPanel`), `Creation/` authoring dialogs. `Ai/` still holds the old terminal classes, compiled for their checks but no longer used by the editor. |
| `SunriseStudio.App` | Entry point and command line. |

`tests/` holds one console check program per area (no test framework); `verify.ps1` builds and runs them all.
## Data flow

### 1. Packages to the editor to AI clients

```
game .pkg files
   |  SunriseStudio.Packages (PackageCatalog)
   v
typed readers (Formats / Scene / Animation / Audio / Scene.Collision)
   |
   v
SunriseStudio.Editor  (StudioForm + pages; ImGui drawn by Rendering / Viewport)
   |  implements ISolforgeHost (StudioForm.Ai.Host.cs) and ISolforgeMediaHost (StudioForm.Ai.Media.cs)
   v
Solforge.Mcp  (tools in SolforgeTools*.cs, HTTP server McpHttpServer, discovery file McpDiscovery)
   |  Bearer-token HTTP on 127.0.0.1
   +----------------------> MCP clients that speak HTTP
   +--> Solforge.Mcp.Shim --sol (stdio) --> Claude Desktop, Cursor, Cline, Gemini CLI, Codex... (one `sol` server)
```

- The editor never edits the game install. Edits go into the project as deltas (`Projects`) and become a mod on build.
- `Solforge.Mcp` does not reference the editor. It defines the interfaces (`ISolforgeHost`, `ISolforgeMediaHost`), and
  the editor implements them in `StudioForm.Ai.Host.cs`, `.Query.cs`, `.Assets.cs`, `.Media.cs`. Anything that touches UI
  state is marshalled onto the UI thread (`OnUi`); reads of packages run on worker threads.
- `SolforgeMcpService` starts the server and writes a discovery file under `%LOCALAPPDATA%\Solforge\mcp`; the shim reads
  that file to find the running Solforge that has the right project open.
- Action tools wait for an approval card in the Solforge AI panel (`StudioForm.Ai.Host.cs`); a waiting request raises a badge
  on the toolbar's Solforge AI button and a toast, and docks the panel as a tab when the layout has none. The server starts
  with the editor (OnShown), with or without a project.
- Tool names and schemas are a public API for AI clients. See `docs/MCP.md` for client setup and `docs/DATA-TOOLS.md` for the
  decompiled-code and data tools.
- There is no in-editor AI console any more (branch `ue5-ui`, `docs/UI-UE5.md` §4). The AI runs in the separate Solforge AI
  app, which the toolbar's Solforge AI button (Ctrl+Shift+A) launches, focuses or stops through `Solforge.AiLauncher`
  (`StudioForm.SolforgeAi.cs`). The editor still writes the `sol` MCP client configs (`.mcp.json` and the Claude Code, Cline
  and Agy configs) so external clients keep working. `AgyDelegate.cs` and `ClineDelegate.cs` remain upstreams of the `sol`
  aggregate, so their `ask_gemini` and `ask_clinepass` tools join the same list. The old console classes (`Ai/ConPtyTerminal.cs`,
  `VtScreen.cs`, `TerminalView.cs`, `TerminalSelection.cs`, `TerminalPaste.cs`, and `EngineLaunch.cs` in Solforge.Mcp) stay
  compiled for their checks, unwired.

### 2. Graphs to compilers to tags and Lua

```
.solgraph.json file (GraphDocument)  -- GraphEditor (edits, undo)  -- StudioForm.NodeGraph.cs (canvas)
   |  GraphValidation.Validate
   |  GraphFlatten.Flatten  (inline functions)
   v
FlatGraph --+-- Material graph: MaterialGraphCompiler    -> constant/texture writes on a material tag
            |                                              (preview: NodeGraphLivePreview -> MaterialOverrides)
            +-- Weapon graph:   WeaponGraphCompiler      -> item edit ops (apply_item_edit)
            |                      custom perks: PerkLowering -> native perk program
            |                                                    (Editor/Creation/CustomPerkAuthoring.cs packs the perk tag)
            +-- Mission graph:  MissionGraphCompiler     -> Lua mission script module
```

Node definitions are in `NodeLibrary.Core/Material/Mission/Weapon.cs`. The MCP graph tools
(`SolforgeTools.Graphs.cs`, using `GraphCommands.cs`) and the editor use the same document model and compilers.
`GraphEvaluator.cs` computes constant values for data nodes.

### 3. Content families

`Solforge.Content` documents are JSON files in the project (`ContentStore`), validated by each family (`Families.cs`,
`Subclass*.cs`) and exported as plain Lua data for the server (`ContentExport`). `SolforgeTools.Content.cs` is the MCP
surface. `Packaging/` builds `.solmod` containers and snapshots installs so they can be rolled back.

## Where to find each feature

| Feature | Look in |
|---|---|
| Open a project, menus, dialogs | `Editor/StudioForm.Commands.cs`, `StudioForm.cs` |
| Window layout, panels, tabbed areas and tab docking | `Editor/StudioForm.Layout.cs`, `StudioForm.PanelTabs.cs`, `StudioForm.Layouts.cs` (presets, drawer), `StudioLayout` |
| Menu bar, main toolbar, Solforge AI button | `Editor/StudioForm.MenuBar.cs`, `StudioForm.Toolbar.cs`, `StudioForm.SolforgeAi.cs` |
| Theme tokens and UI motion | `Viewport/SolTheme*.cs`, `Viewport/UiMotion*.cs`, `Editor/StudioForm.FramePacing.cs` |
| Place panel, Output Log | `Editor/StudioForm.PlacePanel.cs`, `StudioForm.OutputLog.cs` |
| Settings and preferences | `Editor/StudioSettings.cs`, `StudioForm.Preferences.cs` |
| 3D viewport, selection, gizmos | `Viewport/AssetViewport.cs`, `NativeSceneRenderer*.cs` |
| Live material preview | `Editor/NodeGraphLivePreview.cs`, `Rendering/MaterialOverrides.cs`, `Graphs/MaterialLivePreview.cs` (debounce) |
| Collision (Havok shapes, cloth, triggers) | `Scene/Collision/`; MCP tools `havok_packfile`, `export_collision_obj` |
| Audio and video (Wwise, USM) | `SunriseStudio.Audio/`; MCP tools in `SolforgeTools.Media.cs` |
| Items, perks, vendors | `Formats` (investment), `Editor/InvestmentPage*.cs`, `Editor/Creation/` |
| Custom weapon perks | `Graphs/NodeLibrary.Weapon.cs`, `PerkLowering.cs`, `Editor/Creation/CustomPerkAuthoring.cs`, `Editor/StudioWorkspace.CustomPerk.cs` |
| Mission scripts | `Missions/`, `Editor/MissionGraphPage*.cs`, `Graphs/MissionGraphCompiler.cs` |
| Content families (enemies, activities, subclasses) | `Solforge.Content/` |
| Local game server page | `Editor/LocalServerPage.cs`, `Solforge.Mcp/LocalServer/` |
| Live link to a running game | `SunriseStudio.Live/`, `Editor/LivePage*.cs` |
| Player position and follow | `Solforge.Mcp/PlayerTracking.cs`, `Editor/StudioForm.Player.cs` |
| Reading on-screen text (OCR) | `Solforge.Mcp/ScreenOcr.cs`, `ScreenOcr.ps1`; tool `read_screen_text` |
| Decompiled code tools | `Solforge.Mcp/Decomp/`, `SolforgeTools.Data.cs` |
| Solforge AI panel (requests, edits, presence, context) | `Editor/StudioForm.SolforgeAiPanel.cs`, `StudioForm.Ai.cs`, `StudioForm.Ai.Host.cs` |
| Live viewport in a local browser | `Solforge.Mcp/ViewportFeed.cs`, `McpHttpServer.cs` (`/viewport`), `Editor/StudioForm.Ai.Stream.cs` |
| Release build | `RELEASING.md`, `publish.ps1` |
| Agent skills for tasks | `skills/*/SKILL.md` |

## Conventions

- Comments use the `/** ... */` style above types and methods; `@param` / `@return` appear where a signature is not obvious.
- No behaviour lives in the tool lists themselves where avoidable: a tool in `SolforgeTools.*.cs` defines the schema and
  forwards to the host (the editor) or to a library project.
- Tool names, the `.solgraph.json` graph format and project JSON are persisted or public: change them only with a migration.
