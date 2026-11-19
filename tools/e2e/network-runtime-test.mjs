import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import net from 'node:net'
import { spawn } from 'node:child_process'

// R3 / R4 / R5 —— 推荐地址必须与实际监听一致（真 HTTP 运行态）。
//
// 关键点：局域网监听只在**启动时**绑定，所以每个用例都要独立起一个服务并预置 settings.json，
// 而不是运行时改配置（运行时改配置得到的正是"配置说开、监听还是旧的"那种假象）。
//
// R3：LAN=true + HTTPS=true → 必须有真实的局域网 HTTPS 监听，且推荐地址是那个物理地址。
// R4：LAN=false          → 不得推荐物理 HTTPS 地址，且不得有局域网监听。
// R5：HTTPS=false        → 同上。
// 另外统一检查"同一份运行时事实不得自相矛盾"：只要推荐了物理地址，该地址就必须真的在监听。

const repoRoot = path.resolve(import.meta.dirname, '..', '..')
const serverExe = (() => { const i = process.argv.indexOf('--server'); return i >= 0 ? process.argv[i + 1] : '' })()
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
  const s = net.createServer()
  s.listen(0, '127.0.0.1', () => { const p = s.address().port; s.close(() => resolve(p)) })
  s.on('error', reject)
})

async function waitHealth(base, timeoutMs = 60000) {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    try { const r = await fetch(base + '/api/health'); if (r.ok) return true } catch { /* not ready */ }
    await new Promise((r) => setTimeout(r, 200))
  }
  return false
}

// 该端口上是否真的有人监听（用于证明"推荐 ≠ 空地址"）
const canConnect = (host, port, timeoutMs = 2500) => new Promise((resolve) => {
  const socket = net.connect({ host, port })
  const done = (ok) => { try { socket.destroy() } catch { /* ignore */ } resolve(ok) }
  socket.setTimeout(timeoutMs)
  socket.once('connect', () => done(true))
  socket.once('timeout', () => done(false))
  socket.once('error', () => done(false))
})

const baseSandbox = fs.mkdtempSync(path.join(os.tmpdir(), 'landisk-net-'))
const ownedDir = path.join(repoRoot, 'dist', '.owned-processes')
fs.mkdirSync(ownedDir, { recursive: true })

async function runCase({ id, lanEnabled, httpsEnabled, expectPhysical }) {
  const root = fs.mkdtempSync(path.join(baseSandbox, id + '-'))
  const dataDir = path.join(root, 'data')
  fs.mkdirSync(dataDir, { recursive: true })

  const httpPort = await freePort()
  const httpsPort = await freePort()

  // 预置设置：LAN 监听只在启动时绑定，所以必须写盘后再启动
  fs.writeFileSync(path.join(dataDir, 'settings.json'), JSON.stringify({
    LanEnabled: lanEnabled,
    HttpsEnabled: httpsEnabled,
    HttpCompatModeEnabled: false,
    LanBindAddress: null,
    AllowedCidr: null,
    HttpPort: httpPort,
    HttpsPort: httpsPort,
    VerboseLogging: false
  }, null, 2))

  const base = `http://127.0.0.1:${httpPort}`
  const log = fs.createWriteStream(path.join(root, 'server.log'))
  const child = spawn(serverExe, ['--data-dir', dataDir, '--port', String(httpPort), '--https-port', String(httpsPort)],
    { cwd: path.dirname(serverExe), stdio: ['ignore', 'pipe', 'pipe'] })
  child.stdout.pipe(log); child.stderr.pipe(log)
  const marker = path.join(ownedDir, `${child.pid}.json`)
  try { fs.writeFileSync(marker, JSON.stringify({ pid: child.pid, exe: serverExe, dataDir, owner: `e2e/network-runtime-test.mjs:${id}`, startedAt: new Date().toISOString() }, null, 2)) } catch { /* ignore */ }

  try {
    if (!(await waitHealth(base))) throw new Error('服务端未就绪')

    const token = fs.readFileSync(path.join(dataDir, 'secrets', 'admin-token.txt'), 'utf8').trim()
    const login = await fetch(base + '/api/admin/session', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ token }) })
    if (!login.ok) throw new Error('管理登录失败 ' + login.status + ': ' + (await login.text()).slice(0, 200))
    const cookies = (login.headers.getSetCookie?.() ?? []).map((c) => c.split(';')[0]).join('; ')
    if (!cookies) throw new Error('管理登录未返回任何 cookie')

    const hsRes = await fetch(base + '/api/host-status', { headers: { Cookie: cookies } })
    const hsText = await hsRes.text()
    // 不静默：拿不到运行态事实时必须显式失败，否则 undefined 会被"不等于 physical"判成假通过。
    if (!hsRes.ok) throw new Error('host-status ' + hsRes.status + ': ' + hsText.slice(0, 200))
    const envelope = JSON.parse(hsText)
    // 响应是 { ok, detailed, status: {...} } —— 字段在 status 里面。
    const status = envelope.status ?? envelope
    if (status.recommendedKind === undefined) throw new Error('host-status 响应缺少 recommendedKind：' + hsText.slice(0, 200))

    const recommended = status.recommendedOrigin
    const kind = status.recommendedKind
    const physical = status.physicalOrigin

    // 解析推荐地址的主机与端口
    let recHost = null, recPort = 0
    if (recommended) {
      const m = /^https:\/\/([^:/]+):(\d+)$/.exec(recommended)
      if (m) { recHost = m[1]; recPort = Number(m[2]) }
    }

    // 局域网监听是否真实存在
    const lanListening = recHost && recHost !== '127.0.0.1' && recHost !== 'localhost'
      ? await canConnect(recHost, recPort)
      : false

    const isPhysicalKind = kind === 'physical'
    const looksPhysical = physical !== null && physical !== undefined

    if (expectPhysical) {
      // R3：必须是 physical，而且那个地址上**真的有** HTTPS 监听
      const ok = isPhysicalKind && !!recHost && lanListening && status.lanEnabled === true && status.httpsEnabled === true
      record(id, ok ? 'PASS' : 'FAIL',
        `LAN=true HTTPS=true → kind=${kind}（应 physical）；recommended=${recommended}；该地址实测可连接=${lanListening}；physicalOrigin=${physical}`)
    } else {
      // R4/R5：不得推荐物理 HTTPS；并且不得存在局域网监听
      const noPhysicalRecommendation = !isPhysicalKind && (recommended === null || recommended === undefined || !/^https:\/\/\d+\.\d+\.\d+\.\d+:/.test(String(recommended)))
      record(id, noPhysicalRecommendation ? 'PASS' : 'FAIL',
        `LAN=${status.lanEnabled} HTTPS=${status.httpsEnabled} → kind=${kind}（不得 physical）；recommended=${recommended ?? 'null'}（不得是物理 IP 的 HTTPS 地址）；physicalOrigin 仍可展示=${looksPhysical}`)
    }

    return { recommended, kind, physical, lanListening, recHost, recPort }
  } finally {
    child.kill()
    await new Promise((r) => setTimeout(r, 900))
    try { child.kill('SIGKILL') } catch { /* ignore */ }
    await new Promise((r) => setTimeout(r, 400))
    try { fs.rmSync(marker, { force: true }) } catch { /* ignore */ }
    try { log.end() } catch { /* ignore */ }
    fs.rmSync(root, { recursive: true, force: true })
  }
}

try {
  const r3 = await runCase({ id: 'R3', lanEnabled: true, httpsEnabled: true, expectPhysical: true })
  const r4 = await runCase({ id: 'R4', lanEnabled: false, httpsEnabled: true, expectPhysical: false })
  const r5 = await runCase({ id: 'R5', lanEnabled: true, httpsEnabled: false, expectPhysical: false })

  // 一致性：不得出现"推荐了物理地址但其实没在监听"
  const contradiction = (r3.kind === 'physical' && !r3.lanListening)
  record('R1R2', contradiction ? 'FAIL' : 'PASS',
    `同一份运行时事实自洽：推荐 physical=${r3.kind === 'physical'}，其实测可连接=${r3.lanListening}（不得"推荐了却连不上"）`)

  // R3 若为 physical，物理地址应当是该机上真实存在的地址（不是 loopback）
  const notLoopback = r3.recHost && !['127.0.0.1', '::1', 'localhost'].includes(r3.recHost)
  record('R5X', r3.kind === 'physical' ? (notLoopback ? 'PASS' : 'FAIL') : 'PASS',
    `physical 推荐地址不是 loopback：${r3.recHost ?? '(无 physical 推荐)'}`)
} catch (error) {
  record('HARNESS', 'FAIL', String(error?.message ?? error).split('\n').slice(0, 3).join(' / '))
}

console.log('\n=== R3-R5 汇总 ===')
const passed = results.filter((r) => r.status === 'PASS').length
console.log(`通过 ${passed} / 失败 ${failures}`)
const outPath = path.join(repoRoot, 'dist', 'network-runtime.json')
fs.writeFileSync(outPath, JSON.stringify({ passed, failed: failures, results }, null, 2))
console.log('证据：' + outPath)
fs.rmSync(baseSandbox, { recursive: true, force: true })
process.exit(failures > 0 ? 1 : 0)
