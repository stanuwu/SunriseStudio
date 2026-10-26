#requires -Version 7.4
<#
.SYNOPSIS
Packages Solforge AI (ai-runtime + the solforge-ai desktop window) from the sibling solforge-ai folder into -Output.
.DESCRIPTION
The editor is not built here: publish the v2 editor with ..\publish.ps1 -Output <folder> first and pass the same
folder, so ai-runtime\ and solforge-ai\ land beside Solforge.exe (where its Solforge AI launcher looks). Run
build-runtime.ps1 first. Nothing is downloaded: Electron comes from -ElectronZip, checked against its checksums.
#>
param([Parameter(Mandatory)][string]$Output, [string]$NodePath, [string]$NodeLicensePath, [string]$PnpmPath,
    [Parameter(Mandatory)][string]$ElectronZip)
$ErrorActionPreference = 'Stop'

$Output = [IO.Path]::GetFullPath($Output)
foreach ($name in 'ai-runtime', 'solforge-ai') {
    if (Test-Path -LiteralPath (Join-Path $Output $name)) { throw "$Output already has $name; publish into a fresh folder." }
}
if (-not (Test-Path -LiteralPath $ElectronZip -PathType Leaf)) { throw "Electron zip not found: $ElectronZip" }
$ElectronZip = (Resolve-Path -LiteralPath $ElectronZip).Path

# Preflight the owned runtime before anything is written, so no output folder is half-built.
$runtime = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\solforge-ai'))
$webAppPatch = Join-Path $runtime 'packages/bundle/web-app/cordis.patch.yml'
if ([IO.File]::ReadAllText($webAppPatch).Contains('dsh-client-solforge-workspace')) { throw 'web-app cordis.patch.yml still registers the retired Solforge workspace panel.' }
foreach ($required in @('apps/cli/lib/bin.js', 'apps/web/dist/index.html', 'node_modules/tsx/package.json', 'scripts/executable-packaging.ts',
    'scripts/build-exe-for-python-sdk-staging.ts', 'apps/desktop/lib/main.js', 'apps/desktop-host/lib/index.js',
    'apps/desktop/node_modules/electron/checksums.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $runtime $required) -PathType Leaf)) {
        throw "Owned AI runtime prerequisite missing: solforge-ai/$required. Run build-runtime.ps1 first."
    }
}
if (-not ([IO.File]::ReadAllText((Join-Path $runtime 'apps/desktop/lib/main.js')).Contains('SOLFORGE_AI_CONTROL_PIPE'))) {
    throw 'solforge-ai/apps/desktop/lib/main.js lacks the Solforge launch mode. Run build-runtime.ps1.'
}
New-Item -ItemType Directory -Path $Output -Force | Out-Null

$packageArguments = @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'package-runtime.ps1'), '-Output', $Output)
if ($NodePath) { $packageArguments += @('-NodePath', $NodePath) }
if ($NodeLicensePath) { $packageArguments += @('-NodeLicensePath', $NodeLicensePath) }
if ($PnpmPath) { $packageArguments += @('-PnpmPath', $PnpmPath) }
& pwsh @packageArguments
if ($LASTEXITCODE -ne 0) { throw "AI runtime packaging failed. $Output is incomplete; do not use it. Its .ai-runtime-staging-* folder and the solforge-ai-package-* temp scratch are retained." }

$desktopArguments = @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'package-desktop.ps1'), '-Output', $Output, '-ElectronZip', $ElectronZip)
if ($NodePath) { $desktopArguments += @('-NodePath', $NodePath) }
if ($PnpmPath) { $desktopArguments += @('-PnpmPath', $PnpmPath) }
& pwsh @desktopArguments
if ($LASTEXITCODE -ne 0) { throw "Solforge AI desktop packaging failed. $Output is incomplete; do not use it. Its .solforge-ai-staging-* folder and the solforge-ai-desktop-package-* temp scratch are retained." }

Write-Host "Published Solforge AI to $Output (ai-runtime, solforge-ai desktop app)."
Write-Host 'This is a build, not a live acceptance. Launch Solforge AI from the editor to exercise the window, its MCP connections and models.'
