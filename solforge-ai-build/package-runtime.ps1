#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Output,
    [string]$NodePath,
    [string]$NodeLicensePath,
    [string]$PnpmPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ownedRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\solforge-ai'))
$outputRoot = [IO.Path]::GetFullPath($Output)
$destination = Join-Path $outputRoot 'ai-runtime'
if (Test-Path -LiteralPath $destination) { throw "Refusing to overwrite $destination. Choose a fresh build output." }
if ($outputRoot.StartsWith($ownedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $outputRoot -eq $ownedRoot) {
    throw 'Output must be outside the owned runtime workspace.'
}
foreach ($required in @('package.json','pnpm-lock.yaml','pnpm-workspace.yaml','LICENSE','THIRD_PARTY_NOTICES.md',
    'apps/cli/lib/bin.js','apps/web/dist/index.html','scripts/build-exe-for-python-sdk-staging.ts',
    'scripts/executable-packaging.ts','node_modules/tsx/package.json',
    'apps/desktop-host/lib/index.js','apps/desktop-host/lib/cli.js','apps/desktop/package.json','apps/desktop/src/host-protocol.ts')) {
    if (!(Test-Path -LiteralPath (Join-Path $ownedRoot $required) -PathType Leaf)) { throw "Owned runtime prerequisite missing: $required. Complete dependency setup and the owned artifact build first." }
}
if (!$NodePath) { $NodePath = (Get-Command node.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source }
$NodePath = (Resolve-Path -LiteralPath $NodePath).Path
if (!$NodeLicensePath) {
    $nodeDirectory = Split-Path -Parent $NodePath
    foreach ($candidate in @((Join-Path $nodeDirectory 'LICENSE'), (Join-Path $nodeDirectory '../LICENSE'), (Join-Path $PSScriptRoot 'licenses/node/LICENSE'))) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $NodeLicensePath = (Resolve-Path -LiteralPath $candidate).Path; break }
    }
}
if (!$NodeLicensePath -or !(Test-Path -LiteralPath $NodeLicensePath -PathType Leaf)) {
    throw 'Node redistribution license missing. Supply -NodeLicensePath with LICENSE from the same Node distribution. No license is downloaded or fabricated.'
}
$NodeLicensePath = (Resolve-Path -LiteralPath $NodeLicensePath).Path

# Resolve a real pnpm JS entry or executable; never execute a shell wrapper whose argv can be reparsed.
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
if (!$PnpmPath) { throw 'Installed pnpm entrypoint missing. Supply -PnpmPath pointing to pnpm.exe, pnpm.cjs or pnpm.mjs; installation is not automatic.' }
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

function Invoke-RuntimeTool([string]$Executable, [string[]]$Arguments, [string]$Directory) {
    $start = New-ToolStart $Executable $Arguments $Directory
    $process = [Diagnostics.Process]::Start($start)
    try {
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "Runtime packaging command failed with exit code $($process.ExitCode). No portable completion manifest was published." }
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
    if ([IO.Path]::GetExtension($PnpmPath) -eq '.exe') { Invoke-RuntimeTool $PnpmPath $Arguments $Directory }
    else { Invoke-RuntimeTool $NodePath (@($PnpmPath) + $Arguments) $Directory }
}
function Get-PnpmOutput([string[]]$Arguments, [string]$Directory) {
    if ([IO.Path]::GetExtension($PnpmPath) -eq '.exe') { return Get-ToolOutput $PnpmPath $Arguments $Directory }
    return Get-ToolOutput $NodePath (@($PnpmPath) + $Arguments) $Directory
}

# The installed pnpm must match the workspace pin; deploy flags depend on the major version.
$pinned = (Get-Content -LiteralPath (Join-Path $ownedRoot 'package.json') -Raw | ConvertFrom-Json).packageManager
if ($pinned -notmatch '^pnpm@([0-9][^+\s]*)') { throw "Workspace package.json lacks a pnpm@<version> packageManager pin (found: $pinned)." }
$pinnedVersion = $Matches[1]
$actualVersion = Get-PnpmOutput @('--version') $ownedRoot
if ($actualVersion -ne $pinnedVersion) { throw "pnpm version mismatch: workspace pins $pinnedVersion but $PnpmPath reports $actualVersion." }
$storePath = $null
try { $storePath = Get-PnpmOutput @('store','path') $ownedRoot } catch { Write-Warning 'pnpm store path could not be queried; store path will not be part of the leak scan.' }

# Staging lives on the output volume so the final publish is a same-volume rename.
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$staging = Join-Path $outputRoot ('.ai-runtime-staging-' + [Guid]::NewGuid().ToString('N'))

# The complete private copy is intentional: pnpm legacy deploy can rewrite its source's
# installation metadata. It must never do that to the developer's owned workspace.
$work = Join-Path ([IO.Path]::GetTempPath()) ('solforge-ai-package-' + [Guid]::NewGuid().ToString('N'))
$clone = Join-Path $work 'workspace'
$deploy = Join-Path $work 'deployed'
New-Item -ItemType Directory -Path $work -ErrorAction Stop | Out-Null
Write-Host "Packaging scratch directory: $work"

function Get-PathVariants([string]$Path) {
    $trimmed = $Path.TrimEnd('\','/')
    $back = $trimmed.Replace('/', '\')
    @($back, $back.Replace('\', '/'), $back.Replace('\', '\\'))
}
# Specific patterns (this machine's own paths, forbidden legacy roots) fail packaging. Generic ones
# (C:\Users, the OS temp root) only report: dependency docs and fixtures may legitimately contain them.
$failPatterns = [Collections.Generic.List[string]]::new()
$reportPatterns = [Collections.Generic.List[string]]::new()
$failSources = @($work, $ownedRoot, $staging, 'C:\dawn',
    (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'deepseek-harness'))
if ($storePath) { $failSources += $storePath }
foreach ($leakSource in $failSources) { foreach ($variant in (Get-PathVariants $leakSource)) { if ($variant -and !$failPatterns.Contains($variant)) { $failPatterns.Add($variant) } } }
foreach ($leakSource in @([IO.Path]::GetTempPath(), 'C:\Users')) { foreach ($variant in (Get-PathVariants $leakSource)) { if ($variant -and !$reportPatterns.Contains($variant)) { $reportPatterns.Add($variant) } } }
$scanFile = Join-Path $work 'leak-patterns.json'
[IO.File]::WriteAllText($scanFile, (ConvertTo-Json -InputObject ([ordered]@{ fail = @($failPatterns); report = @($reportPatterns) }) -Compress), [Text.UTF8Encoding]::new($false))

$packerFile = Join-Path $work 'packer.mjs'
$packer = @'
import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { pathToFileURL } from 'node:url';
const [mode, source, deployed, portable, node, license, scanFile, solforgeSide] = process.argv.slice(2);
// Solforge AI app packages (automatic mode, nudges, auto-allow; see ai/README-runtime.md). They are not linked in
// pnpm-lock.yaml yet, so the deploy snapshot must not contain them: clone moves them to a side directory and
// finish adds their built files to the closure with addBuiltPackage.
const solforgeAppPackage = rel => /^packages\/experimental\/(?:client-ui-)?solforge-[^/]+$/.test(rel);
const SOLFORGE_APP_PACKAGES = [
  ['solforge-automatic','@deepseek-ai/dsh-experimental-solforge-automatic',['lib/index.js','skills/solforge-automatic/SKILL.md']],
  ['client-ui-solforge-automatic','@deepseek-ai/dsh-experimental-client-ui-solforge-automatic',['lib/index.js','lib/client.js']],
  ['solforge-bundle','@deepseek-ai/dsh-experimental-solforge-bundle',['cordis.patch.yml','patches/brand.patch.yml','patches/models.patch.yml','patches/editor.patch.yml','lib/index.js']],
  // Solforge AI app v3 (ai/SOLFORGE-AI-APP-SPEC.md): plugin cards and project workspace (P2), branding (P1),
  // model tiers (P3), editor panel (P4). The contract is types only; it ships its built type modules.
  ['solforge-contract','@deepseek-ai/dsh-experimental-solforge-contract',['lib/types/index.js']],
  ['solforge-plugins','@deepseek-ai/dsh-experimental-solforge-plugins',['lib/index.js']],
  ['client-ui-solforge-plugins','@deepseek-ai/dsh-experimental-client-ui-solforge-plugins',['lib/index.js','lib/client.js']],
  ['client-ui-solforge-brand','@deepseek-ai/dsh-experimental-client-ui-solforge-brand',['lib/index.js','lib/client.js']],
  ['solforge-model-tiers','@deepseek-ai/dsh-experimental-solforge-model-tiers',['lib/index.js']],
  ['client-ui-solforge-model-tiers','@deepseek-ai/dsh-experimental-client-ui-solforge-model-tiers',['lib/index.js','lib/client.js']],
  ['client-ui-solforge-editor','@deepseek-ai/dsh-experimental-client-ui-solforge-editor',['lib/index.js','lib/client.js']],
];
const inside = (root, file) => { const rel = path.relative(root,file); return rel === '' || (!path.isAbsolute(rel) && rel !== '..' && !rel.startsWith('..'+path.sep)); };
const hash = file => createHash('sha256').update(fs.readFileSync(file)).digest('hex');
const json = file => JSON.parse(fs.readFileSync(file,'utf8'));
// Applies to workspace SOURCE trees only. Names inside node_modules are never filtered.
const forbiddenSource = name => ['.git','.cache','.dsh-build','.dsh','.pnpm-store','coverage','logs','.agents','.claude','tests','stress-tests','__tests__'].includes(name)
  || /\.(?:test|spec|e2e)\.[cm]?[jt]sx?$/i.test(name)
  || /\.(?:pem|key|p12|pfx|sqlite3?)$/i.test(name) || /^id_(?:rsa|dsa|ecdsa|ed25519)/i.test(name)
  || /^\.env(?:\.|$)/i.test(name) || ['.npmrc','.credentials.yaml','auth.json','credentials.json'].includes(name);
// Build output / caches inside the native workspace are not part of the product.
const nativeSkip = rel => /^native\/(?:.*\/)?(?:target|build|\.cache|test|docs)$/.test(rel);
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
    // Directory junctions need no privilege. File symlinks do, so files are copied instead.
    if(directory) fs.symlinkSync(process.platform==='win32'?mappedTarget:path.relative(path.dirname(dst),mappedTarget),dst,'junction');
    else fs.copyFileSync(realTarget,dst);
  }
}
if(mode==='clone') {
  if(process.platform!=='win32') throw new Error('This packager currently targets Windows only.');
  const [major,minor]=process.versions.node.split('.').map(Number);
  if(major<24||(major===24&&minor<9)) throw new Error('The owned runtime requires Node >=24.9.0.');
  const real=fs.realpathSync(source);
  copyTree(real,deployed,{links:true,root:real,mapped:deployed,base:real,skip:solforgeAppPackage});
  // In clone mode `portable` is the side directory that receives the Solforge app packages (sources and builds).
  fs.mkdirSync(portable,{recursive:false});
  const experimental=path.join(real,'packages','experimental');
  for(const name of fs.readdirSync(experimental).sort()) {
    if(!solforgeAppPackage('packages/experimental/'+name)||!fs.statSync(path.join(experimental,name)).isDirectory()) continue;
    copyTree(path.join(experimental,name),path.join(portable,name),{modules:false});
    console.log('Solforge app package kept out of the deploy snapshot: packages/experimental/'+name);
  }
  // apps/cli declares the Solforge bundle in source (workspace:*, so the installation scope holds it in development
  // too). Those packages are not in the snapshot, so the clone's apps/cli manifest and its lockfile importer drop that
  // edge for `pnpm deploy`; finish re-declares the bundle in the packaged dsh manifest (below). The owned source is
  // never changed.
  {
    const solforgeName=name=>/^@deepseek-ai\/dsh-experimental-(?:client-ui-)?solforge-/.test(name);
    const cliManifest=path.join(deployed,'apps','cli','package.json');
    const manifest=json(cliManifest);
    let removed=0;
    for(const section of ['dependencies','optionalDependencies','peerDependencies','devDependencies']){
      if(!manifest[section])continue;
      for(const name of Object.keys(manifest[section]))if(solforgeName(name)){delete manifest[section][name];removed++;}
    }
    if(removed>0){
      fs.writeFileSync(cliManifest,JSON.stringify(manifest,null,2)+'\n');
      const lockFile=path.join(deployed,'pnpm-lock.yaml');
      const lock=fs.readFileSync(lockFile,'utf8');
      const edge=/^      '@deepseek-ai\/dsh-experimental-(?:client-ui-)?solforge-[^']+':\r?\n        specifier: workspace:\*\r?\n        version: link:\.\.\/\.\.\/packages\/experimental\/[^\r\n]+\r?\n/gm;
      // Only inside the apps/cli importer: other importers (e.g. apps/desktop's contract devDependency) stay as they are.
      const start=lock.search(/^  apps\/cli:\r?$/m);
      if(start<0)throw new Error('pnpm-lock.yaml has no apps/cli importer.');
      const after=lock.slice(start+1).search(/^  \S/m);
      const end=after<0?lock.length:start+1+after;
      fs.writeFileSync(lockFile,lock.slice(0,start)+lock.slice(start,end).replace(edge,'')+lock.slice(end));
      console.log('Snapshot apps/cli: dropped '+removed+' Solforge workspace edge(s) for deploy (re-declared after deploy).');
    }
  }
} else if(mode==='finish') {
  // These are the owned fork's maintained deploy-materialization helpers, not upstream paths.
  const {restoreLegacyHoists}=await import(pathToFileURL(path.join(source,'scripts/executable-packaging.ts')).href);
  const {materializeStagedLinks,deduplicateStagedWorkspacePackages}=await import(pathToFileURL(path.join(source,'scripts/build-exe-for-python-sdk-staging.ts')).href);
  await restoreLegacyHoists(deployed,path.join(source,'node_modules'));
  await materializeStagedLinks(deployed);
  await deduplicateStagedWorkspacePackages(deployed,source);
  // Legacy deploy drops packages reachable only through `link:` workspace overrides
  // (e.g. @deepseek-ai/cosmokit, needed by vendored cordis). Restore them from the owned source.
  const workspaceYaml=fs.readFileSync(path.join(source,'pnpm-workspace.yaml'),'utf8');
  for(const match of workspaceYaml.matchAll(/^\s*'?([^'\s:]+(?:\/[^'\s:]+)?)'?\s*:\s*'?link:([^'\s]+)'?\s*$/gm)) {
    const [,name,target]=match;
    const destination=path.join(deployed,'node_modules',...name.split('/'));
    if(fs.existsSync(destination)) continue;
    const input=path.resolve(source,target);
    if(!inside(fs.realpathSync(source),fs.realpathSync(input))) throw new Error('Override link escapes owned source: '+name);
    copyTree(input,destination,{modules:false});
  }
  // Solforge AI desktop app: its Electron shell starts
  // <ai-runtime>/node_modules/@deepseek-ai/dsh-desktop-host/lib/index.js, and that Host imports
  // @deepseek-ai/dsh (installAnchor <ai-runtime>/node_modules/@deepseek-ai/dsh/package.json). The CLI
  // deploy places dsh at the deploy root only, so both are added here as hoisted packages from their
  // built owned sources. The fill-in loop below then completes their workspace closure.
  const addBuiltPackage=(from,name,files)=>{
    const manifest=json(path.join(from,'package.json'));
    if(manifest.name!==name)throw new Error('Unexpected package at '+from+': '+manifest.name);
    const destination=path.join(deployed,'node_modules',...name.split('/'));
    if(fs.existsSync(destination)){
      if(json(path.join(destination,'package.json')).version!==manifest.version)throw new Error('Deployed '+name+' version differs from the owned source.');
      return;
    }
    fs.mkdirSync(destination,{recursive:true});
    fs.copyFileSync(path.join(from,'package.json'),path.join(destination,'package.json'));
    for(const relative of files){
      const input=path.join(from,...relative.split('/'));
      if(!fs.existsSync(input)||!fs.statSync(input).isFile())throw new Error('Built file missing for '+name+': '+relative+'. Run ai/build-runtime.ps1.');
      fs.mkdirSync(path.dirname(path.join(destination,relative)),{recursive:true});
      fs.copyFileSync(input,path.join(destination,relative));
    }
    console.log('Added Solforge AI desktop package: '+name);
  };
  const cliSource=path.join(source,'apps','cli');
  addBuiltPackage(cliSource,'@deepseek-ai/dsh',fs.readdirSync(path.join(cliSource,'lib')).filter(name=>name.endsWith('.js')).sort().map(name=>'lib/'+name));
  addBuiltPackage(path.join(source,'apps','desktop-host'),'@deepseek-ai/dsh-desktop-host',['lib/index.js','lib/cli.js']);
  // Solforge AI app packages from the clone's side directory: package.json, every built lib/** file (types,
  // client chunks and assets included), locale/*.json and the app-owned skills/**/*.md. Their workspace closure is completed below.
  if(!solforgeSide||!fs.existsSync(solforgeSide))throw new Error('Solforge app package side directory missing.');
  const filesUnder=(root,sub)=>{
    const out=[];const start=path.join(root,sub);
    if(!fs.existsSync(start))return out;
    (function walk(dir){for(const name of fs.readdirSync(dir).sort()){
      const file=path.join(dir,name),stat=fs.lstatSync(file);
      if(stat.isDirectory())walk(file);
      else if(stat.isFile())out.push(path.relative(root,file).split(path.sep).join('/'));
    }})(start);
    return out;
  };
  for(const [directory,name,required] of SOLFORGE_APP_PACKAGES){
    const from=path.join(solforgeSide,directory);
    if(!fs.existsSync(path.join(from,'package.json')))throw new Error('Solforge app package missing: packages/experimental/'+directory);
    const files=[...new Set([...required,...filesUnder(from,'lib'),...filesUnder(from,'locale').filter(file=>file.endsWith('.json')),...filesUnder(from,'skills').filter(file=>file.endsWith('.md'))])].sort();
    addBuiltPackage(from,name,files);
  }
  // The Loader resolves plugin names only inside the installation scope: a breadth-first walk of the dependency graph
  // of the install anchor (node_modules/@deepseek-ai/dsh/package.json, dsh-app-boot collectInstallationScopePackages).
  // The Solforge packages are not dependencies of the CLI in the owned workspace, so without this every Solforge row
  // fails with ERR_MODULE_NOT_FOUND at boot (seen in the 2026-10-24 packaged smoke). Declaring the Solforge bundle,
  // whose dependencies are the other Solforge packages, puts all of them in scope. Rows stay gated on SOLFORGE_AI=1.
  {
    const bundleName='@deepseek-ai/dsh-experimental-solforge-bundle';
    const hoisted=name=>path.join(deployed,'node_modules',...name.split('/'));
    const dshManifestPath=path.join(hoisted('@deepseek-ai/dsh'),'package.json');
    const dshManifest=json(dshManifestPath);
    const bundleVersion=json(path.join(hoisted(bundleName),'package.json')).version;
    dshManifest.dependencies={...(dshManifest.dependencies??{}),[bundleName]:bundleVersion};
    fs.writeFileSync(dshManifestPath,JSON.stringify(dshManifest,null,2)+'\n');
    for(const dependency of Object.keys(json(path.join(hoisted(bundleName),'package.json')).dependencies??{})){
      if(!fs.existsSync(path.join(hoisted(dependency),'package.json')))throw new Error('Solforge bundle dependency not hoisted: '+dependency);
    }
    console.log('Declared '+bundleName+' in the packaged @deepseek-ai/dsh manifest (installation scope).');
  }
  // Legacy deploy also omits workspace packages reached only as non-optional peers (e.g.
  // @deepseek-ai/dsh-scope, imported at runtime by dsh-agent). Add them from the owned source
  // until the hoisted closure is complete; third-party gaps are left for the closure check to fail.
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
  const hoisted=name=>path.join(deployed,'node_modules',...name.split('/'));
  const locateDeployed=(base,name)=>{
    for(let dir=base;inside(deployed,dir);dir=path.dirname(dir)) {
      if(fs.existsSync(path.join(dir,'node_modules',name,'package.json'))) return true;
      if(dir===deployed) break;
    }
    return false;
  };
  const queue=[path.join(deployed,'node_modules')];
  const seen=new Set();
  const enqueueModules=modulesDir=>{
    if(!fs.existsSync(modulesDir)) return;
    for(const entry of fs.readdirSync(modulesDir)) {
      if(entry.startsWith('.')) continue;
      const full=path.join(modulesDir,entry);
      if(entry.startsWith('@')) { for(const scoped of fs.readdirSync(full)) queue.push(path.join(full,scoped)); }
      else queue.push(full);
    }
  };
  enqueueModules(queue.pop());
  queue.push(path.join(deployed));
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
  fs.mkdirSync(portable,{recursive:false});
  for(const directory of ['apps','packages','vendor','native','scripts']) {
    const input=path.join(source,directory);
    if(!fs.existsSync(input)) throw new Error('Owned workspace directory missing: '+directory);
    copyTree(input,path.join(portable,directory),{modules:false,base:source});
  }
  for(const name of fs.readdirSync(source).sort()) {
    const file=path.join(source,name);
    if(!forbiddenSource(name)&&fs.lstatSync(file).isFile()&&(/\.(?:json|ya?ml|[cm]?js|ts)$/.test(name)||/^(?:LICENSE|NOTICE|THIRD_PARTY_NOTICES)/.test(name))) {
      fs.copyFileSync(file,path.join(portable,name));
    }
  }
  copyTree(path.join(deployed,'node_modules'),path.join(portable,'node_modules'),{startInModules:true});
  fs.mkdirSync(path.join(portable,'node'));
  fs.copyFileSync(node,path.join(portable,'node/node.exe'));
  fs.copyFileSync(license,path.join(portable,'node/LICENSE'));
  // Drop shims and install metadata that embed absolute paths. Portable tree only, never the source.
  (function strip(directory){
    for(const name of fs.readdirSync(directory)) {
      const file=path.join(directory,name);
      if(!inside(portable,file)) throw new Error('Refusing to strip outside the portable tree: '+file);
      const stat=fs.lstatSync(file);
      if(stat.isDirectory()) {
        if(name==='.bin') fs.rmSync(file,{recursive:true,force:true}); else strip(file);
      } else if(name==='.modules.yaml'||/^\.pnpm-workspace-state.*\.json$/.test(name)) fs.rmSync(file,{force:true});
    }
  })(portable);
  const locate=(base,name)=>{
    for(let dir=base;inside(portable,dir);dir=path.dirname(dir)) {
      const candidate=path.join(dir,'node_modules',name,'package.json');
      if(fs.existsSync(candidate)) return path.dirname(candidate);
      if(dir===portable)break;
    }
    return null;
  };
  const visited=new Set(),packages=[];
  const applicable=m=>['os','cpu'].every(key=>{
    const values=m[key];if(!Array.isArray(values))return true;
    const value=key==='os'?process.platform:process.arch;
    return !values.includes('!'+value)&&(!values.some(v=>!v.startsWith('!'))||values.includes(value));
  });
  function checkPackage(directory) {
    if(visited.has(directory))return; visited.add(directory);
    if(!inside(portable,fs.realpathSync(directory)))throw new Error('Dependency escapes portable root.');
    const manifest=json(path.join(directory,'package.json'));
    packages.push({name:manifest.name,version:manifest.version,path:path.relative(portable,directory).split(path.sep).join('/')});
    const targets=new Set();
    function exportsOf(v){
      if(typeof v==='string'){if(v.startsWith('./')&&!v.includes('*')&&!/\.d\.[cm]?ts$/.test(v))targets.add(v);}
      else if(Array.isArray(v))v.forEach(exportsOf);
      else if(v&&typeof v==='object')for(const[k,x]of Object.entries(v))if(k.startsWith('.')||['node','import','require','default'].includes(k))exportsOf(x);
    }
    exportsOf(manifest.exports);
    if(manifest.main)targets.add(manifest.main);
    // `main` follows CommonJS resolution: extensionless names and directory indexes are valid.
    // Legacy folder exports such as tslib's "./": "./" name a directory, not a file.
    const resolvable=target=>{const t=path.resolve(directory,target);if(target.endsWith('/'))return fs.existsSync(t)&&fs.statSync(t).isDirectory();return ['', '.js','.cjs','.mjs','.json','/index.js','/index.cjs','/index.json'].some(s=>fs.existsSync(t+s)&&fs.statSync(t+s).isFile());};
    for(const target of targets)if(!resolvable(target))throw new Error('Built package entry missing: '+manifest.name+' '+target);
    const required={...manifest.dependencies};
    for(const[name,range]of Object.entries(manifest.peerDependencies??{}))if(!manifest.peerDependenciesMeta?.[name]?.optional)required[name]=range;
    for(const name of Object.keys(required).sort()) {
      if(name in (manifest.optionalDependencies??{}))continue;
      const dependency=locate(directory,name);
      if(!dependency)throw new Error('Production dependency missing: '+manifest.name+' -> '+name);
      if(!applicable(json(path.join(dependency,'package.json'))))throw new Error('Required dependency has incompatible platform: '+name);
      checkPackage(dependency);
    }
    for(const name of Object.keys(manifest.optionalDependencies??{}).sort()) {
      const dependency=locate(directory,name);
      // A win32-x64 platform package must never be silently skipped.
      if(!dependency&&/win32[-_.]x64/i.test(name))throw new Error('Required win32-x64 optional platform package is absent: '+manifest.name+' -> '+name);
      if(dependency&&applicable(json(path.join(dependency,'package.json'))))checkPackage(dependency);
    }
  }
  checkPackage(path.join(portable,'apps/cli'));
  const frontend=locate(path.join(portable,'apps/cli'),'@deepseek-ai/dsh-web-frontend');
  const mcp=locate(path.join(portable,'apps/cli'),'@deepseek-ai/dsh-mcp-client');
  if(!frontend||!mcp)throw new Error('Production closure lacks web frontend or MCP client.');
  checkPackage(frontend); checkPackage(mcp);
  const webApp=locate(path.join(portable,'apps/cli'),'@deepseek-ai/dsh-web-app');
  if(!webApp)throw new Error('Production closure lacks the web app bundle.');
  // The editor-embedded Solforge workspace panel is retired: Solforge AI is its own desktop window.
  if(fs.readFileSync(path.join(webApp,'cordis.patch.yml'),'utf8').includes('dsh-client-solforge-workspace'))throw new Error('web-app cordis.patch.yml still loads the retired Solforge workspace panel.');
  checkPackage(webApp);
  // Solforge AI desktop Host and the dsh package it boots, both resolved from the ai-runtime root.
  const desktopHost=path.join(portable,'node_modules','@deepseek-ai','dsh-desktop-host');
  const dshPackage=path.join(portable,'node_modules','@deepseek-ai','dsh');
  for(const directory of [desktopHost,dshPackage])if(!fs.existsSync(path.join(directory,'package.json')))throw new Error('Solforge AI desktop package missing: '+path.relative(portable,directory));
  checkPackage(dshPackage); checkPackage(desktopHost);
  const hostEntry=fs.readFileSync(path.join(desktopHost,'lib/index.js'),'utf8');
  if(!['SOLFORGE_AI_PATCH','SOLFORGE_AI_DISABLE_OFFICE','SOLFORGE_AI_APP_OVERLAY','dsh-experimental-solforge-bundle'].every(marker=>hostEntry.includes(marker)))throw new Error('Packaged desktop Host lacks the Solforge launch-mode code; rebuild with ai/build-runtime.ps1.');
  // The Host resolves the bundle patch from the install anchor (node_modules/@deepseek-ai/dsh) and fails without it.
  for(const [,name] of SOLFORGE_APP_PACKAGES){
    const directory=path.join(portable,'node_modules',...name.split('/'));
    if(!fs.existsSync(path.join(directory,'package.json')))throw new Error('Solforge AI app package missing from the closure: '+name);
    checkPackage(directory);
  }
  const bundlePatchFile=path.join(portable,'node_modules','@deepseek-ai','dsh-experimental-solforge-bundle','cordis.patch.yml');
  if(!fs.existsSync(bundlePatchFile)||!fs.statSync(bundlePatchFile).isFile())throw new Error('Solforge bundle cordis.patch.yml is missing from the closure.');
  const bundlePatch=fs.readFileSync(bundlePatchFile,'utf8');
  // Every runtime package is a bundle row; the contract (types only) and the bundle itself are not.
  for(const [,name] of SOLFORGE_APP_PACKAGES.filter(([directory])=>directory!=='solforge-bundle'&&directory!=='solforge-contract'))if(!bundlePatch.includes(name))throw new Error('Solforge bundle cordis.patch.yml does not register '+name);
  for(const split of ['brand','models','editor']){
    const file=path.join(path.dirname(bundlePatchFile),'patches',split+'.patch.yml');
    if(!fs.existsSync(file)||!fs.statSync(file).isFile())throw new Error('Solforge bundle patches/'+split+'.patch.yml is missing from the closure.');
  }
  // Descriptor the Electron shell validates before starting the Host (apps/desktop/src/runtime-tree.ts).
  // files[] is not verified at runtime; integrity is covered by ai-runtime-manifest.json instead.
  const desktopVersion=json(path.join(source,'apps/desktop/package.json')).version;
  for(const directory of [dshPackage,desktopHost])if(json(path.join(directory,'package.json')).version!==desktopVersion)throw new Error('Desktop release version mismatch: '+path.relative(portable,directory));
  const protocolMatch=/DESKTOP_HOST_PROTOCOL_VERSION\s*=\s*(\d+)/.exec(fs.readFileSync(path.join(source,'apps/desktop/src/host-protocol.ts'),'utf8'));
  if(!protocolMatch)throw new Error('DESKTOP_HOST_PROTOCOL_VERSION not found in apps/desktop/src/host-protocol.ts');
  const pnpmMatch=/^pnpm@([0-9][^+\s]*)/.exec(String(json(path.join(source,'package.json')).packageManager));
  if(!pnpmMatch)throw new Error('Workspace packageManager pin missing.');
  const sharedPackage=directory=>{const m=json(path.join(directory,'package.json'));return {name:m.name,version:m.version,path:'node_modules/'+m.name};};
  const desktopRuntime={schemaVersion:1,
    release:{schemaVersion:1,version:desktopVersion,hostProtocolVersion:Number(protocolMatch[1]),nodeVersion:process.versions.node,pnpmVersion:pnpmMatch[1]},
    platform:process.platform,arch:process.arch,
    sharedPackages:[sharedPackage(dshPackage),sharedPackage(desktopHost)].sort((a,b)=>a.name<b.name?-1:1),files:[]};
  fs.writeFileSync(path.join(portable,'desktop-runtime.json'),JSON.stringify(desktopRuntime,null,2)+'\n');
  const required=['apps/cli/lib/bin.js','apps/web/dist/index.html','node/node.exe','node/LICENSE','LICENSE','THIRD_PARTY_NOTICES.md',
    path.relative(portable,path.join(frontend,'dist/index.html')).split(path.sep).join('/')];
  for(const file of required)if(!fs.statSync(path.join(portable,file)).isFile())throw new Error('Required runtime file missing: '+file);
  if(hash(path.join(frontend,'dist/index.html'))!==hash(path.join(portable,'apps/web/dist/index.html')))throw new Error('Deployed frontend differs from the owned custom web build.');
  // Absolute-path leak scan over every text file in the portable tree.
  const scanConfig=json(scanFile);
  const patterns=scanConfig.fail.map(p=>p.toLowerCase()), reportPatterns=scanConfig.report.map(p=>p.toLowerCase());
  const reported=[], warnings=[];
  // A real E: path continues with a path character; minified ternaries like `e:"x"` or `e:\"x\"` do not.
const driveE=/(?<![a-z0-9_$.])e:(?:\\\\|\\|\/)(?=[a-z0-9 _.-])/;
  const secretPatterns=[/\bsk-[A-Za-z0-9_-]{32,}/, /-----BEGIN (?:RSA |EC |OPENSSH |DSA |ENCRYPTED )?PRIVATE KEY-----/, /\bBearer\s+[A-Za-z0-9._~+\/-]{40,}/];
  const sensitiveName=name=>/^\.npmrc$/i.test(name)||/^\.env(?:\.|$)/i.test(name)||/\.(?:pem|key|p12|pfx|sqlite3?)$/i.test(name)||/^id_(?:rsa|dsa|ecdsa|ed25519)/i.test(name);
  const secretHits=[];
  const textExt=new Set(['.js','.cjs','.mjs','.ts','.cts','.mts','.json','.yaml','.yml','.md','.txt','.cmd','.ps1','.sh','.bat','.html','.css','.toml','.xml','.map']);
  // The bundler names virtual CSS modules by absolute source path in `//#region` comments only.
  // Rewrite just those comment lines to a neutral prefix; any other occurrence still fails the scan.
  const escapeRe=s=>s.replace(/[.*+?^${}()|[\]\\]/g,'\\$&');
  const regionPrefixes=patterns.map(p=>new RegExp(escapeRe(p),'gi'));
  (function sanitize(directory){for(const name of fs.readdirSync(directory)){
    const file=path.join(directory,name),stat=fs.lstatSync(file);
    if(stat.isDirectory()){sanitize(file);continue;}
    if(!stat.isFile()||!/\.(?:m|c)?js$/i.test(name)||stat.size>16*1024*1024)continue;
    const raw=fs.readFileSync(file,'utf8');
    if(!raw.includes('//#region')&&!raw.includes('//#endregion'))continue;
    const next=raw.replace(/^([ \t]*\/\/#(?:end)?region [^\r\n]*)$/gm,line=>regionPrefixes.reduce((l,re)=>l.replace(re,'<solforge-ai-runtime>'),line));
    if(next!==raw)fs.writeFileSync(file,next);
  }})(portable);
  const leaks=[];
  (function scan(directory){for(const name of fs.readdirSync(directory)){
    const file=path.join(directory,name),stat=fs.lstatSync(file);
    if(stat.isDirectory())scan(file);
    else if(stat.isFile()&&textExt.has(path.extname(name).toLowerCase())&&stat.size<=16*1024*1024){
      const raw=fs.readFileSync(file,'utf8'), text=raw.toLowerCase();
      const rel=path.relative(portable,file).split(path.sep).join('/'), vendored=rel.split('/').includes('node_modules');
      const hit=patterns.find(p=>text.includes(p))??(text.includes('c:/dawn')||text.includes('c:\\dawn')?'c:/dawn':undefined);
      if(hit)leaks.push(rel+'  ['+hit+']');
      else if(driveE.test(text)){ (vendored?reported:leaks).push(rel+'  [drive E:]'); }
      const generic=reportPatterns.find(p=>text.includes(p));
      if(generic)reported.push(rel+'  ['+generic+']');
      if(!vendored&&secretPatterns.some(re=>re.test(raw)))secretHits.push(rel);
    }
  }})(portable);
  if(leaks.length)throw new Error('Absolute path leak in portable tree ('+leaks.length+' files):\n'+leaks.slice(0,25).join('\n'));
  if(secretHits.length)throw new Error('Possible embedded secret in owned sources ('+secretHits.length+' files):\n'+secretHits.slice(0,25).join('\n'));
  if(reported.length)console.log('Report-only generic path mentions in '+reported.length+' files (not failures); binaries are not scanned.');
  const files=[];
  function inventory(directory){for(const name of fs.readdirSync(directory).sort()){
    const file=path.join(directory,name),stat=fs.lstatSync(file);
    if(stat.isFile()&&sensitiveName(name)){
      if(/^\.(?:npmrc|env)/i.test(name))throw new Error('Credential-style file in portable tree: '+path.relative(portable,file));
      if(!path.relative(portable,file).split(path.sep).includes('node_modules'))throw new Error('Key or database file in owned sources: '+path.relative(portable,file));
      warnings.push(path.relative(portable,file).split(path.sep).join('/'));
    }
    if(stat.isSymbolicLink())throw new Error('Portable runtime still contains a link: '+file);
    if(stat.isDirectory())inventory(file);
    else if(stat.isFile())files.push({path:path.relative(portable,file).split(path.sep).join('/'),bytes:stat.size,sha256:hash(file)});
    else throw new Error('Unsupported portable entry: '+file);
  }}
  // Multi-platform packages (e.g. node-pty) carry every OS under prebuilds/<platform-arch>.
  // Keep only win32-x64 in this staging copy; the owned source tree is untouched.
  (function prunePrebuilds(directory){for(const name of fs.readdirSync(directory)){
    const file=path.join(directory,name),stat=fs.lstatSync(file);
    if(!stat.isDirectory())continue;
    if(name==='prebuilds'&&directory.split(path.sep).includes('node_modules')){
      for(const target of fs.readdirSync(file))
        if(target!=='win32-x64'&&fs.lstatSync(path.join(file,target)).isDirectory()){
          fs.rmSync(path.join(file,target),{recursive:true});
          console.log('Pruned foreign prebuild: '+path.relative(portable,path.join(file,target)));
        }
    } else prunePrebuilds(file);
  }})(portable);
  inventory(portable);files.sort((a,b)=>a.path<b.path?-1:a.path>b.path?1:0);
  const nativeFiles=files.filter(file=>file.path.endsWith('.node'));
  const winAddons=nativeFiles.filter(file=>/win32[-_.]x64/i.test(file.path));
  if(winAddons.length===0)throw new Error('No win32-x64 native addon in production closure; native materialization is incomplete.');
  // node-addon-system ships only darwin/linux platform packages (Landlock launcher, POSIX flock); Windows has none.
  const foreign=nativeFiles.filter(file=>/(?:linux|darwin|android|freebsd|win32-(?:arm64|ia32))/i.test(file.path));
  if(foreign.length)throw new Error('Non-win32-x64 native addons present: '+foreign.map(f=>f.path).join(', '));
  if(warnings.length)console.log('Vendored key-like files kept (review): '+warnings.length+'\n'+warnings.slice(0,15).join('\n'));
  packages.sort((a,b)=>a.path<b.path?-1:a.path>b.path?1:0);
  const manifest={schemaVersion:1,application:'Solforge AI',upstream:'DeepSeek Harness',
    version:json(path.join(source,'package.json')).version,platform:process.platform,arch:process.arch,
    nodeVersion:process.versions.node,node:'node/node.exe',entryPoint:'apps/cli/lib/bin.js',frontend:'apps/web/dist/index.html',
    desktopHost:'node_modules/@deepseek-ai/dsh-desktop-host/lib/index.js',desktopRuntime:'desktop-runtime.json',
    lockfileSha256:hash(path.join(source,'pnpm-lock.yaml')),nativeAddons:nativeFiles.map(file=>file.path),
    validation:'structural closure, file integrity and absolute-path scan only; no host, model, or native ABI execution',packages,files};
  fs.writeFileSync(path.join(portable,'ai-runtime-manifest.json'),JSON.stringify(manifest,null,2)+'\n');
} else throw new Error('Unknown packaging phase.');
'@
[IO.File]::WriteAllText($packerFile, $packer, [Text.UTF8Encoding]::new($false))
$solforgeSide = Join-Path $work 'solforge-packages'
Invoke-RuntimeTool $NodePath @($packerFile,'clone',$ownedRoot,$clone,$solforgeSide) $PSScriptRoot
$pnpmArguments = @('--dir',$clone,'--filter','@deepseek-ai/dsh','deploy','--legacy','--prod','--offline',
    '--config.ignore-scripts=true','--config.package-import-method=copy','--config.allow-unused-patches=true',
    '--config.node-linker=hoisted','--config.auto-install-peers=false','--config.link-workspace-packages=true',
    '--config.hoist-workspace-packages=false',$deploy)
Invoke-Pnpm $pnpmArguments $clone
Invoke-RuntimeTool $NodePath @('--import','tsx',$packerFile,'finish',$clone,$deploy,$staging,$NodePath,$NodeLicensePath,$scanFile,$solforgeSide) $clone
if (!(Test-Path -LiteralPath (Join-Path $staging 'ai-runtime-manifest.json'))) { throw "Portable closure was not sealed. Nothing was published. Staging left at $staging" }
# web_fetch loads turndown lazily; prove both modules resolve from the packaged tool-web on the packaged Node.
$stagedToolWeb = Join-Path $staging 'node_modules/@deepseek-ai/dsh-tool-web/package.json'
if (!(Test-Path -LiteralPath $stagedToolWeb)) { throw "Packaged dsh-tool-web is missing. Staging left at $staging" }
$turndownGate = "const r=require('node:module').createRequire(process.argv[1]);const T=r('turndown');const g=r('@joplin/turndown-plugin-gfm');" +
    "if(typeof T!=='function'||typeof g.gfm!=='function')throw new Error('unexpected turndown exports');new T().use(g.gfm);"
& (Join-Path $staging 'node\node.exe') -e $turndownGate $stagedToolWeb
if ($LASTEXITCODE -ne 0) { throw "Packaged dsh-tool-web cannot load turndown / @joplin/turndown-plugin-gfm. Staging left at $staging" }
if (Test-Path -LiteralPath $destination) { throw 'Another packager already published ai-runtime. Existing output was preserved.' }
Move-Item -LiteralPath $staging -Destination $destination -ErrorAction Stop
Write-Host "Packaged: $destination"
Write-Host "Manifest: $(Join-Path $destination 'ai-runtime-manifest.json')"
# Warm the published runtime: one headless Host start in a throwaway profile, so real-time antivirus scans
# its module files now rather than during the user's first launch. Best effort; a failure never fails packaging.
try {
    & (Join-Path $destination 'node\node.exe') (Join-Path $PSScriptRoot 'warm-runtime.mjs') $destination
    if ($LASTEXITCODE -ne 0) { Write-Warning "Runtime warm-up did not reach ready (exit $LASTEXITCODE); the first launch may be slow." }
} catch { Write-Warning "Runtime warm-up failed: $($_.Exception.Message)" }
try {
    $scratchBytes = (Get-ChildItem -LiteralPath $work -Recurse -Force -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
    Write-Host ("Scratch retained (no automatic deletion): $work  (~{0:N1} GB). Delete it manually when no longer needed." -f ($scratchBytes / 1GB))
} catch { Write-Host "Scratch retained (no automatic deletion): $work. Delete it manually." }
