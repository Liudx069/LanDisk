// 手机主题真机根因探针（诊断工具，不是产品代码，不参与发布门禁）。
//
// 目的：在**隔离实例**上用真实 HTTP 与真实磁盘文件，把"B = 刷新后仍旧红色"这件事拆成四组可判定的事实，
// 不允许靠代码推断下结论：
//   fact1  Windows 改主题后：mobile-theme.json 落盘值 + revision 是否变化
//   fact2  GET /api/mobile-theme：profileId / revision / followsDevice / dark.id 是否已是新主题
//   fact3  手机 bootstrap（GET /api/me）返回什么
//   fact4  页面**重新加载后**最终应用的 data-theme-profile 与 CSS 变量
//
// 判定：
//   服务端没保存（A）/ API 返回旧值（B）/ bootstrap 新但前端应用旧值（C）/ Safari 本地状态覆盖（D）。
// 用法：node tools/e2e/theme-root-cause-probe.mjs --server <LanDisk.Server.exe> [--engine webkit|chromium]

import { spawn } from 'node:child_process'
import { createServer } from 'node:net'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const repoRoot = path.resolve(here, '..', '..')

const argv = process.argv.slice(2)
const argOf = (name, fallback = '') => {
  const i = argv.indexOf(name)
  return i >= 0 && i + 1 < argv.length ? argv[i + 1] : fallback
}
const serverExe = argOf('--server')
const engineName = argOf('--engine', 'webkit')
if (!serverExe || !fs.existsSync(serverExe)) {
  console.error('缺少 --server <LanDisk.Server.exe>')
  process.exit(2)
}

const { webkit, chromium } = await import('playwright')
const engine = engineName === 'chromium' ? chromium : webkit

const freePort = () => new Promise((resolve, reject) => {
  const srv = createServer()
  srv.listen(0, '127.0.0.1', () => { const p = srv.address().port; srv.close(() => resolve(p)) })
  srv.on('error', reject)
})
async function waitHealth(base, timeoutMs = 60000) {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    try { const r = await fetch(base + '/api/health'); if (r.ok) return true } catch { /* not ready */ }
    await new Promise((r) => setTimeout(r, 250))
  }
  return false
}

const root = fs.mkdtempSync(path.join(os.tmpdir(), 'landisk-theme-'))
const dataDir = path.join(root, 'data')
const sandbox = path.join(root, 'sandbox')
fs.mkdirSync(dataDir, { recursive: true })
fs.mkdirSync(sandbox, { recursive: true })
fs.writeFileSync(path.join(sandbox, '样例.txt'), '主题根因探针。')

const httpPort = await freePort()
const httpsPort = await freePort()
const base = `http://127.0.0.1:${httpPort}`
const themeFile = path.join(dataDir, 'mobile-theme.json')
const readThemeFile = () => {
  try { return JSON.parse(fs.readFileSync(themeFile, 'utf8')) } catch { return null }
}

const logStream = fs.createWriteStream(path.join(root, 'server.log'))
const child = spawn(serverExe, ['--data-dir', dataDir, '--port', String(httpPort), '--https-port', String(httpsPort)],
  { cwd: path.dirname(serverExe), stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env, LANDISK_TEST_MODE: '1' } })
child.stdout.pipe(logStream)
child.stderr.pipe(logStream)

const ownedDir = path.join(repoRoot, 'dist', '.owned-processes')
fs.mkdirSync(ownedDir, { recursive: true })
const markerPath = path.join(ownedDir, `${child.pid}.json`)
const writeMarker = () => {
  try {
    fs.writeFileSync(markerPath, JSON.stringify({ pid: child.pid, exe: serverExe, dataDir, owner: `e2e/theme-root-cause-probe.mjs:${engineName}`, startedAt: new Date().toISOString() }, null, 2))
  } catch { /* ignore */ }
}
child.on('spawn', writeMarker)
if (child.pid) writeMarker()

const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

// 有界且可验证的回收：温和结束 → 有界等待 → SIGKILL → 有界等待 → **确认退出**才删所有权标记。
// 不确认退出就保留标记，让 Test-OwnedProcessCleanup.ps1 能接着处理（绝不按进程名全杀）。
async function stopChild() {
  if (!child.pid) return true
  const gone = () => child.exitCode !== null || child.signalCode !== null
  try { child.kill() } catch { /* ignore */ }
  for (let i = 0; i < 40 && !gone(); i++) await sleep(100)
  if (!gone()) {
    try { child.kill('SIGKILL') } catch { /* ignore */ }
    for (let i = 0; i < 40 && !gone(); i++) await sleep(100)
  }
  if (gone()) {
    try { fs.rmSync(markerPath, { force: true }) } catch { /* ignore */ }
    return true
  }
  console.error('警告：测试服务端未能在有界时间内退出，保留所有权标记 ' + markerPath)
  return false
}

let browser = null
let adminContext = null
let deviceContext = null

async function api(context, method, url, body, csrf) {
  const headers = {}
  if (csrf) headers['X-LanDisk-CSRF'] = csrf
  const options = { method, headers }
  if (body !== undefined) { headers['Content-Type'] = 'application/json'; options.data = body }
  const response = await context.request.fetch(base + url, options)
  const text = await response.text()
  let json = null
  try { json = JSON.parse(text) } catch { /* not json */ }
  return { status: response.status(), ok: response.ok(), json, text }
}

async function readApplied(page) {
  return page.evaluate(() => {
    const root = document.documentElement
    const style = getComputedStyle(root)
    return {
      profile: root.dataset.themeProfile ?? '(未设置)',
      appearance: root.dataset.theme ?? '(未设置)',
      windowVar: style.getPropertyValue('--window').trim() || '(空)',
      accentVar: style.getPropertyValue('--accent').trim() || '(空)'
    }
  })
}

const facts = []
const fact = (id, text) => { facts.push({ id, text }); console.log(`  [${id}] ${text}`) }

try {
  if (!await waitHealth(base)) throw new Error('隔离实例未就绪')

  browser = await engine.launch()
  adminContext = await browser.newContext()
  const token = fs.readFileSync(path.join(dataDir, 'secrets', 'admin-token.txt'), 'utf8').trim()
  const login = await adminContext.request.post(base + '/api/admin/session', { data: { token } })
  if (!login.ok()) throw new Error('管理端登录失败：' + login.status())
  const csrf = (JSON.parse(await login.text())).csrfToken

  const share = await api(adminContext, 'POST', '/api/admin/shares',
    { rootPath: sandbox, displayName: '主题探针', allowUpload: true, allowRename: true, allowDelete: false }, csrf)
  const shareId = share.json?.share?.id ?? share.json?.id
  if (!shareId) throw new Error('创建共享失败：' + share.text.slice(0, 200))
  const pairing = await api(adminContext, 'POST', '/api/admin/pairing', {}, csrf)
  const pairToken = pairing.json?.token ?? (pairing.json?.url ?? '').split('token=')[1]

  deviceContext = await browser.newContext({ ignoreHTTPSErrors: true })
  const pair = await deviceContext.request.post(base + '/api/pair/redeem', { data: { token: pairToken, deviceName: '主题探针手机' } })
  if (!pair.ok()) throw new Error('配对失败：' + pair.status() + ' ' + (await pair.text()).slice(0, 200))
  const me0 = await api(deviceContext, 'GET', '/api/me')
  const deviceCsrf = me0.json?.csrfToken

  // ---- 选择：当前档（默认 follow-iphone-system）与一个明确不同的非系统档
  const choices = (await api(adminContext, 'GET', '/api/admin/mobile-theme', undefined, csrf)).json
  const profiles = choices?.profiles ?? []
  const currentId = choices?.current
  const target = profiles.find((p) => p.id !== currentId && !p.id.startsWith('follow')) ?? profiles.find((p) => p.id !== currentId)
  if (!target) throw new Error('没有可用于对照的第二个档位')

  console.log('')
  console.log(`  对照：当前档=${currentId}  目标档=${target.id}（${target.name}，appearance=${target.appearance}）`)
  console.log('')

  // ---- fact1：Windows 改主题 → 落盘值与 revision
  const fileBefore = readThemeFile()
  const put = await api(adminContext, 'PUT', '/api/admin/mobile-theme', { profileId: target.id }, csrf)
  const fileAfter = readThemeFile()
  fact('fact1', `PUT 状态=${put.status}（ok=${put.ok}）；mobile-theme.json: profileId ${fileBefore?.profileId} → ${fileAfter?.profileId}，` +
    `revision ${fileBefore?.revision} → ${fileAfter?.revision}`)

  // ---- fact2：API 返回
  const apiTheme = (await api(deviceContext, 'GET', '/api/mobile-theme')).json
  fact('fact2', `GET /api/mobile-theme：profileId=${apiTheme?.profileId} revision=${apiTheme?.revision} ` +
    `followsDevice=${apiTheme?.followsDevice} dark.id=${apiTheme?.dark?.id} dark.appearance=${apiTheme?.dark?.appearance} ` +
    `→ 是否已是目标档=${apiTheme?.profileId === target.id}`)

  // ---- fact3：手机 bootstrap
  const me1 = (await api(deviceContext, 'GET', '/api/me')).json
  fact('fact3', `GET /api/me：theme 相关字段=${JSON.stringify({ themeRevision: me1?.themeRevision, theme: me1?.theme, permissions: !!me1?.permissions })}`)

  // ---- fact4：**重新加载**（真机 B 场景）后页面最终应用
  const page = await deviceContext.newPage()
  await page.goto(base + '/', { waitUntil: 'domcontentloaded' })
  await page.waitForTimeout(1500)
  const applied = await readApplied(page)
  fact('fact4', `重新加载后 data-theme-profile=${applied.profile} data-theme=${applied.appearance} ` +
    `--window=${applied.windowVar} --accent=${applied.accentVar} → 是否等于目标档=${applied.profile === target.id}`)

  await page.close()

  // ---- fact5：入口与资源缓存头（手机长期停在旧外观的常见成因：index.html 被缓存成旧构建的 bundle）
  const headOf = async (p) => {
    const r = await fetch(base + p)
    return { status: r.status, cc: r.headers.get('cache-control') ?? '(无)', etag: r.headers.get('etag') ?? '(无)', lm: r.headers.get('last-modified') ?? '(无)' }
  }
  const rootHead = await headOf('/')
  const indexHead = await headOf('/index.html')
  fact('fact5', `GET / → cc=${rootHead.cc} etag=${rootHead.etag}；GET /index.html → cc=${indexHead.cc} etag=${indexHead.etag}`)
  const html = await (await fetch(base + '/')).text()
  const asset = (html.match(/\/assets\/[^"']+\.js/) ?? [])[0]
  if (asset) {
    const a = await headOf(asset)
    fact('fact5b', `GET ${asset} → cc=${a.cc} etag=${a.etag}`)
  }

  console.log('')
  const f1 = fileAfter?.profileId === target.id && (fileAfter?.revision ?? 0) > (fileBefore?.revision ?? 0)
  const f2 = apiTheme?.profileId === target.id
  const f4 = applied.profile === target.id
  console.log('  ---- 判定 ----')
  console.log(`  A 服务端没保存新主题           : ${f1 ? '排除（已落盘且 revision 前进）' : '成立/可疑'}`)
  console.log(`  B API 返回旧主题               : ${f2 ? '排除（返回的就是新档）' : '成立/可疑'}`)
  console.log(`  C bootstrap 新但前端应用旧值   : ${f2 && !f4 ? '成立' : '排除'}`)
  console.log(`  D Safari 本地状态覆盖          : ${f2 && f4 ? '隔离环境下未复现（需真机确认）' : '待查'}`)
  console.log('')
} catch (error) {
  console.error('探针失败：' + (error?.message ?? error))
  process.exitCode = 1
} finally {
  for (const ctx of [deviceContext, adminContext]) { try { await ctx?.close() } catch { /* ignore */ } }
  try { await browser?.close() } catch { /* ignore */ }
  stopChild()
  // child.on('exit') 也可能删标记；这里再兜一次，且只在确认退出后。
  await sleep(50)
  try { if (child.exitCode !== null || child.signalCode !== null) fs.rmSync(markerPath, { force: true }) } catch { /* ignore */ }
}
