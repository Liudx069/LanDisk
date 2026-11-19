// 页面级染色根因探针（诊断工具，不参与发布门禁）。
//
// 目的：对 Arctic 页面枚举**覆盖 viewport ≥80%** 的元素与所有 filter/opacity/backdrop/fixed 层，
// 输出 selector / rect / background / opacity / filter / backdrop-filter / mix-blend-mode /
// z-index / position / pointer-events 以及 ::before/::after 的 computed style，
// 并给出**像素证据**（截图平均色 + 采样点），用事实判断是否存在全屏粉色 overlay/mask/filter。
//
// 用法：node tools/e2e/theme-overlay-probe.mjs --server <LanDisk.Server.exe> [--engine webkit|chromium] [--out <dir>]

import { spawn } from 'node:child_process'
import { createServer } from 'node:net'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const repoRoot = path.resolve(here, '..', '..')
const argv = process.argv.slice(2)
const argOf = (n, d = '') => { const i = argv.indexOf(n); return i >= 0 && i + 1 < argv.length ? argv[i + 1] : d }
const serverExe = argOf('--server')
const engineName = argOf('--engine', 'webkit')
const profileId = argOf('--profile', 'arctic')
const outDir = argOf('--out', path.join(repoRoot, 'dist', 'theme-overlay-evidence'))
if (!serverExe || !fs.existsSync(serverExe)) { console.error('缺少 --server'); process.exit(2) }
fs.mkdirSync(outDir, { recursive: true })

const { webkit, chromium } = await import('playwright')
const engine = engineName === 'chromium' ? chromium : webkit
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

const freePort = () => new Promise((resolve, reject) => {
  const srv = createServer()
  srv.listen(0, '127.0.0.1', () => { const p = srv.address().port; srv.close(() => resolve(p)) })
  srv.on('error', reject)
})
async function waitHealth(base, timeoutMs = 60000) {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    try { const r = await fetch(base + '/api/health'); if (r.ok) return true } catch { /* not ready */ }
    await sleep(250)
  }
  return false
}

const root = fs.mkdtempSync(path.join(os.tmpdir(), 'landisk-ovl-'))
const dataDir = path.join(root, 'data'), sandbox = path.join(root, 'sandbox')
fs.mkdirSync(dataDir, { recursive: true }); fs.mkdirSync(sandbox, { recursive: true })
fs.writeFileSync(path.join(sandbox, '样例.txt'), '覆盖层探针。')

const httpPort = await freePort(), httpsPort = await freePort()
const base = `http://127.0.0.1:${httpPort}`
const logStream = fs.createWriteStream(path.join(root, 'server.log'))
const child = spawn(serverExe, ['--data-dir', dataDir, '--port', String(httpPort), '--https-port', String(httpsPort)],
  { cwd: path.dirname(serverExe), stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env, LANDISK_TEST_MODE: '1' } })
child.stdout.pipe(logStream); child.stderr.pipe(logStream)
const ownedDir = path.join(repoRoot, 'dist', '.owned-processes')
fs.mkdirSync(ownedDir, { recursive: true })
const markerPath = path.join(ownedDir, `${child.pid}.json`)
const writeMarker = () => { try { fs.writeFileSync(markerPath, JSON.stringify({ pid: child.pid, exe: serverExe, dataDir, owner: `e2e/theme-overlay-probe.mjs:${engineName}`, startedAt: new Date().toISOString() }, null, 2)) } catch { /* ignore */ } }
child.on('spawn', writeMarker); if (child.pid) writeMarker()
async function stopChild() {
  if (!child.pid) return
  const gone = () => child.exitCode !== null || child.signalCode !== null
  try { child.kill() } catch { /* ignore */ }
  for (let i = 0; i < 40 && !gone(); i++) await sleep(100)
  if (!gone()) { try { child.kill('SIGKILL') } catch { /* ignore */ } for (let i = 0; i < 40 && !gone(); i++) await sleep(100) }
  if (gone()) { try { fs.rmSync(markerPath, { force: true }) } catch { /* ignore */ } }
  else console.error('警告：服务端未在有界时间内退出，保留标记 ' + markerPath)
}

// 枚举：覆盖 viewport ≥80% 的元素 + 所有 filter/backdrop/blend/fixed 层 + html/body/#root
const enumerate = () => {
  const describe = (el) => {
    const cs = getComputedStyle(el)
    const r = el.getBoundingClientRect()
    const before = getComputedStyle(el, '::before')
    const after = getComputedStyle(el, '::after')
    const pseudo = (p) => ({
      content: p.content, background: p.backgroundColor, backgroundImage: p.backgroundImage,
      opacity: p.opacity, filter: p.filter, mixBlendMode: p.mixBlendMode,
      position: p.position, inset: `${p.top} ${p.right} ${p.bottom} ${p.left}`, display: p.display
    })
    return {
      selector: el.tagName.toLowerCase() + (el.id ? '#' + el.id : '') + (el.className && typeof el.className === 'string' ? '.' + el.className.trim().split(/\s+/).join('.') : ''),
      rect: { x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height) },
      areaRatio: Math.round((r.width * r.height) / (innerWidth * innerHeight) * 100) / 100,
      backgroundColor: cs.backgroundColor,
      backgroundImage: cs.backgroundImage === 'none' ? 'none' : cs.backgroundImage.slice(0, 120),
      opacity: cs.opacity,
      filter: cs.filter,
      backdropFilter: cs.backdropFilter,
      mixBlendMode: cs.mixBlendMode,
      zIndex: cs.zIndex,
      position: cs.position,
      pointerEvents: cs.pointerEvents,
      before: pseudo(before),
      after: pseudo(after)
    }
  }
  const viewportArea = innerWidth * innerHeight
  const covering = []
  const layered = []
  for (const el of document.querySelectorAll('*')) {
    const cs = getComputedStyle(el)
    const r = el.getBoundingClientRect()
    const area = r.width * r.height
    if (area / viewportArea >= 0.8) covering.push(describe(el))
    const suspicious = (cs.filter && cs.filter !== 'none') ||
      (cs.backdropFilter && cs.backdropFilter !== 'none') ||
      (cs.mixBlendMode && cs.mixBlendMode !== 'normal') ||
      (parseFloat(cs.opacity) < 1 && area / viewportArea >= 0.5) ||
      (cs.position === 'fixed' && area / viewportArea >= 0.8)
    if (suspicious) layered.push(describe(el))
  }
  const roots = ['html', 'body', '#root'].map((sel) => {
    const el = sel === 'html' ? document.documentElement : document.querySelector(sel)
    return el ? { sel, ...describe(el) } : { sel, missing: true }
  })
  return {
    viewport: { w: innerWidth, h: innerHeight, dpr: devicePixelRatio },
    profile: document.documentElement.dataset.themeProfile ?? '(未设置)',
    appearance: document.documentElement.dataset.theme ?? '(未设置)',
    cover80: covering,
    layered,
    roots
  }
}

// 像素统计由外部（PowerShell + .NET）对该 PNG 计算，脚本只负责产出截图与 DOM 证据。

let browser = null, adminContext = null, deviceContext = null
try {
  if (!await waitHealth(base)) throw new Error('隔离实例未就绪')
  browser = await engine.launch()
  adminContext = await browser.newContext()
  const token = fs.readFileSync(path.join(dataDir, 'secrets', 'admin-token.txt'), 'utf8').trim()
  const login = await adminContext.request.post(base + '/api/admin/session', { data: { token } })
  const csrf = (JSON.parse(await login.text())).csrfToken
  const api = async (ctx, method, url, body) => {
    const headers = {}; if (csrf) headers['X-LanDisk-CSRF'] = csrf
    const options = { method, headers }
    if (body !== undefined) { headers['Content-Type'] = 'application/json'; options.data = body }
    const resp = await ctx.request.fetch(base + url, options)
    return { status: resp.status(), ok: resp.ok(), text: await resp.text() }
  }
  await api(adminContext, 'POST', '/api/admin/shares', { rootPath: sandbox, displayName: '覆盖层探针', allowUpload: true, allowRename: true, allowDelete: false })
  const pairing = await api(adminContext, 'POST', '/api/admin/pairing', {})
  const pairToken = JSON.parse(pairing.text)?.token
  deviceContext = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 390, height: 844 } })
  const redeem = await deviceContext.request.post(base + '/api/pair/redeem', { data: { token: pairToken, deviceName: '覆盖层探针手机' } })
  if (!redeem.ok()) throw new Error('配对失败：' + (await redeem.text()).slice(0, 160))

  const put = await api(adminContext, 'PUT', '/api/admin/mobile-theme', { profileId })
  console.log(`  设定 ${profileId}：PUT=${put.status}`)
  const payload = JSON.parse((await (async () => {
    const r = await deviceContext.request.fetch(base + '/api/mobile-theme')
    return await r.text()
  })()) || '{}')

  const page = await deviceContext.newPage()
  await page.goto(base + '/', { waitUntil: 'domcontentloaded' })
  await page.waitForTimeout(1500)

  const shot = path.join(outDir, `${profileId}-${engineName}.png`)
  await page.screenshot({ path: shot, fullPage: false })
  const dom = await page.evaluate(enumerate)
  fs.writeFileSync(path.join(outDir, `${profileId}-${engineName}-dom.json`), JSON.stringify({ payload, ...dom }, null, 2))

  console.log('')
  console.log(`  === Arctic 页面 DOM 枚举（${engineName}，viewport ${dom.viewport.w}x${dom.viewport.h} @${dom.viewport.dpr}x）===`)
  console.log(`  profile=${dom.profile} appearance=${dom.appearance}`)
  for (const r of dom.roots) {
    console.log(`  [root] ${r.sel}: bg=${r.backgroundColor} bgImage=${r.backgroundImage} opacity=${r.opacity} filter=${r.filter} blend=${r.mixBlendMode} position=${r.position}`)
  }
  console.log(`  覆盖 viewport ≥80% 的元素(${dom.cover80.length}):`)
  for (const c of dom.cover80) {
    console.log(`    ${c.selector} rect=${c.rect.w}x${c.rect.h} area=${c.areaRatio} bg=${c.backgroundColor} opacity=${c.opacity} filter=${c.filter} backdrop=${c.backdropFilter} blend=${c.mixBlendMode} z=${c.zIndex} pos=${c.position} pe=${c.pointerEvents}`)
    if (c.before.content !== 'none' || c.before.background !== 'rgba(0, 0, 0, 0)') console.log(`      ::before content=${c.before.content} bg=${c.before.background} opacity=${c.before.opacity} filter=${c.before.filter} pos=${c.before.position} inset=${c.before.inset}`)
    if (c.after.content !== 'none' || c.after.background !== 'rgba(0, 0, 0, 0)') console.log(`      ::after  content=${c.after.content} bg=${c.after.background} opacity=${c.after.opacity} filter=${c.after.filter} pos=${c.after.position} inset=${c.after.inset}`)
  }
  console.log(`  filter/backdrop/blend/半透明大层(${dom.layered.length}):`)
  for (const c of dom.layered) {
    console.log(`    ${c.selector} rect=${c.rect.w}x${c.rect.h} area=${c.areaRatio} bg=${c.backgroundColor} opacity=${c.opacity} filter=${c.filter} backdrop=${c.backdropFilter} blend=${c.mixBlendMode} z=${c.zIndex} pos=${c.position}`)
  }
  console.log('')
  console.log(`  证据文件：${shot}`)
  console.log(`            ${path.join(outDir, `${profileId}-${engineName}-dom.json`)}`)

  // ---- 真实断言：解析后的实际渲染背景 == payload 中**该档实际应使用**的 window 颜色（ARGB → RGB）----
  //
  // "跟随 iPhone 系统"这一档由**手机自己**在 light/dark 之间选（followsDevice=true），
  // 所以期望值要按页面实际生效的 appearance 取 light 或 dark 那一半；
  // 其余档两半相同，取哪一个都一样。
  const followsDevice = payload?.followsDevice === true
  const appliedAppearance = dom.appearance
  const expectedRoles = followsDevice
    ? (appliedAppearance === 'light' ? payload?.light?.roles : payload?.dark?.roles)
    : payload?.dark?.roles
  const windowArgb = expectedRoles?.window ?? ''

  const bodyRoot = dom.roots.find((r) => r.sel === 'body')
  const actual = /rgba?\((\d+),\s*(\d+),\s*(\d+)/.exec(bodyRoot?.backgroundColor ?? '')
  const exp = /^#([0-9a-fA-F]{8})$/.exec(windowArgb)
  if (actual && exp) {
    const [ar, ag, ab] = [Number(actual[1]), Number(actual[2]), Number(actual[3])]
    const [er, eg, eb] = [parseInt(exp[1].slice(2, 4), 16), parseInt(exp[1].slice(4, 6), 16), parseInt(exp[1].slice(6, 8), 16)]
    const match = ar === er && ag === eg && ab === eb
    const swapped = ar === parseInt(exp[1].slice(0, 2), 16) && ag === er && ab === eg
    console.log('')
    console.log(`  [断言] ${profileId}: followsDevice=${followsDevice} 实际 appearance=${appliedAppearance} 期望 window=${windowArgb} → rgb(${er},${eg},${eb})，实际 body=${bodyRoot.backgroundColor} → ${match ? 'PASS' : 'FAIL'}`)
    if (swapped) console.log('         ↑ 实际值等于把 #AARRGGBB 当成 #RRGGBBAA 读的结果（编码错读缺陷复现）')
    if (!match) process.exitCode = 1
  }
  await page.close()
} catch (error) {
  console.error('覆盖层探针失败：' + (error?.message ?? error)); process.exitCode = 1
} finally {
  for (const ctx of [deviceContext, adminContext]) { try { await ctx?.close() } catch { /* ignore */ } }
  try { await browser?.close() } catch { /* ignore */ }
  await stopChild()
  await sleep(50)
  try { if (child.exitCode !== null || child.signalCode !== null) fs.rmSync(markerPath, { force: true }) } catch { /* ignore */ }
}
