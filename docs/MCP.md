# Sol MCP server

Solforge exposes one MCP server, named **sol**, whenever the editor runs, and `solforge-mcp.exe --sol` also serves it with
no editor running (see [Standalone MCP](#standalone-mcp-no-editor-running)). It aggregates the Solforge editor tools, the
workspace's `sol-testkit` and `tiger-mcp`, and the optional `ask_gemini`/`ask_clinepass` delegates, so clients see one
server with one merged tool/resource/prompt surface instead of four or five entries. Any MCP client can use it: Claude,
Gemini, GPT, Cline or a local model. The model needs nothing special.

| Feature | How it works |
| --- | --- |
| Transports | stdio, through `solforge-mcp.exe --sol` next to `Solforge.exe`; the streamable HTTP endpoint on `127.0.0.1` remains the Solforge-only transport |
| Tool names | Solforge tools keep their names (`get_context`, `open_map`...); other upstreams are namespaced (`testkit_*`, `tiger_*`), and the delegates stay `ask_gemini`/`ask_clinepass` |
| Resources and prompts | Static resources, resource templates and prompts from every upstream are merged too. Static resource URIs become `sol://<server>/<base64url(original)>`; template variables are preserved so expansion still works. Prompt names are namespaced the same way as tools. |
| Tool inputs | JSON Schema objects. Every property has a description. |
| Images | Each image tool also returns text: frame id, size, time, what changed and the labelled objects on screen. Models without vision can work from the text. OCR is not done; the on-screen labels come from Solforge's own scene. |
| Approval | Action tools (edits, builds, UI clicks, opening maps) wait for the user's click on the approval card in Solforge's AI panel (the AI Requests tab; file edits open a before/after review). This works for every client, because the approval lives in Solforge, not in the client. |
| Skills | Plain Markdown in `skills/` (one `SKILL.md` per job). Any agent can read them. |

The aggregate answers `resources/list`, `resources/templates/list`, `resources/read`, `prompts/list` and `prompts/get`
when an upstream provides them. Use the returned `sol://` URI or the returned prompt name; the aggregate maps both back
to the owning server. Resource-list change and subscription notifications are not forwarded, so the aggregate advertises
those capabilities as false. `tools.listChanged` is true: the aggregate sends `notifications/tools/list_changed` when the
Solforge route switches between the editor and the headless host, or when a failed upstream comes up on its retry.

## Client config

`<solforge>` is the folder that holds `Solforge.exe`; `<project>` is the Solforge project folder. `<upstreams>` is the
local list Solforge writes on each project start, `%LOCALAPPDATA%\Solforge\mcp\sol-upstreams-<hash>.json`. It holds the
commands and env for the aggregate's upstreams and is deliberately outside the project (the env may contain credentials).
The shim finds the Solforge window that has that project open.

**Claude Code** (`.mcp.json` in the project, or `claude mcp add sol -- "<solforge>\solforge-mcp.exe" --sol --project "<project>" --upstreams "<upstreams>"`):
```json
{ "mcpServers": { "sol": { "type": "stdio", "command": "<solforge>\\solforge-mcp.exe", "args": ["--sol", "--project", "<project>", "--upstreams", "<upstreams>"] } } }
```

**Claude Desktop** (`%APPDATA%\Claude\claude_desktop_config.json`), **Cursor** (`.cursor\mcp.json` or `%USERPROFILE%\.cursor\mcp.json`), **Cline** (MCP Servers > Configure > `cline_mcp_settings.json`):
```json
{ "mcpServers": { "sol": { "command": "<solforge>\\solforge-mcp.exe", "args": ["--sol", "--project", "<project>", "--upstreams", "<upstreams>"] } } }
```

**Gemini CLI / Antigravity** (`%USERPROFILE%\.gemini\settings.json`, or Antigravity's MCP config, which takes the same shape):
```json
{ "mcpServers": { "sol": { "command": "<solforge>\\solforge-mcp.exe", "args": ["--sol", "--project", "<project>", "--upstreams", "<upstreams>"], "timeout": 130000 } } }
```

**Pi harness** (`.pi/mcp.json` in the project; Pi reads it after the project is trusted):
```json
{ "mcpServers": { "sol": { "type": "stdio", "command": "<solforge>\\solforge-mcp.exe", "args": ["--sol", "--project", "<project>", "--upstreams", "<upstreams>"], "exposure": "direct" } } }
```
Solforge starts the custom `pi-cust.cmd` harness in the AI workspace and writes this entry on each project start; the `direct` exposure makes the `mcp__sol__*` tools visible without a tool search.

**Codex CLI** (`%USERPROFILE%\.codex\config.toml`):
```toml
[mcp_servers.sol]
command = 'C:\path\to\solforge\solforge-mcp.exe'
args = ["--sol", "--project", 'C:\path\to\project', "--upstreams", 'C:\path\to\sol-upstreams.json']
startup_timeout_sec = 20
tool_timeout_sec = 130
```

Without `--upstreams`, `--sol` reads the project's own list (`sol-upstreams-<hash>.json`) when `--project` is given, then
the per-user default `%LOCALAPPDATA%\Solforge\mcp\sol-upstreams-default.json`, and only then serves Solforge alone.
`--project` alone remains the direct stdio bridge to Solforge (headless when no editor runs), and `--gemini` /
`--clinepass` remain available for a client that wants only one delegate; Solforge's own config writer uses the single
`sol` entry.

| Argument | Meaning |
| --- | --- |
| `--sol` | The aggregate (`sol`). |
| `--project <folder>` | The session's project. With `--sol` it scopes the Solforge route; alone it is the direct bridge. |
| `--upstreams <file>` | The upstream list (see above for the fallbacks). |
| `--from-env` | `--sol` with the project from `SOLFORGE_NEXT_PROJECT` (solforge-next's overlay row). `SOLFORGE_NEXT_PIPE` is ignored: the editor is found through discovery. A missing or wrong variable never makes it exit non-zero; it serves with no project. |
| `--worker` | A model-workflow worker session: reads and reviewed `apply_project_edit` only (`WorkerSessionPolicy`); no workflow, automatic or other write tools. |
| `--context` | Prints the live context and exits (the Claude Code prompt hook). |

**HTTP-only clients.** Solforge writes `%LOCALAPPDATA%\Solforge\mcp\<pid>.json` with `url` (`http://127.0.0.1:<port>/mcp`) and `token`. Send `Authorization: Bearer <token>`. The port and token change on every run, so prefer the shim when the client can start a command.

Use tool timeouts of about 130 s: `wait_for_player_near` waits up to 120 s, and `wait_for_viewport_change` up to 60 s.

## Delegates: Gemini (Antigravity CLI) and ClinePass

Each delegate runs on its own official client and sign-in. Solforge adds it as an upstream of the one `sol` server when
its CLI is installed, so Claude Code or Pi can use it without a second MCP entry.

- **`ask_gemini`.** This tool runs `agy -p <task> --output-format json --mode plan|accept-edits` in the folder you give it. It returns Gemini's reply and a conversation id, which you pass back as `conversation` to continue.
  - It does not need Solforge to be running, but through `sol` it is available alongside the editor tools.
  - A client that wants only this delegate can still use `solforge-mcp.exe --gemini`.
- **`ask_clinepass`.** This tool works like `ask_gemini` but runs your own Cline CLI on ClinePass: `cline --json -P cline-pass [-m cline-pass/<model>] [-p] -t <seconds> -- <task>`. The default `plan` mode reads and answers only; `act` lets the model edit files and run commands. The prompt is capped at 24,000 characters and the time limit at 30 minutes. It returns the reply plus the model and finish reason. The headless Cline CLI can't resume a session in JSON mode, so each call starts fresh.
  - Sign in once with `cline auth` and pick ClinePass, or set `CLINE_API_KEY`.
  - A client that wants only this delegate can still use `solforge-mcp.exe --clinepass`.
- **Claude Code on ClinePass (provider for Claude Code).** Claude Code itself runs on ClinePass models through a translator on 127.0.0.1 inside Solforge, which converts Anthropic Messages to OpenAI Chat Completions against `https://api.cline.bot/api/v1`. Cline documents this API for your own scripts and apps with a ClinePass API key ([ClinePass docs](https://docs.cline.bot/getting-started/clinepass)), and its FAQ allows ClinePass "with other coding agents by using your ClinePass API key" ([cline.bot/cline-pass](https://cline.bot/cline-pass)).
  - Setup: create a key at app.cline.bot (Settings > API Keys), set it as the user environment variable `CLINE_API_KEY`, and pick the provider "Claude Code on ClinePass" for Claude Code.
  - The model field takes a `cline-pass/` id; the default is `cline-pass/kimi-k3`. Inside Claude Code, `/model cline-pass/qwen3.8-max` (or any other `cline-pass/` id) switches the model, and the haiku alias maps to `cline-pass/deepseek-v4.1-flash`.
  - That run has every Anthropic and Claude login variable removed. Claude Code gets only a random local token that opens nothing but the translator port. Your Claude and Google sign-ins never pass through it, and Antigravity stays tool-only (`ask_gemini`). The key is read from your environment per request and never stored or logged.
  - It is personal use only: Cline's [terms](https://cline.bot/tos) forbid sharing a subscription or transferring API keys.
- **Handoff helper (legacy).** The AI workspace now starts only the Pi harness; the old session-handoff helper remains in `EngineLaunch.cs` for delegate callers but is no longer offered by the workspace.
- **Not allowed (both providers' terms).** Never copy or proxy a subscription's OAuth token into another client. That rules out pointing Claude Code at Gemini through an Antigravity-login proxy, and putting a Claude login in a third-party harness. Google suspends accounts that do the first. Gemini inside Claude Code itself is possible only with a paid Gemini API key through an LLM gateway such as LiteLLM. That is billed API use, not AI Pro, and Anthropic doesn't support it.

## Solforge's own UI

- `list_ui_controls` lists the controls drawn right now: buttons, menus, checkboxes, tabs, text fields, combos, sliders and list rows. Each row has a stable id (`main:1A2B3C4D`), its kind, label, panel, scope, rect, value and options.
- `ui_invoke`, `ui_set_value`, `ui_set_checked` and `ui_select` act on a control by `id` or by visible label (`query`, narrowed by `window` and `kind`). A query that matches several controls equally is refused and lists them.
- Every action needs the approval click.
- The AI workspace and Settings > AI are never listed and cannot be driven.
- Collapsing headers and tree nodes are kind `header`: `ui_invoke` opens one, `ui_set_checked` opens or closes it. Icon buttons are listed under their tooltip. Colour fields are `text` controls holding `r, g, b, a` (0-1); `ui_set_value` also takes `#RRGGBB[AA]`.

## Commands, panels and file pickers

- `list_commands` lists every menu command and toolbar toggle by a stable id (`file.*`, `edit.*`, `assets.*`, `build.*`, `view.*`, `toolbar.*`, `window.*`, `nav.*`, `help.*`): menu path, input, the dedicated tool to prefer, and whether it is enabled now. It needs no approval and works while paused.
- `run_command` runs one. A command that opens a native file or folder picker takes `path` instead; exports still refuse paths inside the project or the install.
- `show_panel` opens a panel, editor window or dialog so `list_ui_controls` can see its controls.
- `set_viewport_options` sets wireframe, grid, game shaders, full detail, quality, lighting, projection, view presets, axis gizmo and statistics. `set_camera` places the camera (`eye` + `look_at`), re-centres it (`look_at`), turns it (`yaw`, `pitch` in degrees) or zooms it (`distance`).
- `export_asset` and `import_asset` cover the Assets menu by `kind`. `project_change` reverts, removes, compares or exports Project changes rows.
- `arm_file_dialog` answers the next picker that a `ui_invoke` opens (Browse buttons, audio, weapon and mission page imports and exports).
- `get_help` with `section: tools` maps every menu, panel and dialog to its tools. The full table is `08-docs\McpCoverage.md`.

## Live viewport

Solforge captures the 3D viewport continuously while someone watches: a frame tool used in the last 2 minutes or a browser stream. The capture goes into a ring of 32 JPEG frames. A new frame is kept only when the picture changes. No call waits for a GPU readback.

- `get_viewport_frame`: the latest frame, at once. `since=<id>` returns text only if nothing is newer.
- `get_viewport_frames`: frames since an id.
- `wait_for_viewport_change`: blocks until the view changes, up to the timeout.
- `read_screen_text`: the text on screen via Windows OCR (`Windows.Media.Ocr`, built into Windows, CPU only): lines with `[x, y, w, h]` boxes in image pixels; `region`, `contains`, `words`, `source=screenshot` for full size. `get_viewport_frame` and `wait_for_viewport_change` take `ocr: true` to add the same text to a frame.
- `get_viewport_stream`: a local URL, `http://127.0.0.1:<port>/viewport?key=<view key>`, for an MJPEG stream on this PC. The view key is not the MCP token.

## Player position

- `get_player_position` returns where the player is now:
  - the source;
  - map, zone (bubble), activity and mission;
  - `xyz` in map space and `yaw` in degrees (0 = +X, 90 = +Y);
  - the nearest named objects and mission slots, with distances;
  - other players on the local server;
  - for each source, why it is silent if it is.
- `wait_for_player_near` waits until the player is within `radius` m of an object, a slot or a point `[x, y, z]`.

Sources, first that answers wins:
1. **Live link** (`studio_attach`, Live page > Attach). The most exact.
2. **Test recorder.** The newest `bin\x64\Sunrise\logs\recorder\<session>\snapshots.jsonl` (launch with `SOL_RECORDER=1`; `SOLFORGE_RECORDER` overrides the folder). The recorder's `pos` freezes at spawn after a load, so the camera (`cam`, lowered 1.7 m) is used whenever `pos` is missing, stale or more than 3 m away from it.
3. **Local server.** The admin page `http://127.0.0.1:8099/coop` lists every joined player (`SOLFORGE_SERVER_ADMIN` overrides the address; `SOLFORGE_PLAYER` picks the player).

All three report game world space, which is the space Solforge draws maps in.

In the viewport, the player shows as an orange ring with a facing arrow and a breadcrumb trail. The toolbar's **Follow player** toggle keeps the camera on the player. With the recorder or the server, it also opens the player's map when their activity matches a known mission. With the Live link, Live > Sync > Map does that.

## Maps and missions

- `open_map` takes a map, a mission, or both. A mission alone opens on its own map, and a map alone lists its missions.
- `list_missions` shows only the open map's missions unless you give `map` or `all=true`.
- `check_mission_script` {path} checks a Lua mission script without the game: syntax, every mission key and hash it names, Slot constants against the open mission's objects, and the script library's `check()`. Problems carry line numbers. It is the Graph panel's **Checks > Check script** button.
- `simulate_mission` {path, mode} walks a campaign script step by step through a fake game host. The modes are `play`, `skip`, `cutscene_never_loads` and `rejoin`. It returns the step trace, stalls, script errors and missing mocks. It is the **Checks > Simulate** button.

## Retail backporting

- The `retail_*` tools read the retail Destiny 2 extraction (build 245048); see docs/DATA-TOOLS.md and the
  `solforge-retail-port` skill. Start with `retail_status`.
- `retail_open_map` {bubble} shows a retail bubble in the viewport (approval). The Retail workspace's Map viewer tab names
  the retail tag behind the selected object; `select_object`, `focus_camera` and screenshots work as on 86657 maps.
- `retail_port_mission`, `retail_port_strings` and `retail_convert_tag` write into the open project (approval); nothing is
  written to game packages.
- Without the editor, `solforge-retail-mcp.exe` serves the read-only retail tools over stdio:
```json
{ "mcpServers": { "solforge-retail": { "command": "<solforge>\\solforge-retail-mcp.exe", "args": ["--root", "E:\\sol-live-pkg-extract"] } } }
```

## Several agents on one Solforge

- Each `solforge-mcp` shim sends its pid (`X-Solforge-Client-Pid`); with the `Mcp-Session-Id` from `initialize` and the clientInfo name, that identifies each agent.
- `get_sessions` lists the connected sessions, who opened the map the UI shows, and which build, deploy or map load is running and who started it. The AI workspace shows the same list under its header.
- `find_object`, `get_object`, `list_mission_objects`, `get_mission_object`, `find_world_objects` and `get_placement` take `map=<tag>` (and `mission=<tag>`). They answer from a background copy of that map, and the UI's map does not change. The first call on a large map may say it is still loading; call again. Solforge keeps two copies at most.
- `open_map` and `close_map` refuse while another session opened the current map less than 10 minutes ago (`SOLFORGE_MAP_GUARD_MINUTES`), unless `force=true`.

## Shaders, animations and audio

Read-only metadata from the installed game. No map needs to be open, and these tools keep working while context publishing is paused. They use the same package readers as the editor's material, animation and audio pages, and they decode nothing those readers don't already read.

| Tool | Gives |
| --- | --- |
| `list_shaders` | Shader headers, paged: tag, stage, bytecode tag, package, size. Filter by `stage`, `package` or `filter`. |
| `get_shader` | A shader header tag gives its profile (for example `ps_5_0`), constant buffers (slot, size in float4 registers and bytes), texture and sampler slots, `dcl_` declarations, and the materials that use it (from the owner index, which can be partial). A material tag gives every stage, plus the bound textures (stage, slot, texture tag, size, format, sRGB), empty slots and diagnostics. `disassembly=true` adds the DXBC disassembly, one page of lines at a time (`offset`, `limit`). |
| `list_animations` | Animation clips, paged: tag, name, package. With `entity`, you get only that entity's clips, with definition, clip index and config. |
| `get_animation` | A clip gives its frame count, sample rate, length in seconds and payload sections, plus the definitions that list it (`users`). When the clip's codec is supported, you also get its control tracks: node and control counts, groups, and the key counts of each track, paged. Add `entity` to get that entity's skeleton too (bones with name, hash and parent, paged). An entity tag gives its skeleton and a count of its clips. |
| `list_audio` | Wwise events, banks and streams, paged: tag, kind, name, package, size and Wwise id. Filter by `kind`. |
| `get_audio` | An event, bank or stream gives its Wwise event id, related event id, linked bank (id, Wwise version, object counts by HIRC type, embedded media count), media (id, stream tag, name, size, embedded or streamed) and diagnostics. No audio is decoded. |

The first `get_animation` call scans every animation definition once to find each clip's users, so it can be slow. Later calls use the cached result.

## Automatic mode

Keeps the AI working on one goal until it is done.

- Seth types `/automatic <goal>` in the AI terminal (or in the remote page's AI chat). Solforge catches the line before the engine sees it, sends the goal with working instructions, and answers one-time allow prompts while it runs. `/automatic status` shows the state, and `/automatic stop` ends it.
- When the engine's screen stays still for the idle time and there is no allow prompt, Solforge pastes a continue nudge. The nudge holds the goal, the last `goal_progress` and the remaining steps.
- A nudge round counts as no progress when the screen is the same as at the last nudge, or when there was no Solforge tool call and no `goal_progress`. After 3 rounds like that, the nudge asks for a different tactic.
- It stops on `goal_complete` (summary, evidence), `goal_blocked` (reason), `/automatic stop` or `automatic_stop`, or at the limits. The limits are in Settings > AI: max nudges (default 50), max duration (default 240 min) and idle seconds (default 45).
- The AI can start this mode on itself with `automatic_start(goal)` for a long multi-step goal. External clients can also use `automatic_status` and `automatic_stop`.
  - None of the automatic tools needs an approve click.
  - Only one goal runs at a time. A second `automatic_start` is refused unless it passes `replace: true`. A typed `/automatic` always replaces.
  - An engine can't type `/automatic` into its own input line, so it uses the tools.
- The running goal is saved to `automatic-goal.json` beside the settings file. After a restart, the AI panel asks whether to resume it or discard it.
- `get_editor_state` and the remote AI state both include `automatic`, and the stream page's Status tab shows it.


## Standalone MCP (no editor running)

UI-UE5 §4.4, for the relayed request "the mcp should be able to be launched if its standalone too". `solforge-mcp --sol`
(and `--project`) no longer needs a running editor:

- **Route.** On every `tools/list` and `tools/call` the shim looks for a running editor through the discovery files
  (`McpDiscovery`, cached for 2 s) and checks again every 5 s in the background. With an editor it talks to it over HTTP;
  without one it answers in-process from the headless host (`Solforge.Mcp.Headless.HeadlessSolforgeHost`). When an editor
  starts later, its tools attach to the same shim session; when it stops, the shim falls back to headless.
- **Tool names stay stable.** The Solforge part of `tools/list` is the union of the live editor catalogue, the cached one
  (`%LOCALAPPDATA%\Solforge\mcp\tools-cache.json`, written on every editor start) and the headless tools, each with
  `_meta["solforge/requiresEditor"]`. A client that ignores `list_changed` still sees every name.
- **Headless tools** (`SolforgeTools.HeadlessTools`): project files (`read_project_file`, `search_project`,
  `list_project_files`), the reference library, content reads, graph reads, the tag schema registry, the decomp tools,
  the retail reads, the local server reads, `testkit_status`, `get_client_build`, `get_sessions` and `mcp_job`. Every other
  tool, and every tool that needs an approve click, answers `needs the Solforge editor: start Solforge (and open <project>)`.
  With no project, project-folder tools answer `no project: pass --project or open one in the editor`; with no
  `--project`, the shim follows the attached editor's project.
- **Other upstreams** (sol-testkit, tiger-mcp, delegates) start once, as before, plus one retry 30 s after a failed start.
- **Per-user files** for a Solforge AI started directly: the editor (and the Solforge AI settings page) writes
  `sol-upstreams-default.json` (solforge, the delegates and the carried workspace MCPs, no project entries) and
  `solforge-ai-mcp.json`, the descriptor solforge-next's default overlay reads:
  `{ version: 1, shim, args: ["--sol", "--upstreams", <default file>], failOnStartupError: false, toolCallTimeoutMs, destinyModder, skillDirs }`.
- **Project binding.** Each MCP session is bound to the project it was opened for. After the editor switches projects,
  its calls are refused with JSON-RPC error `-32001` ("project changed"); the shim then opens a new session for the
  current project on the next call. `open_project`, `create_project` and `close_project` move their own session along.

Not verified: none of this has been run. The acceptance checks (editor closed: `tools/list` returns the union and a headless
tool answers; then start the editor: an editor tool answers through the same shim session) are left to the user.

## Solforge AI

The AI console/terminal is gone from the editor; the toolbar's Solforge AI button launches the separate Solforge AI app
(solforge-next) per its launch contract (`Solforge.AiLauncher`). Its overlay row is
`solforge-mcp.exe --sol --project <p> --upstreams <file>` (or `--sol --upstreams <per-user default>` with no project), with
`failOnStartupError: false`, so the MCP comes up headless when the editor is gone.

## Reviewed edits, editor capture and model workflows

- `read_project_file` returns `sha256` (the whole file's version). `apply_project_edit` takes `expected_sha256` and refuses a
  stale write; the user reviews the exact before/after text in a side-by-side diff (3 minutes), and the write is checked
  against the reviewed version again before it happens. `get_open_documents` entries with a file carry `dirty` and `sha256`.
- `get_editor_screenshot`: the whole editor window (uncropped back buffer). `get_recent_activity` is an alias of
  `get_attention_log`.
- The configured credential file (Settings > Solforge AI) is never read, searched, opened, edited or captured:
  `read_project_file`, `search_project`, `open_file`, `apply_project_edit`, the screenshot and frame tools refuse it.
- `start_model_workflow`, `get_model_workflow`, `cancel_model_workflow`: the model team from Settings > Models & workflows.
  A start always opens Solforge's own confirm dialog (task and routed models); it is never auto-approved. Workers reach the
  editor through `solforge-mcp --worker --project <p>`.