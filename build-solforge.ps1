#requires -Version 7.4
<#
Builds an editor + MCP test publish. Solforge AI and its runtime are intentionally not built.
This script only writes a new/empty test folder; deployment stays a separate user action.
#>
[CmdletBinding()]
param(
    [string]$Output,
    [switch]$PreflightOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$logDir = Join-Path $root 'obj\build-solforge-logs'
$lock = Join-Path $root '.build-solforge.lock'
$log = Join-Path $logDir "mcp-only-$stamp.log"
$lockTaken = $false
$results = [System.Collections.Generic.List[object]]::new()

New-Item -ItemType Directory -Force -Path $logDir | Out-Null
function Say([string]$text) { Write-Host $text; Add-Content -LiteralPath $log -Value $text }
function Result([string]$name, [string]$state, [string]$detail = '') {
    $results.Add([pscustomobject]@{ Check = $name; State = $state; Detail = $detail })
    Write-Host ("[{0}] {1} {2}" -f $state, $name, $detail)
    Add-Content -LiteralPath $log -Value ("[{0}] {1} {2}" -f $state, $name, $detail)
}
function Stop-Build([string]$message) { throw $message }

try {
    Say "Solforge MCP-only build $stamp"
    if (Test-Path -LiteralPath $lock) { Stop-Build "Another build may be running (lock: $lock)." }
    try {
        $lockStream = [IO.File]::Open($lock, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $lockTaken = $true
        try {
            $bytes = [Text.UTF8Encoding]::new($false).GetBytes("pid $PID started $(Get-Date -Format o)")
            $lockStream.Write($bytes, 0, $bytes.Length)
        } finally { $lockStream.Dispose() }
    } catch { Stop-Build "Could not take build lock $lock." }

    if (Get-Process -Name destiny2 -ErrorAction SilentlyContinue) { Stop-Build 'destiny2.exe is running; close the game before building.' }
    foreach ($tool in 'dotnet', 'git') { if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { Stop-Build "$tool is not on PATH." } }
    $publishScript = Join-Path $root 'publish.ps1'
    if (-not (Test-Path -LiteralPath $publishScript -PathType Leaf)) { Stop-Build "Editor publish script missing: $publishScript" }

    if ($PreflightOnly) { Result 'preflight' 'PASS'; return }
    if (-not $Output) { $Output = Join-Path $root "publish-test-mcp-only-$stamp" }
    $Output = [IO.Path]::GetFullPath($Output)
    if (Test-Path -LiteralPath $Output) {
        if (-not (Test-Path -LiteralPath $Output -PathType Container) -or (Get-ChildItem -LiteralPath $Output -Force | Select-Object -First 1)) {
            Stop-Build "Output must be a new or empty folder: $Output"
        }
    }
    foreach ($process in Get-Process -Name Solforge, solforge-mcp -ErrorAction SilentlyContinue) {
        try {
            if ($process.Path -and $process.Path.StartsWith($Output.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
                Stop-Build "Close $($process.ProcessName) ($($process.Id)) using the output folder before rebuilding."
            }
        } catch [System.ComponentModel.Win32Exception] { }
    }

    Say "Output: $Output"
    Say 'Publishing the editor and MCP server only.'
    & pwsh -NoProfile -File $publishScript -Output $Output *>&1 | Tee-Object -FilePath $log -Append | Out-Host
    if ($LASTEXITCODE -ne 0) { Stop-Build "publish.ps1 failed with exit code $LASTEXITCODE." }

    foreach ($file in 'Solforge.exe', 'solforge-mcp.exe') {
        if (-not (Test-Path -LiteralPath (Join-Path $Output $file) -PathType Leaf)) { Stop-Build "Missing expected output: $file" }
        Result $file 'PASS'
    }
    $skillFiles = @(Get-ChildItem -LiteralPath (Join-Path $Output 'skills') -Recurse -File -Filter 'SKILL.md' -ErrorAction SilentlyContinue)
    if ($skillFiles.Count -eq 0) { Stop-Build 'No project skill playbooks were packaged with the MCP editor.' }
    Result 'project skills' 'PASS' "$($skillFiles.Count) playbooks"
    foreach ($folder in 'solforge-ai', 'ai-runtime') {
        if (Test-Path -LiteralPath (Join-Path $Output $folder)) { Stop-Build "Unexpected retired component in output: $folder" }
    }
    Result 'no AI desktop/runtime' 'PASS'
    Result 'editor + MCP publish' 'PASS' $Output
} catch {
    Result 'build' 'FAIL' $_.Exception.Message
} finally {
    if ($lockTaken -and (Test-Path -LiteralPath $lock)) { Remove-Item -LiteralPath $lock -Force }
    $failed = @($results | Where-Object State -eq 'FAIL').Count
    Say ("{0} ({1} failed) Log: {2}" -f $(if ($failed -eq 0) { 'PASS' } else { 'FAIL' }), $failed, $log)
    if ($failed -gt 0) { exit 1 }
}
