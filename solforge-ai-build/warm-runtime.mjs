// Warm a freshly published ai-runtime: start its Desktop Host once, headless, in a throwaway profile, wait for
// ready (or a fatal startup report), then shut it down. The Host's ~1,400 module files are then scanned by
// real-time antivirus and sit in the OS file cache before the user's first launch, which otherwise spends
// 40-65 s reading cold files. Helps only until the antivirus scan cache or the file cache is reset.
//
// Usage: node warm-runtime.mjs <ai-runtime folder>
// - No model calls: the profile has no providers, no credentials and no MCP rows.
// - The throwaway folder lives in %TEMP%, is printed, and is deleted at the end (it is this script's own).
// - The user's profile, %LOCALAPPDATA%\Solforge and credential files are never read or written: DSH_HOME,
//   LOCALAPPDATA, APPDATA and USERPROFILE point into the throwaway folder, and key-like variables are dropped.
// - Only the process tree this script started is stopped. The ready URL (it carries a token) is never printed.
import { spawn, execFileSync } from 'node:child_process'
import { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'

const runtime = resolve(process.argv[2] ?? '')
const node = join(runtime, 'node', 'node.exe')
const entry = join(runtime, 'node_modules', '@deepseek-ai', 'dsh-desktop-host', 'lib', 'index.js')
if (!existsSync(node) || !existsSync(entry)) {
  console.error(`warm-runtime: ${runtime} is not an ai-runtime folder (node/node.exe or the desktop host is missing)`)
  process.exit(2)
}

const work = mkdtempSync(join(tmpdir(), 'solforge-ai-warm-'))
console.log(`Warm-up scratch directory: ${work}`)
const home = join(work, 'home')
const profile = join(home, 'profiles', 'desktop')
for (const dir of [profile, join(work, 'electron'), join(work, 'localappdata'), join(work, 'appdata'), join(work, 'user')]) {
  mkdirSync(dir, { recursive: true })
}
const overlay = join(home, 'solforge-app-overlay.yml')
writeFileSync(overlay, '[]\n')
// The same minimal desktop profile the shell creates for a new user: the two default bundles, no patches.
writeFileSync(join(profile, 'package.json'), `${JSON.stringify({ name: 'dsh-profile-desktop', private: true, dependencies: {},
  dsh: { profile: { bundles: ['@deepseek-ai/dsh-base', '@deepseek-ai/dsh-web-app'] } } }, null, 2)}\n`)
writeFileSync(join(profile, 'cordis.yml'), '[]\n')
writeFileSync(join(profile, 'pnpm-workspace.yaml'), 'packages:\n  - .\n\nnodeLinker: hoisted\nautoInstallPeers: false\n')

const KEY_LIKE = /(KEY|TOKEN|SECRET|PASSWORD|CREDENTIAL|AUTH)/iu
const env = Object.fromEntries(Object.entries(process.env).filter(([name]) => !KEY_LIKE.test(name)
  && !['NODE_OPTIONS', 'ELECTRON_RUN_AS_NODE', 'SOLFORGE_AI_PATCH', 'SOLFORGE_AI_PROJECT'].includes(name.toUpperCase())))
Object.assign(env, {
  SOLFORGE_AI: '1', SOLFORGE_AI_MODE: 'standalone', SOLFORGE_AI_DISABLE_OFFICE: '1',
  SOLFORGE_AI_USER_DATA: join(work, 'electron'), SOLFORGE_AI_APP_CONFIG: join(work, 'electron', 'solforge-app.json'),
  SOLFORGE_AI_APP_OVERLAY: overlay, DSH_HOME: home, DSH_TELEMETRY_DISABLED: '1',
  LOCALAPPDATA: join(work, 'localappdata'), APPDATA: join(work, 'appdata'), USERPROFILE: join(work, 'user'),
})

const started = performance.now()
const elapsed = () => `${String(Math.round(performance.now() - started))} ms`
const child = spawn(node, ['--expose-internals', entry, runtime, profile, join(runtime, 'primary-runtime')],
  { cwd: profile, env, stdio: ['ignore', 'pipe', 'pipe', 'ipc'], windowsHide: true })
child.stdout.resume()
let stderr = ''
child.stderr.setEncoding('utf8')
child.stderr.on('data', (chunk) => { stderr = (stderr + chunk).slice(-4000) })

const stopTree = () => {
  if (child.exitCode !== null || child.pid === undefined) return
  try { execFileSync('taskkill', ['/T', '/F', '/PID', String(child.pid)], { stdio: 'ignore' }) } catch { /* already gone */ }
}
const cleanup = (code) => {
  stopTree()
  try { rmSync(work, { recursive: true, force: true, maxRetries: 5, retryDelay: 500 }); console.log(`Warm-up scratch deleted: ${work}`) }
  catch { console.log(`Warm-up scratch retained (could not delete): ${work}`) }
  process.exit(code)
}

const deadline = setTimeout(() => { console.log(`warm-runtime: no ready within 240 s (${elapsed()}); stopping`); cleanup(1) }, 240_000)
let settled = false
const finish = (label, code) => {
  if (settled) return
  settled = true
  clearTimeout(deadline)
  console.log(`warm-runtime: ${label} after ${elapsed()}`)
  if (child.connected) child.send({ type: 'shutdown' }, () => { /* a Host that already left cannot take it */ })
  const hardStop = setTimeout(() => cleanup(code), 15_000)
  child.once('exit', () => { clearTimeout(hardStop); cleanup(code) })
}
child.on('error', () => { /* IPC write after the Host left; exit handling covers it */ })
child.on('message', (message) => {
  if (message?.type === 'ready') finish('Host ready', 0)
  // A startup failure still loaded most modules; report it without its diagnostic body.
  else if (message?.type === 'fatal') {
    const reason = String(message.message ?? '').replace(/token=[^&\s"]+/gu, 'token=REDACTED').slice(0, 400)
    finish(`Host reported a startup failure (${reason})`, 1)
  }
})
child.on('exit', (code) => {
  if (settled) return
  settled = true
  clearTimeout(deadline)
  console.log(`warm-runtime: Host exited (${String(code)}) after ${elapsed()} before ready; stderr tail:\n${stderr.replace(/token=[^&\s"]+/gu, 'token=REDACTED').slice(-1200)}`)
  // A Host that crashed during warm-up warmed nothing; exit 3 so package-runtime.ps1 prints its warning.
  cleanup(3)
})
