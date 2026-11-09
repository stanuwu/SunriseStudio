#requires -Version 7.0
<#
.SYNOPSIS
    Build and publish the Solforge editor and MCP server.
.DESCRIPTION
    Publishes the editor and MCP server only. The separate Solforge AI desktop app and runtime are not part of this build.
#>
[CmdletBinding()]
param(
    [string]$SolRoot = "C:\Users\Seth\Desktop\Project - Sol\sol\09-solforge",
    [string]$FinalOutput = "C:\Users\Seth\Desktop\Project - Sol\sol\09-solforge\publish"
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Resolves a file path from a list of preferred candidate paths, returning the first one that exists on disk.
function Resolve-FirstExisting([string[]]$Candidates) {
    foreach ($cand in $Candidates) {
        if ($cand -and (Test-Path -LiteralPath $cand -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $cand).Path
        }
    }
    return $null
}

# Refuses to overwrite a running Solforge process; the caller closes it deliberately and retries.
function Close-RunningSolforgeProcesses([string[]]$ProcessNames) {
    foreach ($name in $ProcessNames) {
        $procs = Get-Process -Name $name -ErrorAction SilentlyContinue
        if ($procs) {
            throw "Close active process '$name' before publishing; this build will not terminate the editor or MCP server."
        }
    }
}

# Copies staged files into the final output without deleting unrelated content.
function Sync-DirectoryContents([string]$SourceDir, [string]$DestDir) {
    if (-not (Test-Path -LiteralPath $DestDir)) {
        New-Item -ItemType Directory -Path $DestDir -Force | Out-Null
    }
    # No mirror/delete: retired AI directories are removed explicitly after the Drive backup below.
    $robocopy = Get-Command robocopy.exe -ErrorAction SilentlyContinue
    if ($robocopy) {
        & robocopy.exe $SourceDir $DestDir /E /R:2 /W:1 /NP /NFL /NDL | Out-Null
        # In Robocopy, exit codes 0-7 indicate successful copy operations (8+ indicates fatal error)
        if ($LASTEXITCODE -ge 8) {
            throw "Robocopy synchronization failed with exit code $LASTEXITCODE."
        }
    } else {
        # Fallback to Copy-Item
        Copy-Item -Path (Join-Path $SourceDir "*") -Destination $DestDir -Recurse -Force
    }
}

# Archive the previously published AI app/runtime to Drive before removing those two retired build outputs.
function Archive-RetiredAiOutput([string]$OutputDir) {
    $names = @('solforge-ai', 'ai-runtime')
    $present = @($names | Where-Object { Test-Path -LiteralPath (Join-Path $OutputDir $_) -PathType Container })
    if ($present.Count -eq 0) { return }
    $drive = 'G:\My Drive'
    if (-not (Test-Path -LiteralPath $drive -PathType Container)) { throw 'Google Drive is unavailable; leaving the previous Solforge AI output untouched.' }
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $batch = Join-Path (Join-Path $drive ("Backups\sol-" + (Get-Date -Format 'yyyyMMdd'))) ("solforge-ai-retired-" + $stamp)
    if (Test-Path -LiteralPath $batch) { throw "Backup target already exists: $batch" }
    New-Item -ItemType Directory -Path $batch -Force | Out-Null
    $origins = [System.Collections.Generic.List[string]]::new()
    foreach ($name in $present) {
        $source = Join-Path $OutputDir $name
        $target = Join-Path $batch $name
        Copy-Item -LiteralPath $source -Destination $target -Recurse -Force
        $a = Get-ChildItem -LiteralPath $source -Recurse -File -Force | Measure-Object Length -Sum
        $b = Get-ChildItem -LiteralPath $target -Recurse -File -Force | Measure-Object Length -Sum
        if ($a.Count -ne $b.Count -or $a.Sum -ne $b.Sum) { throw "Drive backup of $name failed count/size verification; original left in place." }
        $origins.Add("- ``$name`` came from ``$source`` ($($a.Count) files, $($a.Sum) bytes).")
    }
    $readme = @("# Retired Solforge AI build output", '', "Archived: $(Get-Date -Format 'yyyy-MM-dd').", '', 'Origins:') + $origins.ToArray()
    Set-Content -LiteralPath (Join-Path $batch 'README.md') -Value $readme -Encoding utf8
    foreach ($name in $present) {
        $source = Join-Path $OutputDir $name
        Remove-Item -LiteralPath $source -Recurse -Force
    }
}

# --- Path Resolution & Pre-flight Validation ---
Write-Host "=====================================================================" -ForegroundColor Green
Write-Host " Solforge Editor + MCP Build & Publish" -ForegroundColor Green
Write-Host "=====================================================================" -ForegroundColor Green

$SolRoot = [IO.Path]::GetFullPath($SolRoot)
$FinalOutput = [IO.Path]::GetFullPath($FinalOutput)

Write-Host "Solforge Root:   $SolRoot" -ForegroundColor Gray
Write-Host "Final Output:    $FinalOutput" -ForegroundColor Gray

# Close any running Solforge / AI processes to release file locks
Close-RunningSolforgeProcesses @('Solforge', 'solforge-mcp', 'solforge-ai')

# Setup clean staging directory for the editor and MCP server.
$stagingDir = Join-Path $env:TEMP ("solforge-publish-staging-" + [Guid]::NewGuid().ToString("N"))
if (Test-Path -LiteralPath $stagingDir) {
    Remove-Item -LiteralPath $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

try {
    # --- Publish Editor & MCP Shim into Staging ---
    Write-Host "`nPublishing Solforge Editor & MCP Shim (publish.ps1)..." -ForegroundColor Cyan
    $publishScript = Join-Path $SolRoot "publish.ps1"
    if (-not (Test-Path -LiteralPath $publishScript)) {
        throw "publish.ps1 not found in $SolRoot"
    }
    & pwsh -NoProfile -File $publishScript -Output $stagingDir
    if ($LASTEXITCODE -ne 0) { throw "publish.ps1 failed with exit code $LASTEXITCODE." }

    # --- Synchronize & Replace Files in Final Destination ---
    Write-Host "`n[Finalizing] Synchronizing files into $FinalOutput..." -ForegroundColor Cyan
    Archive-RetiredAiOutput $FinalOutput
    Sync-DirectoryContents -SourceDir $stagingDir -DestDir $FinalOutput

    Write-Host "`n=====================================================================" -ForegroundColor Green
    Write-Host " BUILD AND PUBLISH COMPLETE!" -ForegroundColor Green
    Write-Host " Output Folder: $FinalOutput" -ForegroundColor Green
    Write-Host "=====================================================================" -ForegroundColor Green
}
finally {
    # Clean up temporary staging
    if (Test-Path -LiteralPath $stagingDir) {
        Remove-Item -LiteralPath $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
