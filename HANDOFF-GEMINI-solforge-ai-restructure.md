# Handoff for Gemini: finish the Solforge AI restructure (2026-11-02)

You are picking up work that a Claude session started and could not finish (it hit a usage limit).
Read this whole file before changing anything. Owner: Seth.

## What Solforge AI is
- `C:\Users\Seth\Desktop\Project - Sol\sol\09-solforge\` holds the Solforge editor (C#/WinForms + Dear ImGui,
  git branch `ue5-ui`) and `solforge-ai\` (an Electron/TypeScript agent chat app, pnpm monorepo, git branch
  `feat/agent-os`, no GitHub remote).
- The shipped app lives in `09-solforge\publish\` (`publish\solforge-ai`, `publish\ai-runtime`, editor files).
  Do not touch `publish\` except through the build script's `-Swap` (see below).
- Memory vault: `C:\Users\Seth\Documents\AgentOS` (plain Markdown, `[[wikilinks]]`; `Context.md` is the index).
  `Projects\Sol\` in it is reference docs written by another session: read-only for the app.

## Goal (the agreed plan)
1. **Bug: skills not found.** App-started Claude runs use `--restricted`, which hides `~/.claude/skills`.
   Keep the confinement; have the app pass the skill's instructions to the run directly
   (started in `packages/experimental/solforge-model-tiers/src/skill-roots.ts`, untracked/new).
2. **Bug: empty runs.** A skill that needs text must refuse an empty box; its button stays disabled until
   there is text, and says why.
3. **Providers:** remove OpenCode, Cline/ClinePass and Google/Gemini; keep only Claude and Codex.
   Tiers: frontier = Opus or Codex, mid = Sonnet or GPT-6, low = Haiku. One-time migration cleans
   `harness-settings.json` (copy it to Drive first). The DeepSeek "flash-free" and ClinePass models go too.
4. **Editor:** remove the `ask_clinepass` MCP tool and its C# worker code (`src\Solforge.Mcp*`).
5. **Vault memory in chat** (package `packages/experimental/solforge-vault-memory`, new):
   - at session start, inject `Context.md` + the one matching project note, capped ~4 KB, marked as data;
   - tools `vault_search` (file-count/size caps, skip binaries and dot-folders) and `vault_append`
     (append-only, only under Inbox, Sessions, Projects; never rewrite/reorganise existing files);
   - tests must use a temporary vault, never the real one; treat vault text as untrusted.
6. **Skills as `/commands`** in the composer: `sol-handoff`, `sol-status`, `sol-changes`, `sol-mod-note`,
   `sol-deploy-log`, plus `research` and `vault-memory` (SKILL.md files already exist in the package's
   `skills\`). Delete `morning-report` and `priorities` from `~/.claude/skills` only after copying them to Drive.
7. **Token chip** in the composer: today's usage vs the cap; refuses a turn once the cap is hit.
8. **Agent OS page** replaced by a slim side panel: Today, note search, run log.
9. **Skills + MCP everywhere:** every run path (main chat on all tiers, Claude Code and Codex subagents,
   the editor's C# worker, which today only has Read/Glob/Grep and a strict MCP config, model-tier CLI runs,
   background/handoff runs) loads the user's skills and MCP servers (Solforge editor, Destiny modding,
   graphify, REA). Add one or two sentences to the system prompt: check for a matching skill/MCP tool first.
   Do NOT widen shell/write permissions; editor-changing MCP tools stay behind the existing ApprovalGate.
   Report a table: run path / skills yes-no / MCP servers.

## Where it stands (checked 2026-11-02 ~11:35 ET)
- Done: plan; backups (local `backup/20261102-*` snapshot branches in both repos + verified bundles in
  `G:\My Drive\d2-dev-archive\sol-wip-20261102\`); build script `09-solforge\build-solforge.ps1` and
  `build-solforge.cmd` (parses cleanly; never run for a full build).
- In progress (uncommitted; `solforge-ai` has ~125 changed files, the editor ~66, some older than today):
  - `solforge-vault-memory` package exists (src `index.ts`, `notes.ts`, locales, 7 SKILL.md files) and is
    being registered in `solforge-bundle`, `plugin-manager.ts`, `register-solforge-packages.cjs`,
    `tsconfig.*`, `build-runtime.ps1`, `package-runtime.ps1`.
  - Provider removal has started (`apps/desktop/src/solforge-provider-catalogs.ts`, `solforge-model-*.ts`,
    `solforge-model-tiers` adapter/subscription-cli, tier UI). About 10 source files still mention
    opencode/cline/gemini: `grep -rliE 'opencode|clinepass|\bcline\b|gemini' apps/desktop/src packages/experimental/*/src`.
- Not started: editor `ask_clinepass` removal (verify), empty-run guard, token chip, side panel,
  skills+MCP-everywhere pass, settings migration.
- Before editing, run `git status` / `git diff` in both repos to see exactly what was already changed.

## Build and verify
- `build-solforge.ps1` options: `-PreflightOnly` (checks only), default (build into a new
  `publish-test-<date-time>` folder), `-Quick` (only if the fast single-package rebuild is proven),
  `-Swap` (after PASS: back up current `publish\solforge-ai` and `ai-runtime` to Drive, verify, then swap).
- One full build at the end, into a test folder. Run every repo check/test first. The script refuses to
  start if `destiny2.exe`, Solforge or another build is running.
- Builds are offline from the lockfile/cache; if something is missing it prints the fix command.
- Seth checks the test build in the app before any swap. Do not run `-Swap` yourself.

## Hard rules
- Never upload or print secrets (keys, tokens, credentials) or account/player databases.
- Backups go to Google Drive (`G:\My Drive`, dated subfolder + short README); delete originals only after
  confirming the copy is the same size. Don't leave `.bak`/old builds lying around on the PC.
- Git: commit only to `backup/...` branches if you commit at all; never main/master, never force-push.
  `github.com/wyndxgo777/Solforge` appears PUBLIC: push nothing there.
- Never `rm -rf` / `rsync --delete` with a path built from a variable that could be empty.
- No game running during builds; one build at a time.
- "Settings > AI" (auto-approve) in Solforge is Seth's; don't change it.
- When done, append one dated line to `C:\Users\Seth\Desktop\Project - Sol\sol\STATUS.md`
  (append-only) and tell Seth: what changed, test build path, PASS/FAIL, and what's left.
