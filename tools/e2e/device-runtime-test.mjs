import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import http from 'node:http'
import { createServer } from 'node:net'
import { spawn } from 'node:child_process'
import { chromium, webkit } from 'playwright'

// R6-R21 的真实运行态验收（真 HTTP + 真浏览器）。
//
// 与 ui-test.mjs 的分工：那个脚本覆盖现有的 40 项界面/流程；本脚本只覆盖本轮真机缺陷的
// 定向回归（权限删除链、手机主题防回归、下载状态机），并保留可核对的证据。
//
// 隔离保证：独立 data root、动态端口、独立身份；不接触 Owner 的正式安装实例。

const repoRoot = path.resolve(import.meta.dirname, '..', '..')
const args = process.argv.slice(2)
const engineName = (() => { const i = args.indexOf('--engine'); return i >= 0 ? args[i + 1] : 'chromium' })()
const serverExe = (() => { const i = args.indexOf('--server'); return i >= 0 ? args[i + 1] : '' })()
if (!serverExe || !fs.existsSync(serverExe)) { console.error('找不到服务端可执行文件：' + serverExe); process.exit(2) }

const results = []
let failures = 0
function record(id, status, detail) {
  results.push({ id, status, detail })
  const color = status === 'PASS' ? '\u001b[32m' : status === 'FAIL' ? '\u001b[31m' : '\u001b[33m'
  console.log(`  [${color}${status}\u001b[0m] ${id} - ${detail}`)
  if (status === 'FAIL') failures += 1
}

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

const root = fs.mkdtempSync(path.join(os.tmpdir(), 'landisk-rt-'))
const dataDir = path.join(root, 'data')
const sandbox = path.join(root, 'sandbox')
fs.mkdirSync(dataDir, { recursive: true })
fs.mkdirSync(sandbox, { recursive: true })

// 小文件 / 0 字节 / 大文件（用于"传输中途取消"）
fs.writeFileSync(path.join(sandbox, '小文件.txt'), '这是一个用于运行态验收的文本文件。')
fs.writeFileSync(path.join(sandbox, '空文件.txt'), Buffer.alloc(0))
// 专供删除权限用例：R7 会真的把它移入回收站，不能拿后续下载用例还要用的文件去删。
fs.writeFileSync(path.join(sandbox, '待删除.txt'), '用于删除权限用例。')
const BIG = 48 * 1024 * 1024
fs.writeFileSync(path.join(sandbox, '大文件.bin'), Buffer.alloc(BIG, 5))

const httpPort = await freePort()
const httpsPort = await freePort()
const base = `http://127.0.0.1:${httpPort}`
const logStream = fs.createWriteStream(path.join(root, 'server.log'))

const child = spawn(serverExe, ['--data-dir', dataDir, '--port', String(httpPort), '--https-port', String(httpsPort)],
  { cwd: path.dirname(serverExe), stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env, LANDISK_TEST_MODE: '1', LANDISK_TEST_DOWNLOAD_THROTTLE_MS: '60', LANDISK_TEST_DOWNLOAD_CHUNK_BYTES: String(256 * 1024) } })
child.stdout.pipe(logStream)
child.stderr.pipe(logStream)

const ownedDir = path.join(repoRoot, 'dist', '.owned-processes')
fs.mkdirSync(ownedDir, { recursive: true })
const markerPath = path.join(ownedDir, `${child.pid}.json`)
const writeMarker = () => {
  try {
    fs.writeFileSync(markerPath, JSON.stringify({ pid: child.pid, exe: serverExe, dataDir, owner: `e2e/device-runtime-test.mjs:${engineName}`, startedAt: new Date().toISOString() }, null, 2))
  } catch { /* ignore */ }
}
child.on('spawn', writeMarker)
if (child.pid) writeMarker()

let browser = null
let adminBrowser = null
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

// 传输记录统一从**管理会话**读：管理端能看到全部记录，而设备端只会看到自己那条。
// 先前这里把只带 csrf 的 info 当成 request context 传进 api()，会直接抛 TypeError。
async function stateOfTransfer(shareId, fileName) {
  const res = await api(adminContext, 'GET', '/api/transfers?limit=100')
  const list = res.json?.transfers ?? []
  // 服务端发的是枚举名（Committed / Cancelled / Failed / InProgress），统一成小写再比较，
  // 避免把大小写差异误判成"状态不对"。
  return list
    .filter((t) => t.direction === 'Download' && t.fileName === fileName)
    .map((t) => ({ ...t, state: String(t.state ?? '').toLowerCase() }))
}

try {
  if (!(await waitHealth(base))) throw new Error('服务端未就绪，日志：' + path.join(root, 'server.log'))

  adminBrowser = await chromium.launch()
  adminContext = await adminBrowser.newContext()
  const token = fs.readFileSync(path.join(dataDir, 'secrets', 'admin-token.txt'), 'utf8').trim()
  const login = await adminContext.request.post(base + '/api/admin/session', { data: { token } })
  if (!login.ok()) throw new Error('管理登录失败: ' + (await login.text()))
  const csrf = (await login.json()).csrfToken

  // 共享：先不允许删除（R6 的前提）
  const share = await api(adminContext, 'POST', '/api/admin/shares',
    { rootPath: sandbox, displayName: '运行态沙箱', allowUpload: true, allowRename: true, allowDelete: false }, csrf)
  if (!share.ok) throw new Error('创建共享失败: ' + share.text)
  // 创建共享的响应是 {"share":{...}}，不是裸对象 —— 取错字段会让 shareId 变成 undefined，
  // 之后每个请求都带 shareId=undefined，表现为"能力全是 undefined"，极易被误读成权限缺陷。
  const shareId = share.json?.share?.id ?? share.json?.id
  if (!shareId) throw new Error('创建共享成功但未取到 id：' + share.text.slice(0, 200))
  const info = { csrf }

  // 配对一台设备
  const pairing = await api(adminContext, 'POST', '/api/admin/pairing', {}, csrf)
  const pairToken = pairing.json.token
  const engine = engineName === 'webkit' ? webkit : chromium
  browser = await engine.launch()
  deviceContext = await browser.newContext()
  const redeem = await deviceContext.request.post(base + '/api/pair/redeem', { data: { token: pairToken, deviceName: '运行态手机' } })
  if (!redeem.ok()) throw new Error('配对失败: ' + (await redeem.text()))
  const me = await api(deviceContext, 'GET', '/api/me')
  const deviceId = me.json.device.id
  // 设备端的写操作必须带自己的 CSRF 令牌；不带会得到 403 CSRF_REQUIRED，
  // 那与"权限被拒绝"是两回事，会把权限用例判成假通过。
  const deviceCsrf = me.json.csrfToken

  const setLevel = async (level) => api(adminContext, 'PATCH', `/api/admin/devices/${deviceId}`, { level }, csrf)
  const setShareDelete = async (allow) => api(adminContext, 'PATCH', `/api/admin/shares/${shareId}`, { allowDelete: allow }, csrf)

  // ---------------------------------------------------------------- R6-R10 权限
  await setLevel('readwrite')

  // 服务端的删除带乐观并发校验：必须把 browse 里条目的 version 一起提交，
  // 否则即使权限允许也会因为版本缺失/不符而失败 —— 那不是权限缺陷。
  async function deleteAttempt(file = '待删除.txt') {
    const b = await api(deviceContext, 'GET', `/api/browse?shareId=${shareId}&path=`)
    const entry = (b.json?.entries ?? []).find((e) => e.name === file)
    return api(deviceContext, 'POST', '/api/file/delete', { shareId, path: file, version: entry?.version }, deviceCsrf)
  }

  // R6：设备读写 + 共享不允许删除
  {
    await setShareDelete(false)
    const listing = await api(deviceContext, 'GET', `/api/browse?shareId=${shareId}&path=`)
    const caps = listing.json?.capabilities ?? {}
    const del = await deleteAttempt()
    const ok = caps.canDelete === false && (del.status === 403 || del.status === 401)
    record('R6', ok ? 'PASS' : 'FAIL',
      `设备=读写、共享删除=关 → listing.canDelete=${caps.canDelete}（应为 false）；POST /api/file/delete 状态=${del.status}（应拒绝）`)
  }

  // R7：共享允许删除 → 能力出现 + API 允许 + 进入回收站
  {
    await setShareDelete(true)
    const listing = await api(deviceContext, 'GET', `/api/browse?shareId=${shareId}&path=`)
    const caps = listing.json?.capabilities ?? {}
    const del = await deleteAttempt()
    const recycle = await api(adminContext, 'GET', '/api/admin/recycle')
    const inRecycle = JSON.stringify(recycle.json ?? {}).includes('待删除.txt')
    const ok = caps.canDelete === true && del.ok && inRecycle
    record('R7', ok ? 'PASS' : 'FAIL',
      `共享删除=开 → canDelete=${caps.canDelete}（应为 true）；删除请求 ok=${del.ok}（状态=${del.status}）；回收站中出现该文件=${inRecycle}`)
    // 放回去，后面的用例仍需要这个目标文件
    fs.writeFileSync(path.join(sandbox, '待删除.txt'), '用于删除权限用例。')
  }

  // R8：false → true，不重新配对，刷新后能力出现
  {
    await setShareDelete(false)
    const before = (await api(deviceContext, 'GET', `/api/browse?shareId=${shareId}&path=`)).json?.capabilities?.canDelete
    await setShareDelete(true)
    const after = (await api(deviceContext, 'GET', `/api/browse?shareId=${shareId}&path=`)).json?.capabilities?.canDelete
    const meAfter = await api(deviceContext, 'GET', '/api/me')
    const ok = before === false && after === true && meAfter.ok
    record('R8', ok ? 'PASS' : 'FAIL',
      `不重新配对：改前 canDelete=${before}，改后 canDelete=${after}；同一会话 /api/me 仍可用=${meAfter.ok}`)
  }

  // R9：true → false，入口消失且 API 立即拒绝
  {
    await setShareDelete(false)
    const caps = (await api(deviceContext, 'GET', `/api/browse?shareId=${shareId}&path=`)).json?.capabilities ?? {}
    const del = await deleteAttempt()
    const ok = caps.canDelete === false && (del.status === 403 || del.status === 401)
    record('R9', ok ? 'PASS' : 'FAIL',
      `共享删除 开→关：canDelete=${caps.canDelete}（应 false）；API 状态=${del.status}（应立即拒绝）`)
  }

  // R10：设备只读 + 共享允许删除 → 总闸门收窄
  {
    await setShareDelete(true)
    await setLevel('readonly')
    const caps = (await api(deviceContext, 'GET', `/api/browse?shareId=${shareId}&path=`)).json?.capabilities ?? {}
    const del = await deleteAttempt()
    const ok = caps.canDelete === false && (del.status === 403 || del.status === 401)
    record('R10', ok ? 'PASS' : 'FAIL',
      `设备=只读、共享删除=开 → canDelete=${caps.canDelete}（总闸门应收窄为 false）；API 状态=${del.status}`)
    await setLevel('readwrite')
  }

  // R11：手机端读到的能力必须与 EffectiveCapabilities 同源（这里用 /api/me 与 listing 交叉核对）
  {
    await setShareDelete(true)
    const listing = (await api(deviceContext, 'GET', `/api/browse?shareId=${shareId}&path=`)).json?.capabilities
    const meShares = (await api(deviceContext, 'GET', '/api/me')).json?.shares ?? []
    const fromMe = meShares.find((s) => s.id === shareId)
    const ok = listing?.canDelete === true && fromMe?.canDelete === true &&
               listing?.canRead === fromMe?.canRead && listing?.canUpload === fromMe?.canUpload && listing?.canRename === fromMe?.canRename
    record('R11', ok ? 'PASS' : 'FAIL',
      `listing 与 /api/me 的能力一致：canRead/Upload/Rename/Delete = ${listing?.canRead}/${listing?.canUpload}/${listing?.canRename}/${listing?.canDelete}`)
  }

  // R27：真机链路（关键）。
  //
  // 真机上 Owner 在 Windows 打开"允许删除"后，iPhone 上**删除入口一直没有出现**，只有手动刷新或
  // 重新配对才会出现。原因是手机的被动路径：它自己不轮询 listing，只在**心跳报告的 revision 变化**
  // 时才重新读取权利。所以"改了共享能力有没有通知设备"是这条链路的真正判据。
  //
  // R6–R11 之所以全都通过却漏掉真机缺陷，正是因为它们每次改完都**自己**重新 GET 了
  // /api/browse（见 R7/R8/R11），把自己的缺陷掩盖了。本用例刻意**只走手机真实会走的那一步**：
  // 改完之后只发一次心跳，然后断言 revision 确实变了。
  {
    await setShareDelete(false)
    const hb0 = (await api(deviceContext, 'POST', '/api/heartbeat', {}, deviceCsrf)).json
    const r0 = hb0?.permissionRevision

    await setShareDelete(true)   // 这就是 Windows 上的那一次操作，手机并不知情

    const hb1 = (await api(deviceContext, 'POST', '/api/heartbeat', {}, deviceCsrf)).json
    const r1 = hb1?.permissionRevision

    const moved = typeof r0 === 'number' && typeof r1 === 'number' && r1 > r0
    record('R27', moved ? 'PASS' : 'FAIL',
      `共享删除 关→开 后，手机只看心跳：permissionRevision ${r0} → ${r1}` +
      `（必须变大，手机才会重读权利并显示删除入口）${moved ? '' : ' ← 真机删除入口缺失的成因'}`)
  }

  // ---------------------------------------------------------------- R12-R15 主题
  const themeChoices = (await api(adminContext, 'GET', '/api/admin/mobile-theme', undefined, csrf)).json
  const profiles = (themeChoices?.profiles ?? themeChoices?.choices?.profiles ?? []).map((p) => p.id).filter(Boolean)
  const pickA = profiles.find((p) => !p.startsWith('follow')) ?? profiles[0]
  const pickB = profiles.find((p) => !p.startsWith('follow') && p !== pickA)
  if (!pickA || !pickB) throw new Error('可用主题不足，无法做 R13：' + JSON.stringify(profiles).slice(0, 200))

  const page = await deviceContext.newPage()
  const themeCalls = []
  page.on('request', (r) => { if (r.url().includes('/api/mobile-theme')) themeCalls.push(Date.now()) })
  await api(adminContext, 'PUT', '/api/admin/mobile-theme', { profileId: pickA }, csrf)
  await page.goto(base + '/', { waitUntil: 'networkidle' })
  await page.waitForFunction(() => document.documentElement.dataset.themeProfile !== undefined, null, { timeout: 20000 }).catch(() => {})
  const appliedA = await page.evaluate(() => document.documentElement.dataset.themeProfile)
  const accentA = await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--accent').trim())

  // R12
  record('R12', appliedA === pickA ? 'PASS' : 'FAIL',
    `server=${pickA} → mobile themeProfile=${appliedA}（应相同）；--accent=${accentA}（按 theme id 判定，不靠像素颜色）`)

  // R13：主题改变 + 第一次抓取瞬时失败 → 下一次 heartbeat 必须重试
  {
    await api(adminContext, 'PUT', '/api/admin/mobile-theme', { profileId: pickB }, csrf)
    let armed = true
    let failCount = 0
    await page.route('**/api/mobile-theme*', async (route) => {
      if (armed) { armed = false; failCount += 1; await route.abort('failed'); return }
      await route.continue()
    })
    const callsBefore = themeCalls.length
    // 等 heartbeat 触发一次失败 + 之后的重试成功
    await page.waitForFunction(
      (want) => document.documentElement.dataset.themeProfile === want, pickB, { timeout: 90000 }
    ).catch(() => {})
    const appliedB = await page.evaluate(() => document.documentElement.dataset.themeProfile)
    const callsAfter = themeCalls.length
    await page.unroute('**/api/mobile-theme*')
    const retried = (callsAfter - callsBefore) >= 2
    const ok = failCount === 1 && retried && appliedB === pickB
    record('R13', ok ? 'PASS' : 'FAIL',
      `A→B：第一次 theme 抓取已注入失败=${failCount}；期间 theme 请求数=${callsAfter - callsBefore}（>=2 才证明 ref 未提前推进、发生过重试）；最终 themeProfile=${appliedB}（应 ${pickB}）`)
  }

  // R14：整页 reload 仍是 B
  {
    await page.reload({ waitUntil: 'networkidle' })
    await page.waitForFunction(() => document.documentElement.dataset.themeProfile !== undefined, null, { timeout: 20000 }).catch(() => {})
    const applied = await page.evaluate(() => document.documentElement.dataset.themeProfile)
    record('R14', applied === pickB ? 'PASS' : 'FAIL', `reload 后 themeProfile=${applied}（应 ${pickB}，旧缓存不得覆盖服务器当前值）`)
  }

  // R15：隔离服务重启后仍读持久化的 B（重启在脚本末尾统一做，这里只记录当前值供对照）

  // ---------------------------------------------------------------- R16-R21 传输
  const dl = (file) => `/api/file/download?shareId=${shareId}&path=${encodeURIComponent(file)}`

  // R16：正常下载 → Committed，重新读取仍是终态
  {
    await page.evaluate(async (url) => { const r = await fetch(url, { credentials: 'same-origin' }); await r.arrayBuffer() }, dl('小文件.txt'))
    await new Promise((r) => setTimeout(r, 800))
    const recs = await stateOfTransfer(shareId, '小文件.txt')
    const s1 = recs.at(-1)?.state
    const again = (await stateOfTransfer(shareId, '小文件.txt')).at(-1)?.state
    const ok = s1 === 'committed' && again === 'committed'
    record('R16', ok ? 'PASS' : 'FAIL', `正常下载 → state=${s1}（应 committed）；重新读取=${again}；bytes=${recs.at(-1)?.transferredBytes}`)
  }

  // R19：0 字节 → 快速终态
  {
    await page.evaluate(async (url) => { const r = await fetch(url, { credentials: 'same-origin' }); await r.arrayBuffer() }, dl('空文件.txt'))
    await new Promise((r) => setTimeout(r, 800))
    const rec = (await stateOfTransfer(shareId, '空文件.txt')).at(-1)
    const ok = rec && rec.state === 'committed'
    record('R19', ok ? 'PASS' : 'FAIL', `0 字节下载 → state=${rec?.state}（应 committed，不得 0/0 永久进行中）`)
  }

  // R17：传输中途取消（必须证明 body 尚未发完）
  {
    const before = (await stateOfTransfer(shareId, '大文件.bin')).length
    const evidence = await page.evaluate(async ({ url, abortAfterBytes }) => {
      const controller = new AbortController()
      let received = 0, total = 0, sawInProgress = false, abortedByUs = false
      try {
        const res = await fetch(url, { signal: controller.signal, credentials: 'same-origin' })
        total = Number(res.headers.get('content-length') || 0)
        globalThis.__landiskAbortT0 = Date.now()
        const reader = res.body.getReader()
        for (;;) {
          const { done, value } = await reader.read()
          if (done) break
          received += value.length
          if (!sawInProgress) { sawInProgress = true; globalThis.__landiskMidFlight = { received, total } }
          if (received >= abortAfterBytes) { abortedByUs = true; controller.abort(); break }
        }
      } catch (error) { return { received, total, aborted: true, abortAtMs: Date.now() - (globalThis.__landiskAbortT0 ?? Date.now()), error: String(error?.name ?? error) } }
      return { received, total, aborted: abortedByUs, abortAtMs: Date.now() - (globalThis.__landiskAbortT0 ?? Date.now()) }
    }, { url: dl('大文件.bin'), abortAfterBytes: 1 * 1024 * 1024 })

    const midFlight = await page.evaluate(() => globalThis.__landiskMidFlight ?? null)
    const inFlightRec = await stateOfTransfer(shareId, '大文件.bin')
    const inProgressSeen = inFlightRec.some((t) => t.state === 'inProgress')

    // 等终态
    let terminal = null
    for (let i = 0; i < 40; i++) {
      const recs = await stateOfTransfer(shareId, '大文件.bin')
      const last = recs.at(-1)
      if (last && ['cancelled', 'failed', 'committed'].includes(last.state)) { terminal = last; break }
      await new Promise((r) => setTimeout(r, 250))
    }
    const bodyNotDone = evidence.total > 0 && evidence.received < evidence.total
    // 判据必须是**服务端事实**：服务端是否已把 response body 写完。
    // 由仅测试节流保证：发完需要 serverExpectedFullMs，而中止发生在 abortAtMs，远早于它。
    const serverExpectedFullMs = Math.ceil(evidence.total / (256 * 1024)) * 60
    const serverFinishedFirst = evidence.abortAtMs >= serverExpectedFullMs
    const ok = evidence.aborted && bodyNotDone && !serverFinishedFirst && terminal?.state === 'cancelled'
    record('R17', ok ? 'PASS' : 'FAIL',
      `大文件 ${evidence.total} 字节，客户端在收到 ${evidence.received} 字节时中止（body 未发完=${bodyNotDone}）；中途观察到 inProgress=${inProgressSeen}（mid=${JSON.stringify(midFlight)}）；终态=${terminal?.state}（应 cancelled）`)
  }

  // R18：显式 RequestAborted（短连接直接断开）→ 终态，且不留 running
  {
    const url = base + dl('大文件.bin')
    await new Promise((resolve) => {
      const controller = new AbortController()
      fetch(url, { signal: controller.signal }).then((res) => {
        const reader = res.body.getReader()
        return reader.read().then(() => { controller.abort(); resolve() })
      }).catch(() => resolve())
    })
    let terminal = null
    for (let i = 0; i < 40; i++) {
      const recs = await stateOfTransfer(shareId, '大文件.bin')
      const last = recs.at(-1)
      if (last && ['cancelled', 'failed', 'committed'].includes(last.state)) { terminal = last; break }
      await new Promise((r) => setTimeout(r, 250))
    }
    record('R18', terminal && terminal.state !== 'inProgress' ? 'PASS' : 'FAIL',
      `显式 abort 后最终 state=${terminal?.state}（必须终态，不得 running）`)
  }

  // R20：连接中断 → 终态（failed 或 cancelled，取决于断点被哪一侧观察到）
  {
    const url = base + dl('大文件.bin')
    let observed = null
    await new Promise((resolve) => {
      const req = http.get(url, (res) => {
        res.once('data', () => { req.destroy(); resolve() })
        res.on('error', () => resolve())
      })
      req.on('error', () => resolve())
    })
    for (let i = 0; i < 40; i++) {
      const recs = await stateOfTransfer(shareId, '大文件.bin')
      const last = recs.at(-1)
      if (last && ['cancelled', 'failed', 'committed'].includes(last.state)) { observed = last; break }
      await new Promise((r) => setTimeout(r, 250))
    }
    record('R20', observed && observed.state !== 'inProgress' ? 'PASS' : 'FAIL',
      `socket 中断后最终 state=${observed?.state}（须为 failed 或 cancelled 之一；具体哪一个取决于断点被服务端还是连接层观察到）`)
  }

  // R21：终态持久化（重读 API 后不变）
  {
    const before2 = await stateOfTransfer(shareId, '大文件.bin')
    await new Promise((r) => setTimeout(r, 500))
    const after2 = await stateOfTransfer(shareId, '大文件.bin')
    const same = JSON.stringify(before2.map((t) => t.state)) === JSON.stringify(after2.map((t) => t.state))
    const noneRunning = !after2.some((t) => t.state === 'inProgress')
    record('R21', same && noneRunning ? 'PASS' : 'FAIL',
      `重读后状态序列不变=${same}，且没有 inProgress=${noneRunning}（records=${after2.length}）`)
  }

  // ---------------------------------------------------------------- HEAD/304/416 不污染传输页
  {
    // 必须走**已配对的设备会话**。先前用 Node 的全局 fetch，等于匿名请求：
    // 拿到的是 401，于是"没有新增记录"变得毫无意义 —— 请求根本没进到下载处理里。
    const url = dl('小文件.txt')
    const beforeCount = (await stateOfTransfer(shareId, '小文件.txt')).length

    // 先真实下载一次拿 ETag，同时制造一条 Completed 记录作为对照基线
    const first = await deviceContext.request.get(base + url)
    const etag = first.headers()['etag']
    await first.body()
    await new Promise((r) => setTimeout(r, 600))
    const afterReal = (await stateOfTransfer(shareId, '小文件.txt')).length

    const headRes = await deviceContext.request.fetch(base + url, { method: 'HEAD' })

    let notModified = 0
    if (etag) {
      const r = await deviceContext.request.get(base + url, { headers: { 'If-None-Match': etag } })
      notModified = r.status()
    }

    const bad = await deviceContext.request.get(base + url, { headers: { Range: 'bytes=99999999-' } })
    await new Promise((r) => setTimeout(r, 700))
    const afterProbes = (await stateOfTransfer(shareId, '小文件.txt')).length

    const realAdded = afterReal - beforeCount
    const probesAdded = afterProbes - afterReal
    const ok = realAdded === 1 && probesAdded === 0 && bad.status() === 416
    record('R22H', ok ? 'PASS' : 'FAIL',
      `真实下载新增记录=${realAdded}（应 1）；HEAD(状态=${headRes.status()})/304(状态=${notModified})/416(状态=${bad.status()}) 合计新增=${probesAdded}（应 0，不得出现用户未发起的"已取消"条目）`)
  }

  // ---------------------------------------------------------------- R15：隔离服务重启
  {
    child.kill()
    await new Promise((r) => setTimeout(r, 1200))
    try { child.kill('SIGKILL') } catch { /* ignore */ }
    await new Promise((r) => setTimeout(r, 600))

    const child2 = spawn(serverExe, ['--data-dir', dataDir, '--port', String(httpPort), '--https-port', String(httpsPort)],
      { cwd: path.dirname(serverExe), stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env, LANDISK_TEST_MODE: '1', LANDISK_TEST_DOWNLOAD_THROTTLE_MS: '60', LANDISK_TEST_DOWNLOAD_CHUNK_BYTES: String(256 * 1024) } })
    child2.stdout.pipe(logStream)
    child2.stderr.pipe(logStream)
    const marker2 = path.join(ownedDir, `${child2.pid}.json`)
    try { fs.writeFileSync(marker2, JSON.stringify({ pid: child2.pid, exe: serverExe, dataDir, owner: `e2e/device-runtime-test.mjs:${engineName}:restart`, startedAt: new Date().toISOString() }, null, 2)) } catch { /* ignore */ }

    const healthy = await waitHealth(base, 60000)
    let applied = null
    if (healthy) {
      const page2 = await deviceContext.newPage()
      await page2.goto(base + '/', { waitUntil: 'networkidle' })
      await page2.waitForFunction(() => document.documentElement.dataset.themeProfile !== undefined, null, { timeout: 20000 }).catch(() => {})
      applied = await page2.evaluate(() => document.documentElement.dataset.themeProfile)
      await page2.close()
    }
    record('R15', healthy && applied === pickB ? 'PASS' : 'FAIL',
      `隔离服务重启后 healthy=${healthy}，新页面 themeProfile=${applied}（应 ${pickB}，说明持久化生效）`)

    try { child2.kill() } catch { /* ignore */ }
    await new Promise((r) => setTimeout(r, 1200))
    try { child2.kill('SIGKILL') } catch { /* ignore */ }
    try { fs.rmSync(marker2, { force: true }) } catch { /* ignore */ }
  }
} catch (error) {
  record('HARNESS', 'FAIL', String(error?.message ?? error).split('\n').slice(0, 3).join(' / '))
} finally {
  if (deviceContext) await deviceContext.close().catch(() => {})
  if (adminContext) await adminContext.close().catch(() => {})
  if (deviceContext) await deviceContext.close().catch(() => {})
  if (adminBrowser) await adminBrowser.close().catch(() => {})
  const exited = () => child.exitCode !== null || child.signalCode !== null
  try { child.kill() } catch { /* ignore */ }
  for (let i = 0; i < 20 && !exited(); i++) await new Promise((r) => setTimeout(r, 100))
  if (!exited()) { try { child.kill('SIGKILL') } catch { /* ignore */ } for (let i = 0; i < 30 && !exited(); i++) await new Promise((r) => setTimeout(r, 100)) }
  if (exited()) { try { fs.rmSync(markerPath, { force: true }) } catch { /* ignore */ } }
  try { logStream.end() } catch { /* ignore */ }
  fs.rmSync(root, { recursive: true, force: true })
}

console.log('\n=== R6-R21 汇总 ===')
const passed = results.filter((r) => r.status === 'PASS').length
const failed = results.filter((r) => r.status === 'FAIL').length
console.log(`通过 ${passed} / 失败 ${failed} / 待定 ${results.filter((r) => r.status === 'PENDING').length}`)
const outPath = path.join(repoRoot, 'dist', `device-runtime-${engineName}.json`)
fs.mkdirSync(path.dirname(outPath), { recursive: true })
fs.writeFileSync(outPath, JSON.stringify({ engine: engineName, serverExe, passed, failed, results }, null, 2))
console.log('证据：' + outPath)
// 显式退出：浏览器与服务端句柄会让事件循环继续存活，不退出会让 CI 看起来像卡住。
process.exit(failed > 0 ? 1 : 0)
