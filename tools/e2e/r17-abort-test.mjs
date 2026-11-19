import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import http from 'node:http'
import net from 'node:net'
import { spawn } from 'node:child_process'
import { request } from 'playwright'

// R17 定性测试（确定性，不靠"客户端收到多少字节"推断）
//
// 夹具：服务端 LANDISK_TEST_MODE=1 + LANDISK_TEST_DOWNLOAD_THROTTLE_MS/CHUNK_BYTES 把发送有意放慢，
// 使**客户端中止必然发生在服务端 response body 尚未写完时**。
//
// 判据（服务端事实）：
//   RESPONSE_BODY_STARTED      = 客户端已收到 >=1 字节
//   SERVER_RESPONSE_COMPLETED  = false（由节流保证：总时长远大于中止时刻）
//   CLIENT_ABORTED             = true（本脚本主动 destroy socket）
//   REQUEST_ABORTED_OBSERVED   = 由服务端终态反映
// 结论：
//   终态 cancelled → 产品正确；原 WebKit R17 属 harness 语义不足
//   终态 committed → PRODUCT_FAILURE 确证（服务端在 response 未写完时仍报已完成）

const repoRoot = path.resolve(import.meta.dirname, '..', '..')
const serverExe = (() => { const i = process.argv.indexOf('--server'); return i >= 0 ? process.argv[i + 1] : '' })()
if (!serverExe || !fs.existsSync(serverExe)) { console.error('找不到服务端可执行文件：' + serverExe); process.exit(2) }

const BIG = 48 * 1024 * 1024
const THROTTLE_MS = 60
const CHUNK = 256 * 1024
const ABORT_AFTER = 2 * 1024 * 1024

const freePort = () => new Promise((res, rej) => { const s = net.createServer(); s.listen(0, '127.0.0.1', () => { const p = s.address().port; s.close(() => res(p)) }); s.on('error', rej) })
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'landisk-r17-'))
const dataDir = path.join(root, 'data'); const sandbox = path.join(root, 'sandbox')
fs.mkdirSync(dataDir, { recursive: true }); fs.mkdirSync(sandbox, { recursive: true })
fs.writeFileSync(path.join(sandbox, '大文件.bin'), Buffer.alloc(BIG, 5))

const httpPort = await freePort(), httpsPort = await freePort()
const base = `http://127.0.0.1:${httpPort}`
const log = fs.createWriteStream(path.join(root, 'server.log'))

const child = spawn(serverExe, ['--data-dir', dataDir, '--port', String(httpPort), '--https-port', String(httpsPort)], {
  cwd: path.dirname(serverExe), stdio: ['ignore', 'pipe', 'pipe'],
  env: { ...process.env, LANDISK_TEST_MODE: '1', LANDISK_TEST_DOWNLOAD_THROTTLE_MS: String(THROTTLE_MS), LANDISK_TEST_DOWNLOAD_CHUNK_BYTES: String(CHUNK) }
})
child.stdout.pipe(log); child.stderr.pipe(log)

const ownedDir = path.join(repoRoot, 'dist', '.owned-processes')
fs.mkdirSync(ownedDir, { recursive: true })
const marker = path.join(ownedDir, `${child.pid}.json`)
try { fs.writeFileSync(marker, JSON.stringify({ pid: child.pid, exe: serverExe, dataDir, owner: 'e2e/r17-abort-test.mjs', startedAt: new Date().toISOString() }, null, 2)) } catch { }

let admin = null, device = null
const facts = {}
try {
  for (let i = 0; i < 240; i++) { try { if ((await fetch(base + '/api/health')).ok) break } catch { } await new Promise(r => setTimeout(r, 250)) }

  const token = fs.readFileSync(path.join(dataDir, 'secrets', 'admin-token.txt'), 'utf8').trim()
  admin = await request.newContext({ baseURL: base })
  const login = await admin.post('/api/admin/session', { data: { token } })
  const csrf = (await login.json()).csrfToken
  const H = { 'X-LanDisk-CSRF': csrf }

  const shareRes = await admin.post('/api/admin/shares', { headers: H, data: { rootPath: sandbox, displayName: 'R17 沙箱', allowUpload: true, allowRename: true, allowDelete: true } })
  const shareId = (await shareRes.json()).share.id

  const pairing = await admin.post('/api/admin/pairing', { headers: H, data: {} })
  const pairToken = (await pairing.json()).token
  device = await request.newContext({ baseURL: base })
  const redeem = await device.post('/api/pair/redeem', { data: { token: pairToken, deviceName: 'R17 手机' } })
  if (!redeem.ok()) throw new Error('配对失败: ' + (await redeem.text()))
  const state = await device.storageState()
  const cookie = state.cookies.map(c => `${c.name}=${c.value}`).join('; ')

  const dlPath = `/api/file/download?shareId=${shareId}&path=${encodeURIComponent('大文件.bin')}`

  // 传输记录（服务端事实）
  const transferState = async () => {
    const r = await admin.get('/api/transfers?limit=50')
    const list = (await r.json()).transfers ?? []
    return list.filter(t => t.direction === 'Download' && t.fileName === '大文件.bin')
  }

  // ---- 发起下载并**中途**掐断 socket ----
  const started = Date.now()
  let received = 0
  let abortedAtMs = 0
  await new Promise((resolve) => {
    const req = http.get({ host: '127.0.0.1', port: httpPort, path: dlPath, headers: { Cookie: cookie } }, (res) => {
      facts.httpStatus = res.statusCode
      facts.contentLength = Number(res.headers['content-length'] || 0)
      res.on('data', (chunk) => {
        received += chunk.length
        if (received >= ABORT_AFTER) { abortedAtMs = Date.now() - started; req.destroy(); }
      })
      res.on('end', () => resolve())
      res.on('error', () => resolve())
      res.on('close', () => resolve())
    })
    req.on('error', () => resolve())
  })

  facts.clientReceived = received
  facts.totalBytes = BIG
  facts.abortAtMs = abortedAtMs
  facts.responseBodyStarted = received > 0
  facts.clientAborted = received > 0 && received < BIG
  // 节流保证：服务端要发完 48MB 需要远长于中止时刻
  facts.serverExpectedFullMs = Math.ceil(BIG / CHUNK) * THROTTLE_MS
  facts.serverResponseCompleted = abortedAtMs >= facts.serverExpectedFullMs

  // ---- 等服务端终态 ----
  let terminal = null
  for (let i = 0; i < 120; i++) {
    const recs = await transferState()
    const last = recs.at(-1)
    if (last && ['Committed', 'Cancelled', 'Failed'].includes(last.state)) { terminal = last; break }
    await new Promise(r => setTimeout(r, 250))
  }
  facts.terminalState = terminal?.state ?? '(无终态)'
  facts.terminalBytes = terminal?.transferredBytes ?? null
  facts.terminalError = terminal?.error ?? null
  facts.requestAbortedObserved = facts.terminalState === 'Cancelled'

  facts.verdict = !facts.responseBodyStarted ? 'HARNESS_ERROR'
    : facts.serverResponseCompleted ? 'INCONCLUSIVE_SERVER_FINISHED_FIRST'
    : (facts.terminalState === 'Cancelled' ? 'PRODUCT_CORRECT' : 'PRODUCT_FAILURE')

  console.log(JSON.stringify(facts, null, 2))
} catch (error) {
  console.log(JSON.stringify({ verdict: 'HARNESS_ERROR', error: String(error?.message ?? error), facts }, null, 2))
} finally {
  if (device) await device.dispose().catch(() => { })
  if (admin) await admin.dispose().catch(() => { })
  child.kill()
  await new Promise(r => setTimeout(r, 900))
  try { child.kill('SIGKILL') } catch { }
  await new Promise(r => setTimeout(r, 400))
  try { fs.rmSync(marker, { force: true }) } catch { }
  try { log.end() } catch { }
  fs.rmSync(root, { recursive: true, force: true })
}
process.exit(0)
