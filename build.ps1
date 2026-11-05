# Developer build, used by verify.ps1. Users build with publish.ps1. This is the public configuration
# (SolforgeRelay=false, Directory.Build.props): no owner-only features (RELEASING.md).
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
& dotnet build (Join-Path $PSScriptRoot 'Sunrise Studio.sln') -c $Configuration -m:1 -nr:false -p:BuildInParallel=false -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw 'Studio build failed.' }
# Build the native CLI bridge used by MCP recipe validation and package staging.
& cargo build --locked --manifest-path (Join-Path $PSScriptRoot 'tools/parhelion-bridge/Cargo.toml')
if ($LASTEXITCODE -ne 0) { throw 'Parhelion bridge build failed.' }
$appOutput = Join-Path $PSScriptRoot "src/SunriseStudio.App/bin/$Configuration/net9.0-windows"
$bridge = Join-Path $PSScriptRoot 'tools/parhelion-bridge/target/debug/solforge-parhelion-bridge.exe'
if (Test-Path -LiteralPath $appOutput) { Copy-Item -LiteralPath $bridge -Destination $appOutput -Force }
