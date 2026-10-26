# Owned Solforge AI runtime and desktop app

`ai/runtime` is the owned DeepSeek Harness source fork. Solforge AI is a
**separate desktop application** (its own Electron window, branded "Solforge AI")
that the Solforge editor starts as a child process. It is not embedded in the
editor's WebView2 and the former editor workspace panel is retired: the web-app
patch no longer registers `@deepseek-ai/dsh-client-solforge-workspace`, and
build-runtime, publish, package-runtime and verify refuse a patch that registers
it. The package is still declared in `packages/bundle/web-app/package.json`
(removing it needs a relink the user has not authorized), so it is still copied
into the deployed closure, unloaded; `build-runtime.ps1` warns about this. The
editor's Solforge AI tab is a launcher/status panel only. Run scripts with PowerShell 7.4 or newer.

## Build and package

Dependencies must already be installed in `ai/runtime` (no script runs
`pnpm install`). From the Solforge Next root:

```powershell
pwsh -NoProfile -File .\ai\build-runtime.ps1
pwsh -NoProfile -File .\publish.ps1 -Output ..\solforge-builds\<new-folder> `
    -ElectronZip C:\path\to\electron-v44.7.0-win32-x64.zip `
    -NodeLicensePath C:\path\to\matching-node-distribution\LICENSE
pwsh -NoProfile -File .\verify.ps1 -BuildFolder ..\solforge-builds\<new-folder>
```

`build-runtime.ps1` builds the owned artifacts (CLI, host, web, the desktop shell
`apps/desktop/lib/**` and the desktop host `apps/desktop-host/lib/**`) and checks
each for its Solforge launch markers.

`publish.ps1` runs `ai/package-runtime.ps1` (creates `<build>/ai-runtime`) and then
`ai/package-desktop.ps1` (creates `<build>/solforge-ai`). Both can also be run
directly with the same `-Output`. Optional `-NodePath` (Windows Node >= 24.9.0) and
`-PnpmPath` (must equal the `packageManager` pin) apply to both.

`-ElectronZip` is mandatory. Nothing is downloaded: the zip is verified offline
against `apps/desktop/node_modules/electron/checksums.json` (SHA-256 for
`electron-v44.7.0-win32-x64.zip`) and must be supplied by the user.

## Build layout

```text
<build>/
  Solforge.Next.exe
  solforge-next-mcp.exe
  ai-runtime/
    apps/cli/lib/bin.js
    apps/web/dist/index.html
    node/node.exe  node/LICENSE
    node_modules/                         # materialized production closure
      @deepseek-ai/dsh/                   # apps/cli build (profile-boot export)
      @deepseek-ai/dsh-desktop-host/      # apps/desktop-host build
      @deepseek-ai/dsh-skill-filesystem/  # consolidated skills provider
      @deepseek-ai/dsh-experimental-solforge-automatic/            # automatic mode, nudges, auto-allow (Host)
      @deepseek-ai/dsh-experimental-client-ui-solforge-automatic/  # strip, settings, mode indicator (client)
      @deepseek-ai/dsh-experimental-solforge-bundle/               # cordis.patch.yml registering both
    desktop-runtime.json                  # host release descriptor read by the shell
    ai-runtime-manifest.json
  solforge-ai/
    Solforge AI.exe                       # renamed electron.exe
    resources/app/                        # owned shell: lib/main.js, preloads, welcome, renderer, node_modules
                                          # package.json carries "solforgeAi": true (standalone marker)
    resources/icon.png  resources/tray.ico
    solforge-ai-manifest.json
```

`desktop-runtime.json` lists `@deepseek-ai/dsh` and `@deepseek-ai/dsh-desktop-host`
at `node_modules/<name>`, release version equal to the `apps/desktop` version, and
the host protocol version parsed from `apps/desktop/src/host-protocol.ts`.
`solforge-ai` has no `resources/app-update.yml` (auto-update is off) and no
`default_app.asar`. Existing `ai-runtime` / `solforge-ai` folders are never
overwritten; staging folders `.ai-runtime-staging-*` / `.solforge-ai-staging-*`
and the scratch copies in `%TEMP%` are retained after failure, never auto-deleted.

## Launch contract for the C# side

The editor owns this; it is documented here so the editor can launch the app
without knowing shell internals. `<base>` is the folder that holds
`Solforge.Next.exe`.

**Process**

- File: `<base>/solforge-ai/Solforge AI.exe`
- Arguments: `--user-data-dir=<LocalAppData>/SolforgeNext/AiApp/<projectKey>/electron`
  (one directory per project; the same value is also passed as
  `SOLFORGE_AI_USER_DATA`, which wins when both are present).
- WorkingDirectory: the project folder.
- `UseShellExecute = false`, `CreateNoWindow = true`; redirect and continuously
  drain stdout/stderr, but never log them verbatim (they may echo environment).
- Keep the `Process` object; the editor may only stop the process it started.

**Environment** (start from an empty block, exactly as the current `SolforgeAiHost`)

| Variable | Value |
| --- | --- |
| inherited allowlist | the existing `InheritedEnvironment` names (SystemRoot, TEMP, PATH, ...) |
| credentials | the existing credential allowlist, including `DEEPSEEK_API_KEY`. A configured name must contain KEY, TOKEN, SECRET or PASSWORD (the runtime's child scrub pattern) and must not be an inherited, proxy/TLS or `NODE_`/`UV_`/`npm_` name, or the launch is refused |
| `DSH_HOME` | `<LocalAppData>/SolforgeNext/AiApp/<projectKey>/home` (profile lives in `home/profiles/desktop`) |
| `DSH_TELEMETRY_DISABLED` | `1` |
| `SOLFORGE_AI` | `1` (enables Solforge mode; all other `SOLFORGE_AI_*` are ignored without it) |
| `SOLFORGE_AI_RUNTIME_DIR` | `<base>/ai-runtime` (must exist) |
| `SOLFORGE_AI_NODE` | `<base>/ai-runtime/node/node.exe` (host runs on this Node, not Electron-as-Node, so native addons keep their ABI) |
| `SOLFORGE_AI_PATCH` | absolute path of the launch overlay (below); must be an existing file |
| `SOLFORGE_AI_USER_DATA` | same absolute path as `--user-data-dir` |
| `SOLFORGE_AI_CONTROL_PIPE` | `\\.\pipe\solforge-ai-<id>`, `<id>` = 8-64 chars `[A-Za-z0-9-]` (e.g. a GUID) |
| `SOLFORGE_AI_CONTROL_TOKEN` | random 32-256 chars; removed from the app's environment at startup (with `SOLFORGE_AI_CONTROL_PIPE`), never reaches child processes |
| `SOLFORGE_AI_DISABLE_OFFICE` | `1` (office skills/workspace-dependency tools not mounted; recommended) |
| `SOLFORGE_AI_SKIP_WELCOME` | optional `1`: open the main window directly instead of the API-key welcome page |
| `SOLFORGE_AI_PROJECT` | optional: the project display name for the mode badge. Unset: the basename of the working directory, or empty (badge shows `-`) when the working directory is `DSH_HOME` (no project open) |
| `SOLFORGE_AI_PRIMARY_RUNTIME` | optional; default `<ai-runtime>/primary-runtime` |
| `DSH_DESKTOP_OPEN_DEVTOOLS` | `0` |

Do not set `ELECTRON_RUN_AS_NODE`; it is stripped from the host environment anyway.

**Launch overlay (patch yml)**

Write `<home>/solforge-launch-<guid>.yml` in the same Cordis patch format the
current `SolforgeAiHost` overlay uses and pass it via `SOLFORGE_AI_PATCH`. The
desktop host applies it as a `--patch` file to the `desktop` profile. Rows:

- `solforge-next-mcp`: `@deepseek-ai/dsh-mcp-client`, command
  `<base>/solforge-next-mcp.exe`, args `['--from-env']`, env
  `SOLFORGE_NEXT_PIPE` / `SOLFORGE_NEXT_PROJECT` written as literals (they are
  not in the app's own environment, so other children do not inherit the editor
  pipe name), cwd = the project folder,
  `toolCallTimeoutMs: 180000`, `failOnStartupError: true` (editor and model
  workflow tools).
- `solforge-destiny-modder-mcp`: unchanged from the current overlay.
- `solforge-shared-skills`: `@deepseek-ai/dsh-skill-filesystem`, `providerName:
  solforge-shared-resources`, `includeDefaultRoots: false`, `customSkillDirs`
  (consolidated skills). The editor's presence check path is
  `<base>/ai-runtime/node_modules/@deepseek-ai/dsh-skill-filesystem/lib/index.js`.
- Drop `solforge-host-control` (it belonged to the web-server host). Do not pass
  `--profile web` or any web/port arguments; the shell owns the window.

The desktop host re-reads the overlay on every host (re)start during the app's
lifetime, so it must exist until the Electron process exits; delete it only then.
The editor writes `<overlay>.owner` (PID and start time of the app) and, before a
launch, deletes only overlays whose owner is no longer alive (or unmarked files
older than 2 minutes).

**Control pipe**

The app serves `SOLFORGE_AI_CONTROL_PIPE`. A client connects, writes
`<token>\n<command>\n` (max 4096 bytes, 5 s deadline), reads one JSON line, and
the server closes the connection. Wrong token: `{"ok":false,"error":"unauthorized"}`.

| Command | Effect / reply |
| --- | --- |
| `status` | `{"ok":true,"pid","backend","backendReady","enteredWorkspace","windowVisible","quitting","confirmingQuit"}` |
| `focus` | show, restore and focus the window |
| `quit` | graceful `app.quit()` (may ask to confirm while a turn is running) |
| `quit-force` | quit without confirmation; the host is shut down over IPC |

**Stop, focus, lifetime**

- The editor connects with `CurrentUserOnly` and sends the token only when
  `GetNamedPipeServerProcessId` equals the PID it started.
- User Stop: send `quit`, then poll `status`. If the app reports
  `confirmingQuit`, keep waiting (up to 10 minutes); if it answers idle (not
  quitting, no confirmation open) the user cancelled, and the editor keeps the
  process. Once it is quitting, stops answering or exits: `WaitForExit(25 s)`,
  then `Kill(entireProcessTree: true)` on the started `Process` only.
- Editor shutdown, project switch, or a Stop during launch: `quit-force`, then
  `WaitForExit(25 s)`, then the tree kill.
- Fatal recovery in editor mode offers Exit only (no self-relaunch), and the
  development "restart app host" menu item is hidden: a relaunched copy would
  lack the control token and overlay. Relaunch from the editor. (A standalone
  launch may restart itself; see below.)
- Focus: send `focus`. Fallback: start the same exe with the same
  `--user-data-dir` (an empty environment is fine); the single-instance lock
  is per user-data dir, so the running instance is focused and the second
  process exits. A marker-only launch honours an explicit absolute
  `--user-data-dir=` instead of the standalone profile's directory, so this
  fallback reaches the editor's instance. Before it exits, that second process
  still regenerates the standalone overlay in the standalone home from the
  standalone profile's settings (same content as a direct launch).
- Closing the window quits the app (no tray residency). Status in the editor
  tab = process alive + `status` reply.
- No `dsh://` protocol registration, no update checks, no CLI-install menu item
  in Solforge mode. Plugin package install is unavailable (no pnpm is shipped).

## App v3: shared profile, Solforge plugins, project workspace

Spec: `ai/SOLFORGE-AI-APP-SPEC.md` (sections 4, 7, 8). This section supersedes the per-user
`_standalone` profile and `solforge-standalone.json` described below. Built and type-checked only;
none of it has been observed in a launched app yet.

**Shared profile** (`apps/desktop/src/solforge-app-config.ts`, `solforge-standalone.ts`)

- Every launch uses `<LOCALAPPDATA>/SolforgeNext/AiApp/shared/{electron,home}`; only the workspace (the
  open project) changes. The editor's launcher must pass the same root as `SOLFORGE_AI_USER_DATA`/`DSH_HOME`
  (P4, `ProjectKey.AppRoot`).
- First run copies, never moves: the newest old profile's `electron/solforge-standalone.json` (converted)
  and `home/profiles/desktop/cordis.patch.yml`. Never `.credentials.yaml`, never sessions, nothing deleted.
  `shared/migrated-from.txt` records the source. Old profiles stay readable.

**App config** `shared/electron/solforge-app.json` (`SolforgeAppConfig` in
`packages/experimental/solforge-contract/src/app-config.ts`)

- Written only by the Electron main process (`window.solforgeApp.setConfig`), read-modify-write with
  unknown keys preserved, atomic rename. The Host reads it (`SOLFORGE_AI_APP_CONFIG`) and polls it.
- `plugins.<id>` holds the switches of the eight Plugins page cards: `solforge-editor-mcp`,
  `destiny-modding`, `graphify`, `rea`, `solforge-automatic`, `solforge-model-tiers`, `solforge-editor`,
  `solforge-skills`.
- MCP and skill plugins apply through the app overlay (regenerated on every change and before every
  Host start); the card then shows "Restart AI host". Host restart from the main process is wired by
  main.ts's `restartHost` (P1) and is unverified until observed. Feature plugins (automatic, tiers,
  editor panels) switch live: their Host halves read `enabled` from the file.
- Editor MCP row: `solforge` stdio row from the standalone descriptor's `solforge-mcp.exe`, never with
  `--project` (the shim follows discovery); optional `gemini`/`clinepass` delegate rows. In editor mode the
  editor's launch overlay rows are kept, and rows the user switched off here are disabled by
  `- id: <row>` / `disabled: true` entries (the app overlay is the last patch layer).

**Patch layers** (`apps/desktop-host/src/index.ts` `solforgePatchFiles()`): `solforge-bundle/cordis.patch.yml`
(P2, all package rows), then `patches/brand.patch.yml` (P1), `patches/models.patch.yml` (P3),
`patches/editor.patch.yml` (P4), then `SOLFORGE_AI_PATCH`, then `SOLFORGE_AI_APP_OVERLAY`. A missing split
patch fails startup.

**Project workspace**

- `SOLFORGE_AI_PROJECT_DIR` (non-secret) goes to the Host: the launcher's value, else `lastProject`, else
  `%LOCALAPPDATA%/Solforge/last-project.json` (written by the editor; the app only reads it).
- Host plugin `solforge-plugins` registers it with `workspaceRegistry.create(dir)` before the browser restores.
  In Solforge mode `ui-workspace` never initializes a default workspace, prefers the project workspace on
  restore, and labels a leftover default-titled workspace by its folder name. With no workspace the
  "Open a project" empty state shows (`client-ui-solforge-plugins`).
- Live follow: `solforge:project` (editor link `project.changed`, or a folder the user opens) makes the folder a
  workspace and opens its newest session, or a fresh one, without restarting.
- Single instance: one shared profile means one app; an editor launching it again passes its pid as
  single-instance data and the running app pairs with it.

**Main-process API for main.ts (P1 call sites)**: `installSolforgeShell({ getWindow, restartHost })` before the
first `backend.start`, and `...solforgeHostEnvironment()` in the Host spawn environment (both in
`apps/desktop/src/solforge-shell.ts`).

**Registration without `pnpm install`**: new packages are registered by hand-written `pnpm-lock.yaml` importer
blocks plus package-local `node_modules` junctions, `tsconfig.base.json` paths, `tsconfig.host.json` /
`tsconfig.client.json` references, `PRIVATE_EXPERIMENTAL_PACKAGE_DIRECTORIES`, `addBuiltPackage` entries in
`ai/package-runtime.ps1`, and the markers in `ai/build-runtime.ps1` and `verify.ps1`.

## Standalone launch

`<base>/solforge-ai/Solforge AI.exe` can also be started directly, without the
editor. The packaged shell's `resources/app/package.json` carries
`"solforgeAi": true` (written by `package-desktop.ps1`); with that marker the
shell enters Solforge mode even without `SOLFORGE_AI=1`.

**Launch kind** (`apps/desktop/src/solforge-mode.ts`)

- `editor`: env `SOLFORGE_AI=1` **and** `SOLFORGE_AI_PATCH` set. Everything in
  the launch contract above applies unchanged (per-project profile, control
  pipe, live editor MCP).
- `standalone`: every other Solforge launch (the marker, or `SOLFORGE_AI=1`
  without `SOLFORGE_AI_PATCH`). No control pipe is served, and an inherited
  `SOLFORGE_AI_PATCH` is dropped.
- Without the env and without the marker, upstream Desktop is unchanged.

**Per-user profile** (`apps/desktop/src/solforge-standalone.ts`)

- Root `<LOCALAPPDATA>/SolforgeNext/AiApp/_standalone`. The editor's project
  keys are 64 hex digits, so they cannot collide with it.
- `electron/` is the Electron userData and `SOLFORGE_AI_USER_DATA`. `home/` is
  `DSH_HOME`, and the profile lives in `home/profiles/desktop`.
- A direct launch uses these locations, except that an explicit absolute
  `--user-data-dir=` argument is honoured (the focus fallback). A launcher that
  sets `SOLFORGE_AI=1` may supply its own `SOLFORGE_AI_USER_DATA` and `DSH_HOME`.
- Defaults (only when unset): `SOLFORGE_AI_RUNTIME_DIR=<base>/ai-runtime`,
  `SOLFORGE_AI_NODE=<base>/ai-runtime/node/node.exe`,
  `DSH_TELEMETRY_DISABLED=1`, `SOLFORGE_AI_DISABLE_OFFICE=1`.
  `<base>` = `resources/../..`.
- `SOLFORGE_AI_SKIP_WELCOME` is not set, so the welcome page collects the API key.
- Settings live in the standalone profile's `electron/solforge-standalone.json`,
  created with defaults on first launch (never created in an editor
  per-project directory; editor mode uses the defaults unless that file exists). An invalid file is left untouched and the defaults are used:
  `{"sharedResourcesPath": "<home>/Desktop/ai/ai-mcps-skills-consilidated",
  "workbenchRoot": "", "importClaudeServers": ["graphify", "rea"]}`.
  An empty `workbenchRoot` means discover it.
- Single-instance lock: per userData, so one standalone window per user.
  Starting the exe again focuses it.
- Fatal recovery offers Restart, and the development "restart app host" item
  is shown. A relaunch is safe because the profile and overlay are regenerated
  at every start. Closing the window still quits, and there are still no
  updates and no `dsh://` registration.

**App overlay** `<DSH_HOME>/solforge-app-overlay.yml`

The shell generates this file at every start, in both launch kinds, before the
logs path and the lock. It is written to a temp file and then renamed. It is
kept for the process lifetime, because the Host re-reads it on every restart.
It holds one `- insert:` list, with scalars written as JSON (as in the C#
overlay), or `[]` when there are no rows.

- `solforge-shared-skills`: `@deepseek-ai/dsh-skill-filesystem` with
  `<collection>/skills`, plus `<base>/Integrations/skills` when
  `solforge-model-workflow/SKILL.md` exists. Added only when the provider
  package is present in ai-runtime.
- `solforge-destiny-modder-mcp`: a TypeScript port of
  `SharedHarnessResources.Inspect`. It checks the Workbench layout and
  `memory/schema/001_records.sql`, the server files, and the
  `@modelcontextprotocol/sdk` and `zod` files.
  - The Workbench is discovered from `<base>` itself, `DestinyWorkbench` beside
    `<base>` and beside its parent, then
    `<home>/Desktop/d2moddingsuite/DestinyWorkbench`.
  - Command: the ai-runtime Node with `src/index.ts`, cwd the server directory.
  - Env: `WORKBENCH_ROOT`, `RE_DB=<wb>/memory/re-notes.db`,
    `WORKBENCH_DB=<wb>/memory/workbench.db`.
  - `failOnStartupError: true`.
  - Node's version metadata is not read (nothing is executed). The ai-runtime
    Node is >= 24.9 by packaging.
- `solforge-app-skills`: `@deepseek-ai/dsh-skill-filesystem` (`providerName:
  solforge-app-skills`) over
  `<ai-runtime>/node_modules/@deepseek-ai/dsh-experimental-solforge-automatic/skills`,
  the app-owned `solforge-automatic` skill that carries the automatic-mode
  agent hint. Both launch kinds; omitted when the skill or the provider is
  missing (always in the unpackaged dev tree).
- `solforge-graphify-mcp`, `solforge-rea-mcp`: imported from the top-level
  `mcpServers` in `~/.claude.json`.
  - Only stdio entries with a string command and string args are accepted.
  - `serverName` must match `[A-Za-z0-9_-]{1,32}`.
  - Env entries default to references: `NAME: !!js "process.env.NAME ?? ''"`.
    The value is never read into the file or logged, and the Host supplies its
    own environment value. Only names on a non-secret allowlist
    (`GRAPHIFY_BACKEND`, `NODE_ENV`, `LOG_LEVEL`, `DEBUG`, `NO_COLOR`,
    `FORCE_COLOR`, `TERM`, `PYTHONUNBUFFERED`, `PYTHONIOENCODING`,
    `PYTHONUTF8`) are copied literally, and only when the name does not match
    `KEY|TOKEN|SECRET|PASS|PWD|AUTH|PAT|CRED|COOKIE|SESSION|PRIVATE|_URL|URI|DSN`
    and the value is not a URL with `user:pass@`.
  - A server whose command or any argument looks credential-like (key, token,
    secret, password, pass, pwd, auth, bearer, cookie, credential or pat as a
    word, or a URL with `user:pass@`) is not imported; the status line names
    only the server.
  - `failOnStartupError: false`. A server launched through `npx` (rea) may
    fetch from npm on first run.
- Editor mode: only `solforge-app-skills` and the imported Claude servers,
  minus any `id` or `serverName` the editor's launch overlay already defines.
  The shared skills and destiny-modder rows are the editor's decision (its own
  preflight); the app never re-adds them, so a destiny-modder the editor
  omitted cannot abort an editor-launched Host.
- The live editor MCP is never written here: neither `solforge-next-mcp`
  (`serverName: solforge_next`, Solforge Next) nor `solforge`
  (`serverName: solforge`, solforge-next-v2 `LaunchOverlay`). It exists only in
  the editor's own overlay.
- Status lines go to the startup console as `Solforge AI: ...`. They carry
  paths and names, never env values.

**Host environment** (set explicitly on the Host spawn, not only on `process.env`)

| Variable | Value |
| --- | --- |
| `SOLFORGE_AI` | `1` (both launch kinds) |
| `SOLFORGE_AI_MODE` | `standalone` or `editor` |
| `SOLFORGE_AI_PROJECT_NAME` | editor only: `SOLFORGE_AI_PROJECT` when set, else the basename of the launch working directory, or empty when that directory is `DSH_HOME` |
| `SOLFORGE_AI_USER_DATA` | absolute Electron userData (both kinds) |
| `SOLFORGE_AI_APP_OVERLAY` | absolute path of the generated app overlay |
| `SOLFORGE_AI_PATCH` | editor only (unchanged contract) |
| `SOLFORGE_AI_DISABLE_OFFICE` | passed through |

**Host patch order in Solforge mode** (`apps/desktop-host/src/index.ts`)

1. The bundle patch: `@deepseek-ai/dsh-experimental-solforge-bundle/cordis.patch.yml`,
   resolved with `createRequire(installAnchor)`. If the package exports hide
   the file, the anchor's `node_modules` lookup paths are used instead.
   Required.
2. `SOLFORGE_AI_PATCH`, when set.
3. `SOLFORGE_AI_APP_OVERLAY`. Required.

Each layer must be an existing absolute file, or Host startup fails with a
named error. These layers come after the profile bundles, the profile patch and
the home patch. In the bundle, the rows `solforge-automatic` and
`ui-solforge-automatic` are `disabled` unless `SOLFORGE_AI === '1'`.

**Mode indicator.** The client package shows "Standalone" or
"Connected to Solforge <project>" from `SOLFORGE_AI_MODE` and
`SOLFORGE_AI_PROJECT_NAME` (`-` when the name is empty). In editor mode it
shows "editor tools not loaded" when no tool named `mcp__solforge_next__*` or
`mcp__solforge__*` was registered when the page was served. Both editor
serverNames are valid: `solforge_next` (Solforge Next) and `solforge`
(solforge-next-v2).

**Packaging.** The three Solforge app packages are not in `pnpm-lock.yaml` yet,
because no relink has been authorized.

- `package-runtime.ps1` moves `packages/experimental/solforge-*` and
  `client-ui-solforge-*` out of the `pnpm deploy` snapshot into a side
  directory. `package-desktop.ps1` keeps them out of its deploy snapshot too
  (the shell does not use them; they ship only in ai-runtime).
- It then adds `package.json`, `lib/**`, `locale/*.json` and `skills/**/*.md`
  of each package to `node_modules/@deepseek-ai/*` with `addBuiltPackage`.
- The fill-in loop completes their workspace closure, and `checkPackage`
  checks each one.
- It also checks that the bundle's `cordis.patch.yml` exists and registers
  both plugins.
- `build-runtime.ps1` requires those build outputs (and the app skill) and the
  new markers in `main.js` and the Host `index.js`.
- `verify.ps1` checks the three packages, the app skill, the bundle patch
  registering both plugins, the Host's `SOLFORGE_AI_APP_OVERLAY` and bundle
  markers, `"solforgeAi": true` in `resources/app/package.json`, and
  `solforge-app-overlay.yml` in the shell's `main.js`.
- In the dev tree the packages are unlinked. Only the packaged layout resolves
  the bundle, so an unpackaged Solforge-mode Host fails loud at step 1.

## Packaging method and hardening

Both packagers snapshot the owned workspace to a unique temp directory and run
`pnpm deploy --legacy --prod --offline` only there (scripts disabled), then
restore legacy hoists, materialize links and check the dependency closure,
native-addon presence (win32-x64), absence of links, and an absolute-path text
scan (scratch, source, store, temp and `C:\Users` paths). `ai/package-desktop.ps1`
also verifies every bare import of the shell's `lib/main.js` resolves inside
`resources/app`, prunes non-win32-x64 prebuilds, refuses `app-update.yml`, and
writes `solforge-ai-manifest.json` (paths, sizes, SHA-256, Electron version,
zip name and hash). Manifests certify structure and file integrity only.

## Known gaps

- `Solforge AI.exe` still carries Electron's own exe icon and version resources;
  no rcedit is bundled. The window/tray use the upstream icons (no Solforge
  artwork exists in the repo yet).
- How the desktop UI picks its initial workspace folder (host cwd is the profile
  directory) and whether the welcome gate sees an env-provided API key are not
  verified; `SOLFORGE_AI_SKIP_WELCOME=1` bypasses the welcome page.
- Standalone launch has not been run. Still unverified:
  - the per-user profile and welcome/API-key flow;
  - Host acceptance of the generated overlay, including an empty `[]` list and
    the `!!js process.env` env references;
  - the destiny-modder, graphify and rea MCP handshakes;
  - the mode indicator.
- rea is started through `npx -y rea-agents@...`, which may download from npm
  on first run. That network access is outside the packager's offline
  guarantee.
- The destiny-modder preflight does not read Node's version metadata. An
  overridden `SOLFORGE_AI_NODE` older than 24 is reported only when the server
  fails to start.
- Without `SOLFORGE_AI_PROJECT`, the badge name relies on the editor setting
  WorkingDirectory to the project folder (or to `DSH_HOME` with no project).
  Neither editor passes `SOLFORGE_AI_PROJECT` yet.
- Automatic mode: quota failover is not implemented (no fallback-model ladder
  exists inside the app); a quota, rate-limit, auth or unknown model failure
  stops the goal. Nudge timing, resume across a real restart, launch
  reconciliation and auto-allow against live approval requests are unverified.

## Current handoff

`build-runtime.ps1` has been run (all gates pass). Packaging scripts and verify
are syntax-checked only; they have not been executed, the app has never been
launched, and no MCP handshake or model call has been exercised. Compiling is not
live acceptance. Run order: `ai\build-runtime.ps1`, `publish.ps1 -ElectronZip`,
`verify.ps1 -BuildFolder`, then a user-authorized launch from the editor.

The standalone launch, app overlay and ordered Host patch list (2026-10-24) have
been type-checked only (`tsc --noEmit` for `apps/desktop` and
`apps/desktop-host`). The packager scripts were syntax-checked, not run.

Review fixes (2026-10-24, later): `ai\build-runtime.ps1` passes (8/8 gates and
all marker checks). `package-desktop.ps1`, `package-runtime.ps1` and
`verify.ps1` were parse-checked and their embedded packer scripts were checked
with `node --check`; none was run. No build folder was published and the app
was not launched.

## Review fixes (2026-10-24, editor link, tiers, branding)

Source, packaging script and docs. `ai\build-runtime.ps1` passes (8/8 gates) and `tsc --noEmit` passes for
`apps/desktop` (host and client configs). Solforge AI was not launched and `package-desktop.ps1` was not run, so the
fuse step below is untested. Compiling is not acceptance.

- Editor link (`apps/desktop/src/solforge-editor-link.ts`): runs only while the `solforge-editor` plugin card is
  enabled. The shell calls `setEnabled` from `setConfig` and from the config-file watch; off unpairs and closes
  `/editor/events`. The event stream is held only while the app window is shown, because the editor treats a held
  stream as "app attached" and then hides its own approval cards. A hidden window closes the stream within one 5 s scan,
  and the editor's fallback cards return. The link notices an editor that restarted its MCP server in the same process
  (its discovery file is gone or names a new base URL) and pairs again. Before, it stayed "paired" against a dead port.
  The 403 refusals `not_solforge_ai` and `peer_mismatch` are terminal, like `denied`, until the user presses Connect.
- Project follow (`solforge-shell.ts`): the last project sent to the renderer is tracked apart from the followed
  project. Reopening a project after the editor closed it is delivered again. An explicit Open project or Choose
  folder always reaches the renderer. `solforge-plugins` publishes the app config's `lastProject` (rewritten on every
  live switch) as the page's project, so a page reload no longer forces the workspace back to the project the Host
  started with.
- Editor panel store (`client-ui-solforge-editor`): a refresh asked for while the same read is in flight is re-run
  once when that read settles, so a reviewed edit's rows are no longer stuck on "comparing". Pairing with a different
  editor pid clears the AI log, sessions, edits, context and folders, because the new editor restarts its log
  sequence at 1. Late replies for the previous editor are dropped.
- Tiers: a subagent that inherited a tier its Session may not select (Mid or Frontier while "subagents may use" is off)
  is moved to `solforge-tier/low` before its first request. Explicit selection is still checked by the Session
  policy. The Plan-first injection names the exact arguments (provider `solforge-tier`, model `low`). The tier
  ladders and the composer seat now say when a Frontier, Mid or Low ladder holds no in-loop (OpenAI-compatible)
  model, and the adapter's "no usable entry" error explains why. Subscriptions are still not in-loop routes; see
  Known gaps.
- Subscription subagent tools (`solforge-model-overlay.ts`) are generated only for Claude or Codex subscription models
  placed in a subagent role ladder (SubagentPlanner, SubagentExecutor or SubagentReviewer, under Advanced ladders).
  A model that is only enabled no longer gets a global delegation tool. Those ladders are part of the launch
  signature, so a change asks for a Host restart.
- Branding: in Solforge mode the `settings.agentPreset`, `entryCustomization`, ui-conversation and ui-plugin-market
  dictionaries replace the product name "DSH" with "Solforge AI" in every locale (the page-global check). The desktop
  `startupAddressInUse` text was already Solforge-specific (English for all locales).
- Packaging: `ai/package-desktop.ps1` step 1c turns off the Electron fuses RunAsNode,
  EnableNodeOptionsEnvironmentVariable and EnableNodeCliInspectArguments on the staged `Solforge AI.exe`, then reads
  them back. It uses `@electron/fuses` from the owned pnpm store, and a missing library or failed flip stops
  packaging. In Solforge mode the Host already runs on the ai-runtime `node.exe`, so nothing in the app needs
  Electron-as-Node. This keeps an agent from running Solforge AI.exe in Node mode (`ELECTRON_RUN_AS_NODE=1`) to pass
  the editor's pairing image check.
- Known gaps (new): Claude and Codex subscriptions serve only as subagent backends, not as tier ladder entries.
  Routing them in-loop needs an llm adapter over the native CLIs, which was not built. Google subscriptions are still
  unwired. The subscription tools remain global, and placing a model in a subagent ladder is the user's opt-in for
  every session. They are not limited by the caller's tier. With "subagents may use Mid" on, a delegation without
  provider or model still inherits Mid.

## Cold start (G4, 2026-10-24)

- The shell shows a branded splash (`apps/desktop/src/solforge-splash.ts`) at `app.whenReady`, before the overlay,
  the Solforge shell and the Host; the main window replaces it when the workspace has loaded. A fatal-recovery
  dialog hides it (never closes it first, so `window-all-closed` cannot quit under the dialog).
- The app overlay is no longer written at module load (it delayed `app.whenReady` by ~0.6 s and was rewritten by
  `installSolforgeShell` anyway); `writeFallbackSolforgeOverlay()` runs only when the shell did not set it.
- MCP rows start in the background: `@deepseek-ai/dsh-mcp-client` has `awaitStartup` (default true = upstream
  behaviour). With `awaitStartup: false` the plugin activates without waiting for the handshake/`npx` fetch; tools
  register when discovery finishes; `failOnStartupError` is ignored. Every app-overlay row (`solforge-standalone.ts`
  `stdioRow`) and the v2 editor launch-overlay rows (`Solforge.AiLauncher/LaunchOverlay.cs`) emit
  `awaitStartup: false` and `failOnStartupError: false`.
- Timing: the shell prints `Solforge AI startup: <mark> +<ms>ms` and writes `<userData>/logs/startup-timing.json`;
  the Host prints `Solforge AI host startup: <step> +<ms>ms` (module-loaded, profile-resolved, plugins-started,
  ready-sent).
- Cold Host start (2026-10-24, late): the cost is reading ~1,400 cold module files under real-time antivirus, not a
  slow plugin. The OpenTelemetry SDK (dsh-otel) and turndown/domino (dsh-tool-web) now load on first use; the Host
  prints `imports-done` (time of the last module load before the application resolved, and the module count;
  loads during plugin apply count too), recorded as `hostMarks` in `startup-timing.json`. The load-counting hook
  is removed right after that mark. There is no `plugins-applied` mark (runProfile exposes no boundary between
  plugin apply and service start). `ai/warm-runtime.mjs <ai-runtime>` starts the Host once headless in a throwaway
  profile so the files are scanned before the user's first launch; `package-runtime.ps1` runs it after publishing
  and warns when it exits non-zero (a Host that exits before ready exits 3). Bundling the Host into a few chunks is
  still open.
- turndown load: a failed `require('turndown')` / GFM plugin is logged once (plugin logger, error) and web_fetch
  returns `[HTML conversion unavailable: turndown failed to load]` instead of the unsafe-markup marker.
  `build-runtime.ps1` (workspace tool-web) and `package-runtime.ps1` (staged tool-web, packaged node) load both
  modules through tool-web's own `createRequire` anchor and fail when either cannot be loaded.

## CLI write mode limits (review fixes, 2026-10-24)

- Default: `cliWriteFrontier`, `cliWriteMid` and `cliWriteLow` are all `off` (Host `DEFAULT_FLAGS` and the shell's
  app-config defaults). An unreadable or malformed `solforge-app.json` keeps the flags adopted before (or the
  all-off defaults when there were none); a missing file reads as the defaults.
- Refused before the gate and the approval (the turn runs read-only, reason recorded `write refused: ...`): a Session
  without its own non-empty `header.cwd`; a drive root; a folder that is, contains or sits inside home (contains
  only), %LOCALAPPDATA%, %APPDATA%, DSH_HOME, the Host's cwd (profile), the app user data, %LOCALAPPDATA%\Solforge,
  ~/.claude, ~/.codex, ~/.gemini, ~/.agents (all compared by realpath); a folder that is, contains (3 levels) or sits
  inside an editor-managed project (`sunrise.project.json`).
- Engines: Claude only on Windows. Codex is refused on win32 (workspace-write sandbox not verifiable); elsewhere it
  runs with `--disable shell_tool|unified_exec|apps|plugins|browser_use|computer_use` and
  `sandbox_workspace_write.exclude_tmpdir_env_var/exclude_slash_tmp=true`. Gemini and agy write mode are refused
  (no changed-file report; agy has no MCP-off flag). Gemini runs with `--allowed-mcp-server-names
  solforge-no-mcp-server -e none` in both modes (flags from docs, no Gemini CLI installed). agy read mode still has
  no MCP restriction.
- Claude write: `--setting-sources ""` (no user/project/local settings) and `--settings` with hooks off,
  `additionalDirectories: []` and deny rules for .claude/.gemini/.codex/.agents, .mcp.json, AGENTS.md, CLAUDE.md,
  GEMINI.md. Unverified live: that the installed claude accepts an empty `--setting-sources`.
- After a write run: reported paths are realpath-resolved; a path outside the folder, a reported config path, or a
  changed config file (mtime/size snapshot of the top level and the config folders) fails the turn. The files are
  already changed (nothing is rolled back); the message says so and the CLI record lists them.
- A write turn that reached the CLI and failed (any code but CLI_MISSING, including RATE_LIMIT/QUOTA) ends the step;
  the ladder is not continued. The record carries the known changed paths or `changesVerified: false` (UI:
  "changed files unknown").
- The approval card and the CLI prompt name the engine's real limits (`writeLimitsOf`) and say edits go straight to
  disk without the editor's review or undo.
- Auto-allow answers the `solforge-cli-write` grant only while a goal the user typed or resumed covers the asker
  (never on the plain Auto-allow setting, never for an `automatic_start` goal). Another request with that origin gets
  the regular filters. Plan first plans an automatic start prompt only for such a goal, and only its goal text.
