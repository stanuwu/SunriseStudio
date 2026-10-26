#requires -Version 7.4
<#
Packages the Solforge AI desktop window: an offline Electron distribution (from a user-supplied,
checksum-verified electron-v<version>-win32-x64.zip) plus the owned apps/desktop shell as an app
directory. It does NOT use upstream electron-builder / package-target.ts (those download Electron,
Node, CPython and wheels and expect signing). Output: <Output>/solforge-ai. Run after
package-runtime.ps1, which produced <Output>/ai-runtime (the Host, dsh and desktop-runtime.json).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Output,
    [Parameter(Mandatory)][string]$ElectronZip,
    [string]$RuntimeDir,
    [string]$NodePath,
    [string]$PnpmPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ownedRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\solforge-ai'))
$outputRoot = [IO.Path]::GetFullPath($Output)
$destination = Join-Path $outputRoot 'solforge-ai'
if (Test-Path -LiteralPath $destination) { throw "Refusing to overwrite $destination. Choose a fresh build output." }
if ($outputRoot.StartsWith($ownedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $outputRoot -eq $ownedRoot) {
    throw 'Output must be outside the owned runtime workspace.'
}
if (!$RuntimeDir) { $RuntimeDir = Join-Path $outputRoot 'ai-runtime' }
$RuntimeDir = [IO.Path]::GetFullPath($RuntimeDir)
foreach ($required in @('desktop-runtime.json', 'ai-runtime-manifest.json', 'node/node.exe',
    'node_modules/@deepseek-ai/dsh-desktop-host/lib/index.js', 'node_modules/@deepseek-ai/dsh/package.json',
    'node_modules/@deepseek-ai/dsh-web-frontend/dist/index.html')) {
    if (!(Test-Path -LiteralPath (Join-Path $RuntimeDir $required) -PathType Leaf)) { throw "ai-runtime is missing $required. Run package-runtime.ps1 (current version) first." }
}
$desktopApp = Join-Path $ownedRoot 'apps/desktop'
foreach ($required in @('package.json', 'lib/main.js', 'lib/preload-app.cjs', 'lib/preload-welcome.cjs', 'lib/preload-platform-account.cjs',
    'lib/preload-mandatory.cjs', 'lib/preload-update-dialog.cjs', 'lib/welcome/welcome.js', 'renderer/welcome.html',
    'resources/icon-windows.png', 'resources/tray-windows.ico')) {
    if (!(Test-Path -LiteralPath (Join-Path $desktopApp $required) -PathType Leaf)) { throw "Owned desktop shell artifact missing: apps/desktop/$required. Run ai/build-runtime.ps1." }
}
$mainText = [IO.File]::ReadAllText((Join-Path $desktopApp 'lib/main.js'))
foreach ($marker in @('SOLFORGE_AI_CONTROL_PIPE', 'SOLFORGE_AI_RUNTIME_DIR')) {
    if (!$mainText.Contains($marker)) { throw "apps/desktop/lib/main.js lacks the Solforge launch mode ($marker). Run ai/build-runtime.ps1." }
}
$desktopVersion = (Get-Content -LiteralPath (Join-Path $desktopApp 'package.json') -Raw | ConvertFrom-Json).version
$runtimeDescriptor = Get-Content -LiteralPath (Join-Path $RuntimeDir 'desktop-runtime.json') -Raw | ConvertFrom-Json
if ($runtimeDescriptor.release.version -ne $desktopVersion) {
    throw "ai-runtime desktop-runtime.json release $($runtimeDescriptor.release.version) does not match apps/desktop $desktopVersion."
}

# Electron: the version the owned shell was built against, verified offline against its bundled checksums.
# Read through the pnpm link; only package.json and checksums.json are used (the binary was never installed).
$electronPackage = Join-Path $desktopApp 'node_modules/electron'
foreach ($required in @('package.json', 'checksums.json')) {
    if (!(Test-Path -LiteralPath (Join-Path $electronPackage $required) -PathType Leaf)) { throw "apps/desktop/node_modules/electron/$required is missing; it is needed to verify the Electron zip offline." }
}
$electronVersion = (Get-Content -LiteralPath (Join-Path $electronPackage 'package.json') -Raw | ConvertFrom-Json).version
$zipName = "electron-v$electronVersion-win32-x64.zip"
$checksums = Get-Content -LiteralPath (Join-Path $electronPackage 'checksums.json') -Raw | ConvertFrom-Json -AsHashtable
if (!$checksums.ContainsKey($zipName)) { throw "Electron checksums.json has no entry for $zipName." }
if (!(Test-Path -LiteralPath $ElectronZip -PathType Leaf)) { throw "Electron zip not found: $ElectronZip. Supply $zipName; nothing is downloaded." }
$ElectronZip = (Resolve-Path -LiteralPath $ElectronZip).Path
$zipHash = (Get-FileHash -LiteralPath $ElectronZip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($zipHash -ne ([string]$checksums[$zipName]).ToLowerInvariant()) { throw "Electron zip SHA-256 does not match $zipName in the bundled checksums.json. Refusing to package it." }
Write-Host "Electron $electronVersion zip verified (SHA-256 $zipHash)."

if (!$NodePath) { $NodePath = (Get-Command node.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source }
$NodePath = (Resolve-Path -LiteralPath $NodePath).Path
if (!$PnpmPath) {
    $pnpmCommand = Get-Command pnpm -ErrorAction SilentlyContinue | Select-Object -First 1
    $candidates = [Collections.Generic.List[string]]::new()
    $candidates.Add((Join-Path $ownedRoot 'node_modules/pnpm/bin/pnpm.cjs'))
    if ($pnpmCommand) {
        if ([IO.Path]::GetExtension($pnpmCommand.Source) -eq '.exe') { $candidates.Add($pnpmCommand.Source) }
        $base = Split-Path -Parent $pnpmCommand.Source
        for ($i = 0; $i -lt 5 -and $base; $i++) {
            foreach ($relative in @('node_modules/pnpm/bin/pnpm.cjs','pnpm/bin/pnpm.cjs','pnpm/bin/pnpm.mjs')) { $candidates.Add((Join-Path $base $relative)) }
            $base = Split-Path -Parent $base
        }
    }
    $PnpmPath = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if (!$PnpmPath) { throw 'Installed pnpm entrypoint missing. Supply -PnpmPath; installation is not automatic.' }
$PnpmPath = (Resolve-Path -LiteralPath $PnpmPath).Path
if ([IO.Path]::GetExtension($PnpmPath) -notin @('.exe','.js','.cjs','.mjs')) { throw 'PnpmPath must be a native executable or a real JavaScript entrypoint, not .cmd/.ps1.' }

function New-ToolStart([string]$Executable, [string[]]$Arguments, [string]$Directory) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $Directory
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start.Environment.Remove('NODE_OPTIONS') | Out-Null
    $start.Environment.Remove('NODE_PATH') | Out-Null
    $start.Environment['COREPACK_ENABLE_NETWORK'] = '0'
    return $start
}
function Invoke-Tool([string]$Executable, [string[]]$Arguments, [string]$Directory) {
    $process = [Diagnostics.Process]::Start((New-ToolStart $Executable $Arguments $Directory))
    try {
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "Desktop packaging command failed with exit code $($process.ExitCode). Nothing was published." }
    } finally { $process.Dispose() }
}
function Get-ToolOutput([string]$Executable, [string[]]$Arguments, [string]$Directory) {
    $start = New-ToolStart $Executable $Arguments $Directory
    $start.RedirectStandardOutput = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        $text = $process.StandardOutput.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "Tool query failed with exit code $($process.ExitCode)." }
        return $text.Trim()
    } finally { $process.Dispose() }
}
function Invoke-Pnpm([string[]]$Arguments, [string]$Directory) {
    if ([IO.Path]::GetExtension($PnpmPath) -eq '.exe') { Invoke-Tool $PnpmPath $Arguments $Directory }
    else { Invoke-Tool $NodePath (@($PnpmPath) + $Arguments) $Directory }
}
function Get-PnpmOutput([string[]]$Arguments, [string]$Directory) {
    if ([IO.Path]::GetExtension($PnpmPath) -eq '.exe') { return Get-ToolOutput $PnpmPath $Arguments $Directory }
    return Get-ToolOutput $NodePath (@($PnpmPath) + $Arguments) $Directory
}
$pinned = (Get-Content -LiteralPath (Join-Path $ownedRoot 'package.json') -Raw | ConvertFrom-Json).packageManager
if ($pinned -notmatch '^pnpm@([0-9][^+\s]*)') { throw "Workspace package.json lacks a pnpm@<version> packageManager pin (found: $pinned)." }
$pinnedVersion = $Matches[1]
$actualVersion = Get-PnpmOutput @('--version') $ownedRoot
if ($actualVersion -ne $pinnedVersion) { throw "pnpm version mismatch: workspace pins $pinnedVersion but $PnpmPath reports $actualVersion." }
$storePath = $null
try { $storePath = Get-PnpmOutput @('store','path') $ownedRoot } catch { Write-Warning 'pnpm store path could not be queried; it will not be part of the leak scan.' }

# Staging lives in the output folder (same-volume rename at the end); scratch lives in the temp folder.
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$staging = Join-Path $outputRoot ('.solforge-ai-staging-' + [Guid]::NewGuid().ToString('N'))
$work = Join-Path ([IO.Path]::GetTempPath()) ('solforge-ai-desktop-package-' + [Guid]::NewGuid().ToString('N'))
$clone = Join-Path $work 'workspace'
$deploy = Join-Path $work 'desktop-deployed'
New-Item -ItemType Directory -Path $work -ErrorAction Stop | Out-Null
New-Item -ItemType Directory -Path $staging -ErrorAction Stop | Out-Null
Write-Host "Desktop packaging scratch directory: $work"
Write-Host "Desktop staging directory: $staging"

# 1. Electron distribution into staging, renamed. Only staging files are changed.
Expand-Archive -LiteralPath $ElectronZip -DestinationPath $staging
$electronExe = Join-Path $staging 'electron.exe'
if (!(Test-Path -LiteralPath $electronExe -PathType Leaf) -or !(Test-Path -LiteralPath (Join-Path $staging 'resources') -PathType Container)) {
    throw "The Electron zip does not have the expected layout (electron.exe, resources/). Staging left at $staging"
}
foreach ($notice in @('LICENSE', 'LICENSES.chromium.html')) {
    if (!(Test-Path -LiteralPath (Join-Path $staging $notice) -PathType Leaf)) { throw "Electron distribution notice missing: $notice. Staging left at $staging" }
}
Rename-Item -LiteralPath $electronExe -NewName 'Solforge AI.exe'
$defaultApp = Join-Path $staging 'resources/default_app.asar'
if (Test-Path -LiteralPath $defaultApp -PathType Leaf) { Remove-Item -LiteralPath $defaultApp }

# 1b. Executable icon and version resources (Sol icon, "Solforge AI" strings). Offline only: rcedit is not part of the
# workspace, so this uses the pure-JS resedit library already installed in the owned workspace's pnpm store (an
# electron-builder dependency). Nothing is downloaded. Without it the executable keeps Electron's icon and version
# resources and the gap is reported. Runs before the app is sealed, so solforge-ai-manifest.json hashes the branded exe.
$exeBranding = 'not applied'
$reseditDir = Get-ChildItem -LiteralPath (Join-Path $ownedRoot 'node_modules/.pnpm') -Directory -Filter 'resedit@*' -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'node_modules/resedit' } |
    Where-Object { Test-Path -LiteralPath (Join-Path $_ 'package.json') -PathType Leaf } | Select-Object -First 1
if ($reseditDir) {
    $brandFile = Join-Path $work 'solforge-exe-branding.cjs'
    $brandScript = @'
'use strict';
// argv: resedit directory, executable, .ico, version. Rewrites only the given staging executable.
const fs = require('fs');
const [reseditDir, exe, ico, version] = process.argv.slice(2);
const ResEdit = require(reseditDir);
const executable = ResEdit.NtExecutable.from(fs.readFileSync(exe), { ignoreCert: true });
const resources = ResEdit.NtExecutableResource.from(executable);
const icon = ResEdit.Data.IconFile.from(fs.readFileSync(ico));
const groups = ResEdit.Resource.IconGroupEntry.fromEntries(resources.entries);
const group = groups[0];
ResEdit.Resource.IconGroupEntry.replaceIconsForResource(resources.entries, group ? group.id : 1, group ? group.lang : 1033,
  icon.icons.map(item => item.data));
const infos = ResEdit.Resource.VersionInfo.fromEntries(resources.entries);
const info = infos[0] || ResEdit.Resource.VersionInfo.createEmpty();
const parts = (String(version).match(/^(\d+)\.(\d+)\.(\d+)/) || [null, '0', '0', '0']).slice(1).map(Number);
const languages = info.getAllLanguagesForStringValues();
if (languages.length === 0) languages.push({ lang: 1033, codepage: 1200 });
for (const language of languages) {
  for (const name of ['CompanyName', 'LegalCopyright']) info.removeStringValue(language, name);
  info.setStringValues(language, {
    FileDescription: 'Solforge AI', ProductName: 'Solforge AI', InternalName: 'Solforge AI',
    OriginalFilename: 'Solforge AI.exe', FileVersion: String(version), ProductVersion: String(version),
  });
  info.setFileVersion(parts[0], parts[1], parts[2], 0, language.lang);
  info.setProductVersion(parts[0], parts[1], parts[2], 0, language.lang);
}
info.outputToResourceEntries(resources.entries);
resources.outputResource(executable);
fs.writeFileSync(exe, Buffer.from(executable.generate()));
'@
    [IO.File]::WriteAllText($brandFile, $brandScript, [Text.UTF8Encoding]::new($false))
    try {
        Invoke-Tool $NodePath @($brandFile, $reseditDir, (Join-Path $staging 'Solforge AI.exe'),
            (Join-Path $desktopApp 'resources/tray-windows.ico'), $desktopVersion) $work
        $exeBranding = "applied with $(Split-Path (Split-Path (Split-Path $reseditDir -Parent) -Parent) -Leaf) (Electron's Authenticode signature is dropped)"
    } catch {
        $exeBranding = "failed ($($_.Exception.Message)); the executable keeps Electron's icon and version resources"
        Write-Warning "Solforge AI exe branding: $exeBranding"
    }
} else {
    $exeBranding = "skipped: no rcedit or resedit in $ownedRoot/node_modules/.pnpm, and none is downloaded"
}
Write-Host "Executable icon and version resources: $exeBranding"

# 1c. Electron fuses: RunAsNode, NODE_OPTIONS and --inspect off. Solforge AI runs its Host on the ai-runtime node.exe
# (SOLFORGE_AI_NODE), never Electron-as-Node, so a "Solforge AI.exe" process is always the Electron browser process. With the
# fuse on, any agent shell could run `ELECTRON_RUN_AS_NODE=1 "Solforge AI.exe" -e ...` and pass the editor's pairing image
# check. Offline only: @electron/fuses comes from the owned workspace's pnpm store (an electron-builder dependency). A missing
# library or a failed flip stops packaging, because the editor's pairing check relies on it.
$fusesDir = Get-ChildItem -LiteralPath (Join-Path $ownedRoot 'node_modules/.pnpm') -Directory -Filter '@electron+fuses@*' -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'node_modules/@electron/fuses' } |
    Where-Object { Test-Path -LiteralPath (Join-Path $_ 'package.json') -PathType Leaf } | Select-Object -First 1
if (!$fusesDir) { throw "Electron fuses: @electron/fuses is not in $ownedRoot/node_modules/.pnpm (nothing is downloaded). Staging left at $staging" }
$fuseFile = Join-Path $work 'solforge-exe-fuses.cjs'
$fuseScript = @'
'use strict';
// argv: @electron/fuses directory, executable. Flips the fuses of the given staging executable and reads them back.
const [fusesDir, exe] = process.argv.slice(2);
const { flipFuses, getCurrentFuseWire, FuseVersion, FuseV1Options } = require(fusesDir);
const { FuseState } = require(require('path').join(fusesDir, 'dist', 'constants.js'));
(async () => {
  await flipFuses(exe, {
    version: FuseVersion.V1,
    [FuseV1Options.RunAsNode]: false,
    [FuseV1Options.EnableNodeOptionsEnvironmentVariable]: false,
    [FuseV1Options.EnableNodeCliInspectArguments]: false,
  });
  const wire = await getCurrentFuseWire(exe);
  for (const option of [FuseV1Options.RunAsNode, FuseV1Options.EnableNodeOptionsEnvironmentVariable, FuseV1Options.EnableNodeCliInspectArguments]) {
    if (wire[option] !== FuseState.DISABLE) throw new Error('fuse ' + FuseV1Options[option] + ' is not disabled after the flip');
  }
})().catch((error) => { console.error(error && error.message ? error.message : String(error)); process.exit(1); });
'@
[IO.File]::WriteAllText($fuseFile, $fuseScript, [Text.UTF8Encoding]::new($false))
Invoke-Tool $NodePath @($fuseFile, $fusesDir, (Join-Path $staging 'Solforge AI.exe')) $work
Write-Host "Electron fuses: RunAsNode, NODE_OPTIONS and inspect arguments disabled"

function Get-PathVariants([string]$Path) {
    $trimmed = $Path.TrimEnd('\','/')
    $back = $trimmed.Replace('/', '\')
    @($back, $back.Replace('\', '/'), $back.Replace('\', '\\'))
}
$failPatterns = [Collections.Generic.List[string]]::new()
$reportPatterns = [Collections.Generic.List[string]]::new()
$failSources = @($work, $ownedRoot, $staging, 'C:\dawn', (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'deepseek-harness'))
if ($storePath) { $failSources += $storePath }
foreach ($leakSource in $failSources) { foreach ($variant in (Get-PathVariants $leakSource)) { if ($variant -and !$failPatterns.Contains($variant)) { $failPatterns.Add($variant) } } }
foreach ($leakSource in @([IO.Path]::GetTempPath(), 'C:\Users')) { foreach ($variant in (Get-PathVariants $leakSource)) { if ($variant -and !$reportPatterns.Contains($variant)) { $reportPatterns.Add($variant) } } }
$scanFile = Join-Path $work 'leak-patterns.json'
[IO.File]::WriteAllText($scanFile, (ConvertTo-Json -InputObject ([ordered]@{ fail = @($failPatterns); report = @($reportPatterns) }) -Compress), [Text.UTF8Encoding]::new($false))

$packerFile = Join-Path $work 'desktop-packer.mjs'
$packer = @'
import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { pathToFileURL } from 'node:url';
const [mode, source, deployed, staging, scanFile, electronVersion, zipName, zipHash] = process.argv.slice(2);
const inside = (root, file) => { const rel = path.relative(root,file); return rel === '' || (!path.isAbsolute(rel) && rel !== '..' && !rel.startsWith('..'+path.sep)); };
const hash = file => createHash('sha256').update(fs.readFileSync(file)).digest('hex');
const json = file => JSON.parse(fs.readFileSync(file,'utf8'));
// Same source filter and link handling as package-runtime.ps1.
const forbiddenSource = name => ['.git','.cache','.dsh-build','.dsh','.pnpm-store','coverage','logs','.agents','.claude','tests','stress-tests','__tests__'].includes(name)
  || /\.(?:test|spec|e2e)\.[cm]?[jt]sx?$/i.test(name)
  || /\.(?:pem|key|p12|pfx|sqlite3?)$/i.test(name) || /^id_(?:rsa|dsa|ecdsa|ed25519)/i.test(name)
  || /^\.env(?:\.|$)/i.test(name) || ['.npmrc','.credentials.yaml','auth.json','credentials.json'].includes(name);
const nativeSkip = rel => /^native\/(?:.*\/)?(?:target|build|\.cache|test|docs)$/.test(rel);
// The Solforge AI app packages ship only in ai-runtime (see package-runtime.ps1), so they stay out of this deploy
// snapshot. Exception: solforge-contract (types only, no dependencies) is a workspace devDependency of apps/desktop
// and must be present, or pnpm deploy fails with ERR_PNPM_WORKSPACE_PKG_NOT_FOUND.
const solforgeAppPackage = rel => /^packages\/experimental\/(?:client-ui-)?solforge-[^/]+$/.test(rel) && rel !== 'packages/experimental/solforge-contract';
function copyTree(from,to,{links=false,modules=true,root=from,mapped=to,base=root,startInModules=false,skip=()=>false}={}) {
  const pending=[];
  function walk(src,dst,inModules) {
    const stat=fs.lstatSync(src);
    if(stat.isSymbolicLink()) {
      if(!links) throw new Error('Unmaterialized link in portable input: '+src);
      const target=fs.realpathSync(src);
      if(!inside(root,target)) throw new Error('Workspace link escapes owned source: '+src);
      pending.push([dst,path.join(mapped,path.relative(root,target)),target,fs.statSync(target).isDirectory()]);
    } else if(stat.isDirectory()) {
      fs.mkdirSync(dst,{recursive:true});
      for(const name of fs.readdirSync(src).sort()) {
        const child=path.join(src,name);
        let childModules=inModules;
        if(name==='node_modules') { if(!modules) continue; childModules=true; }
        else if(!inModules) {
          const rel=path.relative(base,child).split(path.sep).join('/');
          if(forbiddenSource(name)||nativeSkip(rel)||skip(rel)) continue;
        }
        walk(child,path.join(dst,name),childModules);
      }
    } else if(stat.isFile()) {
      fs.mkdirSync(path.dirname(dst),{recursive:true}); fs.copyFileSync(src,dst);
    } else throw new Error('Unsupported filesystem entry: '+src);
  }
  walk(from,to,startInModules);
  for(const [dst,mappedTarget,realTarget,directory]of pending) {
    fs.mkdirSync(path.dirname(dst),{recursive:true});
    if(directory) fs.symlinkSync(process.platform==='win32'?mappedTarget:path.relative(path.dirname(dst),mappedTarget),dst,'junction');
    else fs.copyFileSync(realTarget,dst);
  }
}
if(mode==='clone') {
  if(process.platform!=='win32') throw new Error('This packager currently targets Windows only.');
  const real=fs.realpathSync(source);
  copyTree(real,deployed,{links:true,root:real,mapped:deployed,base:real,skip:solforgeAppPackage});
} else if(mode==='finish') {
  const {restoreLegacyHoists}=await import(pathToFileURL(path.join(source,'scripts/executable-packaging.ts')).href);
  const {materializeStagedLinks,deduplicateStagedWorkspacePackages}=await import(pathToFileURL(path.join(source,'scripts/build-exe-for-python-sdk-staging.ts')).href);
  await restoreLegacyHoists(deployed,path.join(source,'node_modules'));
  await materializeStagedLinks(deployed);
  await deduplicateStagedWorkspacePackages(deployed,source);
  // Restore `link:` override packages and workspace packages reached only as non-optional peers,
  // exactly as package-runtime.ps1 does for the CLI closure.
  const workspaceYaml=fs.readFileSync(path.join(source,'pnpm-workspace.yaml'),'utf8');
  const hoisted=name=>path.join(deployed,'node_modules',...name.split('/'));
  const locateDeployed=(base,name)=>{
    for(let dir=base;inside(deployed,dir);dir=path.dirname(dir)) {
      if(fs.existsSync(path.join(dir,'node_modules',name,'package.json'))) return true;
      if(dir===deployed) break;
    }
    return false;
  };
  const workspacePackages=new Map();
  for(const group of ['apps','vendor']) for(const name of fs.readdirSync(path.join(source,group))) {
    const manifestPath=path.join(source,group,name,'package.json');
    if(fs.existsSync(manifestPath)) workspacePackages.set(json(manifestPath).name,path.join(source,group,name));
  }
  for(const group of fs.readdirSync(path.join(source,'packages'))) {
    const groupDir=path.join(source,'packages',group);
    if(!fs.statSync(groupDir).isDirectory()) continue;
    for(const name of fs.readdirSync(groupDir)) {
      const manifestPath=path.join(groupDir,name,'package.json');
      if(fs.existsSync(manifestPath)) workspacePackages.set(json(manifestPath).name,path.join(groupDir,name));
    }
  }
  const queue=[], seen=new Set();
  const enqueueModules=modulesDir=>{
    if(!fs.existsSync(modulesDir)) return;
    for(const entry of fs.readdirSync(modulesDir)) {
      if(entry.startsWith('.')) continue;
      const full=path.join(modulesDir,entry);
      if(entry.startsWith('@')) { for(const scoped of fs.readdirSync(full)) queue.push(path.join(full,scoped)); }
      else queue.push(full);
    }
  };
  for(const match of workspaceYaml.matchAll(/^\s*'?([^'\s:]+(?:\/[^'\s:]+)?)'?\s*:\s*'?link:([^'\s]+)'?\s*$/gm)) {
    const [,name,target]=match;
    // Only overrides the desktop closure actually reaches are restored (checked after the fill-in pass).
    const destination=hoisted(name);
    if(fs.existsSync(destination)) continue;
    const input=path.resolve(source,target);
    if(!inside(fs.realpathSync(source),fs.realpathSync(input))) throw new Error('Override link escapes owned source: '+name);
    copyTree(input,destination,{modules:false});
  }
  enqueueModules(path.join(deployed,'node_modules'));
  queue.push(deployed);
  while(queue.length) {
    const directory=queue.pop();
    const manifestPath=path.join(directory,'package.json');
    if(!fs.existsSync(manifestPath)) continue;
    const real=fs.realpathSync(directory);
    if(seen.has(real)) continue; seen.add(real);
    const manifest=json(manifestPath);
    const wanted={...manifest.dependencies};
    for(const name of Object.keys(manifest.peerDependencies??{})) if(!manifest.peerDependenciesMeta?.[name]?.optional) wanted[name]=true;
    for(const name of Object.keys(wanted)) {
      if(!workspacePackages.has(name)||locateDeployed(directory,name)) continue;
      const destination=hoisted(name);
      copyTree(workspacePackages.get(name),destination,{modules:false});
      console.log('Added workspace package omitted by deploy: '+name);
      queue.push(destination);
    }
    enqueueModules(path.join(directory,'node_modules'));
  }
  // Application directory: exactly the shell files electron-builder would ship (scripts/electron-builder-config.mjs).
  const resources=path.join(staging,'resources');
  const app=path.join(resources,'app');
  if(fs.existsSync(app)) throw new Error('Electron distribution already contains resources/app.');
  const desktop=path.join(source,'apps','desktop');
  const libFiles=['lib/main.js','lib/command-manager-entry.js',...fs.readdirSync(path.join(desktop,'lib')).filter(n=>/^preload-.*\.cjs$/.test(n)).sort().map(n=>'lib/'+n)];
  for(const relative of libFiles){ fs.mkdirSync(path.dirname(path.join(app,relative)),{recursive:true}); fs.copyFileSync(path.join(desktop,relative),path.join(app,relative)); }
  copyTree(path.join(desktop,'lib','welcome'),path.join(app,'lib','welcome'),{modules:false});
  copyTree(path.join(desktop,'renderer'),path.join(app,'renderer'),{modules:false});
  copyTree(path.join(deployed,'node_modules'),path.join(app,'node_modules'),{startInModules:true});
  const desktopManifest=json(path.join(desktop,'package.json'));
  // productName drives app.name; version must equal the dsh release in ai-runtime/desktop-runtime.json.
  // No update channel, no mandatory-update policy, no app-update.yml: updates and policy stay off.
  // solforgeAi marks this packaged shell as Solforge AI, so a direct (standalone) launch without the editor's
  // SOLFORGE_AI=1 still enters Solforge mode (apps/desktop/src/solforge-mode.ts reads it).
  const appManifest={name:'solforge-ai',productName:'Solforge AI',version:desktopManifest.version,type:'module',main:'lib/main.js',private:true,
    solforgeAi:true,license:desktopManifest.license,dependencies:desktopManifest.dependencies};
  fs.writeFileSync(path.join(app,'package.json'),JSON.stringify(appManifest,null,2)+'\n');
  fs.copyFileSync(path.join(desktop,'resources','icon-windows.png'),path.join(resources,'icon.png'));
  fs.copyFileSync(path.join(desktop,'resources','tray-windows.ico'),path.join(resources,'tray.ico'));
  if(fs.existsSync(path.join(resources,'app-update.yml'))) throw new Error('app-update.yml must not be shipped.');
  (function strip(directory){
    for(const name of fs.readdirSync(directory)) {
      const file=path.join(directory,name);
      if(!inside(app,file)) throw new Error('Refusing to strip outside the app directory: '+file);
      const stat=fs.lstatSync(file);
      if(stat.isDirectory()) { if(name==='.bin') fs.rmSync(file,{recursive:true,force:true}); else strip(file); }
      else if(name==='.modules.yaml'||/^\.pnpm-workspace-state.*\.json$/.test(name)) fs.rmSync(file,{force:true});
    }
  })(app);
  (function prunePrebuilds(directory){for(const name of fs.readdirSync(directory)){
    const file=path.join(directory,name),stat=fs.lstatSync(file);
    if(!stat.isDirectory())continue;
    if(name==='prebuilds'&&directory.split(path.sep).includes('node_modules')){
      for(const target of fs.readdirSync(file))
        if(target!=='win32-x64'&&fs.lstatSync(path.join(file,target)).isDirectory()){ fs.rmSync(path.join(file,target),{recursive:true}); console.log('Pruned foreign prebuild: '+path.relative(staging,path.join(file,target))); }
    } else prunePrebuilds(file);
  }})(app);
  // Closure check from the app manifest: every external import of lib/main.js must resolve inside app/node_modules.
  const locate=(base,name)=>{
    for(let dir=base;inside(app,dir);dir=path.dirname(dir)) {
      const candidate=path.join(dir,'node_modules',name,'package.json');
      if(fs.existsSync(candidate)) return path.dirname(candidate);
      if(dir===app)break;
    }
    return null;
  };
  const applicable=m=>['os','cpu'].every(key=>{
    const values=m[key];if(!Array.isArray(values))return true;
    const value=key==='os'?process.platform:process.arch;
    return !values.includes('!'+value)&&(!values.some(v=>!v.startsWith('!'))||values.includes(value));
  });
  const visited=new Set(), packages=[];
  function checkPackage(directory,root=false) {
    if(visited.has(directory))return; visited.add(directory);
    if(!inside(app,fs.realpathSync(directory)))throw new Error('Dependency escapes app directory.');
    const manifest=json(path.join(directory,'package.json'));
    if(!root)packages.push({name:manifest.name,version:manifest.version,path:path.relative(staging,directory).split(path.sep).join('/')});
    const targets=new Set();
    function exportsOf(v){
      if(typeof v==='string'){if(v.startsWith('./')&&!v.includes('*')&&!/\.d\.[cm]?ts$/.test(v))targets.add(v);}
      else if(Array.isArray(v))v.forEach(exportsOf);
      else if(v&&typeof v==='object')for(const[k,x]of Object.entries(v))if(k.startsWith('.')||['node','import','require','default'].includes(k))exportsOf(x);
    }
    exportsOf(manifest.exports);
    if(manifest.main)targets.add(manifest.main);
    const resolvable=target=>{const t=path.resolve(directory,target);if(target.endsWith('/'))return fs.existsSync(t)&&fs.statSync(t).isDirectory();return ['', '.js','.cjs','.mjs','.json','/index.js','/index.cjs','/index.json'].some(s=>fs.existsSync(t+s)&&fs.statSync(t+s).isFile());};
    for(const target of targets)if(!resolvable(target))throw new Error('Built package entry missing: '+manifest.name+' '+target);
    const required={...manifest.dependencies};
    for(const[name,range]of Object.entries(manifest.peerDependencies??{}))if(!manifest.peerDependenciesMeta?.[name]?.optional)required[name]=range;
    for(const name of Object.keys(required).sort()) {
      if(name in (manifest.optionalDependencies??{}))continue;
      if(name==='electron')continue; // Provided by the Electron executable itself.
      const dependency=locate(directory,name);
      if(!dependency)throw new Error('Production dependency missing: '+manifest.name+' -> '+name);
      if(!applicable(json(path.join(dependency,'package.json'))))throw new Error('Required dependency has incompatible platform: '+name);
      checkPackage(dependency);
    }
    for(const name of Object.keys(manifest.optionalDependencies??{}).sort()) {
      const dependency=locate(directory,name);
      if(!dependency&&/win32[-_.]x64/i.test(name))throw new Error('Required win32-x64 optional platform package is absent: '+manifest.name+' -> '+name);
      if(dependency&&applicable(json(path.join(dependency,'package.json'))))checkPackage(dependency);
    }
  }
  checkPackage(app,true);
  const mainText=fs.readFileSync(path.join(app,'lib/main.js'),'utf8');
  const bare=new Set([...mainText.matchAll(/(?:^|[;\n])\s*import\s[^'"]*?from\s*['"]([^'"./][^'"]*)['"]/g),...mainText.matchAll(/import\(\s*['"]([^'"./][^'"]*)['"]\s*\)/g)].map(m=>m[1]).filter(s=>!s.startsWith('node:')&&s!=='electron'));
  for(const specifier of bare){
    const parts=specifier.split('/');const name=specifier.startsWith('@')?parts.slice(0,2).join('/'):parts[0];
    if(!locate(path.join(app,'lib'),name))throw new Error('lib/main.js imports '+specifier+' but '+name+' is not in the app closure.');
  }
  // Leak and secret scans over every text file of the staged app (same rules as package-runtime.ps1).
  const scanConfig=json(scanFile);
  const patterns=scanConfig.fail.map(p=>p.toLowerCase()), reportPatterns=scanConfig.report.map(p=>p.toLowerCase());
  const escapeRe=s=>s.replace(/[.*+?^${}()|[\]\\]/g,'\\$&');
  const regionPrefixes=patterns.map(p=>new RegExp(escapeRe(p),'gi'));
  (function sanitize(directory){for(const name of fs.readdirSync(directory)){
    const file=path.join(directory,name),stat=fs.lstatSync(file);
    if(stat.isDirectory()){sanitize(file);continue;}
    if(!stat.isFile()||!/\.(?:m|c)?js$/i.test(name)||stat.size>16*1024*1024)continue;
    const raw=fs.readFileSync(file,'utf8');
    if(!raw.includes('//#region')&&!raw.includes('//#endregion'))continue;
    const next=raw.replace(/^([ \t]*\/\/#(?:end)?region [^\r\n]*)$/gm,line=>regionPrefixes.reduce((l,re)=>l.replace(re,'<solforge-ai-desktop>'),line));
    if(next!==raw)fs.writeFileSync(file,next);
  }})(app);
  const driveE=/(?<![a-z0-9_$.])e:(?:\\\\|\\|\/)(?=[a-z0-9 _.-])/;
  const secretPatterns=[/\bsk-[A-Za-z0-9_-]{32,}/, /-----BEGIN (?:RSA |EC |OPENSSH |DSA |ENCRYPTED )?PRIVATE KEY-----/, /\bBearer\s+[A-Za-z0-9._~+\/-]{40,}/];
  const textExt=new Set(['.js','.cjs','.mjs','.ts','.cts','.mts','.json','.yaml','.yml','.md','.txt','.cmd','.ps1','.sh','.bat','.html','.css','.toml','.xml','.map']);
  const leaks=[], reported=[], secretHits=[];
  (function scan(directory){for(const name of fs.readdirSync(directory)){
    const file=path.join(directory,name),stat=fs.lstatSync(file);
    if(stat.isDirectory())scan(file);
    else if(stat.isFile()&&textExt.has(path.extname(name).toLowerCase())&&stat.size<=16*1024*1024){
      const raw=fs.readFileSync(file,'utf8'), text=raw.toLowerCase();
      const rel=path.relative(staging,file).split(path.sep).join('/'), vendored=rel.split('/').includes('node_modules')||!rel.startsWith('resources/app/');
      const hit=patterns.find(p=>text.includes(p))??(text.includes('c:/dawn')||text.includes('c:\\dawn')?'c:/dawn':undefined);
      if(hit)leaks.push(rel+'  ['+hit+']');
      else if(driveE.test(text)){ (vendored?reported:leaks).push(rel+'  [drive E:]'); }
      const generic=reportPatterns.find(p=>text.includes(p));
      if(generic)reported.push(rel+'  ['+generic+']');
      if(!vendored&&secretPatterns.some(re=>re.test(raw)))secretHits.push(rel);
    }
  }})(staging);
  if(leaks.length)throw new Error('Absolute path leak in Solforge AI desktop app ('+leaks.length+' files):\n'+leaks.slice(0,25).join('\n'));
  if(secretHits.length)throw new Error('Possible embedded secret in the desktop shell ('+secretHits.length+' files):\n'+secretHits.slice(0,25).join('\n'));
  if(reported.length)console.log('Report-only generic path mentions in '+reported.length+' files (not failures); binaries are not scanned.');
  const files=[];
  (function inventory(directory){for(const name of fs.readdirSync(directory).sort()){
    const file=path.join(directory,name),stat=fs.lstatSync(file);
    const rel=path.relative(staging,file).split(path.sep).join('/');
    if(stat.isFile()&&(/^\.(?:npmrc|env)/i.test(name)))throw new Error('Credential-style file in desktop app: '+rel);
    if(stat.isSymbolicLink())throw new Error('Desktop app still contains a link: '+rel);
    if(stat.isDirectory())inventory(file);
    else if(stat.isFile())files.push({path:rel,bytes:stat.size,sha256:hash(file)});
    else throw new Error('Unsupported desktop app entry: '+rel);
  }})(staging);
  files.sort((a,b)=>a.path<b.path?-1:a.path>b.path?1:0);
  packages.sort((a,b)=>a.path<b.path?-1:a.path>b.path?1:0);
  const manifest={schemaVersion:1,application:'Solforge AI',component:'desktop shell',upstream:'DeepSeek Harness apps/desktop',
    version:desktopManifest.version,platform:process.platform,arch:process.arch,
    electron:{version:electronVersion,zip:zipName,zipSha256:zipHash},executable:'Solforge AI.exe',appDirectory:'resources/app',
    runtime:'../ai-runtime',lockfileSha256:hash(path.join(source,'pnpm-lock.yaml')),
    validation:'structural closure, file integrity and absolute-path scan only; Electron was not launched',packages,files};
  fs.writeFileSync(path.join(staging,'solforge-ai-manifest.json'),JSON.stringify(manifest,null,2)+'\n');
} else throw new Error('Unknown packaging phase.');
'@
[IO.File]::WriteAllText($packerFile, $packer, [Text.UTF8Encoding]::new($false))

# 2. Private clone (pnpm legacy deploy can rewrite its source's install metadata), then the desktop deploy.
Invoke-Tool $NodePath @($packerFile, 'clone', $ownedRoot, $clone) $PSScriptRoot
$pnpmArguments = @('--dir', $clone, '--filter', '@deepseek-ai/dsh-desktop', 'deploy', '--legacy', '--prod', '--offline',
    '--config.ignore-scripts=true', '--config.package-import-method=copy', '--config.allow-unused-patches=true',
    '--config.node-linker=hoisted', '--config.auto-install-peers=false', '--config.link-workspace-packages=true',
    '--config.hoist-workspace-packages=false', $deploy)
Invoke-Pnpm $pnpmArguments $clone
Invoke-Tool $NodePath @('--import', 'tsx', $packerFile, 'finish', $clone, $deploy, $staging, $scanFile, $electronVersion, $zipName, $zipHash) $clone
if (!(Test-Path -LiteralPath (Join-Path $staging 'solforge-ai-manifest.json'))) { throw "Desktop app was not sealed. Nothing was published. Staging left at $staging" }
if (Test-Path -LiteralPath $destination) { throw 'Another packager already published solforge-ai. Existing output was preserved.' }
Move-Item -LiteralPath $staging -Destination $destination -ErrorAction Stop
Write-Host "Packaged: $destination"
Write-Host "Executable: $(Join-Path $destination 'Solforge AI.exe')"
Write-Host "Branding note: executable icon and version resources $exeBranding; the window and tray icons, taskbar ID, About text and product name are Solforge AI."
try {
    $scratchBytes = (Get-ChildItem -LiteralPath $work -Recurse -Force -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
    Write-Host ("Scratch retained (no automatic deletion): $work  (~{0:N1} GB). Delete it manually when no longer needed." -f ($scratchBytes / 1GB))
} catch { Write-Host "Scratch retained (no automatic deletion): $work. Delete it manually." }
