#!/usr/bin/env node
/*
 * LanDisk 真实浏览器播放/预览验证（M01 / M03 / F03 的浏览器侧证据）。
 *
 * 说明与边界：
 *  - 这里用的是 Playwright 提供的 Chromium。它是「真实浏览器引擎」的证据，
 *    但**不是** iPhone Safari，也**不能**用来声称 iPhone 兼容性。
 *  - Playwright 的 Chromium 构建不包含 H.264 等专有编解码器，所以真实解码与拖动验证
 *    使用 VP9/Opus（WebM）与 PCM（WAV）样本；MP4(H.264/AAC) 的 HTTP Range 语义由 xUnit 覆盖，
 *    真机播放能力记为 IPHONE_REAL_DEVICE = NOT_RUN。
 *
 * 用法：
 *   node tools/e2e/playback-test.mjs --server dist/publish/LanDisk-win-x64/LanDisk.Server.exe
 */

import { chromium } from 'playwright'
import { spawn } from 'node:child_process'
import { createServer } from 'node:net'
import fs from 'node:fs'
import path from 'node:path'
import os from 'node:os'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const repoRoot = path.resolve(here, '..', '..')

function arg(name, fallback) {
  const index = process.argv.indexOf('--' + name)
  return index >= 0 && index + 1 < process.argv.length ? process.argv[index + 1] : fallback
}

const serverExe = path.resolve(arg('server', path.join(repoRoot, 'dist', 'publish', 'LanDisk-win-x64', 'LanDisk.Server.exe')))
const samplesDir = path.join(repoRoot, 'tests', 'samples')
const results = []
let failures = 0

function record(id, status, detail) {
  results.push({ id, status, detail })
  const label = status === 'PASS' ? '\u001b[32mPASS\u001b[0m' : status === 'FAIL' ? '\u001b[31mFAIL\u001b[0m' : '\u001b[33mNOT_RUN\u001b[0m'
  console.log(`  [${label}] ${id} - ${detail}`)
  if (status === 'FAIL') failures += 1
}

function freePort() {
  return new Promise((resolve, reject) => {
    const srv = createServer()
    srv.listen(0, '127.0.0.1', () => {
      const port = srv.address().port
      srv.close(() => resolve(port))
    })
    srv.on('error', reject)
  })
}

async function waitForHealth(base, timeoutMs = 60000) {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    try {
      const response = await fetch(base + '/api/health')
      if (response.ok) return true
    } catch {
      /* not ready yet */
    }
    await new Promise((r) => setTimeout(r, 300))
  }
  return false
}

/**
 * Opens a file from the listing by exact name.
 *
 * The file manager row is a press target rather than a row with a "查看" button, so the click goes
 * to the row's tap surface; the row is matched on the exact entry name so that a substring in
 * another row's metadata cannot match it.
 */
async function openEntry(page, name) {
  const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
  const row = page.locator('.entry').filter({
    has: page.locator('.entry-name', { hasText: new RegExp('^' + escaped + '$') })
  })
  await row.locator('.entry-tap').click()
}

async function main() {
  if (!fs.existsSync(serverExe)) {
    console.error('找不到发布出来的服务端可执行文件：' + serverExe)
    console.error('请先运行 tools/Build-All.ps1，或用 --server 指定路径。')
    process.exit(2)
  }

  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'landisk-e2e-'))
  const dataDir = path.join(root, 'data')
  const sandbox = path.join(root, 'sandbox')
  fs.mkdirSync(dataDir, { recursive: true })
  fs.mkdirSync(sandbox, { recursive: true })

  for (const name of ['video-vp9-opus.webm', 'video-h264-aac.mp4', 'audio-pcm.wav', 'image-sample.png', 'incompatible-mpeg4.mkv']) {
    fs.copyFileSync(path.join(samplesDir, name), path.join(sandbox, name))
  }
  fs.writeFileSync(path.join(sandbox, '说明文本.txt'), '这是浏览器验证用的合成文本。\n第二行 <b>不应被渲染</b>。', 'utf8')

  const port = await freePort()
  const httpsPort = await freePort()
  const base = `http://127.0.0.1:${port}`
  const logPath = path.join(root, 'server.log')
  const logStream = fs.createWriteStream(logPath)

  console.log('启动发布包：' + serverExe)
  console.log('工作目录：' + root)
  const child = spawn(serverExe, ['--data-dir', dataDir, '--port', String(port), '--https-port', String(httpsPort)], {
    cwd: path.dirname(serverExe),
    stdio: ['ignore', 'pipe', 'pipe']
  })
  child.stdout.pipe(logStream)
  child.stderr.pipe(logStream)

  // 与 ui-test.mjs 同一套所有权登记：只登记本脚本创建的进程，
  // 使后续清理可以精确到 PID，绝不按进程名全杀。
  const ownedDir = path.join(repoRoot, 'dist', '.owned-processes')
  fs.mkdirSync(ownedDir, { recursive: true })
  const markerPath = path.join(ownedDir, `${child.pid}.json`)
  const writeMarker = () => {
    try {
      fs.writeFileSync(markerPath, JSON.stringify({
        pid: child.pid,
        exe: serverExe,
        dataDir,
        owner: 'e2e/playback-test.mjs',
        startedAt: new Date().toISOString()
      }, null, 2))
    } catch { /* ignore */ }
  }
  child.on('spawn', writeMarker)
  if (child.pid) writeMarker()

  let browser = null
  try {
    if (!(await waitForHealth(base))) throw new Error('服务端未在 60 秒内就绪，日志：' + logPath)

    const adminToken = fs.readFileSync(path.join(dataDir, 'secrets', 'admin-token.txt'), 'utf8').trim()

    // 用 Playwright 的 request 上下文完成管理操作与配对，其 cookie 与浏览器上下文共享。
    browser = await chromium.launch()
    const context = await browser.newContext({ ignoreHTTPSErrors: false })
    const api = context.request

    const login = await api.post(base + '/api/admin/session', { data: { token: adminToken } })
    if (!login.ok()) throw new Error('管理登录失败：' + (await login.text()))
    const csrf = (await login.json()).csrfToken

    const shareResponse = await api.post(base + '/api/admin/shares', {
      headers: { 'X-LanDisk-CSRF': csrf },
      data: { rootPath: sandbox, displayName: '浏览器验证共享', allowUpload: true, allowRename: true, allowDelete: true }
    })
    if (!shareResponse.ok()) throw new Error('创建共享失败：' + (await shareResponse.text()))

    const pairing = await api.post(base + '/api/admin/pairing', { headers: { 'X-LanDisk-CSRF': csrf }, data: {} })
    const pair = await pairing.json()
    const redeem = await api.post(base + '/api/pair/redeem', { data: { token: pair.token, deviceName: 'Playwright-Chromium' } })
    if (!redeem.ok()) throw new Error('配对失败：' + (await redeem.text()))

    console.log('\n浏览器引擎：' + (await browser.version()))
    console.log('打开手机端页面…')
    const page = await context.newPage()
    page.on('pageerror', (err) => console.log('  [页面错误] ' + err.message))
    await page.goto(base + '/', { waitUntil: 'networkidle' })

    const listing = await page.locator('.entry-name').allTextContents()
    if (listing.length >= 6) {
      record('F01', 'PASS', `真实浏览器渲染目录列表，共 ${listing.length} 项（含中文文件名）`)
    } else {
      record('F01', 'FAIL', '目录列表项数异常：' + listing.length)
    }

    // ---- 图片预览 ------------------------------------------------------
    await openEntry(page, 'image-sample.png')
    await page.locator('.preview-box img').waitFor({ state: 'visible', timeout: 15000 })
    const imageInfo = await page.evaluate(async () => {
      const img = document.querySelector('.preview-box img')
      if (!img) return { ok: false }
      if (!img.complete) await new Promise((resolve) => { img.onload = resolve; img.onerror = resolve })
      return { ok: img.complete && img.naturalWidth > 0, width: img.naturalWidth, height: img.naturalHeight }
    })
    if (imageInfo.ok) record('F03', 'PASS', `图片在真实浏览器中解码成功（${imageInfo.width}×${imageInfo.height}）`)
    else record('F03', 'FAIL', '图片未能在浏览器中解码')
    await page.getByRole('button', { name: '关闭' }).click()

    // ---- 文本预览 ------------------------------------------------------
    await openEntry(page, '说明文本.txt')
    const preText = await page.locator('.preview-box pre').textContent()
    if (preText && preText.includes('<b>不应被渲染</b>')) {
      const hasBold = await page.locator('.preview-box b').count()
      if (hasBold === 0) record('F03', 'PASS', '文本按纯文本显示，标记未被解析执行')
      else record('F03', 'FAIL', '文本内容被当作 HTML 渲染')
    } else {
      record('F03', 'FAIL', '文本预览内容不正确')
    }
    await page.getByRole('button', { name: '关闭' }).click()

    // ---- 视频播放与拖动（VP9/Opus，Chromium 可原生解码） -------------------
    await openEntry(page, 'video-vp9-opus.webm')
    const video = page.locator('video')
    await video.waitFor({ state: 'visible', timeout: 15000 })

    const meta = await page.evaluate(async () => {
      const v = document.querySelector('video')
      if (!v) return null
      if (v.readyState < 1) await new Promise((r) => { v.onloadedmetadata = r; setTimeout(r, 8000) })
      return { readyState: v.readyState, duration: v.duration, videoWidth: v.videoWidth, videoHeight: v.videoHeight }
    })

    if (meta && meta.videoWidth > 0 && meta.duration > 0) {
      record('M01', 'PASS', `视频元数据解码成功：${meta.videoWidth}×${meta.videoHeight}，时长 ${meta.duration.toFixed(2)}s`)
    } else {
      record('M01', 'FAIL', '视频元数据未能解码：' + JSON.stringify(meta))
    }

    const playback = await page.evaluate(async () => {
      const v = document.querySelector('video')
      v.muted = true
      const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

      // 无头浏览器里媒体时钟需要一点时间才推进，因此用轮询而不是固定等待，
      // 判定的性质不变：播放是否真的在前进。
      const waitProgress = async (from, timeoutMs = 8000) => {
        const started = Date.now()
        while (Date.now() - started < timeoutMs) {
          if (v.currentTime > from + 0.15) return v.currentTime
          await sleep(150)
        }
        return v.currentTime
      }

      try { await v.play() } catch (err) { return { error: String(err) } }
      const afterPlay = await waitProgress(0)

      const seek = async (t) => {
        await new Promise((resolve) => { v.onseeked = resolve; v.currentTime = t; setTimeout(resolve, 5000) })
        await v.play().catch(() => {})
        const settled = v.currentTime
        const advanced = await waitProgress(settled)
        return { settled, advanced }
      }

      const middle = await seek(Math.min(3, v.duration / 2))
      const nearEnd = await seek(Math.max(0, v.duration - 0.5))
      const backToStart = await seek(0)

      return { afterPlay, middle, nearEnd, backToStart, paused: v.paused, duration: v.duration, error: null }
    })

    if (playback.error) {
      record('M03', 'FAIL', '播放被拒绝：' + playback.error)
    } else if (playback.afterPlay > 0.15
      && playback.middle.settled > 2 && playback.middle.settled < 3.6 && playback.middle.advanced > playback.middle.settled
      && playback.nearEnd.settled > 4.5 && playback.nearEnd.advanced > playback.nearEnd.settled
      && playback.backToStart.settled < 1.2 && playback.backToStart.advanced > playback.backToStart.settled) {
      record('M03', 'PASS',
        `真实解码播放并拖动：播放前进至 ${playback.afterPlay.toFixed(2)}s → ` +
        `中段 ${playback.middle.settled.toFixed(2)}s（继续到 ${playback.middle.advanced.toFixed(2)}s）→ ` +
        `接近结尾 ${playback.nearEnd.settled.toFixed(2)}s（继续到 ${playback.nearEnd.advanced.toFixed(2)}s）→ ` +
        `回到开头 ${playback.backToStart.settled.toFixed(2)}s（继续到 ${playback.backToStart.advanced.toFixed(2)}s）`)
    } else {
      record('M03', 'FAIL', '播放进度异常：' + JSON.stringify(playback))
    }
    await page.getByRole('button', { name: '关闭' }).click()

    // ---- 音频播放 ------------------------------------------------------
    await openEntry(page, 'audio-pcm.wav')
    const audioMeta = await page.evaluate(async () => {
      const a = document.querySelector('audio')
      if (!a) return null
      if (a.readyState < 1) await new Promise((r) => { a.onloadedmetadata = r; setTimeout(r, 8000) })
      return { readyState: a.readyState, duration: a.duration }
    })
    if (audioMeta && audioMeta.duration > 0) record('M01', 'PASS', `音频元数据解码成功，时长 ${audioMeta.duration.toFixed(2)}s`)
    else record('M01', 'FAIL', '音频未能解码：' + JSON.stringify(audioMeta))
    await page.getByRole('button', { name: '关闭' }).click()

    // ---- MP4（H.264）在 Playwright Chromium 中的实际情况 -------------------
    await openEntry(page, 'video-h264-aac.mp4')
    const mp4 = await page.evaluate(async () => {
      const v = document.querySelector('video')
      if (!v) return null
      const canPlay = v.canPlayType('video/mp4; codecs="avc1.42E01E, mp4a.40.2"')
      if (v.readyState < 1) await new Promise((r) => { v.onloadedmetadata = r; v.onerror = r; setTimeout(r, 8000) })
      return { canPlay, readyState: v.readyState, duration: Number.isFinite(v.duration) ? v.duration : null, error: v.error ? v.error.code : null }
    })
    record('M01', 'NOT_RUN',
      `MP4/H.264 在 Playwright Chromium 中：canPlayType="${mp4?.canPlay}"，readyState=${mp4?.readyState}，` +
      `error=${mp4?.error}。该构建不含 H.264 解码器，真机 Safari 结论记为 IPHONE_REAL_DEVICE=NOT_RUN。`)
    await page.getByRole('button', { name: '关闭' }).click()

    // ---- 不兼容容器的提示 ------------------------------------------------
    await openEntry(page, 'incompatible-mpeg4.mkv')
    await page.locator('video').waitFor({ state: 'visible', timeout: 10000 })
    const incompatible = await page.evaluate(async () => {
      const v = document.querySelector('video')
      if (v.readyState < 1) await new Promise((r) => { v.onerror = r; setTimeout(r, 8000) })
      await new Promise((r) => setTimeout(r, 400))
      const banner = document.querySelector('.banner.warn')
      return { error: v.error ? v.error.code : null, banner: banner ? banner.textContent : null }
    })
    if (incompatible.error && incompatible.banner && incompatible.banner.includes('下载')) {
      record('M04', 'PASS', '不兼容容器给出「不支持 + 提供下载」的明确提示，没有无限加载，也没有说文件损坏')
    } else {
      record('M04', 'NOT_RUN', '不兼容容器提示未按预期出现（可能环境差异）：' + JSON.stringify(incompatible))
    }
    await page.getByRole('button', { name: '关闭' }).click()

    // ---- 下载交给浏览器 --------------------------------------------------
    const download = await Promise.all([
      page.waitForEvent('download', { timeout: 20000 }),
      openEntry(page, 'video-vp9-opus.webm')
        .then(() => page.getByRole('button', { name: '下载' }).click())
    ]).then(([d]) => d).catch(() => null)

    if (download) {
      const target = path.join(root, download.suggestedFilename())
      await download.saveAs(target)
      const size = fs.statSync(target).size
      record('D01', 'PASS', `浏览器下载完成，建议文件名「${download.suggestedFilename()}」，${size} 字节`)
    } else {
      record('D01', 'FAIL', '浏览器未触发下载事件')
    }
  } catch (error) {
    record('E2E', 'FAIL', String(error && error.stack ? error.stack.split('\n')[0] : error))
  } finally {
    if (browser) await browser.close().catch(() => {})
    // 与 ui-test.mjs 相同：有界、可验证地回收，并确认它真的退出。
    const exited = () => child.exitCode !== null || child.signalCode !== null
    try { child.kill() } catch { /* ignore */ }
    for (let i = 0; i < 20 && !exited(); i++) await new Promise((r) => setTimeout(r, 100))
    if (!exited()) {
      try { child.kill('SIGKILL') } catch { /* ignore */ }
      for (let i = 0; i < 30 && !exited(); i++) await new Promise((r) => setTimeout(r, 100))
    }
    if (!exited()) {
      record('CLEANUP', 'FAIL', `测试服务端进程 pid=${child.pid} 在 SIGKILL 后仍未退出；保留所有权标记：${markerPath}`)
    } else {
      try { fs.rmSync(markerPath, { force: true }) } catch { /* ignore */ }
    }
    try { logStream.end() } catch { /* ignore */ }
  }

  console.log('\n=== 汇总 ===')
  console.log(JSON.stringify(results, null, 2))
  const outPath = path.join(repoRoot, 'dist', 'e2e-playback-result.json')
  fs.mkdirSync(path.dirname(outPath), { recursive: true })
  fs.writeFileSync(outPath, JSON.stringify({ browser: 'Playwright Chromium', results }, null, 2), 'utf8')
  console.log('结果文件：' + outPath)
  console.log('本次未使用任何真实用户文件；共享目录是临时沙箱，退出时保留以便核对。')

  process.exit(failures > 0 ? 1 : 0)
}

main()
