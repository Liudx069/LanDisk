import { useCallback, useEffect, useRef, useState } from 'react'
import QRCode from 'qrcode'
import { ApiError, type AdminDevice, type AdminShare, type PairingRequest, type RecycleEntry } from '../types'
import { apiDelete, apiGet, apiPatch, apiPost, apiUpload, formatBytes, formatTime, refreshCsrfFromCookie, setCsrf } from '../api'
import { Banner, ConfirmDialog, Modal, NamePrompt } from '../components/ui'
import OnboardingPanel from '../components/OnboardingPanel'

type Tab = 'status' | 'onboard' | 'shares' | 'devices' | 'pairing' | 'network' | 'tls' | 'recycle' | 'update' | 'diagnostics' | 'audit'

type UpdatePending = {
  version: string
  stagingRoot: string
  packageSha256?: string | null
  signerKeyId?: string | null
  releaseNotes?: string | null
  buildCommit?: string | null
  stagedAt?: string | null
}

type UpdateHistoryEntry = {
  fromVersion: string
  toVersion: string
  outcome: string
  message?: string | null
  startedAt: string
  finishedAt: string
}

type UpdateStatusResponse = {
  currentVersion: string
  installedVersion: string
  installRoot: string
  dataRoot: string
  updaterAvailable: boolean
  updaterPath?: string | null
  releaseKeyId: string
  releaseKeyValid: boolean
  packageExtension: string
  maxPackageBytes: number
  stage: string
  stageText: string
  stageMessage?: string | null
  stageUpdatedAt?: string | null
  lastOutcome?: string | null
  lastMessage?: string | null
  lastBackupDir?: string | null
  lastAttemptAt?: string | null
  pending?: UpdatePending | null
  history: UpdateHistoryEntry[]
}

type UpdateUploadResponse = {
  ok: boolean
  version: string
  packageSha256: string
  signerKeyId: string
  releaseNotes?: string | null
  buildCommit?: string | null
  fileCount: number
  totalBytes: number
  verifySeconds: number
  message: string
  plan?: { filesToWrite: number; filesToDelete: number; bytesToWrite: number; warnings: string[] } | null
  planError?: string | null
}

type StatusResponse = {
  version: string
  buildTime: string
  startedAt: string
  uptimeSeconds: number
  dataRoot: string
  environment: { os: string; architecture: string; framework: string; machineName: string; processArchitecture: string; is64BitProcess: boolean }
  sharing: { shareCount: number; enabledShareCount: number; deviceCount: number; recycleCount: number }
  network: {
    lanEnabled: boolean
    httpsEnabled: boolean
    httpCompatModeEnabled: boolean
    httpPort: number
    httpsPort: number
    bindAddress: string
    allowedCidr: string
    boundUrls: string[]
  }
}

type VolumeInfo = {
  mountPoint: string
  volumeGuidPath: string | null
  label: string
  driveType: string
  fileSystem: string
  totalBytes: number
  freeBytes: number
  isReady: boolean
  isSystemVolume: boolean
  recommendedForWholeVolumeShare: boolean
}

type NetworkSettings = {
  lanEnabled: boolean
  bindAddress: string
  bindAddressFallback?: string | null
  httpsEnabled: boolean
  httpCompatModeEnabled: boolean
  allowedCidr: string
  httpPort: number
  httpsPort: number
}

type NetworkResponse = {
  settings: NetworkSettings
  boundUrls: string[]
  interfaces: { name: string; description: string; address: string; subnetMask: string; isPrivate: boolean; status: string; type: string }[]
}

type TlsResponse = {
  certificate: {
    subject: string
    thumbprint: string
    sha256Fingerprint: string
    notBefore: string
    notAfter: string
    dnsNames: string[]
    ipAddresses: string[]
    isSelfSigned: boolean
  }
  httpsEnabled: boolean
  guidance: string[]
}

export default function AdminPage() {
  const [authed, setAuthed] = useState(false)
  const [token, setToken] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [tab, setTab] = useState<Tab>('status')
  const [checked, setChecked] = useState(false)

  const [status, setStatus] = useState<StatusResponse | null>(null)
  const [shares, setShares] = useState<AdminShare[]>([])
  const [devices, setDevices] = useState<AdminDevice[]>([])
  const [pairings, setPairings] = useState<PairingRequest[]>([])
  const [network, setNetwork] = useState<NetworkResponse | null>(null)
  const [tls, setTls] = useState<TlsResponse | null>(null)
  const [recycle, setRecycle] = useState<RecycleEntry[]>([])
  const [diagnostics, setDiagnostics] = useState<{ checks: { id: string; status: string; message: string }[] } | null>(null)
  const [audit, setAudit] = useState<{ at: string; category: string; action: string; deviceName?: string; path?: string; detail?: string; success: boolean }[]>([])
  const [updateStatus, setUpdateStatus] = useState<UpdateStatusResponse | null>(null)
  const [updateProgress, setUpdateProgress] = useState<{ sent: number; total: number } | null>(null)
  const [updateResult, setUpdateResult] = useState<UpdateUploadResponse | null>(null)
  const [confirmApply, setConfirmApply] = useState(false)
  const [applying, setApplying] = useState(false)
  const [updateBusy, setUpdateBusy] = useState(false)
  const updateFileInput = useRef<HTMLInputElement | null>(null)

  const [pickerOpen, setPickerOpen] = useState(false)
  const [pickerPath, setPickerPath] = useState('')
  const [pickerDirs, setPickerDirs] = useState<{ name: string; path: string; isDrive?: boolean; isLink?: boolean }[]>([])
  const [volumes, setVolumes] = useState<VolumeInfo[]>([])
  const [newShareName, setNewShareName] = useState('')
  const [newSharePerms, setNewSharePerms] = useState({ upload: false, rename: false, delete: false })

  const [pairing, setPairing] = useState<{ id: string; token: string; shortCode: string; expiresAt: string; url: string; urls: string[] } | null>(null)
  const [qrData, setQrData] = useState<string>('')
  const [renamingDevice, setRenamingDevice] = useState<AdminDevice | null>(null)
  const [confirmEmpty, setConfirmEmpty] = useState(false)
  const [confirmPurge, setConfirmPurge] = useState<RecycleEntry | null>(null)

  const pairingTimer = useRef<number | null>(null)

  const loadAll = useCallback(async () => {
    try {
      const [s, sh, dv, nw] = await Promise.all([
        apiGet<StatusResponse>('/api/admin/status'),
        apiGet<{ shares: AdminShare[] }>('/api/admin/shares'),
        apiGet<{ devices: AdminDevice[] }>('/api/admin/devices'),
        apiGet<NetworkResponse>('/api/admin/network')
      ])
      setStatus(s)
      setShares(sh.shares)
      setDevices(dv.devices)
      setNetwork(nw)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '读取状态失败。')
    }
  }, [])

  const loadTab = useCallback(async (which: Tab) => {
    try {
      if (which === 'pairing') {
        const result = await apiGet<{ requests: PairingRequest[] }>('/api/admin/pairing')
        setPairings(result.requests)
      } else if (which === 'tls') {
        setTls(await apiGet<TlsResponse>('/api/admin/tls'))
      } else if (which === 'recycle') {
        const result = await apiGet<{ entries: RecycleEntry[] }>('/api/admin/recycle')
        setRecycle(result.entries)
      } else if (which === 'diagnostics') {
        const result = await apiGet<{ checks: { id: string; status: string; message: string }[] }>('/api/admin/diagnostics')
        setDiagnostics(result)
      } else if (which === 'update') {
        setUpdateStatus(await apiGet<UpdateStatusResponse>('/api/admin/update/status'))
      } else if (which === 'audit') {
        const result = await apiGet<{ entries: typeof audit }>('/api/admin/audit?limit=200')
        setAudit(result.entries)
      }
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '读取失败。')
    }
  }, [])

  // The local management page has no login step: a request that genuinely comes from this
  // machine's loopback socket in a same-origin browser context is accepted directly. The one time
  // recovery token only appears if that automatic entry is refused.
  const [entryMessage, setEntryMessage] = useState<string | null>(null)

  const enter = useCallback(async () => {
    try {
      const result = await apiGet<{ csrfToken: string }>('/api/admin/session')
      setCsrf(result.csrfToken)
      setAuthed(true)
      setEntryMessage(null)
      setError(null)
      await loadAll()
    } catch (err) {
      setAuthed(false)
      if (err instanceof ApiError && (err.code === 'CROSS_SITE' || err.code === 'ORIGIN_MISMATCH')) {
        setEntryMessage('当前页面不是从本机回环地址打开的，管理端拒绝自动进入。请在电脑上改用 http://127.0.0.1: 开头的地址打开。')
      } else if (err instanceof ApiError && err.status === 403) {
        setEntryMessage(err.message)
      } else {
        setEntryMessage(err instanceof ApiError ? err.message : '无法连接 LanDisk，请确认程序正在运行。')
      }
    } finally {
      setChecked(true)
    }
  }, [loadAll])

  const bootstrapWithToken = useCallback(async (adminToken: string) => {
    try {
      const result = await apiPost<{ csrfToken: string }>('/api/admin/session', { token: adminToken })
      setCsrf(result.csrfToken)
      setAuthed(true)
      setEntryMessage(null)
      setError(null)
      await loadAll()
    } catch (err) {
      setAuthed(false)
      setError(err instanceof ApiError ? err.message : '管理令牌不正确。')
    } finally {
      setChecked(true)
    }
  }, [loadAll])

  useEffect(() => {
    // A recovery link with #token=… is still honoured, but it is no longer the normal path.
    const match = /(?:^|[#&])token=([^&]+)/.exec(location.hash)
    const fromHash = match ? decodeURIComponent(match[1]) : ''
    if (fromHash) {
      history.replaceState(null, '', '/admin')
      void bootstrapWithToken(fromHash)
      return
    }

    refreshCsrfFromCookie()
    void enter()
  }, [bootstrapWithToken, enter])

  useEffect(() => {
    if (!authed) return
    void loadTab(tab)
    if (tab === 'network') {
      void apiGet<{ volumes: VolumeInfo[] }>('/api/admin/volumes').then((r) => setVolumes(r.volumes)).catch(() => {})
    }
  }, [authed, tab, loadTab])

  useEffect(() => {
    if (!authed || tab !== 'pairing') return
    pairingTimer.current = window.setInterval(() => { void loadTab('pairing') }, 3000)
    return () => { if (pairingTimer.current !== null) window.clearInterval(pairingTimer.current) }
  }, [authed, tab, loadTab])

  // The desktop shell opens /admin#pair from its "手机连接二维码" menu item. The hash is consumed
  // and cleared so a later refresh does not silently restart a pairing request. hashchange is
  // handled too: navigating to #pair from an already open admin page is a fragment change, which
  // does not reload the document.
  useEffect(() => {
    if (!authed) return

    const applyPairHash = () => {
      if (!/(?:^|[#&])pair\b/.test(location.hash)) return
      history.replaceState(null, '', '/admin')
      setTab('pairing')
    }

    // The installer's completion screen opens /admin#view=onboard after it has started the wizard on
    // the user's behalf, so the QR code the user is told to scan is on screen when the page loads.
    const applyViewHash = () => {
      if (!/(?:^|[#&])view=onboard\b/.test(location.hash)) return
      history.replaceState(null, '', '/admin')
      setTab('onboard')
    }

    applyPairHash()
    applyViewHash()
    window.addEventListener('hashchange', applyPairHash)
    window.addEventListener('hashchange', applyViewHash)
    return () => {
      window.removeEventListener('hashchange', applyPairHash)
      window.removeEventListener('hashchange', applyViewHash)
    }
  }, [authed])

  const refreshQr = useCallback(async (url: string) => {
    try {
      setQrData(await QRCode.toDataURL(url, { width: 440, margin: 1, errorCorrectionLevel: 'M' }))
    } catch {
      setQrData('')
    }
  }, [])

  async function createPairing() {
    try {
      const result = await apiPost<{ id: string; token: string; shortCode: string; expiresAt: string; url: string; urls: string[] }>('/api/admin/pairing')
      setPairing(result)
      await refreshQr(result.url)
      setNotice('配对请求已生成，2 分钟内有效，只能使用一次。')
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '生成配对请求失败。')
    }
  }

  async function confirmPairing(id: string) {
    try {
      await apiPost(`/api/admin/pairing/${id}/confirm`, {})
      setNotice('已确认该设备配对。设备初始权限为只读。')
      await loadTab('pairing')
      await loadAll()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '确认失败。')
    }
  }

  async function cancelPairing(id: string) {
    try {
      await apiDelete(`/api/admin/pairing/${id}`)
      await loadTab('pairing')
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '取消失败。')
    }
  }

  async function openPicker(initial: string) {
    setPickerOpen(true)
    setPickerPath(initial)
    try {
      const result = await apiGet<{ current: string; directories: typeof pickerDirs }>(`/api/admin/browse-local?path=${encodeURIComponent(initial)}`)
      setPickerPath(result.current)
      setPickerDirs(result.directories)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '无法列出该目录。')
    }
  }

  async function addShare(rootPath: string) {
    try {
      await apiPost('/api/admin/shares', {
        rootPath,
        displayName: newShareName.trim() || undefined,
        allowUpload: newSharePerms.upload,
        allowRename: newSharePerms.rename,
        allowDelete: newSharePerms.delete
      })
      setPickerOpen(false)
      setNewShareName('')
      setNotice('共享已添加。手机端需要刷新后才能看到。')
      await loadAll()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '添加共享失败。')
    }
  }

  async function updateShare(share: AdminShare, patch: Record<string, unknown>) {
    try {
      await apiPatch(`/api/admin/shares/${share.id}`, patch)
      await loadAll()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '更新共享失败。')
    }
  }

  async function removeShare(share: AdminShare) {
    if (!window.confirm(`删除共享“${share.name}”？共享目录中的文件不会被删除，只会停止对外提供。`)) return
    try {
      await apiDelete(`/api/admin/shares/${share.id}`)
      await loadAll()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '删除共享失败。')
    }
  }

  async function updateDevice(device: AdminDevice, patch: Record<string, unknown>) {
    try {
      await apiPatch(`/api/admin/devices/${device.id}`, patch)
      await loadAll()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '更新设备失败。')
    }
  }

  async function revokeDevice(device: AdminDevice) {
    if (!window.confirm(`撤销设备“${device.name}”？该设备之后需要重新配对。已经下载到它上面的内容无法收回。`)) return
    try {
      const result = await apiPost<{ cancelledUploads: number; note: string }>(`/api/admin/devices/${device.id}/revoke`, {})
      setNotice(`已撤销设备。取消的进行中上传：${result.cancelledUploads}。${result.note}`)
      await loadAll()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '撤销失败。')
    }
  }

  async function deleteDevice(device: AdminDevice) {
    if (!window.confirm(`彻底删除设备记录“${device.name}”？`)) return
    try {
      await apiDelete(`/api/admin/devices/${device.id}`)
      await loadAll()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '删除失败。')
    }
  }

  async function saveNetwork(patch: Partial<NetworkSettings>) {
    if (!network) return
    const merged = { ...network.settings, ...patch }
    try {
      const result = await apiPost<{ message: string }>('/api/admin/network', {
        lanEnabled: merged.lanEnabled,
        bindAddress: merged.bindAddress,
        httpsEnabled: merged.httpsEnabled,
        httpCompatModeEnabled: merged.httpCompatModeEnabled,
        allowedCidr: merged.allowedCidr,
        httpPort: merged.httpPort,
        httpsPort: merged.httpsPort
      })
      setNotice(result.message)
      await loadAll()
      await loadTab('network')
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '保存网络设置失败。')
    }
  }

  // ---------------------------------------------------------------- 系统更新

  const refreshUpdate = useCallback(async () => {    try {
      setUpdateStatus(await apiGet<UpdateStatusResponse>('/api/admin/update/status'))
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '读取更新状态失败。')
    }
  }, [])

  async function uploadPackage(file: File) {
    setUpdateBusy(true)
    setUpdateResult(null)
    setUpdateProgress({ sent: 0, total: file.size })
    try {
      const result = await apiUpload<UpdateUploadResponse>('/api/admin/update/upload', file, (sent, total) => {
        setUpdateProgress({ sent, total })
      })
      setUpdateResult(result)
      setNotice(`更新包 ${result.version} 已通过签名与哈希校验并暂存，尚未安装。`)
      await refreshUpdate()
    } catch (err) {
      setError(describeUpdateError(err, '上传更新包失败。'))
    } finally {
      setUpdateProgress(null)
      setUpdateBusy(false)
      if (updateFileInput.current) updateFileInput.current.value = ''
    }
  }

  async function applyUpdate() {
    setConfirmApply(false)
    setApplying(true)
    const target = updateStatus?.pending?.version ?? '新版本'
    try {
      await apiPost('/api/admin/update/apply', { confirm: true })
      setNotice(`正在安装 ${target}。LanDisk 会重启，本页面会在服务恢复后自动刷新。`)
      await waitForRestart(target)
    } catch (err) {
      setError(describeUpdateError(err, '无法启动更新。'))
      setApplying(false)
    }
  }

  /**
   * Watches the machine across the restart. The session is held in memory by the server, so the
   * page has to obtain a new one (loopback auto entry) before it can read the update status again.
   */
  async function waitForRestart(target: string) {
    const deadline = Date.now() + 5 * 60 * 1000
    while (Date.now() < deadline) {
      await new Promise((resolve) => window.setTimeout(resolve, 2000))
      try {
        const response = await fetch('/api/admin/health', { credentials: 'same-origin' })
        if (!response.ok) continue
        const health = await response.json() as { ok?: boolean }
        if (!health?.ok) continue
      } catch {
        continue
      }

      await enter()
      const status = await apiGet<UpdateStatusResponse>('/api/admin/update/status')
      setUpdateStatus(status)
      setApplying(false)

      if (status.lastOutcome === 'RolledBack' || status.stage === 'RolledBack') {
        setError(status.lastMessage ?? `更新 ${target} 失败，已自动回滚。`)
        return
      }

      if (status.installedVersion === target) {
        setNotice(`已更新到 ${target}，健康自检通过。`)
        setUpdateResult(null)
        await loadAll()
        return
      }

      if (status.lastOutcome === 'Failed') {
        setError(status.lastMessage ?? `更新 ${target} 未能完成。`)
        return
      }
    }

    setApplying(false)
    setError('等待 LanDisk 重启超时。请检查本机托盘图标或数据目录中的 updates\\updater.log。')
  }

  /** Update failures carry a stable machine code; showing it makes a bug report actionable. */
  function describeUpdateError(err: unknown, fallback: string): string {
    if (err instanceof ApiError) return err.code ? `${err.message}（${err.code}）` : err.message
    return err instanceof Error ? err.message : fallback
  }

  async function discardUpdate() {
    try {
      await apiPost('/api/admin/update/discard', {})
      setUpdateResult(null)
      setNotice('已放弃待安装的更新包。')
      await refreshUpdate()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '放弃更新包失败。')
    }
  }

  async function shutdown() {
    if (!window.confirm('退出 LanDisk？退出后手机将无法访问，直到你再次启动本程序。')) return
    try {
      await apiPost('/api/admin/shutdown', {})
      setNotice('LanDisk 正在退出。可以关闭此页面。')
    } catch {
      setNotice('LanDisk 正在退出。可以关闭此页面。')
    }
  }

  if (!checked) return <div className="app wide"><div className="card center pad">正在检查管理会话…</div></div>

  if (!authed) {
    return (
      <div className="app">
        <header className="top"><h1>LanDisk 本机管理</h1></header>

        <Banner kind="warn">
          {entryMessage ?? '管理端只允许在这台电脑本机访问。'}
        </Banner>

        <div className="card">
          <h2>正常情况不需要任何登录</h2>
          <p className="muted">
            在电脑上用浏览器打开 <span className="mono">http://127.0.0.1:8891/admin</span> 时会直接进入，
            不需要复制或粘贴令牌。局域网上的手机无法访问管理端。
          </p>
          <button className="primary block" onClick={() => void enter()}>重新尝试进入</button>
        </div>

        <div className="card">
          <h2>恢复方式</h2>
          <p className="muted">
            如果浏览器禁用了 Cookie，或你通过其它方式打开了这个页面，可以使用启动时打印的一次性管理令牌进入。
            令牌也保存在数据目录的 <span className="mono">admin-url.txt</span> 中。
          </p>
          <label className="field">
            <span>管理令牌（仅在自动进入失败时需要）</span>
            <input type="password" value={token} onChange={(e) => setToken(e.target.value)} placeholder="粘贴管理令牌" />
          </label>
          <button className="block" onClick={() => void bootstrapWithToken(token.trim())} disabled={!token.trim()}>
            使用令牌进入
          </button>
        </div>

        {error && <Banner kind="error">{error}</Banner>}
      </div>
    )
  }

  return (
    <div className="app wide">
      <header className="top">
        <h1>LanDisk 本机管理</h1>
        <button className="small" onClick={() => void loadAll()}>刷新状态</button>
        <button className="small danger" onClick={() => void shutdown()}>退出 LanDisk</button>
      </header>

      {error && <Banner kind="error">{error} <button className="small" onClick={() => setError(null)}>知道了</button></Banner>}
      {notice && <Banner kind="ok">{notice} <button className="small" onClick={() => setNotice(null)}>知道了</button></Banner>}
      {status?.network.httpCompatModeEnabled && !status.network.httpsEnabled && (
        <Banner kind="warn">
          当前处于 HTTP 兼容模式：局域网内的其他人可能截获或篡改传输内容。请仅在你信任的局域网中临时使用，
          正式使用建议开启 HTTPS。
        </Banner>
      )}

      <div className="tabs">
        {(['status', 'onboard', 'shares', 'devices', 'pairing', 'network', 'tls', 'recycle', 'update', 'diagnostics', 'audit'] as Tab[]).map((item) => (
          <button key={item} className={tab === item ? 'active' : ''} onClick={() => setTab(item)}>{tabName(item)}</button>
        ))}
      </div>

      {tab === 'status' && status && (
        <div className="card">
          <h2>运行状态</h2>
          <table className="grid">
            <tbody>
              <tr><th>版本</th><td>{status.version}</td></tr>
              <tr><th>构建时间</th><td>{status.buildTime}</td></tr>
              <tr><th>启动时间</th><td>{formatTime(status.startedAt)}（已运行 {Math.floor(status.uptimeSeconds / 60)} 分钟）</td></tr>
              <tr><th>数据目录</th><td className="mono">{status.dataRoot}</td></tr>
              <tr><th>操作系统</th><td>{status.environment.os} / {status.environment.architecture}</td></tr>
              <tr><th>运行时</th><td>{status.environment.framework}</td></tr>
              <tr><th>监听地址</th><td className="mono">{status.network.boundUrls.join('、') || '—'}</td></tr>
              <tr><th>局域网</th><td>{status.network.lanEnabled ? '已开启' : '未开启（仅本机）'}</td></tr>
              <tr><th>HTTPS</th><td>{status.network.httpsEnabled ? '已启用' : '未启用'}</td></tr>
              <tr><th>共享 / 设备 / 回收站</th><td>{status.sharing.shareCount}（启用 {status.sharing.enabledShareCount}） / {status.sharing.deviceCount} / {status.sharing.recycleCount}</td></tr>
            </tbody>
          </table>
          <p className="muted">
            首次启动不会共享任何目录，也不会自动开机启动或修改系统设置。局域网访问需要你在这里显式开启。
          </p>
        </div>
      )}

      {tab === 'onboard' && (
        <OnboardingPanel onError={setError} onNotice={setNotice} />
      )}

      {tab === 'shares' && (
        <div className="card">
          <div className="row">
            <h2 style={{ flex: '1 1 auto' }}>共享目录</h2>
            <button className="small primary" onClick={() => void openPicker('')}>添加共享</button>
          </div>
          <p className="muted">
            LanDisk 不会自动共享任何磁盘。每次添加都需要你在这里明确选择一个目录。
            整卷共享（例如整个移动硬盘）也必须在下面的选择器中手动选择盘符。
          </p>
          <table className="grid">
            <thead>
              <tr><th>名称</th><th>路径</th><th>状态</th><th>权限</th><th>启用</th><th>操作</th></tr>
            </thead>
            <tbody>
              {shares.map((share) => (
                <tr key={share.id}>
                  <td>{share.name}</td>
                  <td className="mono">{share.rootPath}<br /><span className="muted">{share.volumeGuidPath}</span></td>
                  <td>
                    <span className={`badge ${share.state === 'Online' ? 'ok' : 'warn'}`}>{share.state}</span>
                    {share.stateDetail && <div className="muted">{share.stateDetail}</div>}
                  </td>
                  <td>
                    <div className="check"><input type="checkbox" checked={share.capabilities.includes('Upload')} onChange={(e) => void updateShare(share, { allowUpload: e.target.checked })} /><span>上传</span></div>
                    <div className="check"><input type="checkbox" checked={share.capabilities.includes('Rename')} onChange={(e) => void updateShare(share, { allowRename: e.target.checked })} /><span>重命名</span></div>
                    <div className="check"><input type="checkbox" checked={share.capabilities.includes('Delete')} onChange={(e) => void updateShare(share, { allowDelete: e.target.checked })} /><span>删除</span></div>
                  </td>
                  <td><input type="checkbox" checked={share.enabled} onChange={(e) => void updateShare(share, { enabled: e.target.checked })} /></td>
                  <td>
                    <div className="row tight">
                      <button className="small" onClick={() => void apiPost(`/api/admin/shares/${share.id}/reconfirm`, {}).then(() => loadAll()).catch((err) => setError(err instanceof ApiError ? err.message : '重新确认失败。'))}>重新确认磁盘</button>
                      <button className="small danger" onClick={() => void removeShare(share)}>删除</button>
                    </div>
                  </td>
                </tr>
              ))}
              {shares.length === 0 && <tr><td colSpan={6} className="muted">还没有共享。手机端会看到空列表，这是正常的初始状态。</td></tr>}
            </tbody>
          </table>
        </div>
      )}

      {tab === 'devices' && (
        <div className="card">
          <h2>已配对设备</h2>
          <table className="grid data">
            <thead><tr><th>名称</th><th>权限</th><th>最近活动</th><th>来源</th><th>状态</th><th>操作</th></tr></thead>
            <tbody>
              {devices.map((device) => (
                <tr key={device.id}>
                  <td>{device.name}</td>
                  <td>
                    <select value={device.level} onChange={(e) => void updateDevice(device, { level: e.target.value })} disabled={device.revoked}>
                      <option value="ReadOnly">只读</option>
                      <option value="ReadWrite">可读写</option>
                    </select>
                  </td>
                  <td className="muted">{formatTime(device.lastSeenAt)}</td>
                  <td className="mono muted">{device.lastSeenIp ?? '—'}</td>
                  <td>{device.revoked ? <span className="badge err">已撤销</span> : <span className="badge ok">有效</span>}</td>
                  <td>
                    <div className="row tight">
                      <button className="small" onClick={() => setRenamingDevice(device)}>改名</button>
                      {!device.revoked && <button className="small danger" onClick={() => void revokeDevice(device)}>撤销</button>}
                      <button className="small danger" onClick={() => void deleteDevice(device)}>删除记录</button>
                    </div>
                  </td>
                </tr>
              ))}
              {devices.length === 0 && <tr><td colSpan={6} className="muted">还没有配对设备。</td></tr>}
            </tbody>
          </table>
          <p className="muted">
            撤销只会停止 LanDisk 仍在控制中的传输，无法收回手机已经下载或缓存的字节。
            新配对的设备默认是只读；要给某台设备开启写操作，请在这里把它改为“可读写”，同时共享本身的权限也要勾选。
          </p>
        </div>
      )}

      {tab === 'pairing' && (
        <div className="card">
          <div className="row">
            <h2 style={{ flex: '1 1 auto' }}>添加手机设备</h2>
            <button className="small primary" onClick={() => void createPairing()}>生成配对请求</button>
          </div>

          {pairing && (
            <div className="stack">
              <div className="center">
                {qrData && <img className="qr" src={qrData} alt="配对二维码" />}
                <div className="code-display">{pairing.shortCode}</div>
                <div className="muted">有效期至 {formatTime(pairing.expiresAt)}，仅可使用一次。</div>
              </div>
              <div className="muted">
                二维码指向 <span className="mono">{pairingHost(pairing.url)}</span>。
                <strong>手机以后请一直用这个地址打开</strong>：配对凭据按主机名隔离，换用 IP 或其它名称会被当成
                “未配对”，并要求重新扫码。
              </div>
              {pairing.urls.length > 1 && (
                <details className="advanced">
                  <summary>其它可用地址（只选一个，之后固定使用）</summary>
                  <ul className="mono">
                    {pairing.urls.map((u) => <li key={u}>{originOf(u)}</li>)}
                  </ul>
                </details>
              )}
              {/*
                The raw link carries the one-time token, so it is folded away rather than shown next to
                the QR code. Scanning the code is the normal path; reading the token out loud or
                screenshotting it is not something we should be inviting.
              */}
              <details className="advanced">
                <summary>高级：显示原始配对链接（含一次性令牌）</summary>
                <label className="field">
                  <span>配对链接</span>
                  <input type="text" readOnly value={pairing.url} onFocus={(e) => e.currentTarget.select()} />
                </label>
                <div className="muted">
                  该链接包含一次性的 256 位随机令牌，不包含任何长期密码。排障时使用；请勿截图或转发。
                </div>
              </details>
              <div className="muted">
                使用 6 位配对码时，还需要在下面的列表里点“确认配对”。
              </div>
            </div>
          )}

          <h3>进行中的配对请求</h3>
          <table className="grid data">
            <thead><tr><th>配对码</th><th>状态</th><th>设备</th><th>来源</th><th>操作</th></tr></thead>
            <tbody>
              {pairings.map((item) => (
                <tr key={item.id}>
                  <td className="mono">{item.shortCode}</td>
                  <td>{item.awaitingConfirmation ? <span className="badge warn">等待本机确认</span> : <span className="badge">{item.state}</span>}</td>
                  <td>{item.deviceName ?? '—'}</td>
                  <td className="mono muted">{item.remoteAddress ?? '—'}</td>
                  <td>
                    <div className="row tight">
                      {item.awaitingConfirmation && <button className="small primary" onClick={() => void confirmPairing(item.id)}>确认配对</button>}
                      <button className="small" onClick={() => void cancelPairing(item.id)}>取消</button>
                    </div>
                  </td>
                </tr>
              ))}
              {pairings.length === 0 && <tr><td colSpan={5} className="muted">没有进行中的配对请求。</td></tr>}
            </tbody>
          </table>
        </div>
      )}

      {tab === 'network' && network && (
        <div className="card">
          <h2>网络与监听</h2>
          <p className="muted">
            默认只监听本机回环地址。开启局域网访问后，端口和监听地址会在下次启动时生效，
            保存后请退出并重新启动 LanDisk。LanDisk 不会自动修改 Windows 防火墙。
          </p>

          <div className="check">
            <input type="checkbox" checked={network.settings.lanEnabled} onChange={(e) => void saveNetwork({ lanEnabled: e.target.checked })} />
            <span>允许局域网设备访问</span>
          </div>
          <div className="check">
            <input type="checkbox" checked={network.settings.httpsEnabled} onChange={(e) => void saveNetwork({ httpsEnabled: e.target.checked })} />
            <span>启用 HTTPS（推荐）</span>
          </div>
          <div className="check">
            <input type="checkbox" checked={network.settings.httpCompatModeEnabled} onChange={(e) => void saveNetwork({ httpCompatModeEnabled: e.target.checked })} />
            <span>启用未加密的 HTTP 兼容模式（有风险）</span>
          </div>
          <p className="muted">
            开启局域网访问时，至少要启用 HTTPS 或明确勾选 HTTP 兼容模式。HTTP 模式下同网段的人可能截获配对令牌和文件内容。
          </p>

          <label className="field">
            <span>绑定网卡地址（留空表示所有私有地址）</span>
            <select value={network.settings.bindAddress} onChange={(e) => void saveNetwork({ bindAddress: e.target.value })}>
              <option value="">所有私有地址</option>
              {network.interfaces.filter((i) => i.isPrivate).map((i) => (
                <option key={i.address} value={i.address}>{i.address}（{i.name}）</option>
              ))}
            </select>
          </label>

          {network.settings.bindAddressFallback && (
            <p className="muted" style={{ color: 'var(--warn)' }}>
              你固定绑定的地址 <span className="mono">{network.settings.bindAddressFallback}</span>
              已经不在这台电脑的任何网卡上（换网或 DHCP 变了）。本次启动已自动改用当前物理网卡，
              <strong>局域网仍然可用</strong>，但下次还会出现同样提示。请把上面的「绑定网卡地址」改回
              「所有私有地址」，或选择当前地址。
            </p>
          )}

          <div className="row">
            <label className="field" style={{ flex: '1 1 140px' }}>
              <span>HTTP 端口</span>
              <input type="number" defaultValue={network.settings.httpPort} onBlur={(e) => void saveNetwork({ httpPort: Number(e.target.value) })} />
            </label>
            <label className="field" style={{ flex: '1 1 140px' }}>
              <span>HTTPS 端口</span>
              <input type="number" defaultValue={network.settings.httpsPort} onBlur={(e) => void saveNetwork({ httpsPort: Number(e.target.value) })} />
            </label>
            <label className="field" style={{ flex: '1 1 180px' }}>
              <span>允许的来源网段（可留空）</span>
              <input type="text" defaultValue={network.settings.allowedCidr} placeholder="192.168.1.0/24" onBlur={(e) => void saveNetwork({ allowedCidr: e.target.value })} />
            </label>
          </div>

          <h3>本机可用地址</h3>
          <table className="grid data">
            <thead><tr><th>地址</th><th>网卡</th><th>类型</th></tr></thead>
            <tbody>
              {network.interfaces.map((i) => (
                <tr key={i.address + i.name}>
                  <td className="mono">{i.address}</td>
                  <td>{i.name}</td>
                  <td>{i.isPrivate ? '私有地址' : <span className="badge warn">非私有，不会对外提供服务</span>}</td>
                </tr>
              ))}
            </tbody>
          </table>

          <h3>当前监听</h3>
          <div className="mono">{network.boundUrls.join('\n') || '—'}</div>

          <h3>整卷/目录选择参考</h3>
          <table className="grid data">
            <thead><tr><th>盘符</th><th>卷身份</th><th>类型</th><th>可用/总量</th></tr></thead>
            <tbody>
              {volumes.map((v) => (
                <tr key={v.mountPoint}>
                  <td className="mono">{v.mountPoint} {v.label}</td>
                  <td className="mono muted">{v.volumeGuidPath ?? '—'}</td>
                  <td>{v.driveType}{v.isSystemVolume ? '（系统卷）' : ''}</td>
                  <td>{formatBytes(v.freeBytes)} / {formatBytes(v.totalBytes)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {tab === 'tls' && tls && (
        <div className="card">
          <h2>HTTPS 证书</h2>
          <p className="muted">
            证书由本程序在本机生成，私钥只保存在这台电脑上，不会下发给手机，也不会自动安装到任何信任库。
            iPhone 手动安装证书后必须再明确打开“证书信任设置”，否则 Safari 仍会报证书错误。
          </p>
          <table className="grid">
            <tbody>
              <tr><th>主题</th><td className="mono">{tls.certificate.subject}</td></tr>
              <tr><th>SHA-256 指纹</th><td className="mono">{tls.certificate.sha256Fingerprint}</td></tr>
              <tr><th>有效期</th><td>{formatTime(tls.certificate.notBefore)} 至 {formatTime(tls.certificate.notAfter)}</td></tr>
              <tr><th>域名</th><td className="mono">{tls.certificate.dnsNames.join('、')}</td></tr>
              <tr><th>IP 地址</th><td className="mono">{tls.certificate.ipAddresses.join('、')}</td></tr>
              <tr><th>自签名</th><td>{tls.certificate.isSelfSigned ? '是' : '否'}</td></tr>
            </tbody>
          </table>
          <div className="row" style={{ marginTop: 10 }}>
            <a className="btn" href="/api/admin/tls/cert" download>下载证书文件（.cer）</a>
            <button className="small danger" onClick={() => void apiGet<TlsResponse>('/api/admin/tls?regenerate=1').then(setTls).catch((err) => setError(err instanceof ApiError ? err.message : '重新生成失败。'))}>
              重新生成证书
            </button>
          </div>
          <h3>iPhone 安装与信任步骤</h3>
          <ol>{tls.guidance.map((line, index) => <li key={index}>{line}</li>)}</ol>
          <Banner kind="warn">
            手动安装证书不会自动等于已信任 TLS。在“证书信任设置”中打开开关之前，Safari 会一直提示证书不受信任。
            在公共网络上不要为了提高便利而跳过这一步。
          </Banner>
        </div>
      )}

      {tab === 'recycle' && (
        <div className="card">
          <div className="row">
            <h2 style={{ flex: '1 1 auto' }}>回收站</h2>
            <button className="small danger" onClick={() => setConfirmEmpty(true)}>永久清空全部</button>
          </div>
          <p className="muted">永久清空只在本机管理端可用，且只作用于本索引中记录的对象，只清空后无法恢复。</p>
          <table className="grid data">
            <thead><tr><th>名称</th><th>原位置</th><th>大小</th><th>删除时间</th><th>操作</th></tr></thead>
            <tbody>
              {recycle.map((entry) => (
                <tr key={entry.id}>
                  <td>{entry.name}</td>
                  <td className="mono muted">{entry.shareName} / {entry.path}</td>
                  <td>{entry.sizeText}</td>
                  <td className="muted">{formatTime(entry.deletedAt)}</td>
                  <td><button className="small danger" onClick={() => setConfirmPurge(entry)}>永久清空</button></td>
                </tr>
              ))}
              {recycle.length === 0 && <tr><td colSpan={5} className="muted">回收站是空的。</td></tr>}
            </tbody>
          </table>
        </div>
      )}

      {tab === 'update' && (
        <div className="card">
          <div className="row">
            <h2 style={{ flex: '1 1 auto' }}>系统更新</h2>
            <button className="small" onClick={() => void refreshUpdate()}>刷新</button>
          </div>

          {!updateStatus ? (
            <p className="muted">正在读取更新状态…</p>
          ) : (
            <div className="stack">
              {applying ? (
                <Banner kind="warn">
                  LanDisk 正在安装 {updateStatus.pending?.version ?? '新版本'} 并重启。
                  本页面会在服务恢复后自动刷新，请不要关闭它。
                </Banner>
              ) : null}

              {updateStatus.lastOutcome === 'RolledBack' || updateStatus.stage === 'RolledBack' ? (
                <Banner kind="error">
                  上一次更新未能完成，已自动回滚到 {updateStatus.installedVersion}。
                  {updateStatus.lastMessage ? ' ' + updateStatus.lastMessage : ''}
                </Banner>
              ) : null}

              {updateStatus.lastOutcome === 'Interrupted' || updateStatus.lastOutcome === 'Failed' ? (
                <Banner kind="error">{updateStatus.lastMessage ?? '上一次更新没有正常结束。'}</Banner>
              ) : null}

              {!updateStatus.releaseKeyValid ? (
                <Banner kind="error">
                  本程序内置的更新签名公钥无效，无法校验任何更新包。
                </Banner>
              ) : null}

              {!updateStatus.updaterAvailable ? (
                <Banner kind="warn">
                  安装目录中没有 <span className="mono">updater\LanDisk.Updater.exe</span>，
                  自动更新与自动回滚不可用。请使用完整的安装包升级，或重新解压发布包。
                </Banner>
              ) : null}

              <table className="grid">
                <tbody>
                  <tr><th>当前程序版本</th><td>{updateStatus.currentVersion}</td></tr>
                  <tr><th>已安装到磁盘的版本</th><td>{updateStatus.installedVersion}</td></tr>
                  <tr><th>安装目录</th><td className="mono">{updateStatus.installRoot}</td></tr>
                  <tr><th>更新状态</th><td>{updateStatus.stageText}{updateStatus.stageMessage ? ' — ' + updateStatus.stageMessage : ''}</td></tr>
                  <tr><th>更新签名公钥</th><td className="mono">{updateStatus.releaseKeyId}</td></tr>
                  <tr><th>包格式 / 上限</th><td className="mono">{updateStatus.packageExtension} / {formatBytes(updateStatus.maxPackageBytes)}</td></tr>
                </tbody>
              </table>

              <div>
                <h3>选择更新包</h3>
                <p className="muted">
                  只接受由 LanDisk 发布密钥签名的 <span className="mono">{updateStatus.packageExtension}</span> 包。
                  上传后会先校验签名和每个文件的哈希，校验不通过的内容不会写入磁盘；
                  安装前会自动备份当前程序与状态文件，重启后自检失败会自动回滚。
                  LanDisk 不会联网检查更新，也不会在后台自动安装任何东西。
                </p>
                <input
                  ref={updateFileInput}
                  type="file"
                  accept=".lupd,application/octet-stream"
                  disabled={updateBusy || applying}
                  onChange={(e) => {
                    const file = e.target.files?.[0]
                    if (file) void uploadPackage(file)
                  }}
                />
                {updateProgress ? (
                  <p className="muted">
                    正在上传 {formatBytes(updateProgress.sent)} / {formatBytes(updateProgress.total)}
                    {updateProgress.total > 0 ? `（${Math.floor((updateProgress.sent / updateProgress.total) * 100)}%）` : ''}
                  </p>
                ) : null}
              </div>

              {(updateResult ?? updateStatus.pending) ? (
                <div>
                  <h3>待安装的更新</h3>
                  <table className="grid">
                    <tbody>
                      <tr><th>目标版本</th><td>{updateResult?.version ?? updateStatus.pending?.version}</td></tr>
                      <tr>
                        <th>签名者</th>
                        <td className="mono">{updateResult?.signerKeyId ?? updateStatus.pending?.signerKeyId ?? '—'}</td>
                      </tr>
                      <tr>
                        <th>包 SHA256</th>
                        <td className="mono" style={{ wordBreak: 'break-all' }}>
                          {updateResult?.packageSha256 ?? updateStatus.pending?.packageSha256 ?? '—'}
                        </td>
                      </tr>
                      {updateResult ? (
                        <tr>
                          <th>内容</th>
                          <td>{updateResult.fileCount} 个文件，{formatBytes(updateResult.totalBytes)}，校验用时 {updateResult.verifySeconds} 秒</td>
                        </tr>
                      ) : null}
                      {updateResult?.plan ? (
                        <tr>
                          <th>将执行</th>
                          <td>
                            写入 {updateResult.plan.filesToWrite} 个文件，删除 {updateResult.plan.filesToDelete} 个过期文件，
                            共 {formatBytes(updateResult.plan.bytesToWrite)}
                          </td>
                        </tr>
                      ) : null}
                    </tbody>
                  </table>

                  {updateResult?.plan?.warnings?.length ? (
                    <Banner kind="warn">
                      {updateResult.plan.warnings.map((warning, index) => <div key={index}>{warning}</div>)}
                    </Banner>
                  ) : null}

                  {updateResult?.planError ? <Banner kind="warn">{updateResult.planError}</Banner> : null}

                  {(updateResult?.releaseNotes ?? updateStatus.pending?.releaseNotes) ? (
                    <details>
                      <summary>更新说明</summary>
                      <pre className="mono small" style={{ whiteSpace: 'pre-wrap' }}>
                        {updateResult?.releaseNotes ?? updateStatus.pending?.releaseNotes}
                      </pre>
                    </details>
                  ) : null}

                  <div className="row">
                    <button
                      className="primary"
                      disabled={applying || !updateStatus.updaterAvailable}
                      onClick={() => setConfirmApply(true)}
                    >
                      {applying ? '正在安装…' : '安装并重启'}
                    </button>
                    <button disabled={applying} onClick={() => void discardUpdate()}>放弃这个更新包</button>
                  </div>
                </div>
              ) : null}

              <div>
                <h3>更新记录</h3>
                <table className="grid data">
                  <thead><tr><th>时间</th><th>版本变化</th><th>结果</th><th>说明</th></tr></thead>
                  <tbody>
                    {(updateStatus.history ?? []).map((entry, index) => (
                      <tr key={index}>
                        <td className="muted">{formatTime(entry.finishedAt)}</td>
                        <td className="mono">{entry.fromVersion} → {entry.toVersion}</td>
                        <td>
                          <span className={`badge ${entry.outcome === 'Committed' ? 'ok' : 'err'}`}>
                            {entry.outcome === 'Committed' ? '完成' : entry.outcome === 'RolledBack' ? '已回滚' : entry.outcome}
                          </span>
                        </td>
                        <td className="muted">{entry.message ?? '—'}</td>
                      </tr>
                    ))}
                    {(updateStatus.history ?? []).length === 0 && (
                      <tr><td colSpan={4} className="muted">还没有更新记录。</td></tr>
                    )}
                  </tbody>
                </table>
              </div>
            </div>
          )}
        </div>
      )}

      {tab === 'diagnostics' && diagnostics && (
        <div className="card">
          <h2>自检与故障排查</h2>
          <ul className="list">
            {diagnostics.checks.map((check) => (
              <li key={check.id}>
                <span className={`badge ${check.status === 'ok' ? 'ok' : check.status === 'warn' ? 'warn' : ''}`}>{check.status}</span>
                <div className="entry-main"><span className="entry-sub">{check.message}</span></div>
              </li>
            ))}
          </ul>
        </div>
      )}

      {tab === 'audit' && (
        <div className="card">
          <h2>操作日志（仅保留最近若干条，已做脱敏）</h2>
          <table className="grid data">
            <thead><tr><th>时间</th><th>类别</th><th>动作</th><th>设备</th><th>路径</th><th>结果</th></tr></thead>
            <tbody>
              {audit.map((entry, index) => (
                <tr key={index}>
                  <td className="muted">{formatTime(entry.at)}</td>
                  <td>{entry.category}</td>
                  <td>{entry.action}</td>
                  <td>{entry.deviceName ?? '—'}</td>
                  <td className="mono muted">{entry.path ?? entry.detail ?? '—'}</td>
                  <td>{entry.success ? '成功' : <span className="badge err">失败</span>}</td>
                </tr>
              ))}
              {audit.length === 0 && <tr><td colSpan={6} className="muted">暂无日志。</td></tr>}
            </tbody>
          </table>
        </div>
      )}

      {pickerOpen && (
        <Modal title="选择要共享的目录" onClose={() => setPickerOpen(false)}>
          <div className="stack">
            <Banner kind="warn">
              只会共享你在这里明确选择的目录。LanDisk 不会扫描或自动共享其他磁盘。
            </Banner>
            <div className="crumbs">
              {pickerPath && <button className="small" onClick={() => void openPicker(pickerPath.replace(/[\\/][^\\/]+[\\/]?$/, '') || pickerPath)}>上一级</button>}
              <span className="mono muted">{pickerPath || '此电脑'}</span>
            </div>
            <ul className="list" style={{ maxHeight: '40vh', overflow: 'auto' }}>
              {pickerDirs.map((dir) => (
                <li key={dir.path}>
                  <span className="entry-icon">{dir.isDrive ? '💽' : '📁'}</span>
                  <div className="entry-main" onClick={() => void openPicker(dir.path)} role="button" tabIndex={0}>
                    <span className="entry-name">{dir.name}</span>
                    <span className="entry-sub mono">{dir.path}</span>
                  </div>
                  <button className="small" onClick={() => void openPicker(dir.path)}>进入</button>
                </li>
              ))}
            </ul>

            <label className="field">
              <span>共享名称（可选）</span>
              <input type="text" value={newShareName} onChange={(e) => setNewShareName(e.target.value)} placeholder="默认使用文件夹名" />
            </label>

            <div>
              <div className="muted">授予该共享的能力（设备自身权限会进一步限制）</div>
              <div className="check"><input type="checkbox" checked={newSharePerms.upload} onChange={(e) => setNewSharePerms({ ...newSharePerms, upload: e.target.checked })} /><span>允许上传</span></div>
              <div className="check"><input type="checkbox" checked={newSharePerms.rename} onChange={(e) => setNewSharePerms({ ...newSharePerms, rename: e.target.checked })} /><span>允许重命名</span></div>
              <div className="check"><input type="checkbox" checked={newSharePerms.delete} onChange={(e) => setNewSharePerms({ ...newSharePerms, delete: e.target.checked })} /><span>允许删除（移入 LanDisk 回收站）</span></div>
            </div>

            <div className="row">
              <button className="primary" disabled={!pickerPath} onClick={() => void addShare(pickerPath)}>共享此目录</button>
              <button onClick={() => setPickerOpen(false)}>取消</button>
            </div>
          </div>
        </Modal>
      )}

      {renamingDevice && (
        <NamePrompt
          title="重命名设备"
          label="设备名称"
          initial={renamingDevice.name}
          onConfirm={(value) => { void updateDevice(renamingDevice, { name: value }); setRenamingDevice(null) }}
          onCancel={() => setRenamingDevice(null)}
        />
      )}

      {confirmPurge && (
        <ConfirmDialog
          title="永久清空"
          danger
          confirmLabel="永久清空"
          message={<>确定要永久清空“{confirmPurge.name}”吗？此操作无法撤销。</>}
          onConfirm={() => { void apiPost(`/api/admin/recycle/${confirmPurge.id}/purge`, {}).then(() => loadTab('recycle')).catch((err) => setError(err instanceof ApiError ? err.message : '清空失败。')); setConfirmPurge(null) }}
          onCancel={() => setConfirmPurge(null)}
        />
      )}

      {confirmApply && (
        <ConfirmDialog
          title="安装更新并重启"
          confirmLabel="安装并重启"
          message={
            <>
              将安装 <b>{updateResult?.version ?? updateStatus?.pending?.version}</b>。
              安装过程会先备份当前程序与状态文件，然后停止 LanDisk、替换程序文件、重新启动并自检；
              自检不通过会自动回滚到 {updateStatus?.installedVersion}。
              <br /><br />
              正在进行的手机传输会中断，共享目录与配对设备不会被改动。
            </>
          }
          onConfirm={() => void applyUpdate()}
          onCancel={() => setConfirmApply(false)}
        />
      )}

      {confirmEmpty && (
        <ConfirmDialog
          title="永久清空回收站"
          danger
          confirmLabel="永久清空全部"
          message="确定要永久清空回收站中的全部对象吗？此操作无法撤销，也不等于系统回收站。"
          onConfirm={() => { void apiPost('/api/admin/recycle/empty', { confirm: true }).then(() => loadTab('recycle')).catch((err) => setError(err instanceof ApiError ? err.message : '清空失败。')); setConfirmEmpty(false) }}
          onCancel={() => setConfirmEmpty(false)}
        />
      )}
    </div>
  )
}

function tabName(tab: Tab): string {
  switch (tab) {
    case 'status': return '状态'
    case 'onboard': return '连接 iPhone'
    case 'shares': return '共享'
    case 'devices': return '设备'
    case 'pairing': return '添加设备'
    case 'network': return '网络'
    case 'tls': return 'HTTPS'
    case 'recycle': return '回收站'
    case 'update': return '系统更新'
    case 'diagnostics': return '自检'
    case 'audit': return '日志'
  }
}

/** The host a pairing QR points at. The phone must keep using it: pairing credentials are host-scoped. */
function pairingHost(url: string): string {
  try { return new URL(url).host } catch { return url }
}

/** Origin only — pairing URLs carry a one-time token in the fragment, so never render the whole string. */
function originOf(url: string): string {
  try { return new URL(url).origin } catch { return url }
}