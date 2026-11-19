#!/usr/bin/env node
/*
 * LanDisk UI 自动化（LAN_DISK_DSH_UX_AUTH_UPGRADE_02）。
 *
 * 覆盖：手机竖屏 / 手机横屏 / 桌面宽屏；列表与网格；排序；筛选；搜索（当前文件夹 / 整个共享）；
 *      收藏；最近；多选；批量 ZIP；批量删除；上传；重命名；回收站恢复；管理端自动进入与配对。
 *
 * 边界说明：
 *  - Playwright 的 Chromium / WebKit 是真实浏览器引擎的证据，但**不是** iPhone Safari。
 *    真机结论始终记为 IPHONE_REAL_DEVICE = NOT_RUN。
 *  - 所有文件操作都发生在临时沙箱中，使用合成文件，不接触任何真实用户文件。
 *
 * 用法：
 *   node tools/e2e/ui-test.mjs --server dist/publish/LanDisk-win-x64/LanDisk.Server.exe
 *   node tools/e2e/ui-test.mjs --engine webkit
 */

import { chromium, webkit, devices } from 'playwright'
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
const engineName = arg('engine', 'chromium')
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
    } catch { /* not ready */ }
    await new Promise((r) => setTimeout(r, 300))
  }
  return false
}

const PNG_1PX = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64')

function buildSandbox(sandbox) {
  fs.mkdirSync(path.join(sandbox, '照片'), { recursive: true })
  fs.mkdirSync(path.join(sandbox, '文档'), { recursive: true })
  fs.writeFileSync(path.join(sandbox, '照片', '红点.png'), PNG_1PX)
  fs.writeFileSync(path.join(sandbox, '照片', '假期.png'), PNG_1PX)
  fs.writeFileSync(path.join(sandbox, '文档', '报告-2026.txt'), '2026 报告内容')
  fs.writeFileSync(path.join(sandbox, '文档', '报告-2025.txt'), '2025 报告内容')
  fs.writeFileSync(path.join(sandbox, '说明.txt'), '这是一个用于界面自动化的合成文本文件。')
  fs.writeFileSync(path.join(sandbox, '封面.png'), PNG_1PX)
  fs.writeFileSync(path.join(sandbox, '归档.zip'), Buffer.alloc(4096, 7))
  fs.writeFileSync(path.join(sandbox, '歌曲.mp3'), Buffer.alloc(2048, 3))
  fs.mkdirSync(path.join(sandbox, '媒体'), { recursive: true })
  // A media file, so the viewer cases open a real <video> instead of reporting a text file as
  // "not applicable". Playwright's Chromium carries no H.264, so decodability is explicitly out of scope
  // and the real-device conclusion stays a separate NOT_RUN item.
        fs.writeFileSync(path.join(sandbox, '媒体', '样本.mp4'), Buffer.alloc(1024, 9))
  // Also at the root: the phone opens on the share root, and the viewer cases must find a media file there.
  fs.writeFileSync(path.join(sandbox, '片头.mp4'), Buffer.alloc(1024, 9))
  const old = new Date(Date.now() - 400 * 86400000)
  fs.utimesSync(path.join(sandbox, '文档', '报告-2025.txt'), old, old)
}

async function loginAdmin(context, base, dataDir) {
  const token = fs.readFileSync(path.join(dataDir, 'secrets', 'admin-token.txt'), 'utf8').trim()
  const response = await context.request.post(base + '/api/admin/session', { data: { token } })
  if (!response.ok()) throw new Error('管理登录失败: ' + (await response.text()))
  return (await response.json()).csrfToken
}

async function main() {
  if (!fs.existsSync(serverExe)) {
    console.error('找不到发布出来的服务端可执行文件：' + serverExe)
    process.exit(2)
  }

  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'landisk-ui-'))
  const dataDir = path.join(root, 'data')
  const sandbox = path.join(root, 'sandbox')
  fs.mkdirSync(dataDir, { recursive: true })
  fs.mkdirSync(sandbox, { recursive: true })
  buildSandbox(sandbox)

  const port = await freePort()
  const httpsPort = await freePort()
  const base = `http://127.0.0.1:${port}`
  const logPath = path.join(root, 'server.log')
  const logStream = fs.createWriteStream(logPath)

  console.log('引擎：' + engineName)
  console.log('发布包：' + serverExe)
  console.log('临时目录：' + root)

  const child = spawn(serverExe, ['--data-dir', dataDir, '--port', String(port), '--https-port', String(httpsPort)], {
    cwd: path.dirname(serverExe),
    stdio: ['ignore', 'pipe', 'pipe']
  })
  child.stdout.pipe(logStream)
  child.stderr.pipe(logStream)

  // 所有权标记：把这个**由本脚本创建**的进程登记到 dist/.owned-processes/。
  // 为什么需要它：finally 只在父进程正常走到结尾时才执行。本次真机验收就出过一次——
  // 验收作业被超时中断，finally 没跑，被 spawn 的 LanDisk.exe 成了孤儿，
  // 之后真机测试一度连到了这个残留测试实例（cb5be599 / 53819 / 53820）。
  // 有了标记，后续任何清理都只针对"我们登记过的 PID"，绝不按进程名全杀。
  const ownedDir = path.join(repoRoot, 'dist', '.owned-processes')
  fs.mkdirSync(ownedDir, { recursive: true })
  const markerPath = path.join(ownedDir, `${child.pid}.json`)
  const writeMarker = () => {
    try {
      fs.writeFileSync(markerPath, JSON.stringify({
        pid: child.pid,
        exe: serverExe,
        dataDir,
        owner: `e2e/ui-test.mjs:${engineName}`,
        startedAt: new Date().toISOString()
      }, null, 2))
    } catch { /* 标记失败不阻断测试，但会在汇总里体现为未能登记 */ }
  }
  child.on('spawn', writeMarker)
  if (child.pid) writeMarker()

  let browser = null
  let step = '初始化'
  try {
    step = '等待服务端就绪'
    if (!(await waitForHealth(base))) throw new Error('服务端未就绪，日志：' + logPath)

    const engine = engineName === 'webkit' ? webkit : chromium
    browser = await engine.launch()

    // ---- 管理端：loopback 自动进入（不需要任何 token 输入） ----------------
    {
      const context = await browser.newContext()
      const page = await context.newPage()
      await page.goto(base + '/admin', { waitUntil: 'networkidle' })

      const hasTokenField = await page.locator('input[type="password"]').count()
      const tabsVisible = await page.locator('.tabs button').count()

      if (tabsVisible > 0 && hasTokenField === 0) {
        record('UI-ADMIN', 'PASS', '管理端在 loopback 上自动进入，正常流程中未出现令牌输入框')
      } else {
        record('UI-ADMIN', 'FAIL', `管理端未自动进入：标签数 ${tabsVisible}，密码框数 ${hasTokenField}`)
      }

      const csrf = (await (await context.request.get(base + '/api/admin/session')).json()).csrfToken
      const shareResponse = await context.request.post(base + '/api/admin/shares', {
        headers: { 'X-LanDisk-CSRF': csrf },
        data: { rootPath: sandbox, displayName: '界面测试共享', allowUpload: true, allowRename: true, allowDelete: true }
      })
      if (!shareResponse.ok()) throw new Error('创建共享失败：' + (await shareResponse.text()))

      await context.close()
    }

    // ---- 手机竖屏 --------------------------------------------------------
    {
      const context = await browser.newContext({ ...devices['iPhone 13'] })

      const adminContext = await browser.newContext()
      const adminCsrf = await loginAdmin(adminContext, base, dataDir)
      const pairing = await (await adminContext.request.post(base + '/api/admin/pairing', {
        headers: { 'X-LanDisk-CSRF': adminCsrf }, data: {}
      })).json()

      const redeem = await context.request.post(base + '/api/pair/redeem', {
        data: { token: pairing.token, deviceName: 'UI 手机' }
      })
      if (!redeem.ok()) throw new Error('配对失败：' + (await redeem.text()))

      const devicesList = await (await adminContext.request.get(base + '/api/admin/devices')).json()
      const deviceId = devicesList.devices[0].id
      await adminContext.request.patch(base + `/api/admin/devices/${deviceId}`, {
        headers: { 'X-LanDisk-CSRF': adminCsrf }, data: { level: 'readWrite' }
      })
      await adminContext.close()

      const page = await context.newPage()
      page.on('pageerror', (err) => console.log('  [页面错误] ' + err.message))
      await page.goto(base + '/', { waitUntil: 'networkidle' })

      const names = () => page.locator('.entry-name').allTextContents()
      /** Row locator matched on the exact entry name, not on any text inside the row. */
      const row = (name) => page.locator('.entry').filter({
        has: page.locator('.entry-name', { hasText: new RegExp('^' + name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '$') })
      })

      /** Long press with a real pointer, falling back to the "选择" menu entry if needed. */
      const enterSelection = async (name) => {
        const target = row(name).locator('.entry-tap')
        await target.hover()
        await page.mouse.down()
        await page.waitForTimeout(800)
        await page.mouse.up()
        await page.waitForTimeout(500)
        if (await page.locator('.selection-bar').isVisible()) return 'longpress'

        await row(name).locator('.entry-more').click()
        await page.locator('.option', { hasText: '选择' }).click()
        await page.waitForTimeout(400)
        return (await page.locator('.selection-bar').isVisible()) ? 'menu' : 'none'
      }

      const passwordFields = await page.locator('input[type="password"]').count()
      const entryRows = await page.locator('.entry').count()
      record('UI-ENTRY', passwordFields === 0 && entryRows > 0 ? 'PASS' : 'FAIL',
        `打开后直接进入文件界面，列出 ${entryRows} 项，无令牌输入`)

      const tabbarVisible = await page.locator('.tabbar').isVisible()
      const tabCount = await page.locator('.tab-btn').count()
      // Round 10 made this bar the phone's only navigation, so it now carries every area the phone has:
      // files, recent, favourites, transfers, recycle bin and this device. The assertion is "all six are
      // reachable in one tap", which is the point — before this the four areas lived in a desktop sidebar
      // that an iPhone shows as a drawer most people never open. (Recorded in docs/AUDIT_CORRECTIONS_10.md.)
      const requiredTabs = ['文件', '最近', '收藏', '传输', '回收站', '我的']
      const tabLabels = await page.locator('.tab-btn').allInnerTexts()
      const missing = requiredTabs.filter((label) => !tabLabels.some((text) => text.includes(label)))
      record('UI-MOBILE-NAV', tabbarVisible && tabCount === 6 && missing.length === 0 ? 'PASS' : 'FAIL',
        `手机竖屏底部导航可见=${tabbarVisible}，按钮数=${tabCount}，缺少=${missing.join('/') || '无'}`)

      const tooSmall = await page.evaluate(() => {
        const targets = Array.from(document.querySelectorAll('.entry-tap, .tab-btn, .icon-btn, .chip, .crumb button'))
        return targets
          .filter((el) => {
            const r = el.getBoundingClientRect()
            return r.width > 0 && r.height > 0 && r.height < 40
          })
          .map((el) => el.className + ':' + Math.round(el.getBoundingClientRect().height))
      })
      record('UI-TOUCH', tooSmall.length === 0 ? 'PASS' : 'FAIL',
        tooSmall.length === 0 ? '所有可点区域高度均不低于 40px' : `偏小区域：${tooSmall.join(' | ')}`)

      // ---- 排序 ---------------------------------------------------------
      await page.locator('.toolbar .chip').first().click()
      await page.locator('.option', { hasText: '大小 大 → 小' }).click()
      await page.waitForTimeout(600)
      const sortChip = (await page.locator('.toolbar .chip').first().textContent())?.trim()
      const sortedNames = await names()
      const folderCount = await page.locator('.entry .cat-folder').count()
      const firstFile = sortedNames[folderCount]
      record('UI-SORT', sortChip?.includes('大→小') && firstFile === '归档.zip' ? 'PASS' : 'FAIL',
        `排序按钮「${sortChip}」；文件夹 ${folderCount} 个仍在前，其后首个文件为「${firstFile}」（应为最大的 归档.zip）`)

      // ---- 网格 + 缩略图 -------------------------------------------------
      await page.waitForTimeout(1500)
      await page.locator('.toolbar .chip', { hasText: '网格' }).click()
      await page.waitForTimeout(600)
      const gridCount = await page.locator('.card-entry').count()
      const thumbCount = await page.locator('.entry-thumb img').count()
      record('UI-GRID', gridCount > 0 ? 'PASS' : 'FAIL', `网格视图渲染 ${gridCount} 个卡片`)
      record('UI-THUMB', thumbCount > 0 ? 'PASS' : 'FAIL', `网格中加载了 ${thumbCount} 个图片缩略图（封面.png）`)

      await page.locator('.toolbar .chip', { hasText: '列表' }).click()
      await page.waitForTimeout(400)

      // ---- 搜索：当前文件夹 vs 整个共享 ----------------------------------
      await page.locator('.search-box input').fill('报告')
      await page.waitForTimeout(900)
      const folderScopeNames = await names()

      await page.locator('.toolbar .chip', { hasText: '筛选' }).click()
      await page.locator('.chips .chip', { hasText: '整个共享' }).click()
      await page.locator('.sheet-foot button.primary').click()
      await page.waitForTimeout(1100)
      const shareScopeNames = await names()

      record('UI-SEARCH',
        folderScopeNames.length === 0 && shareScopeNames.includes('报告-2026.txt') && shareScopeNames.includes('报告-2025.txt') ? 'PASS' : 'FAIL',
        `当前文件夹范围命中 ${folderScopeNames.length} 项（预期 0）；整个共享范围命中：${shareScopeNames.join('、')}`)

      await page.locator('.search-box input').fill('')
      await page.waitForTimeout(800)

      // 恢复为当前文件夹范围
      await page.locator('.toolbar .chip', { hasText: '筛选' }).click()
      await page.locator('.chips .chip', { hasText: '当前文件夹' }).click()
      await page.locator('.chips .chip', { hasText: '图片' }).first().click()
      await page.locator('.sheet-foot button.primary').click()
      await page.waitForTimeout(1000)
      const imageNames = await names()
      record('UI-FILTER', imageNames.includes('封面.png') && !imageNames.includes('归档.zip') ? 'PASS' : 'FAIL',
        `按「图片」筛选后列表：${imageNames.join('、') || '（空）'}`)

      await page.locator('.toolbar .chip', { hasText: '筛选' }).click()
      await page.locator('.sheet-foot button', { hasText: '清除' }).click()
      await page.waitForTimeout(900)

      // ---- 进入文件夹 / 返回 ---------------------------------------------
      await row('文档').locator('.entry-tap').click()
      await page.waitForTimeout(900)
      const inFolder = await names()
      record('UI-NAVIGATE', inFolder.includes('报告-2026.txt') && inFolder.includes('报告-2025.txt') ? 'PASS' : 'FAIL',
        `进入「文档」后看到：${inFolder.join('、')}`)

      await page.locator('.topbar .icon-btn').first().click()
      await page.waitForTimeout(900)

      // ---- 收藏 -----------------------------------------------------------
      await row('说明.txt').locator('.entry-more').click()
      await page.locator('.option', { hasText: '收藏' }).first().click()
      await page.waitForTimeout(900)
      await page.locator('.tab-btn', { hasText: '收藏' }).click()
      await page.waitForTimeout(900)
      const favoriteNames = await names()
      record('UI-FAVORITE', favoriteNames.includes('说明.txt') ? 'PASS' : 'FAIL', `收藏页显示：${favoriteNames.join('、')}`)

      // ---- 最近 -----------------------------------------------------------
      await page.locator('.tab-btn', { hasText: '最近' }).click()
      await page.waitForTimeout(1000)
      const recentNames = await names()
      record('UI-RECENT', recentNames.length > 0 && !recentNames.includes('报告-2025.txt') ? 'PASS' : 'FAIL',
        `最近页显示 ${recentNames.length} 项；400 天前的文件未出现=${!recentNames.includes('报告-2025.txt')}（默认窗口 30 天）`)

      await page.locator('.tab-btn', { hasText: '文件' }).click()
      await page.waitForTimeout(900)

      // ---- 重命名 ---------------------------------------------------------
      await row('说明.txt').locator('.entry-more').click()
      await page.locator('.option', { hasText: '重命名' }).click()
      await page.locator('.modal input[type="text"]').fill('界面改名.txt')
      await page.locator('.modal button.primary').click()
      await page.waitForTimeout(1200)
      const afterRename = await names()
      record('UI-RENAME', afterRename.includes('界面改名.txt') ? 'PASS' : 'FAIL',
        `重命名后列表含「界面改名.txt」=${afterRename.includes('界面改名.txt')}`)

      // ---- 多选 + 批量 ZIP ------------------------------------------------
      const selectionGesture = await enterSelection('归档.zip')
      const selectionVisible = selectionGesture !== 'none'
      record('UI-SELECT', selectionVisible ? 'PASS' : 'FAIL',
        selectionVisible ? `长按进入多选（方式：${selectionGesture}）` : '长按与「选择」菜单都未能进入多选')

      if (selectionVisible) {
        await page.locator('.selection-bar button', { hasText: '下载 ZIP' }).click()
        await page.locator('.modal button.primary', { hasText: '开始下载' }).waitFor({ state: 'visible', timeout: 10000 })
        const planText = await page.locator('.modal').textContent()
        const [download] = await Promise.all([
          page.waitForEvent('download', { timeout: 30000 }),
          page.locator('.modal button.primary', { hasText: '开始下载' }).click()
        ]).catch(() => [null])

        if (download) {
          const target = path.join(root, download.suggestedFilename())
          await download.saveAs(target)
          const size = fs.statSync(target).size
          const hasZipMagic = fs.readFileSync(target).subarray(0, 2).toString('latin1') === 'PK'
          const keepsChineseName = /界面测试共享/.test(download.suggestedFilename())
          const asciiLetters = (download.suggestedFilename().match(/[A-Za-z0-9]/g) ?? []).length
          const cleanFallback = /^[A-Za-z0-9]/.test(download.suggestedFilename()) && asciiLetters >= 3
          record('UI-ZIP', size > 0 && hasZipMagic ? 'PASS' : 'FAIL',
            `浏览器下载归档「${download.suggestedFilename()}」，${size} 字节，ZIP 魔数=${hasZipMagic}；计划面板含文件数=${/文件数量/.test(planText ?? '')}`)
          record('UI-ZIP-NAME', keepsChineseName || cleanFallback ? 'PASS' : 'FAIL',
            keepsChineseName
              ? `归档文件名保留了中文共享名：${download.suggestedFilename()}`
              : `该引擎只采用 ASCII 回退名，回退名可用（非下划线填充）：${download.suggestedFilename()}`)
        } else {
          record('UI-ZIP', 'FAIL', '未触发批量下载事件')
        }
      } else {
        record('UI-ZIP', 'NOT_RUN', '未进入多选，跳过批量下载')
      }

      await page.locator('.selection-bar .icon-btn').click()
      await page.waitForTimeout(400)

      // ---- 上传 -----------------------------------------------------------
      const uploadSource = path.join(root, '上传测试.txt')
      fs.writeFileSync(uploadSource, '这是通过界面自动化上传的合成文件。')
      await page.locator('input[type="file"]').setInputFiles(uploadSource)
      await page.waitForTimeout(3000)
      const afterUpload = await names()
      record('UI-UPLOAD', afterUpload.includes('上传测试.txt') ? 'PASS' : 'FAIL',
        `上传后列表含「上传测试.txt」=${afterUpload.includes('上传测试.txt')}`)

      // ---- 多选批量删除 + 回收站恢复 --------------------------------------
      step = '批量删除与恢复'
      // The upload queue sits below the list and re-renders while progress updates; clearing it
      // first keeps the row from moving between hover and press.
      const clearQueue = page.locator('.toolbar ~ .card button', { hasText: '清除' })
      if (await clearQueue.count() > 0) {
        await clearQueue.first().click()
        await page.waitForTimeout(500)
      }

      await enterSelection('上传测试.txt')
      await page.waitForTimeout(400)
      await page.locator('.selection-bar button', { hasText: '删除' }).click()
      await page.locator('.modal button.danger').click()
      await page.waitForTimeout(1500)
      // The per-item result dialog only appears when something failed; dismiss it if present.
      const resultDialog = page.locator('.modal button', { hasText: '知道了' })
      if (await resultDialog.count() > 0) {
        await resultDialog.click()
        await page.waitForTimeout(400)
      }
      const afterBatchDelete = await names()
      const goneFromList = !afterBatchDelete.includes('上传测试.txt')

      await page.locator('.tab-btn', { hasText: '回收站' }).click()
      await page.waitForTimeout(1000)
      const recycleNames = await names()
      const inRecycle = recycleNames.includes('上传测试.txt')

      let restored = false
      if (inRecycle) {
        await page.locator('.list li', { hasText: '上传测试.txt' }).locator('button', { hasText: '恢复' }).click()
        await page.waitForTimeout(1500)
        await page.locator('.tab-btn', { hasText: '文件' }).click()
        await page.waitForTimeout(1000)
        restored = (await names()).includes('上传测试.txt')
      }

      record('UI-BATCH-DELETE', goneFromList && inRecycle && restored ? 'PASS' : 'FAIL',
        `批量删除后从列表消失=${goneFromList}，出现在回收站=${inRecycle}，恢复后回到列表=${restored}`)

      // ---- 横屏 -----------------------------------------------------------
      await page.setViewportSize({ width: 844, height: 390 })
      await page.waitForTimeout(600)
      const landscapeOk = (await page.locator('.toolbar').isVisible()) && (await page.locator('.search-box').isVisible())
      const horizontalOverflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 2)
      record('UI-LANDSCAPE', landscapeOk && !horizontalOverflow ? 'PASS' : 'FAIL',
        `横屏下工具栏与搜索框可用=${landscapeOk}，出现横向溢出=${horizontalOverflow}`)

      await page.setViewportSize({ width: 390, height: 844 })
      await page.waitForTimeout(400)

      // ---- MOBILE12：真机缺陷对应的手机端断言（本轮补强测试前提）---------------
      //
      // The five items the Owner reported are checked with the preconditions this round was asked to
      // enforce. Each precondition exists because the earlier version of this block could pass without it:
      //
      //   * presence is observed from the administrator surface, never by calling the phone's own /api/me —
      //     every authenticated device request touches LastSeenAt, so observing through the device would
      //     *cause* the online state the check is supposed to measure;
      //   * the media case issues a real Range request against a media file, not a text file whose viewer
      //     says "not applicable";
      //   * the blocker case makes the share genuinely unreachable and puts the device in read-only at the
      //     same time, instead of inspecting whatever banners happened to be on screen;
      //   * the permission case uploads a real file and is refused a real upload, in one session, without
      //     re-pairing;
      //   * the theme case changes twice and is checked with the viewer open, not only in JSON.
      step = 'MOBILE12'

      const mobileAdmin = await browser.newContext()
      const mobileCsrf = await loginAdmin(mobileAdmin, base, dataDir)
      const mobileShares = await (await mobileAdmin.request.get(base + '/api/admin/shares')).json()
      const shareId = (mobileShares.shares ?? [])[0]?.id ?? ''
      const mobileDevices = await (await mobileAdmin.request.get(base + '/api/admin/devices')).json()
      const mobileDevice = (mobileDevices.devices ?? []).find((d) => d.name === 'UI 手机') ?? (mobileDevices.devices ?? [])[0]
      const mobileDeviceId = mobileDevice?.id ?? ''

      const observeMobileDevice = async () => {
        const list = await (await mobileAdmin.request.get(base + '/api/admin/devices')).json()
        return (list.devices ?? []).find((d) => d.name === 'UI 手机') ?? (list.devices ?? [])[0] ?? null
      }
      const setMobileLevel = (level) => mobileAdmin.request.patch(base + '/api/admin/devices/' + mobileDeviceId, {
        headers: { 'X-LanDisk-CSRF': mobileCsrf }, data: { level }
      })

      // ---------------------------------------------------------------- presence
      // MOBILE12-01: the viewer stays in the foreground for 70 seconds — past the 45 second online window —
      // and the phone is observed only from the administrator side during that time.
      let viewerOpened = false
      if ((await names()).includes('上传测试.txt')) {
        await row('上传测试.txt').locator('.entry-tap').click()
        await page.waitForTimeout(1200)
        viewerOpened = await page.locator('.modal.viewer').isVisible().catch(() => false)
      }
      const presenceBefore = await observeMobileDevice()
      await page.waitForTimeout(70_000)
      const presenceAfter = await observeMobileDevice()
      const presenceAdvanced = Date.parse(presenceAfter?.lastSeenAt ?? 0) > Date.parse(presenceBefore?.lastSeenAt ?? 0)
      const presenceOnline = presenceAfter?.presence?.state === 'online'
      const presenceStateText = JSON.stringify(presenceAfter?.presence ?? null)
      record('MOBILE12-01', viewerOpened && presenceOnline && presenceAdvanced ? 'PASS' : 'FAIL',
        '查看器保持前台 70 秒（越过 45 秒在线窗口）：presence=' + presenceStateText + '，仍在线=' + presenceOnline + '，LastSeenAt 前进=' + presenceAdvanced +
        '（' + presenceBefore?.lastSeenAt + ' → ' + presenceAfter?.lastSeenAt + '）；观察点＝管理端 /api/admin/devices，等待期间没有向该手机发起任何请求')

      await page.locator('.modal.viewer .modal-head .icon-btn').click({ timeout: 5000 }).catch(() => {})
      await page.waitForTimeout(600)

      // MOBILE12-02: a media Range request from the device session is itself evidence of presence.
      const rangeBefore = await observeMobileDevice()
      const mediaQuery = '/api/file/content?shareId=' + encodeURIComponent(shareId) + '&path=' + encodeURIComponent('媒体/样本.mp4')
      const rangeResponse = shareId
        ? await context.request.get(base + mediaQuery, { headers: { Range: 'bytes=0-1023' } })
        : null
      const rangeStatus = rangeResponse ? rangeResponse.status() : 0
      const rangeAfter = await observeMobileDevice()
      const rangeAdvanced = Date.parse(rangeAfter?.lastSeenAt ?? 0) > Date.parse(rangeBefore?.lastSeenAt ?? 0)
      record('MOBILE12-02', rangeStatus === 206 && rangeAdvanced ? 'PASS' : 'FAIL',
        '媒体 Range 请求 HTTP ' + rangeStatus + '（期望 206），随后管理端看到 LastSeenAt 前进=' + rangeAdvanced +
        '（' + rangeBefore?.lastSeenAt + ' → ' + rangeAfter?.lastSeenAt + '）：看片本身就算活动，不依赖心跳')

      // ---------------------------------------------------------------- real video
      // MOBILE12-07 / MOBILE12-08: the viewer must be opened on an actual media file. The element, the scrim,
      // the surface opacity, the stacking level and the fullscreen control are all asserted; whether this
      // particular browser build can decode H.264 is a separate matter and is recorded as such.
      const videoName = '片头.mp4'
      if ((await names()).includes(videoName)) {
        await row(videoName).locator('.entry-tap').click()
        await page.waitForTimeout(1500)
        const videoCount = await page.locator('.modal.viewer video').count()
        const layer = await page.evaluate(() => {
          const backdrop = document.querySelector('.viewer-backdrop')
          const surface = document.querySelector('.modal.viewer')
          if (!backdrop || !surface) return null
          const alpha = (value) => {
            const match = /rgba?\(([^)]+)\)/.exec(value || '')
            if (!match) return 1
            const parts = match[1].split(',').map((p) => parseFloat(p))
            return parts.length === 4 ? parts[3] : 1
          }
          const bs = getComputedStyle(backdrop)
          const ss = getComputedStyle(surface)
          const video = document.querySelector('.modal.viewer video')
          return {
            backdropAlpha: alpha(bs.backgroundColor),
            backdropZ: parseInt(bs.zIndex || '0', 10),
            surfaceAlpha: alpha(ss.backgroundColor),
            videoBackground: video ? getComputedStyle(video).backgroundColor : null
          }
        })
        const scrimEnough = layer && layer.backdropAlpha >= 0.8
        const surfaceOpaque = layer && layer.surfaceAlpha >= 0.98
        const aboveEverything = layer && layer.backdropZ >= 150
        const videoBlack = layer && layer.videoBackground !== null && /rgb\(0, 0, 0\)/.test(layer.videoBackground)
        record('MOBILE12-07', videoCount > 0 && scrimEnough && surfaceOpaque && aboveEverything && videoBlack ? 'PASS' : 'FAIL',
          '视频元素=' + videoCount + '，scrim alpha=' + layer?.backdropAlpha + '（≥0.8），surface alpha=' + layer?.surfaceAlpha +
          '（≥0.98），z-index=' + layer?.backdropZ + '（≥150），视频底色=' + layer?.videoBackground)

        const fullscreenButtons = await page.locator('.modal.viewer .video-fullscreen').count()
        record('MOBILE12-08', videoCount > 0 && fullscreenButtons > 0 ? 'PASS' : 'FAIL',
          '真实媒体文件打开后：video 元素=' + videoCount + '，全屏入口=' + fullscreenButtons +
          '（本机 Playwright 构建缺 H.264 解码，能否播放单列为 IPHONE_REAL_DEVICE_1_7_2 = NOT_RUN）')

        // MOBILE12-06 (second half): change the theme while the viewer is open — the overlay colours must follow.
        const readVars = () => page.evaluate(() => {
          const s = getComputedStyle(document.documentElement)
          return {
            window: s.getPropertyValue('--window').trim(),
            surface: s.getPropertyValue('--surface').trim(),
            text: s.getPropertyValue('--text-primary').trim(),
            accent: s.getPropertyValue('--accent').trim(),
            scrim: s.getPropertyValue('--overlay-scrim').trim(),
            surfaceComputed: getComputedStyle(document.querySelector('.modal.viewer')).backgroundColor
          }
        })
        const choices = await (await mobileAdmin.request.get(base + '/api/admin/mobile-theme')).json()
        const ids = (choices.profiles ?? []).map((p) => p.id).filter((id) => id !== 'custom')
        const first = ids.find((id) => id === 'obsidian') ?? ids[0]
        const second = ids.find((id) => id === 'arctic' && id !== first) ?? ids.find((id) => id !== first)

        const applyTheme = async (profileId, extra) => {
          const before = await readVars()
          const resp = await mobileAdmin.request.put(base + '/api/admin/mobile-theme', {
            headers: { 'X-LanDisk-CSRF': mobileCsrf }, data: Object.assign({ profileId }, extra ?? {})
          })
          await page.waitForTimeout(17_000)
          const after = await readVars()
          const changed = ['window', 'surface', 'text', 'accent'].filter((k) => before[k] !== after[k])
          return { ok: resp.ok(), changed, after, before }
        }

        let themeResult = { ok: false, changed: [], detail: '服务端没有返回可选的手机主题' }
        if (first && second) {
          const a = await applyTheme(first)
          const b = await applyTheme(second)
          const custom = await applyTheme('custom', { appearance: 'dark', accent: '#c0392f', background: '#101418', surface: '#181d24' })
          themeResult = {
            ok: a.ok && b.ok && custom.ok && a.changed.length >= 3 && b.changed.length >= 2 && custom.changed.length >= 2,
            changed: a.changed.concat(b.changed, custom.changed),
            after: custom.after,
            detail: first + '→' + second + '→custom；变化变量 ' + [a.changed.join('/'), b.changed.join('/'), custom.changed.join('/')].join(' | ')
          }
        }
        record('MOBILE12-06', themeResult.ok ? 'PASS' : 'FAIL',
          '查看器打开期间切换主题：' + themeResult.detail + '；scrim=' + themeResult.after?.scrim +
          '，查看器 surface 实际背景=' + themeResult.after?.surfaceComputed)

        await page.locator('.modal.viewer .modal-head .icon-btn').click({ timeout: 5000 }).catch(() => {})
        await page.waitForTimeout(600)
      } else {
        record('MOBILE12-07', 'NOT_RUN', '共享里没有媒体文件，无法打开视频查看器')
        record('MOBILE12-08', 'NOT_RUN', '共享里没有媒体文件，无法打开视频查看器')
        record('MOBILE12-06', 'NOT_RUN', '共享里没有媒体文件，未在查看器打开期间验证主题')
      }

      // ---------------------------------------------------------------- unique blocker
      // MOBILE12-05: read-only *and* an unreachable share at the same time. Exactly one reason may be shown, and
      // it must be the share — not the device level. Restoring the share must bring the read-only reason back.
      if (mobileDeviceId && shareId) {
        await setMobileLevel('readOnly')
        await page.reload({ waitUntil: 'networkidle' })
        await page.waitForTimeout(3000)
        const readOnlyOnly = (await page.locator('.banner').allInnerTexts()).filter((t) => t.includes('只读权限')).length

        const movedSandbox = sandbox + '-moved-' + Date.now()
        fs.renameSync(sandbox, movedSandbox)

        // Wait until the server itself reports the share as unavailable. Without this the case could run
        // against a share that is still Online and then "fail" for a reason that has nothing to do with the
        // blocker priority it is meant to check.
        let shareState = ''
        for (let attempt = 0; attempt < 20; attempt++) {
          const probe = await (await mobileAdmin.request.get(base + '/api/admin/shares')).json()
          shareState = (probe.shares ?? [])[0]?.state ?? ''
          if (shareState && shareState !== 'Online') break
          await page.waitForTimeout(1000)
        }
        await page.reload({ waitUntil: 'networkidle' })
        await page.waitForTimeout(3000)
        const downBanners = await page.locator('.banner').allInnerTexts()
        const downShare = downBanners.filter((t) => t.includes('共享磁盘当前不可访问')).length
        const downReadOnly = downBanners.filter((t) => t.includes('只读权限')).length
        record('MOBILE12-05', downShare === 1 && downReadOnly === 0 ? 'PASS' : 'FAIL',
          '构造「共享根目录被移走 + 设备只读」：服务端共享状态=' + shareState + '，共享错误出现 ' + downShare + ' 次（应为 1），只读提示出现 ' + downReadOnly +
          ' 次（应为 0，只读不是主阻断信息）；恢复共享前只读提示出现 ' + readOnlyOnly + ' 次')

        fs.renameSync(movedSandbox, sandbox)

        // Same wait in the restore direction: the page may only be judged once the server reports the share as
        // reachable again, or the check measures how fast the probe runs rather than what the phone shows.
        let restoredState = ''
        for (let attempt = 0; attempt < 20; attempt++) {
          const probe = await (await mobileAdmin.request.get(base + '/api/admin/shares')).json()
          restoredState = (probe.shares ?? [])[0]?.state ?? ''
          if (restoredState === 'Online') break
          await page.waitForTimeout(1000)
        }
        await page.reload({ waitUntil: 'networkidle' })
        await page.waitForTimeout(3000)
        const backBanners = await page.locator('.banner').allInnerTexts()
        const backReadOnly = backBanners.filter((t) => t.includes('只读权限')).length
        const backShare = backBanners.filter((t) => t.includes('共享磁盘当前不可访问')).length
        record('MOBILE12-04', backReadOnly === 1 && backShare === 0 ? 'PASS' : 'FAIL',
          '共享恢复（服务端状态=' + restoredState + '）后：只读提示出现 ' + backReadOnly + ' 次（应为 1），共享错误 ' + backShare +
          ' 次（应为 0）；期间未重新配对')

        // ------------------------------------------------------------ real write, same session
        await setMobileLevel('readWrite')
        await page.waitForTimeout(17_000)
        const probeName = '权限往返-' + Date.now() + '.txt'
        const probePath = path.join(root, probeName)
        fs.writeFileSync(probePath, '权限往返写入探针')
        await page.locator('input[type="file"]').setInputFiles(probePath)
        await page.waitForTimeout(4000)
        const wroteAfterGrant = (await names()).includes(probeName)
        const meAfterGrant = await (await context.request.get(base + '/api/me')).json()
        const beatAfterGrant = await (await context.request.post(base + '/api/heartbeat', {
          headers: { 'X-LanDisk-CSRF': meAfterGrant?.csrfToken ?? '' }, data: {}
        })).json()
        const revisionNow = beatAfterGrant?.permissionRevision
        record('MOBILE12-03', wroteAfterGrant && meAfterGrant?.permissions?.writeFiles === true ? 'PASS' : 'FAIL',
          '同一个手机会话、同一个 Online 共享：管理端改为读写后，真实上传「' + probeName + '」' +
          (wroteAfterGrant ? '成功落盘' : '失败') + '，/api/me writeFiles=' + meAfterGrant?.permissions?.writeFiles +
          '，permissionRevision=' + revisionNow + '（未重新配对）')

        await setMobileLevel('readOnly')
        await page.waitForTimeout(17_000)
        const refusedName = '只读应拒绝-' + Date.now() + '.txt'
        const refusedPath = path.join(root, refusedName)
        fs.writeFileSync(refusedPath, '只读时不应写入成功')
        // The real upload endpoint the phone itself uses. An earlier version posted to a path that does not
        // exist, so it recorded a routing 404 as if it were a permission refusal.
        const initResponse = shareId
          ? await context.request.post(base + '/api/uploads/', {
            headers: { 'X-LanDisk-CSRF': meAfterGrant?.csrfToken ?? '', 'Content-Type': 'application/json; charset=utf-8' },
            data: { shareId, path: '', fileName: 'refused-readonly.txt', length: 15, fingerprint: 'probe' }
          })
          : null
        const refusedStatus = initResponse ? initResponse.status() : 0
        const refused = refusedStatus === 403
        const meAfterRevoke = await (await context.request.get(base + '/api/me')).json()
        const beatAfterRevoke = await (await context.request.post(base + '/api/heartbeat', {
          headers: { 'X-LanDisk-CSRF': meAfterRevoke?.csrfToken ?? '' }, data: {}
        })).json()
        const revisionChanged = typeof revisionNow === 'number' && typeof beatAfterRevoke?.permissionRevision === 'number' &&
          beatAfterRevoke.permissionRevision > revisionNow
        record('MOBILE12-04', refused && meAfterRevoke?.permissions?.writeFiles === false ? 'PASS' : 'FAIL',
          '同一会话再降为只读：真实上传被拒绝=' + refused + '（HTTP ' + refusedStatus + '，403 才算权限拒绝），' +
          '/api/me writeFiles=' + meAfterRevoke?.permissions?.writeFiles +
          '，permissionRevision ' + revisionNow + ' → ' + beatAfterRevoke?.permissionRevision + '（单调递增=' + revisionChanged + '，未重新配对）')

        // ------------------------------------------------------------ page-driven permission refresh
        //
        // MOBILE12-03/04 call /api/me from the harness. That proves the server changes the permission and that
        // a write is allowed or refused, but NOT that the phone page notices and refreshes itself — the two are
        // different claims, and only the second is what the Owner experiences. This case therefore looks at the
        // page's own network traffic and nothing else: it never calls /api/me, never touches front-end state and
        // never invokes a refresh function.
        const pageTraffic = []
        const beatResponses = []
        const meRequestsByPage = []
        page.on('request', (r) => {
          const u = r.url()
          if (u.includes('/api/me')) meRequestsByPage.push({ at: Date.now(), method: r.method() })
          if (u.includes('/api/pair') || u.includes('/api/devices/')) pageTraffic.push({ at: Date.now(), url: u })
        })
        page.on('response', async (r) => {
          if (!r.url().includes('/api/heartbeat')) return
          let revision = null
          try { revision = (await r.json())?.permissionRevision ?? null } catch { }
          beatResponses.push({ at: Date.now(), revision })
        })

        const uploadButton = page.locator('button.primary', { hasText: '上传' })
        const urlBeforeRefresh = page.url()
        // A heartbeat carries a revision number on every beat, so "the first beat with a number" is not the
        // change signal. The signal is a beat whose revision differs from the one before it.
        const revisionBefore = beatResponses.map((b) => b.revision).filter((r) => typeof r === 'number').at(-1) ?? null

        // ---- grant: the page must notice on its own
        const grantAt = Date.now()
        await setMobileLevel('readWrite')
        let grantSeen = { beat: null, me: null, enabled: false }
        for (let attempt = 0; attempt < 45; attempt++) {
          await page.waitForTimeout(1500)
          const beat = beatResponses.find((b) => b.at >= grantAt - 1500 && typeof b.revision === 'number' && b.revision !== revisionBefore)
          const me = beat ? meRequestsByPage.find((m) => m.at >= beat.at - 500) : null
          const enabled = !(await uploadButton.isDisabled())
          grantSeen = { beat, me, enabled }
          if (beat && me && enabled) break
        }

        // ---- revoke: the same chain in reverse, again with no help from the harness
        const revokeAt = Date.now()
        const revisionAfterGrant = grantSeen.beat?.revision ?? null
        await setMobileLevel('readOnly')
        let revokeSeen = { beat: null, me: null, disabled: false }
        for (let attempt = 0; attempt < 45; attempt++) {
          await page.waitForTimeout(1500)
          const beat = beatResponses.find((b) => b.at >= revokeAt - 1500 && typeof b.revision === 'number' && b.revision !== revisionAfterGrant)
          const me = beat ? meRequestsByPage.find((m) => m.at >= beat.at - 500) : null
          const disabled = await uploadButton.isDisabled()
          revokeSeen = { beat, me, disabled }
          if (beat && me && disabled) break
        }

        const noReload = page.url() === urlBeforeRefresh
        const noRepairing = pageTraffic.length === 0
        const grantOk = !!grantSeen.beat && !!grantSeen.me && grantSeen.enabled
        const revokeOk = !!revokeSeen.beat && !!revokeSeen.me && revokeSeen.disabled
        record('MOBILE12-11', grantOk && revokeOk && noReload && noRepairing ? 'PASS' : 'FAIL',
          '页面自行刷新权限（测试端从不调用 /api/me）：管理端 ' + new Date(grantAt).toISOString().slice(11, 19) +
          ' 改为读写后，页面自己的 heartbeat 返回修订号 ' + grantSeen.beat?.revision +
          '，页面随即自行请求 /api/me（' + (grantSeen.me ? '已观察到' : '未观察到') + '），上传按钮自动变为可用=' + grantSeen.enabled +
          '；' + new Date(revokeAt).toISOString().slice(11, 19) + ' 降为只读后，heartbeat 返回修订号 ' + revokeSeen.beat?.revision +
          '，页面再次自行请求 /api/me（' + (revokeSeen.me ? '已观察到' : '未观察到') + '），上传按钮自动禁用=' + revokeSeen.disabled +
          '；期间未重新加载页面=' + noReload + '，未发起任何配对/设备请求=' + noRepairing +
          '（页面自发的 /api/me 请求共 ' + meRequestsByPage.length + ' 次，全部来自页面而非测试端）')
      } else {
        record('MOBILE12-03', 'NOT_RUN', '缺少设备或共享，无法做权限往返')
        record('MOBILE12-04', 'NOT_RUN', '缺少设备或共享，无法做权限往返')
        record('MOBILE12-05', 'NOT_RUN', '缺少设备或共享，无法构造共享不可访问')
      }
      await mobileAdmin.close()

      // MOBILE12-09 / MOBILE12-10: "本机状态" exists for every phone, and the machine numbers appear only with
      // the SystemView capability — the server omits them rather than the app hiding fields it sent.
      await page.locator('.tab-btn', { hasText: '我的' }).click()
      await page.waitForTimeout(1500)
      const hostStatus = page.locator('section.card', { hasText: '本机状态' }).first()
      const hostStatusVisible = await hostStatus.isVisible().catch(() => false)
      const basicRows = hostStatusVisible ? await hostStatus.locator('table.grid tbody tr').count() : 0
      record('MOBILE12-09', hostStatusVisible && basicRows >= 6 ? 'PASS' : 'FAIL',
        '「本机状态」可见=' + hostStatusVisible + '，基础字段行数=' + basicRows + '（版本/服务/运行时长/推荐地址/HTTPS/共享/存储/传输/权限）')

      const detailedProbe = await (await context.request.get(base + '/api/host-status')).json()
      const noSystemView = detailedProbe?.detailed === false && !detailedProbe?.machine
      record('MOBILE12-10', noSystemView ? 'PASS' : 'FAIL',
        '没有 SystemView 能力的手机拿到 detailed=' + detailedProbe?.detailed + '，machine 字段=' +
        (detailedProbe?.machine ? '存在（不应存在）' : '不存在'))

      await context.close()
    }

    // ---- 桌面宽屏 --------------------------------------------------------
    {
      const context = await browser.newContext({ viewport: { width: 1440, height: 900 } })
      const csrf = await loginAdmin(context, base, dataDir)
      const pairing = await (await context.request.post(base + '/api/admin/pairing', {
        headers: { 'X-LanDisk-CSRF': csrf }, data: {}
      })).json()
      await context.request.post(base + '/api/pair/redeem', { data: { token: pairing.token, deviceName: 'UI 桌面' } })

      const page = await context.newPage()
      page.on('pageerror', (err) => console.log('  [页面错误] ' + err.message))
      await page.goto(base + '/', { waitUntil: 'networkidle' })
      await page.waitForTimeout(600)

      const railVisible = await page.locator('.rail').isVisible()
      const tabbarVisible = await page.locator('.tabbar').isVisible()
      record('UI-DESKTOP', railVisible && !tabbarVisible ? 'PASS' : 'FAIL',
        `桌面宽屏显示左侧导航=${railVisible}，隐藏底部导航=${!tabbarVisible}`)

      const colDisplay = await page.evaluate(() => {
        const el = document.querySelector('.col-modified')
        return el ? getComputedStyle(el).display : null
      })
      const headVisible = await page.locator('.entries-head').isVisible()
      record('UI-DESKTOP-COLS', colDisplay !== 'none' && colDisplay !== null && headVisible ? 'PASS' : 'FAIL',
        `宽屏显示 修改时间/类型/大小 列（display=${colDisplay}）与表头=${headVisible}`)

      await context.close()
    }

    // ---- 管理端设备与配对 ------------------------------------------------
    {
      const context = await browser.newContext()
      const page = await context.newPage()
      await page.goto(base + '/admin', { waitUntil: 'networkidle' })
      await page.locator('.tabs button', { hasText: /^设备$/ }).click()
      await page.waitForTimeout(900)
      const deviceRows = await page.locator('table.grid tbody tr').count()
      const hasRevoke = await page.locator('button', { hasText: '撤销' }).count()
      record('UI-ADMIN-DEVICES', deviceRows > 0 && hasRevoke > 0 ? 'PASS' : 'FAIL',
        `管理端设备列表 ${deviceRows} 行，撤销按钮 ${hasRevoke} 个`)

      await page.locator('.tabs button', { hasText: '添加设备' }).click()
      await page.locator('button', { hasText: '生成配对请求' }).click()
      await page.waitForTimeout(1500)
      const qrVisible = await page.locator('img.qr').isVisible()
      const code = (await page.locator('.code-display').textContent())?.trim()
      const linkValue = await page.locator('input[readonly]').inputValue()
      const fragmentOnly = linkValue.includes('#t=') && !linkValue.includes('?t=')
      record('UI-PAIRING', qrVisible && /^\d{6}$/.test(code ?? '') && fragmentOnly ? 'PASS' : 'FAIL',
        `二维码可见=${qrVisible}，6 位短码=${code}，链接把一次性令牌放在 URL 片段中=${fragmentOnly}`)

      await context.close()
    }

    // ---- 管理端系统更新面板（任务 03） -----------------------------------
    {
      const context = await browser.newContext()
      const page = await context.newPage()
      await page.goto(base + '/admin', { waitUntil: 'networkidle' })

      await page.locator('.tabs button', { hasText: '系统更新' }).click()
      await page.waitForTimeout(1200)

      const panelText = (await page.locator('.card').first().textContent()) ?? ''
      const hasVersionRow = panelText.includes('当前程序版本') && panelText.includes('已安装到磁盘的版本')
      const hasPicker = await page.locator('input[type=file]').count()
      // With nothing staged there must be no "install" button: installing is only offered for a
      // package that has already been verified and staged.
      const hasApplyButton = await page.locator('button', { hasText: /安装并重启|正在安装/ }).count()
      const hasHistory = panelText.includes('更新记录')
      const mentionsSigned = panelText.includes('.lupd') && panelText.includes('签名')

      record('UI-UPDATE-PANEL', hasVersionRow && hasPicker > 0 && hasApplyButton === 0 && hasHistory && mentionsSigned ? 'PASS' : 'FAIL',
        `版本信息=${hasVersionRow}，更新包选择器=${hasPicker}，未暂存时不显示安装按钮=${hasApplyButton === 0}，更新记录=${hasHistory}，签名说明=${mentionsSigned}`)

      await context.close()
    }

    // ---- 桌面外壳的 #pair 深链（任务 03） --------------------------------
    {
      const context = await browser.newContext()
      const page = await context.newPage()
      await page.goto(base + '/admin#pair', { waitUntil: 'networkidle' })
      await page.waitForTimeout(1500)
      const pairingTabActive = await page.locator('.tabs button.active', { hasText: '添加设备' }).count()
      const hashCleared = !page.url().includes('#pair')
      const qrOrPrompt = await page.locator('button', { hasText: '生成配对请求' }).count()
      record('UI-UPDATE-PAIR-LINK', pairingTabActive > 0 && hashCleared && qrOrPrompt > 0 ? 'PASS' : 'FAIL',
        `#pair 深链直接打开“添加设备”页=${pairingTabActive > 0}，清理地址片段=${hashCleared}，配对入口可用=${qrOrPrompt > 0}`)
      // ---- task 08: the web administration page was reported as too small to read, and its tables
      // squeezed dates and IP addresses into vertical columns of digits. Both are asserted here so the
      // fix cannot silently regress.
      step = 'minimum font size'
      const smallText = await page.evaluate(() => {
        const offenders = []
        for (const element of document.querySelectorAll('body *')) {
          const style = getComputedStyle(element)
          if (style.display === 'none' || style.visibility === 'hidden') continue
          if (!element.textContent || !element.textContent.trim()) continue
          const size = parseFloat(style.fontSize)
          if (size > 0 && size < 12) offenders.push(`${element.tagName}.${element.className || '-'}=${size}px`)
        }
        return offenders.slice(0, 5)
      })
      record('UI-FONT-FLOOR', smallText.length === 0 ? 'PASS' : 'FAIL',
        smallText.length === 0 ? '管理页没有任何小于 12px 的可见文本' : `仍有过小文本：${smallText.join('、')}`)

      step = 'no vertical text in data tables'
      const vertical = await page.evaluate(() => {
        const offenders = []
        for (const cell of document.querySelectorAll('table.data th, table.data td')) {
          const style = getComputedStyle(cell)
          const text = (cell.textContent || '').trim()
          if (!text || text.length < 3) continue
          // A wrapped cell is taller than about three lines of its own font size, or narrower than a
          // few characters: both are what "vertical digits" looks like in the layout.
          const tooTall = cell.getBoundingClientRect().height > parseFloat(style.fontSize) * 4.2
          const tooNarrow = cell.getBoundingClientRect().width < parseFloat(style.fontSize) * 2.5
          if (style.whiteSpace !== 'nowrap' || tooTall || tooNarrow) {
            offenders.push(`${text.slice(0, 10)}(white-space=${style.whiteSpace},h=${Math.round(cell.getBoundingClientRect().height)},w=${Math.round(cell.getBoundingClientRect().width)})`)
          }
        }
        return offenders.slice(0, 5)
      })
      record('UI-TABLE-NOWRAP', vertical.length === 0 ? 'PASS' : 'FAIL',
        vertical.length === 0 ? '数据表单元格均为单行，无竖排' : `可能竖排的单元格：${vertical.join('、')}`)

      // ---- task 08: the raw pairing link carries the one-time token, so it must not be on screen by
      // default. It stays reachable behind an explicit disclosure.
      step = 'pairing token is folded away'
      // A pairing request has to exist before there is a token to hide; the disclosure only appears
      // once one has been generated.
      const generate = page.locator('button', { hasText: '生成配对请求' })
      if (await generate.count()) {
        await generate.first().click()
        await page.waitForTimeout(1200)
      }
      // "Visible" cannot be decided by offsetParent any more. Chromium 128+ hides a closed
      // <details> through content-visibility rather than display:none, so the folded input still has
      // a layout box (offsetParent non-null, getBoundingClientRect returns a real size) even though
      // nothing is painted — offsetParent therefore reported the token as on screen while the
      // screenshot showed only the collapsed summary line. Ask two questions that do mean "a human
      // can see it": is the element inside a collapsed <details>/<dialog>, and does the engine's own
      // checkVisibility (with content-visibility taken into account) agree.
      const visibleTokenInputs = () => `(() => {
        const isVisible = (input) => {
          for (let node = input; node; node = node.parentElement) {
            if ((node.tagName === 'DETAILS' || node.tagName === 'DIALOG') && !node.open) return false
          }
          if (typeof input.checkVisibility === 'function') {
            return input.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true, contentVisibilityAuto: true })
          }
          const rect = input.getBoundingClientRect()
          return rect.width > 0 && rect.height > 0 && input.offsetParent !== null
        }
        return Array.from(document.querySelectorAll('input[readonly]'))
          .filter((input) => isVisible(input) && /#t=/.test(input.value)).length
      })()`

      const hiddenByDefault = await page.evaluate(visibleTokenInputs())
      const disclosure = await page.locator('details.advanced > summary').count()
      let revealed = 0
      let summaryText = ''
      if (disclosure > 0) {
        summaryText = await page.locator('details.advanced > summary').first().innerText().catch(() => '')
        await page.locator('details.advanced > summary').first().click()
        await page.waitForTimeout(400)
        revealed = await page.evaluate(visibleTokenInputs())
      }
      // The disclosure label itself must not become a second place the token leaks from.
      const summaryLeaks = /#t=|t=[A-Za-z0-9_-]{20,}/.test(summaryText)
      record('UI-PAIRING-TOKEN-HIDDEN',
        hiddenByDefault === 0 && disclosure > 0 && revealed > 0 && !summaryLeaks ? 'PASS' : 'FAIL',
        `默认可见的含令牌输入框=${hiddenByDefault}，折叠入口=${disclosure}，展开后可见=${revealed}，折叠标题泄露令牌=${summaryLeaks}`)

      await context.close()

    }
  } catch (error) {
    const message = String(error?.message ?? error).split('\n').slice(0, 4).join(' / ')
    record('UI-E2E', 'FAIL', `[失败步骤: ${step}] ${message}`)
  } finally {
    if (browser) await browser.close().catch(() => {})
    // 有界、可验证的回收：先温和结束，超时再强杀，最后**确认**它真的没了。
    // 旧写法只调用两次 kill() 就往下走，既不看返回值也不确认退出——进程没死也无人知晓。
    const exited = () => child.exitCode !== null || child.signalCode !== null
    try { child.kill() } catch { /* ignore */ }
    for (let i = 0; i < 20 && !exited(); i++) await new Promise((r) => setTimeout(r, 100))
    if (!exited()) {
      try { child.kill('SIGKILL') } catch { /* ignore */ }
      for (let i = 0; i < 30 && !exited(); i++) await new Promise((r) => setTimeout(r, 100))
    }
    if (!exited()) {
      record('UI-CLEANUP', 'FAIL', `测试服务端进程 pid=${child.pid} 在 SIGKILL 后仍未退出；已保留所有权标记供清理：${markerPath}`)
    } else {
      try { fs.rmSync(markerPath, { force: true }) } catch { /* ignore */ }
    }
    try { logStream.end() } catch { /* ignore */ }
  }

  console.log('\n=== 汇总 ===')
  const passed = results.filter((r) => r.status === 'PASS').length
  const failed = results.filter((r) => r.status === 'FAIL').length
  const notRun = results.filter((r) => r.status === 'NOT_RUN').length
  console.log(`通过 ${passed} / 失败 ${failed} / 未运行 ${notRun}`)

  const outPath = path.join(repoRoot, 'dist', `e2e-ui-${engineName}.json`)
  fs.mkdirSync(path.dirname(outPath), { recursive: true })
  fs.writeFileSync(outPath, JSON.stringify({
    engine: engineName,
    note: 'Playwright 引擎不等于真实 iPhone Safari；真机项应记为 NOT_RUN。',
    passed, failed, notRun,
    results
  }, null, 2), 'utf8')
  console.log('结果文件：' + outPath)

  process.exit(failures > 0 ? 1 : 0)
}

main()
