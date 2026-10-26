# Solforge AI app: design spec v3 (requirements A–F)

Status: design only. Nothing in this document has been built, launched or accepted. Line numbers come from a read-only pass on 2026-10-24 and will drift. **Compiling is not acceptance.** Every item below is accepted only after it has been seen working in the launched app, and in the launched v2 editor where the editor is involved.

Abbreviations used throughout:
- `RT` is `solforge-next/ai/runtime`.
- `V2` is `solforge-next-v2/src`.
- "Solforge mode" means `process.env.SOLFORGE_AI === '1'` on the host, and `__SOLFORGE_AI_MODE__` present in the client.
- Every runtime change in this spec is gated on Solforge mode. Upstream DSH behaviour stays unchanged when the mode is off.

## 0. Rules that apply to everyone

- Do not run `pnpm install`. Register new packages the way the existing `solforge-*` packages were registered:
  - a hand-edited `pnpm-lock.yaml` importer;
  - a `node_modules` junction;
  - `tsconfig.base.json` paths;
  - a `tsconfig.host.json` or `tsconfig.client.json` reference;
  - `addBuiltPackage` in `ai/package-runtime.ps1`;
  - the gates in `verify.ps1`.

  Only P2 edits these files (see §9).
- PowerShell 7 is `pwsh`. Always pass an explicit working directory.
- Forbidden:
  - OpenCode;
  - `E:` and `C:/dawn`;
  - git commit or push (`solforge-next-v2` stays uncommitted on `ue5-ui`);
  - writing to `09-solforge` or `deepseek-harness` (both are read-only);
  - adding or running tests;
  - model or paid calls;
  - killing processes you did not start.
- Credentials:
  - Never print, log, copy or persist credential values.
  - Config holds variable **names** and file **paths** only.
  - The credential file is read in memory, only in the Electron main process (§5.5).
- **Existing user configuration is preserved.** The user has already configured models, routes and tiers in `%LOCALAPPDATA%\Solforge\harness-settings.json` and in the editor's Studio settings.
  - Nothing resets, migrates destructively or rewrites fields the writer does not own.
  - All writes are read-modify-write with a compare step (§5.4).
- **Editor feature parity must not regress.** The only intended change is moving the four panels to the app. The editor keeps a fallback for every approval path (§6.6).

## 1. Target architecture in one picture

```
 Solforge editor (V2, WinForms+ImGui)                       Solforge AI.exe (Electron main)                 Host (DSH, node)            Renderer (client plugins)
 ─────────────────────────────────                          ───────────────────────────────                 ────────────────            ─────────────────────────
 McpHttpServer 127.0.0.1:<port>                              solforge-shell.ts                               solforge bundle rows        client-ui-solforge-brand   (P1)
   /mcp            Bearer McpToken  ◄── shim, agents         ├ solforge-editor-link.ts (P4)  ── HTTP/SSE ──► solforge-plugins (P2)       client-ui-solforge-plugins (P2)
   /editor/*       Bearer UiToken   ◄─────────────────────── │   holds UiToken in memory only               solforge-model-tiers (P3)   client-ui-solforge-model-tiers (P3)
   /editor/pair    peer-pid check + user confirm             ├ solforge-app-config.ts (P2)                   solforge-automatic (exist.) client-ui-solforge-editor  (P4)
 McpDiscovery  server-<pid>.json (McpToken, project)         ├ solforge-credentials-env.ts (P3)                                          client-ui-solforge-automatic (exist.)
 harness-settings.json  ◄──── shared tier/model truth ────►  └ preload-app.ts: window.solforgeApp / window.solforgeEditor  ─────────────────► (bridge, Electron only)
 last-project.json (written by editor)
```

Three decisions shape everything else:

1. **The editor UI channel is not MCP.**
   - It is `/editor/*` on the same loopback server, with a separate `UiToken`.
   - The token reaches only the Electron **main** process. It is never in the host environment, a file, the renderer's storage, or `server-<pid>.json`.
   - The renderer gets data through a narrow `contextBridge` API.
   - Agents, which run as host children, therefore cannot approve their own requests.
2. **There is one shared app profile** for both standalone and editor launches: `%LOCALAPPDATA%\SolforgeNext\AiApp\shared`.
   - Only the workspace, which is the open project, changes.
   - Sessions are separated by workspace (the session `cwd`), not by profile.
3. **There is one source of truth for models and tiers:** `%LOCALAPPDATA%\Solforge\harness-settings.json`, in its existing schema. Tiers map onto the existing role ladders, so the editor loads the file unchanged.

## 2. Requirement A: Solforge branding and styling

### 2.1 Assets

All assets are copied from existing Solforge files. Nothing is downloaded.

| Use | Source | Destination |
|---|---|---|
| Brand mark (sidebar, hero, about) | `V2/SunriseStudio.Editor/Assets/Sol/emblem-mark.png` | `RT/packages/experimental/client-ui-solforge-brand/src/client/assets/sol-mark.png` |
| Gold mask (hero tint / hover) | `V2/SunriseStudio.Editor/Assets/Sol/emblem-gold-mask.png` | `.../assets/sol-gold-mask.png` |
| Window/app icon PNG | derived from `emblem-mark.png` (256 px on transparent) | `RT/apps/desktop/resources/icon.png`, `icon-windows.png` |
| Tray `.ico` and exe icon | `V2/SunriseStudio.Editor/Assets/Sunrise.ico` until a Sol `.ico` exists | `RT/apps/desktop/resources/tray-windows.ico` |

Notes:
- The `.svg` icon variants (`icon.svg`, `icon-windows.svg`, `icon-macos.*`) stay unless a Sol SVG exists. They are not used on Windows.
- If no offline tool can produce an `.ico` from the PNG, P1 reuses `Sunrise.ico` and reports it. P1 does not download a converter.
- **Exe icon and version info** (`package-desktop.ps1` 452 says there is no rcedit): use `rcedit` only if it is already present in `node_modules` (search `node_modules/.pnpm` for `rcedit`). Otherwise report the gap. Do not download one.

### 2.2 Tokens

The token set comes from `V2/SunriseStudio.Viewport/SolTheme.Tokens.cs`. It is registered as a theme `solforge` (dark), via `ctx.theme.register` in `ui-theme/src/client/index.ts`, and made the default in Solforge mode.

| Token | Hex | Maps to (`--dsw-*` / theme token) |
|---|---|---|
| Bg | `#151515` | app background, sidebar |
| Panel | `#1f1f1f` | main surface, cards |
| Surface / Header | `#2a2a2a` | raised surfaces, inputs, headers |
| Text | `#c8c8c8` | primary text |
| Accent | `#0070e0` | primary buttons, focus ring, selection, toggles |
| Brand | `#f2a33a` | brand mark tint, mode badge, sparing highlights only |
| Ok / Warn / Bad | `#6cbf43` / `#e3a33a` / `#e05252` | status |

The theme is dark only. A light variant is optional. Corners and density follow UE5: small radius (3–4 px) and compact rows.

Follow `RT/docs/web-styling.md`. If a component needs a token that `ThemeDefinition` lacks, use `overrideTokens`. Do not edit the upstream CSS.

### 2.3 What changes where

| Item | Mechanism | Owner |
|---|---|---|
| Sidebar "DSH Local Build", whale and version chip | register `sidebar.brand.mark` (Sol mark) and `sidebar.brand.name` ("Solforge AI", plus the project name chip when one is open) from the new `client-ui-solforge-brand` | P1 |
| Window title | `ai/build-runtime.ps1` `$ClientTitle = 'Solforge AI'` (P2 edits the script during scaffolding; P1 verifies) and `apps/web/index.html`/`vite.config.ts` defaults are left as-is (upstream) | P2/P1 |
| Empty hero "Into the Unknown", Preview badge, whale | slot `conversation.hero.brand.mark` gets the Sol mark. The headline and badge are a source edit in `ui-conversation/src/client/skeleton/EmptyHero.tsx`: in Solforge mode, show "Solforge AI" plus the project name (or "Open a project") and no Preview badge. Strings are added to `ui-conversation/src/client/locales.ts` as new keys; the existing keys stay. | P1 |
| `brand.localBuild` locale | not overridden (duplicate registration throws). The slots above replace every place it shows in Solforge mode. | P1 |
| Desktop About and product strings | `apps/desktop/src/locale.ts`: in Solforge mode, "Solforge AI", no DeepSeek or Harness wording. `main.ts` 537 uses it. | P1 |
| Window and tray icons | `main.ts` 328–342, 1090, 1151 use the resources above. `package-desktop.ps1` 309–310 copies them. | P1 |
| Welcome window (DeepSeek key / sign-in) | never shown in Solforge mode: make `solforgeSkipWelcome()` unconditional when `SOLFORGE_AI=1`, not only when the env flag is set | P1 |
| Theme | `client-ui-solforge-brand` registers and selects theme `solforge` | P1 |

## 3. Requirement F: removing sign-in and the DeepSeek account flows

### 3.1 Patch rows disabled in Solforge mode

These live in P1's patch file, `solforge-bundle/patches/brand.patch.yml` (§9.2). Each row is `- id: X` with `disabled: !!js "process.env.SOLFORGE_AI === '1'"`.

| Row | Origin |
|---|---|
| `ui-settings-account` | web-app 136 (More-menu Sign in, account section, sign-in slot, quota notice, onboarding overlay) |
| `account-controller` | web-app 139 |
| `ui-deepseek-model-label-1017` | web-app 416 |
| `deepseek-account` (`dsh-deepseek-account-platform`) | base 120 |
| `llm-deepseek-account` | base 530 |
| `session-log-deepseek`, `plugin-package-inventory-deepseek` | base 43 / 85: P1 confirms they are account-only before disabling; if unsure, leave enabled and record it |

The rows `llm-deepseek` (527), `deepseek-llm-api-extensions` (37), `web-search-deepseek`/`web-deepseek-api` (469/474) and `agent-default-model` are owned by P3, because they are model plumbing (§5.6).

### 3.2 Replacement More menu

Disabling `ui-settings-account` empties the `settings.launcher` slot. The fallback is a plain Settings button (`SettingsRoot.tsx` 222–238).

`client-ui-solforge-brand` registers its own `settings.launcher` menu with these items:
- Settings (Ctrl+,);
- Feedback, which opens the local `ai/` docs folder or a Solforge feedback link, chosen by P1. It must not be the DeepSeek questionnaire.
- About Solforge AI.

There is no Sign in item.

### 3.3 Desktop shell account code

In Solforge mode the following are not constructed (P1, `main.ts`):
- `account-backend.ts`;
- `welcome-backend.ts`;
- `platform-view.ts`;
- `preload-platform-account.ts`.

The files stay for upstream mode.

The launcher's `DEEPSEEK_API_KEY` allowance and the `DSH_*` DeepSeek env in `V2/Solforge.AiLauncher/SolforgeAiHost.cs` 252–261 are removed by P4 (editor side). The DSH variables needed for the profile path remain.

## 4. Requirement B: Solforge MCPs and features as plugins

### 4.1 Mechanism (chosen: settings cards plus overlay regeneration)

Rows from host `--patch` layers come back `unaddressable` in `listPlugins()`, and making real optional bundles needs install-anchor surgery. So:

- **Every Solforge plugin is a `plugins.item` card** in a new section "Solforge" at the top of the Plugins page. It is rendered by `client-ui-solforge-plugins`.
- **The card state lives in one app config file,** `%LOCALAPPDATA%\SolforgeNext\AiApp\shared\electron\solforge-app.json`. This is a rename and extension of `solforge-standalone.json`; the old file is read once and merged (§7.3).
- **Writes go through the Electron main process,** via the `window.solforgeApp` bridge (§8.2). The renderer never writes files.
- **Applying a change:**
  - **MCP rows and skill roots:** the main process regenerates `SOLFORGE_AI_APP_OVERLAY` (in `solforge-standalone.ts`, renamed in role to "app overlay") with the enabled rows only. The card then shows "Restart AI host to apply" with a button that calls `solforgeApp.restartHost()`.
    - Unverified: restarting the host from main, through the existing `backend-controller`, must be confirmed at launch.
    - There is no live toggle, because the mcp-client has none.
  - **Feature plugins** (automatic, tiers, editor panels):
    - Their host rows are always present in Solforge mode.
    - Each host plugin reads `enabled` from the config on start and when the config changes. The main process notifies the host over host IPC; the fallback is a file watch in the host plugin.
    - While disabled, a plugin is inert: no waterfall hooks, no commands, no slot registrations.
    - Existing volatile settings, such as `solforge-automatic`'s `maxNudges` and the others, keep their existing storage. The card renders them with the existing `configForms` path that `client-ui-solforge-automatic` uses today, moved from `settings.general.item` to `plugins.item`.

### 4.2 Plugin list and fields

| id | Card title | Fields (type, default) | Applies via |
|---|---|---|---|
| `solforge-editor-mcp` | Solforge editor | `enabled` bool, true. `solUpstreams` bool, true (`--sol --upstreams <default>`). `geminiDelegate` bool, false (`--gemini`). `clinePassDelegate` bool, false (`--clinepass`). `toolCallTimeoutMs` int, 180000. Read-only: shim path (from `solforge-ai-mcp.json` / `FindShim`), editor status (pid, project, connected). Retail tools: shown as included; they ride the editor tool set. A toggle exists only if P4 adds a shim filter flag (optional; otherwise read-only text). | overlay row `solforge` (stdio shim, **no `--project`**; the shim follows discovery) |
| `destiny-modding` | Destiny modding | `mcpEnabled` bool, true (destiny-modder row, command/args/cwd from the descriptor `destinyModder`). `skillEnabled` bool, true (destiny-2-modding skill under `sharedResourcesPath`). `workbenchRoot` path. `sharedResourcesPath` path. `toolCallTimeoutMs`. | overlay rows `destiny-modder`, `solforge-shared-skills` |
| `graphify` | graphify | `enabled` bool, true if found in `~/.claude.json`. Read-only command/args. `toolCallTimeoutMs`. | overlay stdio row, imported as today (`stdioRow()`; credential-like args are never imported) |
| `rea` | REA | same as graphify. `startOnDemand` note: an `npx -y` download makes startup slow, so `failOnStartupError:false` | overlay stdio row |
| `solforge-automatic` | Automatic mode | `enabled` bool, true. `maxNudges`, `maxMinutes`, `idleSeconds`, `autoAllow` (existing volatile fields and semantics, unchanged: auto-allow is only for goal-covered asks and never `guard`/`hook` origins) | host row (existing) |
| `solforge-model-tiers` | Model tiers | `enabled` bool, true. `planFirst` bool, true. `subagentsMayUseMid` bool, true. `subagentsMayUseFrontier` bool, false. Read-only mirrors of harness `MaxParallelSubagents` / `MaxSubagentDepth` (editable here; written to harness-settings.json). Read-only: credential file path set yes/no. Button: "Edit tiers…" opens the tier picker (§5). | host row `solforge-model-tiers` |
| `solforge-editor` | Editor context & approvals | `enabled` bool, true. `notifyOnRequest` bool, true (toast plus taskbar flash). `openPanelOnRequest` bool, false. Read-only: editor auto-approve state (changed only in the editor's Preferences, never here). Status: paired / not paired / pairing pending. | client row `ui-solforge-editor`; main-process link (§6) |
| `solforge-skills` | Solforge skills | `appSkillsEnabled` bool, true (`solforge-app-skills`). Read-only list of skill roots. | overlay skill-filesystem rows |

### 4.3 Upstream cards in Solforge mode

| Card | Action | Why |
|---|---|---|
| Subagent (built-in) | hidden | Model choice is owned by tiers. Depth and count are edited on the Model tiers card, which writes both harness settings and the `subagent-model-selection-settings` policy (§5.5). |
| Web search (DeepSeek provider) | hidden; row disabled by P3 | DeepSeek-only provider |
| Agent loop, Shell | kept | generic |
| Agent teams, Voice input, Custom plugin indexes, Auto authorization review | moved from `EXTENSION_BUNDLES` to the "More" list when in Solforge mode | not Solforge features |

The demotion is a mode check in `RT/packages/util/official-bundles/src/plugin-manager.ts`: export a function `extensionBundlesFor(mode)` instead of mutating the static lists. Its consumers are in `ui-plugin-manager`'s `PluginManagerPage.tsx` grouping (≈1541–1600). Do not touch `catalog.generated.ts`.

The Plugins page copy in Solforge mode:
- "Built-in plugin settings" becomes "Solforge" plus "Built-in".
- The subtitle drops DeepSeek wording.

## 5. Requirement E: the tiered smart model system

### 5.1 Tier semantics

| Tier | Meaning | `HarnessRole` ladder (truth) | Also drives |
|---|---|---|---|
| **Frontier** (planner) | Plans; may lead low-tier tasks | `Planner` | `Reviewer` and `SubagentPlanner` keep their own ladders. The tier UI shows them under "Advanced" and offers "same as Frontier" (copies ids; it never links implicitly). |
| **Mid** (executor) | Executes the frontier plan; drives the conversation's tool loop | `Executor` | `SubagentExecutor` (Advanced, "same as Mid") |
| **Low** (free/simple) | Single, simple coding tasks, led by Mid or Frontier | `SmallFix` | `SubagentReviewer` (Advanced) |

Rules:
- A ladder is the exact ordered `ModelIds` list. Fallback moves to the next entry only on `RateLimited`/`CreditsExhausted` in the editor, and on `QUOTA`/`RATE_LIMIT` in llm-pi-ai.
- Disabled models and models in cooldown are skipped.
- There is no implicit provider, no cycling, and no model outside the ladder. This matches `HarnessRoutingEngine.cs` 101–198.
- `MISSING_CREDENTIAL`, `INVALID_CREDENTIAL`, `UNKNOWN_MODEL` and any failure after output has started stop the step and are shown. They never fall through.
- `HarnessModel.Tier` stays descriptive. The picker groups models by it for display only, and never changes it on existing models. A model *added* from the app gets the tier of the ladder it was added to.

### 5.2 The "Select model" button

`ui-model-selection` fills `conversation.input.model` (single/session). In Solforge mode its row (web-app 413) is disabled in `models.patch.yml`, and `client-ui-solforge-model-tiers` registers the slot instead.

- **Button label:** `Frontier › Mid › Low` heads, for example `opus-4 › gpt-5-mini › qwen-coder`, with a small "tiered" glyph. Tooltip: full ladders.
- **Popup:** three columns or sections, Frontier, Mid and Low. Each is an ordered list of the configured models assigned to that ladder.
  - Reorder by drag.
  - Add from "configured models not in this ladder".
  - Remove from the ladder (the model is not deleted).
  - Each row shows a status: ready, missing credential (name only), cooldown, or subscription CLI missing.
- **Session toggles** (stored as session events, not in the harness file):
  - "Tiered" on/off. Off means pin one model for this session, chosen from the configured models.
  - "Plan first" on/off. The default comes from the plugin card.
- **`/model`** popup: also replaced, with the same component.
- **Empty state.** When there are no configured models, show "No models configured" with buttons:
  - "Open Models & tiers settings";
  - "Set credential file…", which opens the picker; the path is stored in harness `CredentialFilePath`.

  The DeepSeek empty text is not shown.

### 5.3 How a session runs (planner, then executor, then low-tier subagents)

Host plugin `solforge-model-tiers` (P3):

1. **Virtual models.** Register an llm adapter route `solforge-tier` (`ctx.llm.registerAdapter(['solforge-tier'], adapter)`) with models `frontier`, `mid` and `low`, listed through `listModels` so the GUI catalog is non-empty.
   - Each `stream()` resolves the ladder at call time and calls `ctx.llm.stream` on the concrete route of the first eligible entry.
   - **Fallback happens only before the first content chunk.** If a QUOTA or RATE_LIMIT terminal finish arrives before any output, the adapter tries the next entry and records which one served. The served model is put in the session log as an attribution note, so model-visible input stays reconstructable.
   - Model metadata (context window, reasoning efforts) is the most conservative across the ladder entries: minimum context and the intersection of efforts. **Risk:** this must be checked against `prepareCall` validation at launch.
2. **Default selection.** `agent-default-model` is re-pointed to `solforge-tier/mid` in `models.patch.yml`, and `initializeDefaultModel` selects it for new sessions.
3. **Plan first.** When the session has "Plan first" on, then on each new user goal the plugin runs a **read-only planner subagent** on `solforge-tier/frontier` before the executor turn.
   - "New user goal" means a user message that is not a reply inside an active plan. `/quick <msg>` skips planning for one message.
   - The planner gets read-only tools only: the fs read/search tools and the editor MCP read tools. Its plan is appended as a durable message that the executor sees.
   - It uses the existing subagent spawn backends. Exact hook: the turn-start waterfall in the agent loop, which P3 identifies; if none fits, use a `/plan` command chained by the client before submit.
4. **Executor.** The session's own loop runs on `solforge-tier/mid`.
5. **Low tier.** The plugin writes the `subagent/model-selection-policy` event for each new top-level session through `subagent-model-selection-settings`: `enabled:true`, `allowedModels:['solforge-tier/low', +mid if subagentsMayUseMid, +frontier if subagentsMayUseFrontier]`, default `solforge-tier/low`.
   - Depth and parallelism come from `MaxSubagentDepth`/`MaxParallelSubagents`.
   - The planner can also delegate to low directly, which is the "led by frontier" case.

### 5.4 Single source of truth and sync

- **File:** `%LOCALAPPDATA%\Solforge\harness-settings.json`. The schema is unchanged, so no `Tiers` field is added.
  - Enums are strings, and no unknown fields are allowed (`UnmappedMemberHandling.Disallow`).
  - The size limit is 1 MB. Saves are atomic.
- **App side.** The host plugin `solforge-model-tiers` reads it and watches its mtime and hash.
  - Writes are read-modify-write. Only the touched fields are changed: `Routes[Planner|Executor|SmallFix|Reviewer|Subagent*].ModelIds`, `MaxParallelSubagents`, `MaxSubagentDepth`, added `Models[]` entries and `CredentialFilePath`.
  - Before rename, re-read and compare the hash. On a mismatch, reload, re-apply the edit, and if it still conflicts, show it to the user.
  - Atomic write: temp file in the same dir, then replace.
  - The app runs a TypeScript port of `HarnessSettingsValidator` (unique ids, routes reference existing models, `BaseUrl` http(s) with no userinfo or query, the `IsLaunchableCredentialName` rule) and refuses to save invalid content.
  - A shared TS type for the schema is in the contract file (§9.1).
- **Editor side** (P3, V2):
  - `HarnessSettingsStore` gains a `FileSystemWatcher`/mtime check and a `Changed` event. `StudioForm` reloads when the settings window is not dirty; when it is dirty, it shows "Changed by Solforge AI — reload?".
  - `SaveHarnessSettings` uses the same compare-before-replace.
  - The editor persists `StudioSettings.CredentialFile` into `CredentialFilePath` on save, only when the file's value is empty or equal. It never overwrites a different value without asking. This lets the standalone app find the credential file.
- **The editor UI** (`StudioForm.SolforgeAiSettings.cs` `DrawRoleLadder`) labels Planner, Executor and SmallFix as "Frontier (Planner)", "Mid (Executor)" and "Low (SmallFix)". Behaviour does not change.

### 5.5 Providers and credentials (fixing "No models available")

Mapping from `HarnessModel.Transport` to the DSH route:

| Transport | DSH route | Notes |
|---|---|---|
| `OpenAiCompatible` | `llm-pi-ai` provider, generated per model: route `sf-<Id>`, `api: openai-completions` (or the provider's API), `baseURL: BaseUrl`, `apiKeyEnv: ApiKeyVariable`, `models: [ModelId]` | config only, no key values |
| `ClaudeSubscription` | **not an in-loop route.** Served through the `dsh-subagent-claude-code` on-demand backend (native `claude` CLI login) | usable for the planner step and for subagents. If a Mid ladder entry is a subscription, the executor step is delegated to that backend. The session loop then acts as a thin orchestrator on the first API entry; if Mid has no API entry, the turn is a delegated run. P3 verifies this backend at launch. **pi-ai's own Anthropic OAuth is not used:** it is not an officially supported third-party sign-in. |
| `CodexSubscription` | same, via `dsh-subagent-codex` (native `codex` login, ChatGPT sign-in as `SubscriptionModelWorker` forces) | same as above |
| `GoogleSubscription` | **unavailable until verified.** `SubscriptionModelWorker.cs` 55–59 deliberately refuses it ("agy is not Gemini"). The picker shows it with "Official Gemini CLI headless contract not verified". Enabling it requires an installed official Gemini CLI signed in with a Google account and a verified headless/auth/tool-isolation contract, with no API-key fallback. It is a separate, user-approved step. | see §11 |
| `PiRpc` | not routable; shown as unsupported | |

How the `llm-pi-ai` config is delivered:
- The Electron main process (P3's `solforge-credentials-env.ts`, called by P2's `solforge-shell.ts`) generates the `llm-pi-ai:` providers section into the app overlay.
- The `llm-pi-ai` row is base; the overlay patches its config. That row's config then becomes overlay-owned, so the Models settings page cannot edit it. This is intended: the truth is harness-settings.json.

Credential resolution:
- The main process reads `CredentialFilePath`. If it is empty, the app asks the user to choose a path; there is no implicit default.
- It parses JSON or dotenv the same way as `V2/Solforge.Harness/Credentials/CredentialEnvironment.cs`.
- It injects **only** the variables named by enabled models' `ApiKeyVariable` that pass `IsLaunchableCredentialName` into the **host** spawn environment.
- Values are never logged and never written.
- Shell-tool children inherit host env, which is the same exposure as the editor-launch path today. **Residual risk, accepted and documented:** agents' shell commands can read these provider keys. The alternative, a `credentials` provider wrapper inside the host, has the same exposure.

DeepSeek rows (`models.patch.yml`, Solforge mode):
- disable `llm-deepseek`, `deepseek-llm-api-extensions`, `web-search-deepseek` and `web-deepseek-api`;
- re-point `agent-default-model`;
- in `ui-settings-models`, mode-gate the `deepseek-official` onboarding dialog and the DeepSeek editor (source edit) and add a "Solforge models & tiers" card (`settings.models.provider-card`) that opens the tier picker.

## 6. Requirement D: moving the editor panels to the app

### 6.1 Editor model changes (P4, V2, `StudioForm.Ai.Host.cs`)

- `AiRequest` gets:
  - `Id`: 128-bit random hex;
  - `Created` and `Expires` (Created + 5 min, as today);
  - `Kind`: `action`, `edit` or `workflow`;
  - `Session`: the `McpSession` label, client name and pid;
  - `Worker` flag.
- `AiRequestLogEntry` gets `Seq` (monotonic) and `Session`. The ring grows from 50 to 500. The Output Log copy is unchanged.
- `AiEdit` gets `Id`.
- `PendingDiff` (DiffReview) and `PendingWorkflowStart` get an `Id` and are listed in `/editor/requests` with `Kind` `edit` and `workflow`.

### 6.2 Authentication and pairing

- **`UiToken`** is 32 random bytes, hex, regenerated per editor process. It is separate from the MCP `Token`.
- `/editor/*` (except `/editor/pair`) requires `Authorization: Bearer <UiToken>`.
- `/editor/*` refuses any request that carries `Mcp-Session-Id`, `X-Solforge-Client-Pid` or `X-Solforge-Worker`.
- `/mcp` refuses the `UiToken`.
- A non-loopback `Origin` is refused, as today.
- The token is never written to disk or to discovery, and never put in any environment.

**`POST /editor/pair`** (no auth), body `{appPid, appVersion, nonce}`:
1. The editor resolves the TCP peer's owning pid with `GetExtendedTcpTable` on the loopback connection. It requires peer pid == `appPid`, and the process image path to be the configured `SolforgeAiPath` exe, or the packaged `Solforge AI.exe` found by `AppDiscovery`.
2. If the editor launched that pid itself (`SolforgeAiHost` knows the pid), the pairing is accepted silently. Otherwise the editor shows a modal: "Solforge AI (pid N) wants to connect to this editor. [Allow] [Deny]". The modal is `UiAuto.PushBlocked`, so MCP UI automation cannot click it.
3. On accept the editor returns `{uiToken, editorPid, project}`. It also keeps a list of paired pids; a paired pid that exits is forgotten.
4. Rate limit: one pending pair at a time, and a 30 s cooldown after a deny.

The app main process (P4, `solforge-editor-link.ts`):
- discovers editors through `%LOCALAPPDATA%\Solforge\mcp\server-*.json` (for `url` only; it ignores `token`);
- picks the editor whose pid is alive, preferring the launching editor (pid passed as non-secret `SOLFORGE_EDITOR_PID` at launch);
- pairs, and keeps the token in memory.

### 6.3 Endpoints (`McpHttpServer` plus a new `EditorUiApi.cs` in `Solforge.Mcp`, P4)

All state changes run on the UI thread via `OnUi`. JSON is camelCase.

| Method / path | Response / effect |
|---|---|
| `GET /editor/state` | `{editorPid, version, project: {path, name} \| null, map, paused, autoApprove, mcp: {port, sessions, calls}, credentialFileSet: bool, pending: n, seq}` |
| `GET /editor/events` (SSE) | events `request.added`, `request.answered`, `log.appended`, `sessions.changed`, `project.changed`, `context.changed`, `edits.changed`, `state.changed`; heartbeat every 15 s; `Last-Event-ID` = seq to resume |
| `GET /editor/requests` | `[{id, kind, tool, summary, session, worker, created, expires, detail}]`. `detail` holds diff rows for `edit`, and `{task, routedModels, smallFix}` for `workflow`. |
| `POST /editor/requests/{id}/approve` | completes the existing `TaskCompletionSource(true)` / diff accept / workflow start. A workflow body may carry an edited `task` and `smallFix`, exactly as the editor dialog. 404 if the request is gone, 409 if it has already been answered. |
| `POST /editor/requests/{id}/deny` | completes with false / cancels |
| `GET /editor/log?after=<seq>` | `{entries:[{seq, time, tool, summary, outcome, session}], lines:[{seq, time, text}]}` (`aiRequestLog` plus the `AppendAiLog` lines) |
| `GET /editor/sessions` | `Sessions.ToJson(null, aiHost)` (the `get_sessions` shape) |
| `GET /editor/edits`, `POST /editor/edits/{id}/undo` | wrap `UndoAiEdit` |
| `GET /editor/context` | `{chip, canAsk, contextJson, pinned:[…], recent: aiAttention.Recent(10), published: !paused}` |
| `POST /editor/context/publish` `{on}` | sets `aiPaused = !on` (paused still cancels pending diff reviews, as today) |
| `POST /editor/context/pins` `{target:'hovered'\|'selected'}`, `DELETE /editor/context/pins/{i}` | `PinAiObject` / unpin |
| `GET /editor/folders`, `POST /editor/folders` `{op:'add'\|'remove'\|'hide'\|'show', path}` | `AiFolders` / `AiHiddenFolders` (`StudioSettings`) |
| `POST /editor/ask` | `{text}` (the `AskAiAboutContext` text, without the clipboard) |
| `POST /editor/screenshot` | `{relativePath}`, via `SendAiScreenshot`/`CaptureAiScreenshot`. The file goes under `<project>/.solforge/screenshots`. |

There is no harness-settings endpoint: the file is the shared truth (§5.4).

Invariants that must hold over this path:
- **Auto-approve stays exactly where it is** (`ApproveAsync` Host.cs 846): only `settings.AutoApproveRequests` on, never for worker sessions. The app cannot change that setting.
- Diff reviews and workflow starts are never auto-approved.
- The 5-minute timeout means deny.
- `StopAiService` denies all pending requests and emits `request.answered{outcome:'denied', reason:'stopped'}`.
- Paused context cancels diff reviews. An expired request, or one whose file has changed, is cancelled and emitted.

### 6.4 Project publishing (requirement C on the editor side, P4)

- `StartAiForProject`:
  - calls `aiService.Publish(project)` on every change, **including null** on close (after the existing 2 s debounce);
  - emits `project.changed {path|null, name}`;
  - writes `%LOCALAPPDATA%\Solforge\last-project.json` `{path, name, updated}` on open. A close leaves the last value.
- `SolforgeAiProjectChanged` **no longer quit-forces** the app.
- The app profile is the single `shared` root: `ProjectKey.AppRoot` returns `...\AiApp\shared` for every launch, editor or standalone.
- The launch overlay's shim row drops `--project`, so the shim follows discovery.
- The control pipe is unchanged.
- Workflows remain per project, keyed by project in `/editor/state`.

### 6.5 App UI for the moved panels (`client-ui-solforge-editor`, P4)

- A global main panel "Editor", registered with `main` keyed `solforge-editor` plus `sidebar.panellist` and a badge showing the pending count. It has tabs:
  - **Requests:** cards with Approve / Deny and expiry countdown; a diff view for `edit`; an editable task for `workflow`.
  - **Log:** paged by seq, with filter.
  - **Sessions.**
  - **Context:** chip, publish toggle, pins, folders manager, "Ask about this" (inserts the text into the composer of the active session through the composer API, without sending), "Screenshot" (inserts the relative path).
- A `shell.overlay` toast on `request.added` with Review / Approve / Deny. No auto-approve happens anywhere in the app.
- Data comes only from `window.solforgeEditor` (§8.2). When it is absent or not paired, the panel shows "Not connected to a Solforge editor" with a "Connect" button that triggers pairing.

### 6.6 Editor shrink and fallback (P4, V2)

- `DrawSolforgeAiPanel` becomes:
  - the launcher/status card (launch / focus / stop, connection, pair state);
  - a line "Requests, log, sessions and context are in Solforge AI › Editor";
  - the **fallback block**.
- **Fallback** is shown when no paired app has polled or held SSE in the last 5 s. In that case:
  - the old AI REQUESTS cards are drawn (approve/deny) together with `DockAiPanelForApproval`;
  - `DrawApprovalToast` gains Approve and Deny buttons;
  - the diff review modal and the workflow start dialog stay unconditionally in the editor as well.

  Whichever side answers first wins; the other side sees `request.answered`.
- The Output Log MCP category, toolbar pill and badge, and shortcuts are unchanged.
- **Context features stay usable in the editor:** chip, right-click "Ask about this", screenshot hotkey, Pin and Folders popups (`StudioForm.Ai.cs`). Only the dedicated panel sections move. This keeps feature parity.

## 7. Requirement C: the workspace is the open project

### 7.1 Live follow (P2)

The Electron main process receives `project.changed` (§6.3) through P4's link module. It then:
1. records `lastProject` in `solforge-app.json`;
2. sends `solforge:project` to the renderer through the bridge.

`client-ui-solforge-plugins` handles the event: `ctx.workspaces.create(path)` (reuses an existing workspace for the canonical path), then `ctx.uiWorkspace.openWorkspace(id)`, then selects the most recent session in it, or `connectWorkspace` for a blank one.

A null project leaves the current workspace selected and shows "Project closed in editor".

### 7.2 Startup without the default workspace

- Main passes the absolute `SOLFORGE_AI_PROJECT_DIR` (non-secret) to the host. It is listed in `SOLFORGE_HOST_VARIABLES`.
- The project is chosen in this order:
  1. the editor's current project (from pairing or launch);
  2. `lastProject` from `solforge-app.json`;
  3. `%LOCALAPPDATA%\Solforge\last-project.json`.

  If none of these exists, the variable is not set.
- Host plugin `solforge-plugins` injects `workspaceRegistry` and calls `create(dir)` before client restore, so `initializeDefault` never fires.
- Source edit (mode-gated):
  - `ui-workspace/src/client/navigation.ts` `restoreSelection()` does not call `initializeDefaultWorkspace()` in Solforge mode. With no workspace it shows an empty state "Open a project" (opens `pickDirectory`), plus "Recent" taken from `solforge-app.json`.
  - `"Default workspace"` labels are not rendered in Solforge mode; the workspace title is the folder name.
- Existing default-workspace records in the old profiles are **not deleted**. The shared profile is new, so none exists there. If one is found, it is archived only if it has zero sessions.

### 7.3 Shared profile and non-destructive migration (P2 runtime, P4 launcher)

- **Root:** `%LOCALAPPDATA%\SolforgeNext\AiApp\shared\{electron,home}`. Both modes use it. The single-instance lock is on the shared root, so an editor launch while standalone is running sends `focus` plus pairing instead of starting a second app.
- **First run** (`shared` does not exist):
  1. Pick the most recently used old profile among `_standalone` and the per-project dirs.
  2. Copy (never move) its `solforge-standalone.json` into `solforge-app.json`, and its home settings YAML (not `.credentials.yaml`, not sessions) into `shared\home`.
  3. Write `shared\migrated-from.txt` listing the source.
- Old dirs are kept. Their sessions stay readable by pointing a future "Import sessions" action at them; that action is out of scope for this spec and is recorded as a known gap.

## 8. Shared protocol contracts

### 8.1 Control pipe

The control pipe is unchanged: `focus`, `quit`, `quit-force`, `status`.

### 8.2 Bridge (`apps/desktop/src/solforge-ipc-contract.ts`, written first by P2; P4 owns the `solforgeEditor` part's semantics)

```ts
// exposed by preload-app.ts via contextBridge only when SOLFORGE_AI=1; ipc handlers validate sender frame origin = host webserver
interface SolforgeAppBridge {          // P2
  getConfig(): Promise<SolforgeAppConfig>
  setConfig(patch: Partial<SolforgeAppConfig>): Promise<{ restartRequired: boolean }>
  restartHost(): Promise<void>
  pickFolder(): Promise<string | null>
  onProject(cb: (p: { path: string; name: string } | null) => void): () => void
  getHarness(): Promise<HarnessSettingsView>               // P3 implements (read via main or host; see 9.1)
}
interface SolforgeEditorBridge {       // P4
  status(): Promise<EditorLinkStatus>  // 'none' | 'pairing' | 'paired' | 'denied'
  connect(): Promise<EditorLinkStatus>
  get<T>(path: EditorGetPath): Promise<T>                  // whitelist of GET paths from §6.3
  post<T>(path: EditorPostPath, body?: unknown): Promise<T>// whitelist of POST/DELETE paths
  onEvent(cb: (e: EditorEvent) => void): () => void
}
```

The renderer never sees `UiToken`.

### 8.3 App config shape (`SolforgeAppConfig`, P2)

```json
{ "version": 1,
  "lastProject": { "path": "...", "name": "..." },
  "recentProjects": [],
  "sharedResourcesPath": "...", "workbenchRoot": "...", "importClaudeServers": true,
  "plugins": { "solforge-editor-mcp": {"enabled": true, "solUpstreams": true, "geminiDelegate": false, "clinePassDelegate": false, "toolCallTimeoutMs": 180000},
               "destiny-modding": {"mcpEnabled": true, "skillEnabled": true, "toolCallTimeoutMs": 180000},
               "graphify": {"enabled": true}, "rea": {"enabled": true},
               "solforge-automatic": {"enabled": true},
               "solforge-model-tiers": {"enabled": true, "planFirst": true, "subagentsMayUseMid": true, "subagentsMayUseFrontier": false},
               "solforge-editor": {"enabled": true, "notifyOnRequest": true, "openPanelOnRequest": false},
               "solforge-skills": {"appSkillsEnabled": true} } }
```

Unknown keys are preserved on write.

## 9. Ownership

### 9.1 Contract files: one writer each, written first, before other work

| File | Writer | Content |
|---|---|---|
| `RT/packages/experimental/solforge-contract/` (new types-only package; scaffold, registration and package.json by **P2**) | P2 | `src/app-config.ts` (§8.3), `src/plugin-ids.ts` (§4.2 ids), `src/bridge.ts` (§8.2 interfaces) |
| `.../solforge-contract/src/harness.ts` | **P3** | TS mirror of `HarnessSettings` (string enums), tier↔role map (§5.1), `TierId`, validator signature |
| `.../solforge-contract/src/editor-api.ts` | **P4** | §6.3 request/response/event types, `EditorGetPath`/`EditorPostPath` whitelists |
| `RT/apps/desktop/src/solforge-ipc-contract.ts` | P2 | IPC channel names (`solforge:app:*`, `solforge:editor:*`, `solforge:project`) re-exporting the bridge types |
| `RT/apps/desktop/src/solforge-shell.ts` (stub with signatures first) | P2 | `installSolforgeShell(ctx)`, `solforgeHostEnvironment()`; calls P3's `buildModelOverlay()`/`credentialEnvironment()` and P4's `startEditorLink()` |
| `solforge-bundle` patch split: `cordis.patch.yml` (P2) loads rows for all new packages; `patches/brand.patch.yml` (P1), `patches/models.patch.yml` (P3), `patches/editor.patch.yml` (P4) | P2 creates the empty files and the loader change in `apps/desktop-host/src/index.ts` `solforgePatchFiles()` | — |
| `V2/Solforge.Mcp/EditorUiContract.cs` | P4 | C# DTOs for §6.3 (the same shapes as `editor-api.ts`) |

### 9.2 P1: branding, Solforge styling, sign-in and DeepSeek account removal

- New package `RT/packages/experimental/client-ui-solforge-brand/**` (src and assets; registration is done by P2).
- `RT/packages/experimental/solforge-bundle/patches/brand.patch.yml`.
- `RT/packages/client/ui-conversation/src/client/skeleton/EmptyHero.tsx` and `ui-conversation/src/client/locales.ts` (new keys only).
- `RT/apps/desktop/src/main.ts` (the only owner): branding, icons, welcome/account skip, and adding the `installSolforgeShell` / `solforgeHostEnvironment` call sites exactly as the P2 stub specifies.
- `RT/apps/desktop/src/{locale.ts, tray.ts, welcome-window.ts}` (mode gates only).
- `RT/apps/desktop/resources/*`.
- `ai/package-desktop.ps1`.

### 9.3 P2: plugins page, Solforge MCP and feature plugins, project workspace following, all registration

- New packages:
  - `RT/packages/experimental/solforge-contract/**` (except `harness.ts` and `editor-api.ts`);
  - `solforge-plugins/**` (host: plugin enable state, workspace bootstrap);
  - `client-ui-solforge-plugins/**` (the `plugins.item` cards, workspace live-follow, open-project empty state).
- `RT/packages/experimental/solforge-bundle/{cordis.patch.yml, package.json, patches/ (empty files)}`.
- `RT/packages/experimental/client-ui-solforge-automatic/**` (move its card to `plugins.item`) and `solforge-automatic/**` (the enabled gate).
- `RT/packages/util/official-bundles/src/plugin-manager.ts` and `RT/packages/client/ui-plugin-manager/**`.
- `RT/packages/client/ui-workspace/src/client/{navigation.ts, locales.ts, WorkspacePicker.tsx, rows/WorkspaceBrowser.tsx, toast/register.ts}`.
- `RT/packages/client/ui-settings-subagent/**` (hide in Solforge mode).
- `RT/apps/desktop/src/{solforge-shell.ts, solforge-ipc-contract.ts, solforge-app-config.ts, solforge-standalone.ts, solforge-mode.ts, paths.ts, single-instance.ts, host-process.ts, preload-app.ts}`.
- `RT/apps/desktop-host/src/index.ts`.
- `RT/pnpm-lock.yaml`, `RT/tsconfig.base.json`, `RT/tsconfig.host.json`, `RT/tsconfig.client.json`, `node_modules` junctions.
- `ai/build-runtime.ps1`, `ai/package-runtime.ps1`, `verify.ps1`.
- Docs: `ai/README-runtime.md`.

### 9.4 P3: tiered model system

- New packages `RT/packages/experimental/solforge-model-tiers/**` (host: virtual adapter, ladder fallback, harness watch/write, subagent policy, plan-first) and `client-ui-solforge-model-tiers/**` (model button, `/model`, tier picker, models settings card).
- `solforge-contract/src/harness.ts` and `solforge-bundle/patches/models.patch.yml`.
- `RT/packages/client/ui-settings-models/**` (mode gates, Solforge card).
- `RT/apps/desktop/src/solforge-credentials-env.ts` and `solforge-model-overlay.ts`.
- V2:
  - `Solforge.Harness/HarnessSettingsStore.cs` (watch, compare-before-replace);
  - `Solforge.Harness/HarnessSettingsValidator.cs` (only if a shared rule needs exposing);
  - `SunriseStudio.Editor/StudioForm.SolforgeAiSettings.cs` (tier labels, reload prompt, persisting `CredentialFilePath`).

### 9.5 P4: editor side and the moved-panel client

- V2:
  - `Solforge.Mcp/{McpHttpServer.cs, EditorUiApi.cs (new), EditorUiContract.cs (new), McpDiscovery.cs (no token change), SolforgeMcpService.cs, McpSessions.cs (Changed wiring)}`;
  - `SunriseStudio.Editor/{StudioForm.SolforgeAiPanel.cs, StudioForm.Ai.Host.cs, StudioForm.Ai.cs, StudioForm.DiffReview.cs, StudioForm.Workflows.cs, StudioForm.Toolbar.cs, StudioForm.SolforgeAi.cs, StudioForm.OutputLog.cs}`;
  - `Solforge.AiLauncher/{ProjectKey.cs, SolforgeAiHost.cs, LaunchOverlay.cs, AppDiscovery.cs}`;
  - optionally `Solforge.Mcp.Shim/Program.cs` (a retail filter flag).
- Runtime: new package `RT/packages/experimental/client-ui-solforge-editor/**`, `solforge-contract/src/editor-api.ts`, `solforge-bundle/patches/editor.patch.yml`, and `RT/apps/desktop/src/solforge-editor-link.ts`.
- Docs: `V2/docs/V2-STATUS.md` and `docs/UI-UE5.md` (panel move).

### 9.6 Order of work

1. **Contract phase.**
   - P2 scaffolds and registers all six new packages with stub `index.ts` files, creates the empty patch files, the loader, the `solforge-shell.ts` stub and the IPC contract.
   - P3 writes `harness.ts`.
   - P4 writes `editor-api.ts` and `EditorUiContract.cs`.
   - P1 adds the main.ts call sites.
2. **Parallel implementation** within the owned files.
3. **Integration.** P2 runs `ai/build-runtime.ps1`. The owners fix their own files. A launch walkthrough is then required for acceptance (§10).

## 10. Acceptance (each item is observed in the running apps; compiling does not count)

- **A.** No "DSH", "DeepSeek", whale, "Into the Unknown" or Preview anywhere visible:
  - sidebar, hero, title bar, About, tray, window icon;
  - the theme greys and blue accent match the editor side by side;
  - the exe icon is changed, or the gap is reported.
- **B.** The Plugins page shows the eight Solforge cards. Toggling an MCP and restarting the host adds or removes its tools. Upstream experimental cards appear under More.
- **C.** Switching the project in the editor switches the app's workspace and its session list without restarting the app.
  - Standalone with no editor opens the last project.
  - No "Default workspace" appears.
- **D.** An approval-requiring MCP call shows in the app. Approve and Deny each work. With the app closed, the editor fallback shows and works. Auto-approve happens only with the editor setting on, and never for worker sessions.
- **E.** The model button shows three ladders built from the user's existing harness models, with nothing reset.
  - A session plans on Frontier, executes on Mid, and spawns Low subagents.
  - A simulated QUOTA result falls through within the ladder only. This is checked by reading the code and logs, not by a paid call.
  - "No models available" disappears once a provider or credential file is configured.
- **F.** The More menu has no Sign in. No DeepSeek welcome, account or onboarding appears.

## 11. Items from `HANDOFF.md` "USER ADDENDUM" (not in this run's relayed request)

`HANDOFF.md` contains a section titled "USER ADDENDUM (2026-10-24…)". This spec treats it as project notes. It was **not** part of the request relayed for this run, so items beyond that request are flagged for the user rather than assigned:

1. **Preserve the existing model configuration:** adopted (§0, §5.4). It is consistent with E.
2. **Google AI Pro subscription:** E already says "where officially supported". The editor deliberately blocks Gemini as unverified, and the code notes agy is not Gemini. The spec keeps it unavailable until an official Gemini CLI headless contract is verified. Enabling it via `agy` or the Gemini CLI needs the user's confirmation.
3. **Cold-start profiling (Solforge AI.exe and the v2 launch path):** not assigned. This needs the user's go-ahead. §4.1's `failOnStartupError:false` and lazy MCPs do not block the first window either way.
4. **Shared settings for both modes:** adopted (§7.3). It is required by C.
5. **Exe icon:** included under A only via an offline `rcedit` if present (§2.1).
