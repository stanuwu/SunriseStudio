# Builds the self-contained Solforge.exe and solforge-mcp.exe into publish\.
# Releases and anything shared come only from a clean publish\ (RELEASING.md).
param([string]$Output)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$flavor = 'publish'
if (-not $Output) { $Output = Join-Path $PSScriptRoot $flavor }
# Source paths in the embedded PDBs become /_/ so no local folder names ship.
$buildProperties = @("-p:PathMap=$PSScriptRoot\=/_/")
# A stale folder could hide or add files, so each run starts from an empty one.
if (Test-Path -LiteralPath $Output) { Remove-Item -LiteralPath $Output -Recurse -Force }
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
# The license notices are embedded; Help > Third-party notices shows them.
$stale = Join-Path $Output 'THIRD_PARTY.md'
if (Test-Path $stale) { Remove-Item $stale }
# Solforge Phase 17: the in-repo Blender add-on ships as its own extension zip beside the exe.
& (Join-Path $PSScriptRoot 'package-blender.ps1') -Output $Output | Out-Null

Write-Host "Published to $Output"
