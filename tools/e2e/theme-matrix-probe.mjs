// 手机主题矩阵探针（诊断工具，不参与发布门禁）。
//
// 目的：用**实际 payload** 与**实际写入页面的 CSS 变量**逐档对照，回答：
//   * 服务端下发的 profile 是否与 palette 一致（Arctic 是否被 pink/custom 覆盖）
//   * payload 里有哪些 role 是客户端**从未写入**的（= 可能残留上一主题）
//   * A → B 之后是否残留 A 的变量
//
// 用法：node tools/e2e/theme-matrix-probe.mjs --server <LanDisk.Server.exe> [--engine webkit|chromium]

import { spawn } from 'node:child_process'
import { createServer } from 'node:net'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const repoRoot = path.resolve(here, '..', '..')
const argv = process.argv.slice(2)
const argOf = (n, d = '') => { const i = argv.indexOf(n); return i >= 0 && i + 1 < argv.length ? argv[i + 1] : d }
const serverExe = argOf('--server')
const engineName = argOf('--engine', 'webkit')
if (!serverExe || !fs.existsSync(serverExe)) { console.error('缺少 --server'); process.exit(2) }

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

const root = fs.mkdtempSync(path.join(os.tmpdir(), 'landisk-thememx-'))
const dataDir = path.join(root, 'data')
const sandbox = path.join(root, 'sandbox')
fs.mkdirSync(dataDir, { recursive: true }); fs.mkdirSync(sandbox, { recursive: true })
fs.writeFileSync(path.join(sandbox, '样例.txt'), '主题矩阵探针。')

const httpPort = await freePort(), httpsPort = await freePort()
const base = `http://127.0.0.1:${httpPort}`
const themeFile = path.join(dataDir, 'mobile-theme.json')
const readState = () => { try { return JSON.parse(fs.readFileSync(themeFile, 'utf8')) } catch { return null } }

const logStream = fs.createWriteStream(path.join(root, 'server.log'))
const child = spawn(serverExe, ['--data-dir', dataDir, '--port', String(httpPort), '--https-port', String(httpsPort)],
  { cwd: path.dirname(serverExe), stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env, LANDISK_TEST_MODE: '1' } })
child.stdout.pipe(logStream); child.stderr.pipe(logStream)
const ownedDir = path.join(repoRoot, 'dist', '.owned-processes')
fs.mkdirSync(ownedDir, { recursive: true })
const markerPath = path.join(ownedDir, `${child.pid}.json`)
const writeMarker = () => { try { fs.writeFileSync(markerPath, JSON.stringify({ pid: child.pid, exe: serverExe, dataDir, owner: `e2e/theme-matrix-probe.mjs:${engineName}`, startedAt: new Date().toISOString() }, null, 2)) } catch { /* ignore */ } }
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

let browser = null, adminContext = null, deviceContext = null
async function api(ctx, method, url, body, csrf) {
  const headers = {}; if (csrf) headers['X-LanDisk-CSRF'] = csrf
  const options = { method, headers }
  if (body !== undefined) { headers['Content-Type'] = 'application/json'; options.data = body }
  const r = await ctx.request.fetch(base + url, options)
  const text = await r.text(); let json = null
  try { json = JSON.parse(text) } catch { /* not json */ }
  return { status: r.status(), ok: r.ok(), json, text }
}
// 页面**内联**写入了哪些变量（= 客户端实际写了什么），以及关键变量的计算值
async function captureApplied(page) {
  return page.evaluate(() => {
    const root = document.documentElement
    const inline = {}
    for (let i = 0; i < root.style.length; i++) {
      const name = root.style[i]
      inline[name] = root.style.getPropertyValue(name).trim()
    }
    const cs = getComputedStyle(root)
    const computed = {}
    for (const v of ['--window', '--surface', '--text-primary', '--text-secondary', '--border', '--accent', '--danger', '--navigation']) {
      computed[v] = cs.getPropertyValue(v).trim()
    }
    return { profile: root.dataset.themeProfile ?? '(未设置)', appearance: root.dataset.theme ?? '(未设置)', inline, computed }
  })
}

const rows = []
try {
  if (!await waitHealth(base)) throw new Error('隔离实例未就绪')
  browser = await engine.launch()
  adminContext = await browser.newContext()
  const token = fs.readFileSync(path.join(dataDir, 'secrets', 'admin-token.txt'), 'utf8').trim()
  const login = await adminContext.request.post(base + '/api/admin/session', { data: { token } })
  const csrf = (JSON.parse(await login.text())).csrfToken
  const share = await api(adminContext, 'POST', '/api/admin/shares', { rootPath: sandbox, displayName: '主题矩阵', allowUpload: true, allowRename: true, allowDelete: false }, csrf)
  if (!share.ok) throw new Error('创建共享失败：' + share.text.slice(0, 160))
  const pairing = await api(adminContext, 'POST', '/api/admin/pairing', {}, csrf)
  const pairToken = pairing.json?.token

  deviceContext = await browser.newContext({ ignoreHTTPSErrors: true })
  const redeem = await deviceContext.request.post(base + '/api/pair/redeem', { data: { token: pairToken, deviceName: '主题矩阵手机' } })
  if (!redeem.ok()) throw new Error('配对失败：' + (await redeem.text()).slice(0, 160))

  const page = await deviceContext.newPage()
  const applyProfile = async (profileId, extra = {}) => {
    const put = await api(adminContext, 'PUT', '/api/admin/mobile-theme', { profileId, ...extra }, csrf)
    const payload = (await api(deviceContext, 'GET', '/api/mobile-theme')).json
    await page.goto(base + '/', { waitUntil: 'domcontentloaded' })
    await page.waitForTimeout(1200)
    const applied = await captureApplied(page)
    return { put, payload, applied, state: readState() }
  }

  // Custom pink：Owner 真机上出现过的那个档
  const targets = [
    ['follow-iphone-system', {}],
    ['obsidian', {}],
    ['graphite', {}],
    ['midnight', {}],
    ['arctic', {}],
    ['custom', { accent: '#FFD81B60', background: '#FFFFF5F9', surface: '#FFFFE8F1', name: 'Pink' }]
  ]

  for (const [id, extra] of targets) {
    const r = await applyProfile(id, extra)
    rows.push({ id, ...r })
    const roles = r.payload?.dark?.roles ?? {}
    console.log('')
    console.log(`  === ${id} ===`)
    console.log(`  PUT=${r.put.status} ok=${r.put.ok}${r.put.ok ? '' : '  ' + r.put.text.slice(0, 140)}`)
    console.log(`  payload: profileId=${r.payload?.profileId} revision=${r.payload?.revision} dark.id=${r.payload?.dark?.id} dark.appearance=${r.payload?.dark?.appearance} followsDevice=${r.payload?.followsDevice}`)
    console.log(`  落盘: ${JSON.stringify(r.state)}`)
    console.log(`  payload roles(${Object.keys(roles).length}): ${Object.keys(roles).sort().join(',')}`)
    console.log(`  页面: data-theme-profile=${r.applied.profile} data-theme=${r.applied.appearance} 内联变量数=${Object.keys(r.applied.inline).length}`)
    console.log(`  关键计算值: window=${r.applied.computed['--window']} accent=${r.applied.computed['--accent']} text=${r.applied.computed['--text-primary']} border=${r.applied.computed['--border']} danger=${r.applied.computed['--danger']}`)
    const sorted = Object.fromEntries(Object.keys(roles).sort().map((k) => [k, roles[k]]))
    console.log(`  ROLES_JSON ${JSON.stringify(sorted)}`)
    console.log(`  页面内联变量 JSON ${JSON.stringify(Object.fromEntries(Object.keys(r.applied.inline).sort().map((k) => [k, r.applied.inline[k]])))}`)
  }

  // ---- 残留检测：Custom(pink) → Arctic
  const pink = rows.find((r) => r.id === 'custom')
  const arctic = rows.find((r) => r.id === 'arctic')
  if (pink && arctic) {
    const pinkInline = pink.applied.inline
    const arcticInline = arctic.applied.inline
    const residue = Object.keys(pinkInline).filter((k) => arcticInline[k] === pinkInline[k] && pinkInline[k] !== '')
    console.log('')
    console.log('  === 残留检测：Custom(pink) → Arctic ===')
    console.log(`  Custom 内联变量: ${Object.keys(pinkInline).length} 个；Arctic 内联变量: ${Object.keys(arcticInline).length} 个`)
    console.log(`  仍与 pink 完全相同的变量(${residue.length}): ${residue.length ? residue.join(', ') : '无'}`)
    const pinkRoles = pink.payload?.dark?.roles ?? {}
    const arcticRoles = arctic.payload?.dark?.roles ?? {}
    const pinkKeys = Object.keys(pinkRoles)
    const arcticKeys = Object.keys(arcticRoles)
    console.log(`  payload role 键差异: pink=${pinkKeys.length} arctic=${arcticKeys.length}  仅 pink 有=[${pinkKeys.filter((k) => !arcticKeys.includes(k)).join(',')}]  仅 arctic 有=[${arcticKeys.filter((k) => !pinkKeys.includes(k)).join(',')}]`)

    // 判定必须用**与应用相同的编码**：payload 是 #AARRGGBB，页面里是 rgb()/rgba()。
    // 之前拿原始 ARGB 去比转换后的值，于是"未被写入"恒等于全部 —— 那是探针自己的错。
    const expectedInline = (roles, convert) => {
      const map = {
        window: ['--window', '--bg'], navigation: ['--navigation'],
        surface: ['--surface', '--panel'], surfaceSecondary: ['--surface-secondary', '--panel-2'],
        surfaceElevated: ['--surface-elevated'], textPrimary: ['--text-primary', '--text'],
        textSecondary: ['--text-secondary', '--muted'], border: ['--border'],
        accent: ['--accent'], accentHover: ['--accent-hover'], selection: ['--accent-soft'],
        success: ['--success', '--ok'], warning: ['--warning', '--warn'], danger: ['--danger']
      }
      const out = {}
      for (const [role, vars] of Object.entries(map)) {
        const raw = roles[role]
        if (!raw) continue
        const css = convert(raw)
        for (const v of vars) out[v] = css
      }
      // 客户端另外**派生**三个变量，写法与主映射不同，这里必须照它算，否则会把正确实现判成不符：
      //   --surface-elevated 用 stripAlpha（去掉 alpha，6 位 hex）
      //   --overlay-scrim / --warn-bg 用 hexWithAlpha（合成 rgba）
      const argbToRgb = (raw) => {
        const h = String(raw || '').replace('#', '')
        if (h.length !== 8) return raw
        return { r: parseInt(h.slice(2, 4), 16), g: parseInt(h.slice(4, 6), 16), b: parseInt(h.slice(6, 8), 16) }
      }
      const stripAlpha = (raw) => { const h = String(raw || '').replace('#', ''); return h.length === 8 ? '#' + h.slice(2) : raw }
      const hexWithAlpha = (raw, a) => {
        const c = argbToRgb(raw)
        return c && typeof c === 'object' ? `rgba(${c.r}, ${c.g}, ${c.b}, ${a})` : raw
      }
      const elevated = roles.surfaceElevated ?? roles.surface ?? roles.window
      if (elevated) out['--surface-elevated'] = stripAlpha(elevated)
      if (roles.window) out['--overlay-scrim'] = hexWithAlpha(roles.window, 0.92)
      if (roles.warning) out['--warn-bg'] = hexWithAlpha(roles.warning, 0.16)
      return out
    }
    const { toCssColor } = await import(pathToFileURL(path.join(repoRoot, 'src', 'LanDisk.Web', 'src', 'themeColor.mjs')).href)
    const expArctic = expectedInline(arcticRoles, toCssColor)
    const expPink = expectedInline(pinkRoles, toCssColor)

    // (1) 该档写入的每个变量，其实际值必须等于该档应产生的值。
    const wrong = Object.keys(expArctic).filter((v) => arcticInline[v] !== expArctic[v])
    console.log(`  实际值与 Arctic 应有值不符的变量(${wrong.length}): ${wrong.length ? wrong.join(', ') : '无'}`)

    // (2) 真正的残留：pink 设置过、arctic 不再设置，却仍留在页面上。
    const leftover = Object.keys(expPink).filter((v) => !(v in expArctic) && v in arcticInline)
    console.log(`  真残留（pink 设过、arctic 未设、却仍在页面）(${leftover.length}): ${leftover.length ? leftover.join(', ') : '无'}`)

    // (3) 可疑：两档应有值不同，但页面上的值没变。
    const stuck = Object.keys(expArctic).filter((v) => v in expPink && expPink[v] !== expArctic[v] && arcticInline[v] === pinkInline[v])
    console.log(`  可疑（两档应不同却未变化）(${stuck.length}): ${stuck.length ? stuck.join(', ') : '无'}`)

    // (4) 允许相同：两档本身取值就一样（例如内置的 success/warning/danger 在自定义档里沿用同一值）。
    const sameByDesign = Object.keys(expArctic).filter((v) => v in expPink && expPink[v] === expArctic[v] && arcticInline[v] === pinkInline[v])
    console.log(`  两档本就相同、因此相同是正常的(${sameByDesign.length}): ${sameByDesign.join(', ')}`)

    if (wrong.length || leftover.length || stuck.length) process.exitCode = 1
  }
  await page.close()
} catch (error) {
  console.error('矩阵探针失败：' + (error?.message ?? error)); process.exitCode = 1
} finally {
  for (const ctx of [deviceContext, adminContext]) { try { await ctx?.close() } catch { /* ignore */ } }
  try { await browser?.close() } catch { /* ignore */ }
  await stopChild()
  await sleep(50)
  try { if (child.exitCode !== null || child.signalCode !== null) fs.rmSync(markerPath, { force: true }) } catch { /* ignore */ }
}
