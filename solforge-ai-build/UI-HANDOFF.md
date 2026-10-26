# Solforge AI web UI handoff (RETIRED, historical only)

> **Superseded by the 2026-10-24 ARCHITECTURE CORRECTION in `HANDOFF.md` and by
> `ai/README-runtime.md`.** Solforge AI is a separate desktop app (its own
> Electron window) that the editor launches; the editor's AI tab is a
> launcher/status panel only. The embedded WebView2 panel described below is
> retired. Do **not** register `@deepseek-ai/dsh-client-solforge-workspace` in
> `packages/bundle/web-app/cordis.patch.yml`: `ai/build-runtime.ps1`,
> `publish.ps1`, `ai/package-runtime.ps1` and `verify.ps1` refuse a patch that
> registers it. The package source is kept for reference only.

## Done (historical)
- New plugin package `ai/runtime/packages/client/solforge-workspace` (`@deepseek-ai/dsh-client-solforge-workspace`): package.json, tsconfig.json, tsdown.config.ts, src/index.ts, src/css-modules.d.ts, src/client/{index.ts, bridge.ts, store.ts, locales.ts, SolforgePanel.tsx, SolforgePanel.module.css}.
- It registers one `shell.overlay` entry (`solforge-workspace.panel`), a floating panel. The upstream DeepSeek chat, history, tools, subagents and approvals are untouched; no upstream package or `apps/web/src/main.ts` was edited.
- Panel: mode toggle Workflow / Direct chat, task textarea, Small fix checkbox, Start (sent `start_workflow` over the WebView2 bridge), Configure models and roles, Open status window, Refresh, Stop, status facts, stage cards from `run.Steps`, host error line.
- Bridge (`bridge.ts`) is inert unless `window.chrome.webview` exists, so outside WebView2 (including the separate Solforge AI app) the panel renders nothing.
- Built at the time: `tsc -b` in the package, its bundle, and `pnpm run build:web` with `DSH_CLIENT_TITLE='Solforge AI'`.

## Former "NOT done" list (withdrawn; do not follow)
The earlier steps to load the panel are withdrawn:
1. ~~Add the package to `ai/runtime/tsconfig.client.json` references.~~ (A reference already exists; it only type-checks the retired package.)
2. ~~Add a `solforge-workspace` row to `packages/bundle/web-app/cordis.patch.yml`.~~ Refused by the build, publish, package and verify guards.
3. ~~Add the dependency to `packages/bundle/web-app/package.json` and relink.~~ The dependency is in fact still declared there; removing it needs a user-authorized relink (no `pnpm install` is run by agents). Until then it is packaged but never loaded.

Model workflows from Solforge AI now use the `start/get/cancel_model_workflow` MCP tools of `solforge-next-mcp`, injected by the editor's launch overlay.

## Caveats (historical)
- Running `pnpm --filter ... bundle` at the time made pnpm perform an automatic dependency check pass; `node_modules` was not verified afterwards.
- Junction links were created by hand in `solforge-workspace/node_modules`.
- None of the panel behaviour was ever verified live. Compilation is not live acceptance.
