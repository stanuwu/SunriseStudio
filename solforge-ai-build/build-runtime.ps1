#requires -Version 7.4
[CmdletBinding()]
param(
    [string]$NodePath,
    [string]$ClientTitle = 'Solforge AI'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# The owned runtime is the sibling solforge-ai folder (09-solforge\solforge-ai).
$runtime = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\solforge-ai'))
if (!(Test-Path -LiteralPath (Join-Path $runtime 'node_modules/tsx/package.json'))) {
    throw 'Owned workspace dependencies are missing. Dependency installation is a separate explicit step; this builder does not install.'
}
if (!$NodePath) { $NodePath = (Get-Command node.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source }
$NodePath = (Resolve-Path -LiteralPath $NodePath).Path
$start = [Diagnostics.ProcessStartInfo]::new($NodePath)
$start.WorkingDirectory = (Resolve-Path -LiteralPath $runtime).Path
$start.UseShellExecute = $false
$start.Environment.Remove('NODE_OPTIONS') | Out-Null
$start.Environment.Remove('NODE_PATH') | Out-Null
$start.Environment['COREPACK_ENABLE_NETWORK'] = '0'
$start.Environment['DSH_CLIENT_TITLE'] = $ClientTitle
# The owned fork carries no .git; build metadata uses the upstream commit it was copied from.
if (!$start.Environment.ContainsKey('DSH_CLIENT_COMMIT_HASH') -or !$start.Environment['DSH_CLIENT_COMMIT_HASH']) {
    $start.Environment['DSH_CLIENT_COMMIT_HASH'] = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'UPSTREAM-COMMIT') -TotalCount 1).Trim()
}
# Artifact graph includes native generation, workspace libraries, host/client bundles and Web.
# No test/check gate or installer is invoked by this wrapper.
foreach ($argument in @('--import','tsx','scripts/build.ts','--scope','artifacts')) { $start.ArgumentList.Add($argument) }
$process = [Diagnostics.Process]::Start($start)
try {
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Owned AI artifact build failed (exit $($process.ExitCode)). No package was produced." }
} finally { $process.Dispose() }

# The artifact graph above already contains the Solforge AI desktop pieces: the host library pass
# compiles and bundles apps/desktop-host (lib/index.js, lib/cli.js), and the 'desktop-bundle' gate runs
# `pnpm --filter @deepseek-ai/dsh-desktop run bundle` (lib/main.js, preloads, lib/welcome). Confirm the
# outputs exist and carry the Solforge launch-mode code, so a stale bundle is never packaged.
$desktopChecks = [ordered]@{
    # Standalone launch: packaged marker (solforgeAi), generated app overlay, explicit Host env.
    # Solforge shell (solforge-shell.ts, reached through main.ts's installSolforgeShell call site): app config IPC,
    # shared profile, project follow and the editor link.
    'apps/desktop/lib/main.js'               = @('SOLFORGE_AI_CONTROL_PIPE', 'SOLFORGE_AI_RUNTIME_DIR', 'Solforge AI',
                                                 'SOLFORGE_AI_APP_OVERLAY', 'solforgeAi', 'SOLFORGE_AI_MODE', 'solforge-app-overlay.yml',
                                                 'solforge:app:set-config', 'solforge-app.json', 'SOLFORGE_AI_PROJECT_DIR',
                                                 'SOLFORGE_AI_APP_CONFIG', 'solforge:editor:get')
    # window.solforgeApp / window.solforgeEditor bridge.
    'apps/desktop/lib/preload-app.cjs'       = @('solforge:app:get-config', 'solforgeEditor')
    'apps/desktop/lib/preload-welcome.cjs'   = @()
    'apps/desktop/lib/welcome/welcome.js'    = @()
    # Ordered Solforge patch list: bundle patch, patches/brand|models|editor, SOLFORGE_AI_PATCH, SOLFORGE_AI_APP_OVERLAY.
    'apps/desktop-host/lib/index.js'         = @('SOLFORGE_AI_PATCH', 'SOLFORGE_AI_DISABLE_OFFICE', 'SOLFORGE_AI_APP_OVERLAY',
                                                 'dsh-experimental-solforge-bundle', 'patches')
    'apps/desktop-host/lib/cli.js'           = @()
    'apps/web/dist/index.html'               = @()
}
foreach ($relative in $desktopChecks.Keys) {
    $file = Join-Path $runtime $relative
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Solforge AI desktop artifact missing after build: solforge-ai/$relative" }
    $text = [IO.File]::ReadAllText($file)
    foreach ($marker in $desktopChecks[$relative]) {
        if (!$text.Contains($marker)) { throw "Solforge AI desktop artifact is stale (no '$marker'): solforge-ai/$relative" }
    }
}
# Automatic mode, nudges and auto-allow (implementer B's experimental packages). The desktop Host requires the
# bundle patch in Solforge mode, so a missing build output would fail every launch.
$solforgePackageChecks = [ordered]@{
    'packages/experimental/solforge-automatic/lib/index.js'           = @('automatic-strip', 'compaction/start')
    # App-owned skill with the agent hint; the app overlay mounts it as solforge-app-skills.
    'packages/experimental/solforge-automatic/skills/solforge-automatic/SKILL.md' = @('automatic_start')
    'packages/experimental/client-ui-solforge-automatic/lib/client.js' = @('solforge.plugins.automatic.fields')
    # Solforge plugins (P2): Plugins page cards, project workspace bootstrap and live-follow.
    'packages/experimental/solforge-plugins/lib/index.js'              = @('__SOLFORGE_AI_APP__', 'workspaceRegistry')
    'packages/experimental/client-ui-solforge-plugins/lib/client.js'   = @('solforge.plugins.modelTiers.fields', 'plugins.item')
    # Branding (P1), model tiers (P3), editor panel (P4).
    'packages/experimental/client-ui-solforge-brand/lib/client.js'     = @()
    'packages/experimental/solforge-model-tiers/lib/index.js'          = @()
    'packages/experimental/client-ui-solforge-model-tiers/lib/client.js' = @()
    'packages/experimental/client-ui-solforge-editor/lib/client.js'    = @()
    'packages/experimental/solforge-bundle/cordis.patch.yml'           = @('@deepseek-ai/dsh-experimental-solforge-automatic',
                                                                           '@deepseek-ai/dsh-experimental-client-ui-solforge-automatic',
                                                                           '@deepseek-ai/dsh-experimental-solforge-plugins',
                                                                           '@deepseek-ai/dsh-experimental-client-ui-solforge-plugins',
                                                                           '@deepseek-ai/dsh-experimental-client-ui-solforge-brand',
                                                                           '@deepseek-ai/dsh-experimental-solforge-model-tiers',
                                                                           '@deepseek-ai/dsh-experimental-client-ui-solforge-model-tiers',
                                                                           '@deepseek-ai/dsh-experimental-client-ui-solforge-editor')
    # Split patch files; the desktop Host fails startup when one is missing.
    'packages/experimental/solforge-bundle/patches/brand.patch.yml'    = @()
    'packages/experimental/solforge-bundle/patches/models.patch.yml'   = @()
    'packages/experimental/solforge-bundle/patches/editor.patch.yml'   = @()
}
foreach ($relative in $solforgePackageChecks.Keys) {
    $file = Join-Path $runtime $relative
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Solforge AI package artifact missing after build: solforge-ai/$relative" }
    $text = [IO.File]::ReadAllText($file)
    foreach ($marker in $solforgePackageChecks[$relative]) {
        if (!$text.Contains($marker)) { throw "Solforge AI package artifact lacks '$marker': solforge-ai/$relative" }
    }
}
# web_fetch loads turndown and its GFM plugin lazily on the first HTML body (packages/web/tool-web/src/fetch.ts).
# A module that cannot be resolved from tool-web would only show up as "HTML conversion unavailable" at run time,
# so load both now through the same createRequire anchor the tool uses.
$toolWebManifest = Join-Path $runtime 'packages/web/tool-web/package.json'
$turndownGate = "const r=require('node:module').createRequire(process.argv[1]);const T=r('turndown');const g=r('@joplin/turndown-plugin-gfm');" +
    "if(typeof T!=='function'||typeof g.gfm!=='function')throw new Error('unexpected turndown exports');new T().use(g.gfm);"
& $NodePath -e $turndownGate $toolWebManifest
if ($LASTEXITCODE -ne 0) { throw 'tool-web cannot load turndown / @joplin/turndown-plugin-gfm; web_fetch HTML conversion would be unavailable.' }
# The editor-embedded workspace panel is no longer loaded; the AI app is a separate desktop window.
if ([IO.File]::ReadAllText((Join-Path $runtime 'packages/bundle/web-app/cordis.patch.yml')).Contains('dsh-client-solforge-workspace')) {
    throw 'web-app cordis.patch.yml still registers the retired Solforge workspace panel.'
}
# Still declared as a web-app dependency (removing it needs a user-authorized relink), so it is in the deployed
# closure but never loaded. Warn rather than fail so the build stays usable until the relink happens.
if ([IO.File]::ReadAllText((Join-Path $runtime 'packages/bundle/web-app/package.json')).Contains('dsh-client-solforge-workspace')) {
    Write-Warning 'web-app/package.json still declares the retired @deepseek-ai/dsh-client-solforge-workspace; it is packaged but not registered. Remove it with an authorized relink.'
}
Write-Host 'Owned AI artifacts built, including the Solforge AI desktop shell (apps/desktop/lib) and host (apps/desktop-host/lib).'
Write-Host 'Run publish.ps1 to package ai-runtime and the solforge-ai desktop app; building is not a launch test.'
