// Register Solforge packages without `pnpm install`: pnpm-lock.yaml importer blocks and package-local node_modules
// junctions (absolute targets). Idempotent. Usage: node register.cjs <runtimeRoot> [--dry]
const fs = require('fs'), path = require('path')
const root = path.resolve(process.argv[2])
const dry = process.argv.includes('--dry')
const posix = p => p.split(path.sep).join('/')

// workspace package map
const ws = {}
for (const g of ['packages', 'apps', 'vendor']) {
  for (const a of fs.readdirSync(path.join(root, g))) {
    const d = path.join(root, g, a)
    if (!fs.statSync(d).isDirectory()) continue
    if (fs.existsSync(path.join(d, 'package.json'))) { ws[JSON.parse(fs.readFileSync(path.join(d, 'package.json'))).name] = d; continue }
    for (const b of fs.readdirSync(d)) {
      const e = path.join(d, b)
      if (fs.existsSync(path.join(e, 'package.json'))) ws[JSON.parse(fs.readFileSync(path.join(e, 'package.json'))).name] = e
    }
  }
}
// pnpm-workspace overrides that are links
const overrides = {}
const wsYaml = fs.readFileSync(path.join(root, 'pnpm-workspace.yaml'), 'utf8')
for (const m of wsYaml.matchAll(/^\s*'?([^'\s:]+(?:\/[^'\s:]+)?)'?\s*:\s*'?link:([^'\s]+)'?\s*$/gm)) overrides[m[1]] = path.resolve(root, m[2])

const lockFile = path.join(root, 'pnpm-lock.yaml')
let lock = fs.readFileSync(lockFile, 'utf8')
const eol = lock.includes('\r\n') ? '\r\n' : '\n'
lock = lock.split('\r\n').join('\n')

/** third-party version used elsewhere in the lockfile for a name+specifier */
function thirdPartyVersion(name, specifier) {
  const q = name.startsWith('@') ? `'${name}'` : name
  const re = new RegExp(`\\n {6}${q.replace(/[.*+?^${}()|[\]\\/]/g, '\\$&')}:\\n {8}specifier: ${specifier.replace(/[.*+?^${}()|[\]\\/]/g, '\\$&')}\\n {8}version: ([^\\n]+)`)
  const m = re.exec(lock)
  if (!m) throw new Error(`no locked version for ${name}@${specifier}`)
  return m[1]
}

function entryFor(pkgDir, name, specifier) {
  if (overrides[name]) {
    const rel = posix(path.relative(pkgDir, overrides[name]))
    return { specifier: `link:${rel}`, version: `link:${rel}`, target: overrides[name] }
  }
  if (specifier.startsWith('workspace:')) {
    const dir = ws[name]
    if (!dir) throw new Error(`unknown workspace package ${name} (from ${pkgDir})`)
    return { specifier, version: `link:${posix(path.relative(pkgDir, dir))}`, target: dir }
  }
  const version = thirdPartyVersion(name, specifier)
  // pnpm store directory: peer suffixes `(x@1)` become `_x@1`, scoped slashes become `+`.
  const storeName = `${name.replace('/', '+')}@${version.replace(/\(([^()]*)\)/g, '_$1').replace(/\//g, '+')}`
  return { specifier, version, target: path.join(root, 'node_modules', '.pnpm', storeName, 'node_modules', ...name.split('/')) }
}

function importerBlock(rel, pkgDir) {
  const manifest = JSON.parse(fs.readFileSync(path.join(pkgDir, 'package.json'), 'utf8'))
  const lines = [`  ${rel}:`]
  const links = []
  for (const section of ['dependencies', 'devDependencies']) {
    const deps = manifest[section] ?? {}
    const names = Object.keys(deps).sort()
    if (names.length === 0) continue
    lines.push(`    ${section}:`)
    for (const name of names) {
      const e = entryFor(pkgDir, name, deps[name])
      lines.push(`      ${name.startsWith('@') ? `'${name}'` : name}:`, `        specifier: ${e.specifier}`, `        version: ${e.version}`)
      links.push([name, e.target])
    }
  }
  if (lines.length === 1) lines[0] += ' {}'
  return { text: lines.join('\n'), links }
}

function upsertImporter(rel, text) {
  const start = lock.indexOf('\nimporters:\n')
  const end = lock.indexOf('\npackages:\n', start)
  if (start < 0 || end < 0) throw new Error('importers section not found')
  const section = lock.slice(start + '\nimporters:\n'.length, end)
  const blocks = section.split(/\n(?=  \S)/).filter(b => b.trim() !== '')
  const keyOf = b => b.slice(2, b.indexOf(':'))
  const i = blocks.findIndex(b => keyOf(b) === rel)
  if (i >= 0) blocks[i] = text
  else {
    const at = blocks.findIndex(b => keyOf(b) > rel)
    blocks.splice(at < 0 ? blocks.length : at, 0, text)
  }
  // pnpm separates importer blocks with a blank line
  const rebuilt = blocks.map(b => b.replace(/\n+$/, '')).join('\n\n') + '\n'
  lock = lock.slice(0, start + '\nimporters:\n'.length) + '\n' + rebuilt.replace(/^\n+/, '') + lock.slice(end)
}

function link(pkgDir, name, target) {
  const at = path.join(pkgDir, 'node_modules', ...name.split('/'))
  if (!fs.existsSync(target)) throw new Error(`link target missing: ${target}`)
  let current
  try { current = fs.realpathSync(at) } catch { current = undefined }
  if (current !== undefined && path.resolve(current).toLowerCase() === fs.realpathSync(target).toLowerCase()) return false
  if (dry) return true
  if (current !== undefined || fs.existsSync(at)) {
    const st = fs.lstatSync(at)
    if (!st.isSymbolicLink()) throw new Error(`refusing to replace a real directory: ${at}`)
    fs.unlinkSync(at)
  }
  fs.mkdirSync(path.dirname(at), { recursive: true })
  fs.symlinkSync(target, at, 'junction')
  return true
}

const full = [
  'packages/experimental/solforge-contract',
  'packages/experimental/solforge-plugins',
  'packages/experimental/client-ui-solforge-plugins',
  'packages/experimental/client-ui-solforge-brand',
  'packages/experimental/solforge-model-tiers',
  'packages/experimental/client-ui-solforge-model-tiers',
  'packages/experimental/client-ui-solforge-editor',
  'packages/experimental/client-ui-solforge-automatic',
  'packages/experimental/solforge-automatic',
  'packages/experimental/solforge-bundle',
]
for (const rel of full) {
  const dir = path.join(root, rel)
  if (!fs.existsSync(path.join(dir, 'package.json'))) { console.log(`skip (no package.json): ${rel}`); continue }
  const { text, links } = importerBlock(rel, dir)
  upsertImporter(rel, text)
  let made = 0
  for (const [name, target] of links) if (link(dir, name, target)) made++
  console.log(`${rel}: importer written, ${links.length} links (${made} created/fixed)`)
}
// apps/desktop: one devDependency added by hand (the rest of its block is pnpm's)
{
  const rel = 'apps/desktop'
  const dir = path.join(root, rel)
  const name = '@deepseek-ai/dsh-experimental-solforge-contract'
  const e = entryFor(dir, name, 'workspace:*')
  const entry = `      '${name}':\n        specifier: workspace:*\n        version: ${e.version}\n`
  const start = lock.indexOf(`\n  ${rel}:\n`)
  if (start < 0) throw new Error('apps/desktop importer missing')
  const blockEnd = lock.indexOf('\n\n', start + 1)
  const block = lock.slice(start, blockEnd)
  if (!block.includes(`'${name}':`)) {
    const dev = block.indexOf('\n    devDependencies:\n')
    if (dev < 0) throw new Error('apps/desktop has no devDependencies section')
    // insert in sorted position among the devDependencies entries
    const devStart = start + dev + '\n    devDependencies:\n'.length
    const rest = lock.slice(devStart, blockEnd)
    const entries = [...rest.matchAll(/^ {6}('?[^\n:]+'?):\n/gm)]
    const after = entries.find(m => m[1].replace(/'/g, '') > name)
    const at = after === undefined ? blockEnd + 1 : devStart + after.index
    lock = lock.slice(0, at) + entry + lock.slice(at)
  }
  console.log(`${rel}: contract devDependency ${link(dir, name, e.target) ? 'linked' : 'already linked'}`)
}
if (!dry) fs.writeFileSync(lockFile, lock.split('\n').join(eol))
console.log(dry ? 'dry run: nothing written' : 'pnpm-lock.yaml written')
