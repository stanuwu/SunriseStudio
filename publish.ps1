# Builds the self-contained Solforge.exe and solforge-mcp.exe.
#   .\publish.ps1          Owner build (SolforgeRelay=true) into publish\: the owner-only features from the private
#                          folder (SolforgeRelayDir, by default ..\09-solforge-private\relay). For the owner's own PC only.
#   .\publish.ps1 -Public  Public build (SolforgeRelay=false) into publish-public\, then scans it for owner-only strings
#                          and fails if any is found. Releases and anything shared come ONLY from publish-public\ (RELEASING.md).
param([string]$Output, [switch]$Public, [string]$RelayDir, [string]$ScanList)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$flavor = if ($Public) { 'publish-public' } else { 'publish' }
if (-not $Output) { $Output = Join-Path $PSScriptRoot $flavor }
if (-not $RelayDir) { $RelayDir = Join-Path $PSScriptRoot '..\09-solforge-private\relay' }
$RelayDir = [IO.Path]::GetFullPath($RelayDir)
if (-not $ScanList) { $ScanList = Join-Path $RelayDir 'release-scan.txt' }
$buildProperties = if ($Public) {
    # Source paths in the embedded PDBs become /_/ so no local folder names ship.
    @('-p:SolforgeRelay=false', "-p:PathMap=$PSScriptRoot\=/_/")
} else {
    if (-not (Test-Path -LiteralPath (Join-Path $RelayDir 'Mcp/RelayTools.cs'))) {
        throw "The owner build needs the private relay sources in $RelayDir (pass -RelayDir), or run .\publish.ps1 -Public."
    }
    @('-p:SolforgeRelay=true', "-p:SolforgeRelayDir=$RelayDir")
}
# The public build must prove it is clean, so its scan list has to be there before anything builds. It lives in the
# private folder; a checkout without that folder has no owner-only sources to leak, so there is nothing to scan for.
$patterns = @()
if ($Public) {
    if (Test-Path -LiteralPath $ScanList) {
        $patterns = @(Get-Content -LiteralPath $ScanList | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })
        if ($patterns.Count -eq 0) { throw "The release scan list $ScanList is empty." }
    }
    elseif (Test-Path -LiteralPath $RelayDir) {
        throw "No release scan list at $ScanList (one string per line; pass -ScanList). The public build cannot be verified without it."
    }
    else { Write-Host "No private folder at $RelayDir, so no owner-only sources exist here; the owner-string scan is skipped." }
}
# A stale folder could hide or add files, so each run starts from an empty one.
if ($Public -and (Test-Path -LiteralPath $Output)) { Remove-Item -LiteralPath $Output -Recurse -Force }
# One self-contained exe that needs no .NET install, with the runtime and the license notices inside.
# The build gets its own folder, so a running developer build does not lock it.
# A trailing backslash would escape the closing quote of the quoted argument.
$buildOutput = "$PSScriptRoot/obj/$flavor/"
$shimOutput = "$PSScriptRoot/obj/$flavor-shim/"
& dotnet publish (Join-Path $PSScriptRoot 'src/SunriseStudio.App/SunriseStudio.App.csproj') -c Release -r win-x64 `
    --self-contained true -o $Output -nr:false "-p:OutDir=$buildOutput" `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=embedded -p:SatelliteResourceLanguages=en @buildProperties
if ($LASTEXITCODE -ne 0) { throw 'Studio publish failed.' }
# Solforge Phase 47B: solforge-mcp.exe, the MCP stdio shim, sits beside Solforge.exe (trimmed, self-contained).
& dotnet publish (Join-Path $PSScriptRoot 'src/Solforge.Mcp.Shim/Solforge.Mcp.Shim.csproj') -c Release -r win-x64 `
    --self-contained true -o $Output -nr:false "-p:OutDir=$shimOutput" `
    -p:PublishSingleFile=true -p:PublishTrimmed=true -p:DebugType=embedded @buildProperties
if ($LASTEXITCODE -ne 0) { throw 'MCP shim publish failed.' }
# Compile the native Parhelion package builder from the pinned Sundial submodule and ship beside Solforge.
& cargo build --locked --release --manifest-path (Join-Path $PSScriptRoot 'tools/parhelion-bridge/Cargo.toml')
if ($LASTEXITCODE -ne 0) { throw 'Parhelion bridge build failed.' }
$bridge = Join-Path $PSScriptRoot 'tools/parhelion-bridge/target/release/solforge-parhelion-bridge.exe'
if (-not (Test-Path -LiteralPath $bridge)) { $bridge = Join-Path $PSScriptRoot 'target/release/solforge-parhelion-bridge.exe' }
if (-not (Test-Path -LiteralPath $bridge)) { throw "Parhelion bridge was not produced: $bridge" }
Copy-Item -LiteralPath $bridge -Destination $Output -Force
# The license notices are embedded; Help > Third-party notices shows them.
$stale = Join-Path $Output 'THIRD_PARTY.md'
if (Test-Path $stale) { Remove-Item $stale }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'tools/parhelion-bridge/NOTICE.md') -Destination (Join-Path $Output 'PARHELION-BRIDGE-NOTICE.md') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'third_party/sundial/LICENSE') -Destination (Join-Path $Output 'PARHELION-GPL-3.0.txt') -Force
# Solforge Phase 17: the in-repo Blender add-on ships as its own extension zip beside the exe.
& (Join-Path $PSScriptRoot 'package-blender.ps1') -Output $Output | Out-Null

if ($Public -and $patterns.Count -gt 0) {
    # Every shipped file, plus the uncompressed assemblies the single-file exes were bundled from (the bundle
    # compresses them, so the exe alone would hide a match). Each string is looked for as UTF-8 and as UTF-16.
    $latin1 = [Text.Encoding]::GetEncoding(28591)
    $needles = foreach ($pattern in $patterns) {
        $pattern
        $latin1.GetString([Text.Encoding]::Unicode.GetBytes($pattern))
    }
    $files = @(Get-ChildItem -LiteralPath $Output -Recurse -File)
    foreach ($folder in $buildOutput, $shimOutput) {
        if (Test-Path -LiteralPath $folder) { $files += @(Get-ChildItem -LiteralPath $folder -Recurse -File -Include '*.dll', '*.exe') }
    }
    $found = @()
    foreach ($file in $files) {
        $text = $latin1.GetString([IO.File]::ReadAllBytes($file.FullName))
        for ($i = 0; $i -lt $needles.Count; $i++) {
            if ($text.IndexOf($needles[$i], [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $found += "$($file.FullName): '$($patterns[[Math]::Floor($i / 2)])'"
            }
        }
    }
    if ($found.Count -gt 0) {
        $found | ForEach-Object { Write-Host "OWNER-ONLY STRING IN PUBLIC BUILD: $_" -ForegroundColor Red }
        Remove-Item -LiteralPath $Output -Recurse -Force
        throw "The public build contains owner-only strings ($($found.Count) hit(s)); $Output was deleted. Do not share it."
    }
    Write-Host "Scanned $($files.Count) files for $($patterns.Count) owner-only strings: clean."
}
Write-Host "Published to $Output"
