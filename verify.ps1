# Builds Studio and runs its checks. -Game adds the checks that read an installation. -Blender adds
# the Blender checks and needs -Plugin, a checkout of the Blender add-on. Output holds captures and scratch.
param([string]$Game, [string]$Blender, [switch]$SkipBuild,
    [string]$Output = (Join-Path ([IO.Path]::GetTempPath()) 'SunriseStudio-verify'),
    [string]$Plugin)
$ErrorActionPreference = 'Stop'
if ($Blender -and -not (Test-Path -LiteralPath (Join-Path "$Plugin" '__init__.py'))) {
    throw 'The Blender checks need -Plugin, a checkout of https://github.com/stanuwu/SunriseBlenderPlugin.'
}
if ($Blender) { $env:SUNRISE_BLENDER_ADDON = [IO.Path]::GetFullPath($Plugin) }
New-Item -ItemType Directory -Force $Output | Out-Null

function Get-Check([string]$Name, [string]$Framework) {
    Join-Path $PSScriptRoot "tests/SunriseStudio.$Name/bin/Release/$Framework/SunriseStudio.$Name.dll"
}
function Invoke-Dotnet([string]$Failure, [string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw $Failure }
}
# Vulkan: Blender's OpenGL backend can reset some AMD machines while EEVEE renders.
function Invoke-Blender([string]$Failure, [string]$Script, [string[]]$Arguments) {
    $command = @('--background', '--factory-startup', '--gpu-backend', 'vulkan', '--python-exit-code', '1',
        '--python', (Join-Path $PSScriptRoot "tests/$Script"))
    if ($Arguments) { $command += @('--') + $Arguments }
    & $Blender @command
    if ($LASTEXITCODE -ne 0) { throw $Failure }
}
function New-Scratch([string]$Name) { Join-Path $Output ($Name + '-' + [Guid]::NewGuid().ToString('N')) }

if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }
# The weapon checks are not in the solution, so they build here.
$weaponChecks = @('WeaponParticleBinding.Tests', 'WeaponPresentation.Tests')
if ($Game -and -not $SkipBuild) {
    foreach ($weaponCheck in $weaponChecks) {
        Invoke-Dotnet "Build failed: $weaponCheck" @('build', (Join-Path $PSScriptRoot "tests/SunriseStudio.$weaponCheck/SunriseStudio.$weaponCheck.csproj"), '-c', 'Release', '-m:1', '-nr:false')
    }
}

# Each check program and its target framework, in run order.
$studioChecks = [ordered]@{
    'Projects.Tests' = 'net9.0'; 'Formats.Tests' = 'net9.0'; 'Missions.Tests' = 'net9.0'; 'Cui.Tests' = 'net9.0'
    'Cui.Gpu.Tests' = 'net9.0-windows'; 'Investment.Tests' = 'net9.0-windows'; 'Live.Tests' = 'net9.0-windows'
    'LiveBinding.Tests' = 'net9.0-windows'; 'LivePose.Tests' = 'net9.0-windows'; 'Audio.Tests' = 'net9.0-windows'
    'Authoring.Tests' = 'net9.0'; 'Overlay.Tests' = 'net9.0'; 'Names.Tests' = 'net9.0'; 'World.Tests' = 'net9.0'
    'Collision.Tests' = 'net9.0'; 'Navigation.Tests' = 'net9.0'; 'CollisionView.Tests' = 'net9.0-windows'; 'AnimationAuthoring.Tests' = 'net9.0'
    'Animation.Tests' = 'net9.0'; 'MaterialInterchange.Tests' = 'net9.0'; 'Interchange.Tests' = 'net9.0-windows'
    'MeshAuthoring.Tests' = 'net9.0-windows'; 'AssetSurface.Tests' = 'net9.0-windows'; 'Shading.Tests' = 'net9.0-windows'
    'PackageReplacement.Tests' = 'net9.0'; 'Editor.Tests' = 'net9.0-windows'; 'Creation.Tests' = 'net9.0-windows'
    'OriginalAsset.Tests' = 'net9.0'; 'Shortcuts.Tests' = 'net9.0-windows'
}
# The arguments each check takes with -Game; checks not listed take none.
$gameArgs = @{
    'Cui.Gpu.Tests' = @('--game-root', $Game, '--output', (Join-Path $Output 'cui-fonts'))
    'Investment.Tests' = @('--game-root', $Game, '--ui', (Join-Path $Output 'features/investment-stat-authoring.png'))
    'LivePose.Tests' = @('--game', $Game); 'Animation.Tests' = @('--game', $Game)
    'CollisionView.Tests' = @('--game-root', $Game, '--output', (New-Scratch 'collision'))
    'AssetSurface.Tests' = @('--game-root', $Game, '--output', (Join-Path $Output 'isolated-native-asset.png'))
}
foreach ($name in 'Projects', 'Formats', 'Missions', 'Cui', 'Audio', 'Authoring', 'AnimationAuthoring', 'MeshAuthoring',
    'Names', 'World', 'Collision', 'Navigation', 'Shading', 'PackageReplacement', 'Editor', 'OriginalAsset') {
    $gameArgs["$name.Tests"] = @('--game-root', $Game)
}
# With -Blender, the Blender section runs the Interchange checks that read the installation.
if ($Blender) { $gameArgs.Remove('Interchange.Tests') } else { $gameArgs['Interchange.Tests'] = @('--game-root', $Game) }
foreach ($name in $studioChecks.Keys) {
    $checkArgs = @(Get-Check $name $studioChecks[$name])
    if ($Game -and $gameArgs.Contains($name)) { $checkArgs += $gameArgs[$name] }
    if ($Game -and $Blender -and $name -eq 'CollisionView.Tests') { $checkArgs += @('--blender', $Blender) }
    if ($Blender -and $name -eq 'AnimationAuthoring.Tests') { $checkArgs += @('--blender', $Blender) }
    Invoke-Dotnet "Check failed: $name" $checkArgs
}

if ($Game) {
    $missionUi = Get-Check 'Missions.UiTests' 'net9.0-windows'
    Invoke-Dotnet 'Mission authoring UI checks failed.' @($missionUi, $Game, (Join-Path $Output 'mission-typed.png'))
    Invoke-Dotnet 'Mission control-flow UI checks failed.' @($missionUi, $Game, '--control-flow', (Join-Path $Output 'features/mission-control-flow.png'))
    Invoke-Dotnet 'Mission scene UI checks failed.' @($missionUi, $Game, '--scenes', (Join-Path $Output 'features/mission-scenes.png'))
    # The checked-in schema stands in for an installation that has not written mission_api.json yet.
    Invoke-Dotnet 'Mission typed node UI checks failed.' @($missionUi, $Game, '--api-nodes',
        (Join-Path $PSScriptRoot 'tests/SunriseStudio.Missions.Tests/mission_api.json'), (Join-Path $Output 'features/mission-api-nodes.png'))
    Invoke-Dotnet 'Mission SDK problem UI checks failed.' @($missionUi, $Game, '--sdk-problems', (Join-Path $Output 'features/mission-sdk-problems.png'))
    foreach ($weaponCheck in $weaponChecks) {
        Invoke-Dotnet "Check failed: $weaponCheck" @((Get-Check $weaponCheck 'net9.0'), '--game-root', $Game)
    }
}
$viewportArgs = @((Get-Check 'Viewport.Tests' 'net9.0-windows'), '--output', $Output)
if ($Game) { $viewportArgs += @('--game', $Game) }
Invoke-Dotnet 'Viewport checks failed.' $viewportArgs
$selectionArgs = @(Get-Check 'Selection.Tests' 'net9.0-windows')
if ($Game) { $selectionArgs += @('--game', $Game) }
Invoke-Dotnet 'Selection checks failed.' $selectionArgs
Invoke-Dotnet 'Editor smoke check failed.' @((Join-Path $PSScriptRoot 'src/SunriseStudio.App/bin/Release/net9.0-windows/Sunrise Studio.dll'),
    '--smoke', '--warp', '--settings', (Join-Path $Output 'verify-settings.json'), '--capture', (Join-Path $Output 'studio-setup.png'))

if ($Blender) {
    $interchange = Get-Check 'Interchange.Tests' 'net9.0-windows'
    $addon = Join-Path $Plugin 'bundle.py'
    $worldOutput = New-Scratch 'world-scene'
    Invoke-Dotnet 'Shared world export checks failed.' @($interchange, '--world-scene', $worldOutput)
    Invoke-Blender 'Shared world Blender checks failed.' 'SunriseStudio.Interchange.Tests/blender_world_scene.py' @(
        (Join-Path $worldOutput 'world.sunrise-scene.json'), $Plugin, (Join-Path ($worldOutput + '-terrain') 'world.sunrise-scene.json'),
        (Join-Path ($worldOutput + '-inputs') 'world.sunrise-scene.json'), (Join-Path ($worldOutput + '-decorators') 'world.sunrise-scene.json'))
    $lightsOutput = New-Scratch 'world-lights'
    Invoke-Dotnet 'World light export checks failed.' @($interchange, '--world-lights', $lightsOutput)
    Invoke-Blender 'World light Blender checks failed.' 'SunriseStudio.Interchange.Tests/blender_world_lights.py' @(
        (Join-Path $lightsOutput 'world.sunrise-scene.json'), $Plugin)
    if ($Game) {
        $worldBasisOutput = New-Scratch 'world-basis'
        Invoke-Dotnet 'Owned world basis export failed.' @($interchange, '--world-basis', $Game, '80F74ABE', $worldBasisOutput)
        Invoke-Blender 'Owned world basis Blender comparison failed.' 'SunriseStudio.Interchange.Tests/blender_world_basis.py' @(
            (Join-Path $worldBasisOutput 'oracle.json'), $Plugin)
    }
    # These scripts load the add-on from a Blender profile that holds a copy of the checkout.
    $pluginHome = New-Scratch 'blender-plugin'
    New-Item -ItemType Directory -Force (Join-Path $pluginHome 'addons') | Out-Null
    Copy-Item -Recurse $Plugin (Join-Path $pluginHome 'addons')
    $priorBlenderScripts = $env:BLENDER_USER_SCRIPTS
    $env:BLENDER_USER_SCRIPTS = $pluginHome
    try {
        Invoke-Blender 'Blender add-on install check failed.' 'SunriseStudio.Interchange.Tests/blender_plugin_install.py'
        Invoke-Blender 'Blender original asset export check failed.' 'SunriseStudio.OriginalAsset.Tests/blender_original_export.py'
        if ($Game) {
            $originalOutput = New-Scratch 'original-material'
            Invoke-Blender 'Original material fixture export failed.' 'SunriseStudio.OriginalAsset.Tests/blender_original_materials.py' @($originalOutput)
            Invoke-Dotnet 'Original native material readback failed.' @((Get-Check 'MeshAuthoring.Tests' 'net9.0-windows'), '--game-root', $Game,
                '--original', (Join-Path $originalOutput 'panels.glb'))
            Invoke-Blender 'Original material Blender comparison failed.' 'SunriseStudio.OriginalAsset.Tests/blender_original_oracle.py' @($originalOutput)
        }
    }
    finally { $env:BLENDER_USER_SCRIPTS = $priorBlenderScripts }
    Invoke-Blender 'Blender asset bundle check failed.' 'SunriseStudio.Interchange.Tests/blender_bundle_smoke.py' @((New-Scratch 'blender-bundle'), $addon)
    Invoke-Blender 'Blender collision bundle check failed.' 'SunriseStudio.Interchange.Tests/blender_collision_smoke.py' @((New-Scratch 'blender-collision'), $addon)
    if ($Game) {
        $ownedBundleOutput = New-Scratch 'owned-bundle'
        Invoke-Dotnet 'Owned asset bundle export or clone check failed.' @($interchange, '--game-root', $Game, '--export-root', $ownedBundleOutput)
        Invoke-Blender 'Owned Blender asset bundle check failed.' 'SunriseStudio.Interchange.Tests/blender_bundle_roundtrip.py' @(
            (Join-Path $ownedBundleOutput 'asset.sunrise-bundle.json'), $addon, '--combined')
        Invoke-Dotnet 'Owned Blender asset bundle reimport failed.' @($interchange, '--game-root', $Game, '--bundle',
            (Join-Path $ownedBundleOutput 'asset.edited.sunrise-bundle.json'))
        $guardianOutput = New-Scratch 'guardian'
        Invoke-Dotnet 'Guardian Blender export check failed.' @((Get-Check 'Editor.Tests' 'net9.0-windows'), '--guardian-export', $Game, $guardianOutput)
        Invoke-Blender 'Guardian Blender material check failed.' 'SunriseStudio.Interchange.Tests/blender_guardian_materials.py' @(
            (Join-Path $guardianOutput 'guardian-export/asset.sunrise-bundle.json'), $addon)
    }
    $materialOutput = New-Scratch 'material'
    $materialCheck = Get-Check 'MaterialInterchange.Tests' 'net9.0'
    Invoke-Dotnet 'Material bundle check failed.' @($materialCheck, '--export', $materialOutput)
    Invoke-Blender 'Blender material check failed.' 'SunriseStudio.MaterialInterchange.Tests/blender_smoke.py' @($materialOutput, $Plugin)
    Invoke-Dotnet 'Blender material reimport failed.' @($materialCheck, '--import-check', (Join-Path $materialOutput 'material.edited.sunrise-material.json'))
}
