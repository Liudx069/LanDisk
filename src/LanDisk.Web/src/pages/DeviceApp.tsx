import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  apiDelete,
  apiGet,
  apiPost,
  entrySizeText,
  fileUrl,
  formatBytes,
  formatRelative,
  formatTime,
  mobileTheme,
  refreshCsrfFromCookie,
  setCsrf,
  extensionCommands,
  extensionExecute,
  extensionTasks,
  hostStatus
} from '../api'
import { noteActivity, onPresence, startPresence } from '../presence'
import { usePreferences } from '../prefs'
import {
  ApiError,
  type BatchPlan, type BatchResponse, type Entry, type FavoriteItem, type Listing, type Me,
  type Permissions, type Presence, type RecentResponse, type RecycleEntry, type StorageItem, type Transfer,
  type HostStatus,
  type ExtensionDescriptor, type ExtensionTask
} from '../types'
import EntryList from '../components/EntryList'
import FileViewer from '../components/FileViewer'
import { EntryActionsSheet, FilterSheet, SortSheet, defaultFilters, sizePresetBytes, type FilterState } from '../components/Sheets'
import { EmptyState, Icon, Spinner, categoryIcon } from '../components/icons'
import { Banner, ConfirmDialog, NamePrompt, Modal, ProgressBar, Sheet } from '../components/ui'
import { toCssColor } from '../themeColor.mjs'
import {
  MAX_RESUME_PREFIX, cancelUpload, fingerprintFile, hashPrefix, listPendingUploads,
  uploadFile, type PendingUpload, type UploadProgress
} from '../upload'

type View = 'home' | 'files' | 'recent' | 'favorites' | 'recycle' | 'transfers' | 'device'
type ConflictChoice = 'rename' | 'skip' | 'replace'

/**
 * A theme role (`#AARRGGBB`) as a translucent CSS colour, for the few places that need a tint rather than a
 * solid fill. Keeping this local means a profile never has to describe transparency.
 */
/** Drops any alpha channel from a colour, so a theme cannot make a surface see-through. */
function stripAlpha(hex: string): string {
  const value = hex.replace('#', '')
  return value.length === 8 ? '#' + value.slice(2) : hex
}

function hexWithAlpha(hex: string, alpha: number): string {
  const value = hex.replace('#', '')
  const rgb = value.length === 8 ? value.slice(2) : value
  if (rgb.length !== 6) return hex
  const r = parseInt(rgb.slice(0, 2), 16)
  const g = parseInt(rgb.slice(2, 4), 16)
  const b = parseInt(rgb.slice(4, 6), 16)
  return `rgba(${r}, ${g}, ${b}, ${alpha})`
}

// toCssColor 放在 ../themeColor.mjs：应用与回归测试引用**同一份实现**，
// 避免"测的转换"和"跑的转换"不是同一段代码。见该模块头部注释说明 #AARRGGBB 与
// CSS #RRGGBBAA 的字节错位为什么会把整页染粉。

export default function DeviceApp() {
  const [me, setMe] = useState<Me | null>(null)
  const [loading, setLoading] = useState(true)
  const [fatal, setFatal] = useState<string | null>(null)
  const [view, setView] = useState<View>('home')

  // The authority the server reports for this phone, refreshed by the heartbeat. The UI is built from this,
  // never from what the device record said when the phone paired.
  const [permissions, setPermissions] = useState<Permissions | null>(null)
  const [presence, setPresence] = useState<Presence | null>(null)
  const themeRevisionRef = useRef(0)
  const permissionsRef = useRef(false)

  const canWrite = permissions?.writeFiles ?? false
  permissionsRef.current = canWrite

  /** Why the write controls are unavailable, in the user's words rather than the server's. */
  const readOnlyReason = canWrite
    ? ''
    : permissions === null
      ? '正在确认这台手机的权限…'
      : '这台手机目前是只读权限：可以浏览、下载和播放，不能上传、重命名、删除或新建。'

  const [shareId, setShareId] = useState('')

  const [path, setPath] = useState('')
  const [listing, setListing] = useState<Listing | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<{ message: string; detail?: string } | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const [search, setSearch] = useState('')
  const [debouncedSearch, setDebouncedSearch] = useState('')
  const [filters, setFilters] = useState<FilterState>(defaultFilters)
  const [prefs, updatePrefs] = usePreferences()

  const [selectionMode, setSelectionMode] = useState(false)
  const [selected, setSelected] = useState<Set<string>>(new Set())

  const [viewer, setViewer] = useState<Entry | null>(null)
  const [renaming, setRenaming] = useState<Entry | null>(null)
  const [deleting, setDeleting] = useState<Entry | null>(null)
  const [actions, setActions] = useState<Entry | null>(null)
  const [creatingFolder, setCreatingFolder] = useState(false)
  const [sortOpen, setSortOpen] = useState(false)
  const [filterOpen, setFilterOpen] = useState(false)
  const [conflictPrompt, setConflictPrompt] = useState<{ files: File[]; conflicts: string[] } | null>(null)
  const [batchPlan, setBatchPlan] = useState<BatchPlan | null>(null)
  const [batchResult, setBatchResult] = useState<BatchResponse | null>(null)
  const [confirmBatchDelete, setConfirmBatchDelete] = useState(false)
  const [working, setWorking] = useState(false)

  const [recent, setRecent] = useState<RecentResponse | null>(null)
  const [favorites, setFavorites] = useState<FavoriteItem[]>([])
  const [recycle, setRecycle] = useState<RecycleEntry[]>([])
  const [transfers, setTransfers] = useState<Transfer[]>([])
  const [storage, setStorage] = useState<StorageItem[]>([])
  const [uploads, setUploads] = useState<UploadProgress[]>([])
  const [pending, setPending] = useState<PendingUpload[]>([])

  const fileInputRef = useRef<HTMLInputElement>(null)
  const abortRef = useRef<AbortController | null>(null)
  const resumeTargets = useRef<Map<string, PendingUpload>>(new Map())
  const appliedDefaults = useRef(false)
  // The last permission revision the phone acted on; a change means "re-read /api/me".
  const permissionRevisionRef = useRef<number | null>(null)

  const currentShare = useMemo(() => me?.shares.find((s) => s.id === shareId) ?? null, [me, shareId])
  const capabilities = listing?.capabilities ?? { canRead: true, canUpload: false, canRename: false, canDelete: false }
  const favoritePaths = useMemo(() => new Set(favorites.map((f) => `${f.shareId}|${f.path}`)), [favorites])
  const selectedEntries = useMemo(
    () => (listing?.entries ?? []).filter((e) => selected.has(e.path)),
    [listing, selected]
  )

  /**
   * The one reason the user cannot do what they came to do, in priority order.
   *
   * Share unreachable > share grants nothing > this device is read-only > a specific capability is
   * missing. Only the first applies: showing several at once is what made the Owner's screenshot
   * ambiguous about which one actually blocked them.
   */
  const blocker = useMemo(() => {
    if (!currentShare) return null

    if (currentShare.state !== 'Online') {
      return {
        kind: 'share-missing' as const,
        title: '共享磁盘当前不可访问',
        detail: currentShare.stateDetail
          ?? '这台电脑上的共享目录暂时打不开（磁盘未接入、被移除或离线）。这不是权限问题，接回磁盘即可。'
      }
    }

    if (!capabilities.canRead) {
      return {
        kind: 'share-permission' as const,
        title: '这个共享没有向这台手机开放',
        detail: '请在电脑端 LanDisk 的「文件与共享」里检查这个目录的共享权限。'
      }
    }

    if (!canWrite) {
      return {
        kind: 'device-readonly' as const,
        title: '这台手机目前是只读权限',
        detail: '可以浏览、下载和播放，不能上传、重命名、删除或新建。在电脑端「设备」页把它改成读写，下一次刷新就会生效，不需要重新配对。'
      }
    }

    return null
  }, [currentShare, capabilities.canRead, canWrite])

  // ---------------------------------------------------------------- session

  const loadMe = useCallback(async () => {
    try {
      refreshCsrfFromCookie()
      const result = await apiGet<Me>('/api/me')
      setCsrf(result.csrfToken)
      setMe(result)
      if (result.permissions) setPermissions(result.permissions)
      if (result.presence) setPresence(result.presence)
      setFatal(null)
      setShareId((current) => current || result.shares[0]?.id || '')
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        setFatal('这台设备还没有配对，或者会话已被电脑端撤销。')
      } else {
        setFatal(err instanceof ApiError ? err.message : '无法连接电脑端。')
      }
    } finally {
      setLoading(false)
    }
  }, [])

  /**
   * Applies the appearance chosen on the Windows side.
   *
   * The server sends two complete profiles (dark and light) plus the id it chose. A "follow the device"
   * profile is resolved here with the phone's own preference, which is the only thing the phone is allowed
   * to decide on its own — everything else comes from the operator. The values are written as CSS variables
   * so no page component needs to know a colour.
   */
  const applyMobileTheme = useCallback(async () => {
    try {
      const theme = await mobileTheme()
      themeRevisionRef.current = theme.revision
      const prefersDark = window.matchMedia?.('(prefers-color-scheme: dark)').matches ?? true

      // "Follow the device" is the operator letting the phone decide light or dark; every other profile is
      // already decided (the server sends the same completed profile twice), so the phone just applies it.
      const profile = theme.followsDevice ? (prefersDark ? theme.dark : theme.light) : theme.dark

      const root = document.documentElement
      // The semantic names are the contract; the older short names stay as aliases so the existing
      // stylesheet keeps working. Writing only the short names is how `.modal.viewer { background:
      // var(--window) }` ended up transparent — the variable it asked for did not exist, so the viewer
      // surface fell back to "no background" and the page behind it showed through.
      const map: Record<string, string[]> = {
        window: ['--window', '--bg'],
        navigation: ['--navigation'],
        surface: ['--surface', '--panel'],
        surfaceSecondary: ['--surface-secondary', '--panel-2'],
        surfaceElevated: ['--surface-elevated'],
        textPrimary: ['--text-primary', '--text'],
        textSecondary: ['--text-secondary', '--muted'],
        border: ['--border'],
        accent: ['--accent'],
        accentHover: ['--accent-hover'],
        selection: ['--accent-soft'],
        success: ['--success', '--ok'],
        warning: ['--warning', '--warn'],
        danger: ['--danger']
      }

      for (const [role, variables] of Object.entries(map)) {
        const value = profile.roles[role]
        if (!value) continue
        // 必须经过 toCssColor：#AARRGGBB 直接写进 CSS 会被当成 #RRGGBBAA（见该函数注释）。
        const css = toCssColor(value)
        for (const variable of variables) root.style.setProperty(variable, css)
      }

      // An elevated surface for overlays: derived from the theme's own surface so a red accent cannot
      // turn the viewer into a red pane — the accent belongs on buttons and selection, not on a layer.
      const elevated = profile.roles.surfaceElevated ?? profile.roles.surface ?? profile.roles.window
      if (elevated) root.style.setProperty('--surface-elevated', stripAlpha(elevated))

      // The scrim is the theme's window colour at high opacity: dark enough that nothing behind the
      // viewer stays legible, and still the product's own colour rather than an unrelated black.
      root.style.setProperty('--overlay-scrim', hexWithAlpha(profile.roles.window ?? '#0b0f14', 0.92))

      // The warning banner's own tint follows the theme's warning colour, so a custom theme does not keep
      // the built-in tint behind differently-coloured text.
      const warning = profile.roles.warning
      if (warning) root.style.setProperty('--warn-bg', hexWithAlpha(warning, 0.16))

      root.dataset.theme = profile.appearance
      root.dataset.themeProfile = profile.id
    } catch {
      // No theme service (for example an older server) simply means the built-in stylesheet applies.
    }
  }, [])

  useEffect(() => { void loadMe() }, [loadMe])

  useEffect(() => {
    const onVisible = () => { if (document.visibilityState === 'visible') void loadMe() }
    document.addEventListener('visibilitychange', onVisible)
    return () => document.removeEventListener('visibilitychange', onVisible)
  }, [loadMe])

  // Presence lives in a module (src/presence.ts), not here.
  //
  // It used to be an effect in this component, which meant anything that remounted the subtree — opening the
  // file viewer, switching to favourites, entering a transfer, going fullscreen — could take the heartbeat
  // with it. The Owner's phone was in the foreground playing a video while Windows still reported "0 online".
  // The heartbeat is now started once for the life of the page and this component only subscribes to it.
  useEffect(() => {
    if (!me || me.kind !== 'device') return
    startPresence()
    return onPresence((snapshot) => {
      if (snapshot.permissions) setPermissions(snapshot.permissions)
      if (snapshot.presence) setPresence(snapshot.presence)

      // The revision moves when the level *or* any capability changes. Comparing the old single boolean
      // missed extension, network and system-view changes, and could not tell "unchanged" from "already
      // applied" — so the phone could sit on stale capabilities until something else forced a reload.
      const revision = snapshot.permissionRevision
      if (typeof revision === 'number' && revision !== permissionRevisionRef.current) {
        permissionRevisionRef.current = revision
        void loadMe()
        // The per-share capabilities the buttons read come from the *listing*, not from /api/me. Reloading
        // only /api/me is why the Owner set the device to read-write on Windows and the phone still showed
        // no upload, rename or delete: the device level had changed, the open directory had not.
        void loadListing()
      }

      if (typeof snapshot.themeRevision === 'number' && snapshot.themeRevision !== themeRevisionRef.current) {
        // 不要在这里先推进 ref 再抓取。
        //
        // 原写法先把 themeRevisionRef.current 设成 snapshot 的修订号，然后才去 fetch；而 fetch 失败会被
        // applyMobileTheme 里的 catch 静默吞掉。结果是：**只要有一次主题抓取失败**（Safari 切到后台、
        // 瞬时网络错误、服务重启瞬间），ref 就已经前进，此后每个心跳都判定"修订号没变"，
        // 于是页面永久停在旧主题上，直到用户整页重载才可能恢复。
        //
        // 现在只负责"发现变化就重新应用"；推进 ref 由 applyMobileTheme 在**成功之后**完成
        // （它用服务器返回的 revision 赋值），失败则 ref 保持不动，下一次心跳会自动重试。
        void applyMobileTheme()
      }
    })
  }, [me, applyMobileTheme, loadMe])

  // The appearance the operator chose on the Windows side, applied as CSS variables. The profile is data
  // (colour roles), never CSS or script, so nothing the operator types can become executable content here.
  useEffect(() => { void applyMobileTheme() }, [applyMobileTheme])

  useEffect(() => () => abortRef.current?.abort(), [])

  // The first non-empty share becomes the initial location; the user lands in files, not in a menu.
  useEffect(() => {
    if (!appliedDefaults.current && me && me.shares.length > 0) {
      appliedDefaults.current = true
      setShareId(me.shares[0].id)
      setView('files')
    }
  }, [me])

  // ---------------------------------------------------------------- data

  const loadListing = useCallback(async () => {
    if (!shareId) { setListing(null); return }
    setBusy(true)
    setError(null)
    try {
      const params = new URLSearchParams({
        shareId,
        path,
        sort: prefs.sort,
        desc: String(prefs.descending),
        limit: '300'
      })
      if (debouncedSearch.trim()) {
        params.set('search', debouncedSearch.trim())
        params.set('scope', filters.scope)
      }
      if (filters.category !== 'all') params.set('category', filters.category)
      if (filters.dateFrom) params.set('after', filters.dateFrom)
      if (filters.dateTo) params.set('before', filters.dateTo)
      const size = sizePresetBytes[filters.sizePreset]
      if (size.min !== undefined) params.set('minSize', String(size.min))
      if (size.max !== undefined) params.set('maxSize', String(size.max))

      const result = await apiGet<Listing>(`/api/browse?${params.toString()}`)
      setListing(result)
    } catch (err) {
      setListing(null)
      setError(toError(err, '读取目录失败。'))
    } finally {
      setBusy(false)
    }
  }, [shareId, path, prefs.sort, prefs.descending, debouncedSearch, filters])

  const loadRecent = useCallback(async () => {
    try {
      setRecent(await apiGet<RecentResponse>('/api/recent?limit=60'))
    } catch (err) {
      setError(toError(err, '读取最近文件失败。'))
    }
  }, [])

  const loadFavorites = useCallback(async () => {
    try {
      setFavorites((await apiGet<{ items: FavoriteItem[] }>('/api/favorites')).items)
    } catch (err) {
      setError(toError(err, '读取收藏失败。'))
    }
  }, [])

  const loadRecycle = useCallback(async () => {
    try {
      setRecycle((await apiGet<{ entries: RecycleEntry[] }>('/api/recycle')).entries)
    } catch (err) {
      setError(toError(err, '读取回收站失败。'))
    }
  }, [])

  const loadTransfers = useCallback(async () => {
    try {
      setTransfers((await apiGet<{ transfers: Transfer[] }>('/api/transfers?limit=60')).transfers)
    } catch { /* informational only */ }
  }, [])

  const loadStorage = useCallback(async () => {
    try {
      setStorage((await apiGet<{ items: StorageItem[] }>('/api/storage')).items)
    } catch { /* informational only */ }
  }, [])

  const loadPending = useCallback(async () => {
    const list = await listPendingUploads()
    setPending(list.filter((p) => p.state === 'Open' || p.state === 'open'))
  }, [])

  useEffect(() => { void loadListing() }, [loadListing])

  useEffect(() => {
    const timer = window.setTimeout(() => setDebouncedSearch(search), 320)
    return () => window.clearTimeout(timer)
  }, [search])

  useEffect(() => {
    if (view === 'recent') void loadRecent()
    if (view === 'favorites') { void loadFavorites(); void loadStorage() }
    if (view === 'recycle') void loadRecycle()
    if (view === 'transfers') { void loadTransfers(); void loadPending() }
    if (view === 'device') void loadStorage()
  }, [view, loadRecent, loadFavorites, loadRecycle, loadTransfers, loadPending, loadStorage])

  // Favourites drive the star icon in the list, so they are loaded once up front as well.
  useEffect(() => { if (me) void loadFavorites() }, [me, loadFavorites])

  // ---------------------------------------------------------------- helpers

  function toError(err: unknown, fallback: string): { message: string; detail?: string } {
    if (err instanceof ApiError) return { message: err.message, detail: err.code + (err.technical ? '\n' + err.technical : '') }
    return { message: err instanceof Error ? err.message : fallback }
  }

  function resetSelection() {
    setSelectionMode(false)
    setSelected(new Set())
  }

  function navigate(nextPath: string) {
    setPath(nextPath)
    setSearch('')
    setDebouncedSearch('')
    resetSelection()
  }

  function openView(next: View) {
    setView(next)
    // Switching screens is activity: tell presence now instead of waiting for the next 15 s tick.
    noteActivity('route:' + next)
    if (next !== 'files') resetSelection()
  }

  function openEntry(entry: Entry) {
    if (entry.isDirectory) {
      if (entry.isLink) {
        setError({ message: '这是一个链接（符号链接/junction），本版本不跟随，无法打开。' })
        return
      }
      navigate(entry.path)
      return
    }
    // Opening a viewer is the moment the Owner's phone looked "offline" while a video played: the heartbeat
    // used to be owned by whatever was mounted, so the viewer replaced it. It is now an explicit activity
    // signal instead.
    noteActivity('viewer')
    setViewer(entry)
  }

  function toggleSelect(entry: Entry) {
    setSelected((current) => {
      const next = new Set(current)
      if (next.has(entry.path)) next.delete(entry.path)
      else next.add(entry.path)
      return next
    })
  }

  function enterSelection(entry: Entry) {
    setSelectionMode(true)
    setSelected(new Set([entry.path]))
  }

  // ---------------------------------------------------------------- operations

  async function doRename(entry: Entry, newName: string) {
    if (!listing) return
    setWorking(true)
    try {
      await apiPost('/api/file/rename', { shareId: listing.shareId, path: entry.path, newName, version: entry.version })
      setRenaming(null)
      setNotice(`已重命名为“${newName}”。`)
      await loadListing()
    } catch (err) {
      setError(toError(err, '重命名失败。'))
    } finally {
      setWorking(false)
    }
  }

  async function doDelete(entry: Entry) {
    if (!listing) return
    setWorking(true)
    try {
      await apiPost('/api/file/delete', { shareId: listing.shareId, path: entry.path, version: entry.version })
      setDeleting(null)
      setNotice(`“${entry.name}”已移入回收站，可在回收站恢复。`)
      await loadListing()
    } catch (err) {
      setError(toError(err, '删除失败。'))
    } finally {
      setWorking(false)
    }
  }

  async function doCreateFolder(name: string) {
    if (!listing) return
    setWorking(true)
    try {
      await apiPost('/api/file/folder', { shareId: listing.shareId, path: listing.path, name })
      setCreatingFolder(false)
      setNotice(`已新建文件夹“${name}”。`)
      await loadListing()
    } catch (err) {
      setError(toError(err, '新建文件夹失败。'))
    } finally {
      setWorking(false)
    }
  }

  async function toggleFavorite(entry: Entry) {
    if (!listing) return
    const key = `${listing.shareId}|${entry.path}`
    const existing = favorites.find((f) => `${f.shareId}|${f.path}` === key)
    try {
      if (existing) {
        await apiDelete(`/api/favorites/${existing.id}`)
        setNotice(`已取消收藏“${entry.name}”。`)
      } else {
        await apiPost('/api/favorites', { shareId: listing.shareId, path: entry.path })
        setNotice(`已收藏“${entry.name}”。`)
      }
      await loadFavorites()
    } catch (err) {
      setError(toError(err, '收藏操作失败。'))
    }
  }

  async function doRestore(entry: RecycleEntry) {
    try {
      await apiPost(`/api/recycle/${entry.id}/restore`, {})
      setNotice(`“${entry.name}”已恢复。`)
      await loadRecycle()
      if (entry.shareId === shareId) await loadListing()
    } catch (err) {
      if (err instanceof ApiError && err.code === 'CONFLICT') {
        const alternate = window.prompt('原位置已有同名文件，请输入新的名称以另存恢复：', entry.name + ' (恢复)')
        if (!alternate) return
        try {
          await apiPost(`/api/recycle/${entry.id}/restore`, { alternateName: alternate })
          setNotice(`已恢复为“${alternate}”。`)
          await loadRecycle()
        } catch (inner) {
          setError(toError(inner, '恢复失败。'))
        }
        return
      }
      setError(toError(err, '恢复失败。'))
    }
  }

  // ---------------------------------------------------------------- upload

  async function handleFilesPicked(files: FileList | null) {
    if (!files || files.length === 0 || !listing) return
    const queue = Array.from(files)

    // Name conflicts are surfaced before anything is transferred instead of being resolved
    // silently by the server.
    const existing = new Set(listing.entries.map((e) => e.name.toLowerCase()))
    const conflicts = queue.filter((f) => existing.has(f.name.toLowerCase())).map((f) => f.name)
    if (conflicts.length > 0) {
      setConflictPrompt({ files: queue, conflicts })
      return
    }

    await runUpload(queue, 'rename')
  }

  async function runUpload(queue: File[], policy: ConflictChoice) {
    if (!listing) return

    // "Replace" is implemented as an explicit, recoverable delete to the recycle bin, never as a
    // silent server side overwrite.
    if (policy === 'replace') {
      const existing = new Map(listing.entries.map((e) => [e.name.toLowerCase(), e]))
      const targets = queue
        .map((f) => existing.get(f.name.toLowerCase()))
        .filter((e): e is Entry => Boolean(e))
      if (targets.length > 0) {
        try {
          await apiPost('/api/batch/delete', {
            shareId: listing.shareId,
            items: targets.map((e) => ({ path: e.path, version: e.version }))
          })
          setNotice(`已有 ${targets.length} 个同名文件被移入回收站，原文件仍可恢复。`)
        } catch (err) {
          setError(toError(err, '无法替换同名文件。'))
          return
        }
      }
    }

    const csrf = refreshCsrfFromCookie()
    const controller = new AbortController()
    abortRef.current = controller

    const initial: UploadProgress[] = queue.map((file, index) => ({
      fileId: `${Date.now()}-${index}-${file.name}`,
      fileName: file.name,
      totalBytes: file.size,
      transferredBytes: 0,
      state: 'queued'
    }))
    setUploads((prev) => [...initial, ...prev])

    for (let i = 0; i < queue.length; i++) {
      const file = queue[i]
      const item = initial[i]
      try {
        const existingSession = resumeTargets.current.get(file.name)
        let existingUploadId: string | undefined

        if (existingSession && existingSession.declaredLength === file.size && existingSession.receivedBytes > 0) {
          const fingerprint = await fingerprintFile(file)
          if (fingerprint.hash === existingSession.fingerprint) {
            if (existingSession.receivedBytes > MAX_RESUME_PREFIX) {
              setNotice(`“${file.name}”已上传较多内容，超过浏览器可校验的前缀上限，已改为重新上传。`)
            } else {
              await hashPrefix(file, existingSession.receivedBytes)
              existingUploadId = existingSession.uploadId
            }
          } else {
            await cancelUpload(existingSession.uploadId, csrf)
            setNotice(`“${file.name}”与未完成的会话内容不一致，已放弃旧会话并重新上传。`)
          }
        }

        const outcome = await uploadFile(item.fileId, file, {
          shareId: listing.shareId,
          directory: listing.path,
          csrfToken: csrf,
          conflictPolicy: policy === 'rename' ? 'rename' : 'fail',
          onProgress: (update) => setUploads((prev) => prev.map((u) => (u.fileId === update.fileId ? { ...u, ...update } : u))),
          signal: controller.signal
        }, existingUploadId)

        resumeTargets.current.delete(file.name)
        setNotice(`“${outcome.name}”上传完成，已提交到服务器。`)
      } catch (err) {
        const aborted = err instanceof DOMException && err.name === 'AbortError'
        setUploads((prev) => prev.map((u) => (u.fileId === item.fileId
          ? { ...u, state: aborted ? 'cancelled' : 'failed', error: aborted ? '已取消' : (err instanceof ApiError ? err.message : String(err)) }
          : u)))
        if (!aborted) setError(toError(err, `“${file.name}”上传失败。`))
      }
    }

    abortRef.current = null
    await loadListing()
    await loadPending()
  }

  async function prepareResume() {
    const list = await listPendingUploads()
    const open = list.filter((p) => p.state === 'Open' || p.state === 'open')
    setPending(open)
    resumeTargets.current = new Map(open.map((p) => [p.fileName, p]))
    if (open.length === 0) {
      setNotice('没有可以续传的上传会话。')
      return
    }
    setNotice(`请重新选择这些文件以继续上传：${open.map((p) => p.fileName).join('、')}`)
    fileInputRef.current?.click()
  }

  // ---------------------------------------------------------------- batch

  async function planBatchDownload() {
    if (!listing || selectedEntries.length === 0) return
    setWorking(true)
    try {
      const plan = await apiPost<BatchPlan>('/api/batch/plan', {
        shareId: listing.shareId,
        items: selectedEntries.map((e) => ({ path: e.path, version: e.version }))
      })
      setBatchPlan(plan)
    } catch (err) {
      if (reportSelectionChanged(err)) return
      setError(toError(err, '无法准备下载。'))
    } finally {
      setWorking(false)
    }
  }

  async function startBatchDownload() {
    if (!listing || selectedEntries.length === 0) return
    setWorking(true)
    try {
      const ticket = await apiPost<{ url: string; archiveName: string }>('/api/batch/ticket', {
        shareId: listing.shareId,
        items: selectedEntries.map((e) => ({ path: e.path, version: e.version }))
      })
      setBatchPlan(null)
      setNotice(`开始下载“${ticket.archiveName}”。下载由浏览器负责，本应用无法确认是否已保存完成。`)

      // A plain navigation lets the archive stream to disk instead of being buffered in memory.
      const link = document.createElement('a')
      link.href = ticket.url
      link.rel = 'noopener'
      document.body.appendChild(link)
      link.click()
      link.remove()
    } catch (err) {
      if (reportSelectionChanged(err)) return
      setError(toError(err, '无法开始下载。'))
    } finally {
      setWorking(false)
    }
  }

  /**
   * The server refuses to build an archive from a selection that no longer matches what is on disk.
   * That is not a generic failure, so it does not get a generic message: the user is told which
   * files moved, the stale selection is dropped, and the listing is refreshed so the next attempt
   * is made against reality.
   */
  function reportSelectionChanged(err: unknown): boolean {
    if (!(err instanceof ApiError) || err.code !== 'CHANGED_SINCE_SELECTION') return false

    const extra = err.extra as { changedCount?: number; totalCount?: number; items?: { path: string; error?: string }[] } | undefined
    const names = (extra?.items ?? []).map((item) => item.path).slice(0, 8)
    const remaining = (extra?.items?.length ?? 0) - names.length

    setBatchPlan(null)
    resetSelection()
    setError({
      message: extra?.changedCount
        ? `你选择的 ${extra.totalCount ?? ''} 项中有 ${extra.changedCount} 项在下载开始前被改动或删除，本次归档已取消，选择已清空。`
        : '选择的项目在下载开始前发生了变化，本次归档已取消，选择已清空。',
      detail: names.length > 0
        ? '涉及：' + names.join('、') + (remaining > 0 ? ` 等 ${remaining + names.length} 项` : '')
        : undefined
    })
    void loadListing()
    return true
  }

  async function doBatchDelete() {
    if (!listing || selectedEntries.length === 0) return
    setWorking(true)
    try {
      const result = await apiPost<BatchResponse>('/api/batch/delete', {
        shareId: listing.shareId,
        items: selectedEntries.map((e) => ({ path: e.path, version: e.version }))
      })
      setConfirmBatchDelete(false)
      resetSelection()
      // Per-item detail is only worth a dialog when something went wrong; a clean run reports
      // through the normal notice so the user is not interrupted.
      if (result.failed > 0) {
        setBatchResult(result)
      } else {
        setNotice(`已把 ${result.succeeded} 项移入回收站，可以在回收站里逐项恢复。`)
      }
      await loadListing()
    } catch (err) {
      setError(toError(err, '批量删除失败。'))
    } finally {
      setWorking(false)
    }
  }

  async function unpair() {
    try { await apiPost('/api/logout', {}) } catch { /* ignore */ }
    location.assign('/pair')
  }

  // ---------------------------------------------------------------- render

  if (loading) {
    return <div className="app"><div className="card center pad-lg"><Spinner label="正在连接电脑端…" /></div></div>
  }

  if (fatal) {
    return (
      <div className="app">
        <header className="top"><h1>LanDisk</h1></header>
        <Banner kind="error">{fatal}</Banner>
        <div className="card">
          <p>请在电脑上打开 LanDisk 管理页面生成配对码，然后<a href="/pair">进入配对页面</a>。</p>
        </div>
      </div>
    )
  }

  const effectiveFilters = search.trim() ? filters : { ...filters, scope: 'Folder' as const }
  const filterActive = filters.category !== 'all' || filters.dateFrom !== '' || filters.dateTo !== '' || filters.sizePreset !== 'any'

  return (
    <div className="shell">
      <aside className="rail">
        <div className="rail-brand"><Icon name="server" size={20} /><span>LanDisk</span></div>
        <nav>
          <RailButton icon="folder" label="文件" active={view === 'files'} onClick={() => openView('files')} />
          <RailButton icon="clock" label="最近" active={view === 'recent'} onClick={() => openView('recent')} />
          <RailButton icon="star" label="收藏" active={view === 'favorites'} onClick={() => openView('favorites')} />
          <RailButton icon="trash" label="回收站" active={view === 'recycle'} onClick={() => openView('recycle')} />
          <RailButton icon="upload" label="传输" active={view === 'transfers'} onClick={() => openView('transfers')} />
        </nav>
        <div className="rail-section">
          <h3>共享位置</h3>
          <ul>
            {(me?.shares ?? []).map((share) => (
              <li key={share.id}>
                <button
                  className={`rail-share${shareId === share.id && view === 'files' ? ' active' : ''}`}
                  onClick={() => { setShareId(share.id); setPath(''); setSearch(''); setView('files'); resetSelection() }}
                >
                  <Icon name="folder" size={16} />
                  <span className="ellipsis">{share.name}</span>
                  {share.state !== 'Online' ? <span className="badge warn">离线</span> : null}
                </button>
              </li>
            ))}
          </ul>
        </div>
        <nav className="rail-foot">
          <RailButton icon="info" label="本机信息" active={view === 'device'} onClick={() => openView('device')} />
        </nav>
      </aside>

      <main className="main">
        <header className="topbar">
          {view === 'files' && path ? (
            <button className="icon-btn" onClick={() => navigate(parentPath(path))} aria-label="返回上一级"><Icon name="back" /></button>
          ) : null}
          <h1 className="topbar-title">
            {view === 'home' ? 'LanDisk'
              : view === 'files' ? (listing?.breadcrumbs[listing.breadcrumbs.length - 1]?.name ?? currentShare?.name ?? '文件')
                : view === 'recent' ? '最近'
                  : view === 'favorites' ? '收藏'
                    : view === 'recycle' ? '回收站'
                      : view === 'transfers' ? '传输' : '本机信息'}
          </h1>
          {view === 'files' || view === 'recent' ? (
            <div className="search-box">
              <Icon name="search" size={17} />
              <input
                type="search"
                value={search}
                placeholder={view === 'files' ? '搜索文件或文件夹' : '搜索'}
                onChange={(e) => setSearch(e.target.value)}
                aria-label="搜索"
              />
              {search ? <button className="icon-btn small" onClick={() => setSearch('')} aria-label="清除搜索"><Icon name="close" size={15} /></button> : null}
            </div>
          ) : null}
          <span className="spacer" />
          <button className="icon-btn" onClick={() => void loadMe()} aria-label="刷新"><Icon name="refresh" size={18} /></button>
        </header>

        {error ? (
          <Banner kind="error" onDismiss={() => setError(null)}>
            {error.message}
            {error.detail ? <details className="error-detail"><summary>技术详情</summary><pre className="mono">{error.detail}</pre></details> : null}
          </Banner>
        ) : null}
        {notice ? <Banner kind="ok" onDismiss={() => setNotice(null)}>{notice}</Banner> : null}
      {presence && presence.state !== 'online' ? (
        <Banner kind="info">本机看到这台设备的状态：{presence.state === 'recently-active' ? '最近活动' : '离线'}（最后活动 {formatRelative(presence.lastSeenAt)}）。</Banner>
      ) : null}

        {view === 'files' ? (
          <>
            {/* Exactly one blocker, with the reason that actually stops the user.
                The Owner's screenshot had "共享根目录当前不可访问" *and* "这台手机目前是只读权限" on screen
                together, which leaves the reader unable to tell which one is the real obstacle. The order is
                deliberate: an unreachable share beats a permission problem (nothing can be done with a disk
                that is not there), a share that grants nothing beats the device level (the operator's device
                setting is not the reason), and only then does the device's own read-only level apply. */}
            {blocker ? (
              <Banner kind="warn">
                <strong>{blocker.title}</strong>
                {blocker.detail ? <> {blocker.detail}</> : null}
              </Banner>
            ) : null}

            {listing?.truncated ? (
              <Banner kind="warn">结果较多，已到达服务器上限，请使用搜索或筛选缩小范围。</Banner>
            ) : null}

            <div className="crumbs">
              {(listing?.breadcrumbs ?? [{ name: currentShare?.name ?? '根目录', path: '' }]).map((crumb, index, all) => (
                <span key={crumb.path + index} className="crumb">
                  <button onClick={() => navigate(crumb.path)}>{crumb.name}</button>
                  {index < all.length - 1 ? <Icon name="chevronRight" size={14} /> : null}
                </span>
              ))}
            </div>

            <div className="toolbar">
              <button className="chip" onClick={() => setSortOpen(true)}>
                <Icon name="sort" size={16} />{sortLabel(prefs.sort, prefs.descending)}
              </button>
              <button className={`chip${filterActive ? ' on' : ''}`} onClick={() => setFilterOpen(true)}>
                <Icon name="filter" size={16} />筛选{filterActive ? ' •' : ''}
              </button>
              <button className="chip" onClick={() => updatePrefs({ view: prefs.view === 'list' ? 'grid' : 'list' })}>
                <Icon name={prefs.view === 'list' ? 'grid' : 'list'} size={16} />{prefs.view === 'list' ? '网格' : '列表'}
              </button>
              <span className="spacer" />
              {/* Shown disabled rather than hidden when the phone may not write: a missing button looks like
                  a missing feature, whereas a disabled one with a reason tells the user what to change and
                  where. `capabilities` is the server's intersection of share flags and the device's *current*
                  authority, re-read on every permission change. */}
              <button
                className="primary"
                disabled={!capabilities.canUpload}
                title={capabilities.canUpload ? undefined : readOnlyReason}
                onClick={() => fileInputRef.current?.click()}
              >
                <Icon name="upload" size={16} />上传
              </button>
              <button
                className="icon-btn"
                disabled={!capabilities.canUpload}
                title={capabilities.canUpload ? undefined : readOnlyReason}
                onClick={() => setCreatingFolder(true)}
                aria-label="新建文件夹"
              >
                <Icon name="folder" size={18} />
              </button>
            </div>

            <div className="list-meta muted">
              {listing ? `${listing.total} 项` : '—'}
              {listing && listing.total > 0 ? ` · ${listing.scanMs} 毫秒` : ''}
              {listing && listing.scanned > listing.total ? ` · 已扫描 ${listing.scanned}` : ''}
              {selectionMode ? ' · 点击选择项目' : ''}
            </div>

            {busy && !listing ? <div className="pad-lg center"><Spinner /></div> : null}

            {listing && listing.entries.length === 0 ? (
              <EmptyState
                icon={search ? 'search' : 'folder'}
                title={search ? '没有匹配的项目' : '这个文件夹是空的'}
                hint={search ? '换一个关键词，或选择更大的搜索范围。' : undefined}
                action={capabilities.canUpload && !search
                  ? <button className="primary" onClick={() => fileInputRef.current?.click()}><Icon name="upload" size={16} />上传文件</button>
                  : undefined}
              />
            ) : null}

            {listing && listing.entries.length > 0 ? (
              <EntryList
                entries={listing.entries}
                shareId={listing.shareId}
                view={prefs.view}
                selectionMode={selectionMode}
                selected={selected}
                onOpen={openEntry}
                onToggleSelect={toggleSelect}
                onEnterSelection={enterSelection}
                onMore={(entry) => setActions(entry)}
              />
            ) : null}

            {listing?.hasMore ? (
              <div className="center muted small">还有 {listing.total - listing.offset - listing.entries.length} 项未显示，请使用搜索或筛选。</div>
            ) : null}

            <input
              ref={fileInputRef}
              className="hidden-input"
              type="file"
              multiple
              onChange={(e) => { void handleFilesPicked(e.target.files); e.target.value = '' }}
            />

            {uploads.length > 0 ? (
              <section className="card">
                <div className="row">
                  <h2 className="grow">上传队列</h2>
                  <button className="small" onClick={() => setUploads([])}>清除</button>
                  <button className="small danger" onClick={() => abortRef.current?.abort()}>全部取消</button>
                </div>
                <ul className="list">
                  {uploads.map((u) => (
                    <li key={u.fileId}>
                      <div className="entry-main">
                        <span className="entry-name">{u.fileName}</span>
                        <ProgressBar value={u.transferredBytes} max={u.totalBytes} />
                        <span className="entry-sub">
                          {formatBytes(u.transferredBytes)} / {formatBytes(u.totalBytes)} · {uploadStateText(u.state)}
                          {u.error ? ' · ' + u.error : ''}
                        </span>
                      </div>
                    </li>
                  ))}
                </ul>
                <p className="muted small">服务端校验完整字节数并落盘后才显示「已提交」。未完成的文件不会出现在列表里。</p>
              </section>
            ) : null}

            {pending.length > 0 ? (
              <section className="card">
                <h2>未完成的上传</h2>
                <ul className="list">
                  {pending.map((p) => (
                    <li key={p.uploadId}>
                      <div className="entry-main">
                        <span className="entry-name">{p.fileName}</span>
                        <span className="entry-sub">{formatBytes(p.receivedBytes)} / {formatBytes(p.declaredLength)} · 目标 {p.path || '根目录'}</span>
                      </div>
                    </li>
                  ))}
                </ul>
                <button className="primary" onClick={() => void prepareResume()}>重新选择文件继续上传</button>
              </section>
            ) : null}
          </>
        ) : null}

        {view === 'home' ? (
          <HomeView
            me={me}
            storage={storage}
            onOpenShare={(id) => { setShareId(id); setPath(''); setView('files'); resetSelection() }}
            onOpenView={openView}
            onUpload={() => { setView('files'); window.setTimeout(() => fileInputRef.current?.click(), 50) }}
          />
        ) : null}

        {view === 'recent' ? (
          <RecentView
            recent={recent}
            search={search}
            onOpen={(shareIdValue, entry) => {
              setShareId(shareIdValue)
              setPath(entry.isDirectory ? entry.path : parentPath(entry.path))
              setSearch('')
              setView('files')
              if (!entry.isDirectory) { noteActivity('viewer'); setViewer(entry) }
            }}
          />
        ) : null}

        {view === 'favorites' ? (
          <FavoritesView
            favorites={favorites}
            onOpen={(favorite) => {
              setShareId(favorite.shareId)
              setPath(favorite.isDirectory ? favorite.path : parentPath(favorite.path))
              setSearch('')
              setView('files')
            }}
            onRemove={async (favorite) => {
              try {
                await apiDelete(`/api/favorites/${favorite.id}`)
                await loadFavorites()
              } catch (err) {
                setError(toError(err, '取消收藏失败。'))
              }
            }}
          />
        ) : null}

        {view === 'recycle' ? (
          <section className="card">
            <div className="row">
              <h2 className="grow">LanDisk 回收站</h2>
              <button className="small" onClick={() => void loadRecycle()}>刷新</button>
            </div>
            <p className="muted small">这里的「删除」是本应用的受控回收，不是 Windows 系统回收站。恢复时会重新校验共享和磁盘。</p>
            {recycle.length === 0 ? <EmptyState icon="trash" title="回收站是空的" /> : null}
            <ul className="list">
              {recycle.map((entry) => (
                <li key={entry.id}>
                  <span className="entry-icon"><Icon name={entry.isDirectory ? 'folder' : 'file'} size={24} /></span>
                  <div className="entry-main">
                    <span className="entry-name">{entry.name}</span>
                    <span className="entry-sub">{entry.shareName} / {entry.path} · {entry.sizeText} · {formatTime(entry.deletedAt)}</span>
                  </div>
                  <button className="small primary" onClick={() => void doRestore(entry)}>恢复</button>
                </li>
              ))}
            </ul>
          </section>
        ) : null}

        {view === 'transfers' ? (
          <section className="card">
            <div className="row">
              <h2 className="grow">传输记录</h2>
              <button className="small" onClick={() => void loadTransfers()}>刷新</button>
            </div>
            <p className="muted small">
              下载状态表示「服务器已把内容交给浏览器」。本应用无法确认文件是否已经保存到设备本地，也不会把服务端发送完成当成保存成功。
            </p>
            <table className="grid">
              <thead><tr><th>类型</th><th>文件</th><th>进度</th><th>状态</th><th>时间</th></tr></thead>
              <tbody>
                {transfers.map((t) => (
                  <tr key={t.id}>
                    <td>{t.direction === 'Upload' ? '上传' : '下载'}</td>
                    <td className="mono">{t.fileName}</td>
                    <td>{formatBytes(t.transferredBytes)} / {formatBytes(t.totalBytes)}</td>
                    <td>{uploadStateText(t.state)}</td>
                    <td className="muted">{formatTime(t.updatedAt)}</td>
                  </tr>
                ))}
                {transfers.length === 0 ? <tr><td colSpan={5} className="muted">暂无记录。</td></tr> : null}
              </tbody>
            </table>
          </section>
        ) : null}

        {view === 'device' && me ? (
          <section className="card">
            <h2>本机信息</h2>
            <table className="grid">
              <tbody>
                <tr><th>设备名称</th><td>{me.device?.name ?? '—'}</td></tr>
                <tr><th>权限</th><td>{me.device?.level === 'ReadWrite' ? '可读写' : '只读（可查看和下载）'}</td></tr>
                <tr><th>配对时间</th><td>{formatTime(me.device?.createdAt ?? '')}</td></tr>
                <tr><th>最近活动</th><td>{formatTime(me.device?.lastSeenAt ?? '')}</td></tr>
                <tr><th>传输加密</th><td>{location.protocol === 'https:' ? 'HTTPS' : 'HTTP（局域网兼容模式，安全性降低）'}</td></tr>
              </tbody>
            </table>

            <h3>存储空间</h3>
            <table className="grid">
              <thead><tr><th>共享</th><th>可用 / 总计</th><th>状态</th></tr></thead>
              <tbody>
                {storage.map((item) => (
                  <tr key={item.shareId}>
                    <td>{item.name}</td>
                    <td>{item.freeText} / {item.totalText}</td>
                    <td>{item.state === 'Online' ? '在线' : <span className="badge warn">离线</span>}</td>
                  </tr>
                ))}
                {storage.length === 0 ? <tr><td colSpan={3} className="muted">暂无数据。</td></tr> : null}
              </tbody>
            </table>

            <h3>可用操作</h3>
            <table className="grid">
              <thead><tr><th>共享</th><th>权限</th></tr></thead>
              <tbody>
                {me.shares.map((share) => (
                  <tr key={share.id}>
                    <td>{share.name}</td>
                    <td>{[share.canRead && '查看', share.canUpload && '上传', share.canRename && '重命名', share.canDelete && '删除'].filter(Boolean).join('、') || '无'}</td>
                  </tr>
                ))}
                {me.shares.length === 0 ? <tr><td colSpan={2} className="muted">电脑端尚未共享任何目录。</td></tr> : null}
              </tbody>
            </table>


            {/* Extension commands need their own capability too. */}
            {permissions?.executeExtensions ? <ExtensionsPanel /> : null}

            {/* What this phone may know about the computer it is talking to. */}
            <HostStatusPanel />

            <div className="row pad-top">
              <button className="danger" onClick={() => void unpair()}><Icon name="logout" size={16} />解除本机配对</button>
            </div>
            <p className="muted small">
              解除配对只清除这台设备上的登录状态。已下载到本地的文件不会被删除，电脑端仍保留这台设备的记录，可以在电脑端彻底撤销。
            </p>
          </section>
        ) : null}
      </main>

      {/* The phone's main navigation. Every area the product has on the phone is here, so nothing depends
          on the desktop sidebar (which an iPhone shows as a drawer most people never open) and nothing is
          more than one tap away. */}
      <nav className="tabbar" aria-label="主导航">
        <TabButton icon="folder" label="文件" active={view === 'files'} onClick={() => openView('files')} />
        <TabButton icon="clock" label="最近" active={view === 'recent'} onClick={() => openView('recent')} />
        <TabButton icon="star" label="收藏" active={view === 'favorites'} onClick={() => openView('favorites')} />
        <TabButton icon="upload" label="传输" active={view === 'transfers'} onClick={() => openView('transfers')} />
        <TabButton icon="trash" label="回收站" active={view === 'recycle'} onClick={() => openView('recycle')} />
        <TabButton icon="info" label="我的" active={view === 'device'} onClick={() => openView('device')} />
      </nav>

      {selectionMode ? (
        <div className="selection-bar">
          <button className="icon-btn" onClick={resetSelection} aria-label="退出多选"><Icon name="close" /></button>
          <span className="grow">已选择 {selected.size} 项</span>
          <button className="small" disabled={selected.size === 0 || working} onClick={() => void planBatchDownload()}>
            <Icon name="download" size={16} />下载 ZIP
          </button>
          {capabilities.canDelete ? (
            <button className="small danger" disabled={selected.size === 0 || working} onClick={() => setConfirmBatchDelete(true)}>
              <Icon name="trash" size={16} />删除
            </button>
          ) : null}
        </div>
      ) : null}

      {viewer && listing ? (
        <FileViewer
          entry={viewer}
          shareId={listing.shareId}
          capabilities={capabilities}
          isFavorite={favoritePaths.has(`${listing.shareId}|${viewer.path}`)}
          onToggleFavorite={() => void toggleFavorite(viewer)}
          onClose={() => setViewer(null)}
          onRename={() => { setRenaming(viewer); setViewer(null) }}
          onDelete={() => { setDeleting(viewer); setViewer(null) }}
        />
      ) : null}

      {actions && listing ? (
        <EntryActionsSheet
          entry={actions}
          canRename={capabilities.canRename}
          canDelete={capabilities.canDelete}
          isFavorite={favoritePaths.has(`${listing.shareId}|${actions.path}`)}
          favoriteBusy={false}
          onOpen={() => { setViewer(actions); setActions(null) }}
          onDownload={() => {
            const link = document.createElement('a')
            link.href = fileUrl('download', listing.shareId, actions.path)
            link.rel = 'noopener'
            document.body.appendChild(link)
            link.click()
            link.remove()
            setActions(null)
          }}
          onRename={() => { setRenaming(actions); setActions(null) }}
          onToggleFavorite={() => { void toggleFavorite(actions); setActions(null) }}
          onDelete={() => { setDeleting(actions); setActions(null) }}
          onSelect={() => { enterSelection(actions); setActions(null) }}
          onClose={() => setActions(null)}
        />
      ) : null}

      {sortOpen ? (
        <SortSheet
          sort={prefs.sort}
          descending={prefs.descending}
          onPick={(sort, descending) => updatePrefs({ sort, descending })}
          onClose={() => setSortOpen(false)}
        />
      ) : null}

      {filterOpen ? (
        <FilterSheet
          filters={effectiveFilters}
          resultCount={listing?.total ?? null}
          onChange={(next) => setFilters(next)}
          onReset={() => setFilters(defaultFilters)}
          onClose={() => setFilterOpen(false)}
        />
      ) : null}

      {renaming ? (
        <NamePrompt
          title="重命名"
          label={`“${renaming.name}”的新名称`}
          initial={renaming.name}
          selectBasename
          busy={working}
          onConfirm={(value) => void doRename(renaming, value)}
          onCancel={() => setRenaming(null)}
        />
      ) : null}

      {deleting ? (
        <ConfirmDialog
          title="移入回收站"
          message={<>确定要把“{deleting.name}”移入 LanDisk 回收站吗？删除后可以在这里恢复。</>}
          confirmLabel="移入回收站"
          danger
          busy={working}
          onConfirm={() => void doDelete(deleting)}
          onCancel={() => setDeleting(null)}
        />
      ) : null}

      {creatingFolder ? (
        <NamePrompt
          title="新建文件夹"
          label="文件夹名称"
          initial="新建文件夹"
          confirmLabel="创建"
          busy={working}
          onConfirm={(value) => void doCreateFolder(value)}
          onCancel={() => setCreatingFolder(false)}
        />
      ) : null}

      {conflictPrompt ? (
        <Sheet title="存在同名文件" onClose={() => setConflictPrompt(null)}>
          <p>
            以下 {conflictPrompt.conflicts.length} 个文件已存在：
            <strong>{conflictPrompt.conflicts.join('、')}</strong>
          </p>
          <p className="muted small">LanDisk 不会静默覆盖已存在的文件。请选择处理方式：</p>
          <ul className="option-list">
            <li><button className="option" onClick={() => { const files = conflictPrompt.files; setConflictPrompt(null); void runUpload(files, 'rename') }}>
              <Icon name="check" size={18} /><span>另存为副本（推荐）</span>
            </button></li>
            <li><button className="option" onClick={() => {
              const existing = new Set(listing?.entries.map((e) => e.name.toLowerCase()) ?? [])
              const remaining = conflictPrompt.files.filter((f) => !existing.has(f.name.toLowerCase()))
              setConflictPrompt(null)
              if (remaining.length === 0) { setNotice('选中的文件都已存在，已全部跳过。'); return }
              void runUpload(remaining, 'rename')
            }}>
              <Icon name="close" size={18} /><span>跳过同名文件</span>
            </button></li>
            <li><button className="option danger" onClick={() => { const files = conflictPrompt.files; setConflictPrompt(null); void runUpload(files, 'replace') }}>
              <Icon name="trash" size={18} /><span>把已有文件移入回收站后上传</span>
            </button></li>
          </ul>
        </Sheet>
      ) : null}

      {batchPlan ? (
        <Modal title="下载 ZIP" onClose={() => setBatchPlan(null)}>
          <div className="stack">
            <table className="grid">
              <tbody>
                <tr><th>归档名称</th><td className="mono">{batchPlan.archiveName}</td></tr>
                <tr><th>文件数量</th><td>{batchPlan.fileCount}</td></tr>
                <tr><th>总大小</th><td>{batchPlan.totalSizeText}</td></tr>
              </tbody>
            </table>
            {batchPlan.truncated ? (
              <Banner kind="warn">归档将被截断：{batchPlan.truncationReason ?? '达到服务器上限'}。</Banner>
            ) : null}
            {batchPlan.skipped.length > 0 ? (
              <div>
                <h3>以下 {batchPlan.skipped.length} 项无法包含</h3>
                <ul className="list compact">
                  {batchPlan.skipped.map((item) => (
                    <li key={item.path}>
                      <div className="entry-main">
                        <span className="entry-name">{item.path}</span>
                        <span className="entry-sub">{item.error}</span>
                      </div>
                    </li>
                  ))}
                </ul>
              </div>
            ) : null}
            <div className="row">
              <button className="primary" disabled={working || batchPlan.fileCount === 0} onClick={() => void startBatchDownload()}>
                {working ? <Spinner /> : <Icon name="download" size={16} />}开始下载
              </button>
              <button onClick={() => setBatchPlan(null)}>取消</button>
            </div>
            <p className="muted small">下载由浏览器负责，本应用只确认已开始发送，无法确认文件是否已保存完成。</p>
          </div>
        </Modal>
      ) : null}

      {confirmBatchDelete ? (
        <ConfirmDialog
          title="批量删除"
          danger
          busy={working}
          confirmLabel={`移入回收站（${selected.size} 项）`}
          message={<>确定要把选中的 {selected.size} 项移入 LanDisk 回收站吗？之后可以逐项恢复。</>}
          onConfirm={() => void doBatchDelete()}
          onCancel={() => setConfirmBatchDelete(false)}
        />
      ) : null}

      {batchResult ? (
        <Modal title="批量删除结果" onClose={() => setBatchResult(null)}>
          <div className="stack">
            <p>
              共 {batchResult.requested} 项，成功 {batchResult.succeeded} 项
              {batchResult.failed > 0 ? `，失败 ${batchResult.failed} 项` : ''}。
            </p>
            {batchResult.failed > 0 ? (
              <ul className="list compact">
                {batchResult.results.filter((r) => !r.ok).map((r) => (
                  <li key={r.path}>
                    <div className="entry-main">
                      <span className="entry-name">{r.path}</span>
                      <span className="entry-sub">{r.error}</span>
                    </div>
                  </li>
                ))}
              </ul>
            ) : null}
            <button className="primary" onClick={() => setBatchResult(null)}>知道了</button>
          </div>
        </Modal>
      ) : null}
    </div>
  )
}

// ------------------------------------------------------------------ sub views

function HomeView({
  me,
  storage,
  onOpenShare,
  onOpenView,
  onUpload
}: {
  me: Me | null
  storage: StorageItem[]
  onOpenShare: (shareId: string) => void
  onOpenView: (view: View) => void
  onUpload: () => void
}) {
  const shares = me?.shares ?? []
  return (
    <>
      <section className="card">
        <h2>共享位置</h2>
        {shares.length === 0 ? (
          <EmptyState icon="folder" title="电脑端还没有共享任何目录"
            hint="请在电脑的 LanDisk 管理页面添加共享文件夹。" />
        ) : null}
        <ul className="list">
          {shares.map((share) => {
            const capacity = storage.find((s) => s.shareId === share.id)
            return (
              <li key={share.id}>
                <span className="entry-icon"><Icon name="server" size={22} /></span>
                <div className="entry-main" onClick={() => onOpenShare(share.id)} role="button" tabIndex={0}
                     onKeyDown={(e) => { if (e.key === 'Enter') onOpenShare(share.id) }}>
                  <span className="entry-name">{share.name}</span>
                  <span className="entry-sub">
                    {share.state === 'Online'
                      ? (capacity ? `可用 ${capacity.freeText} / ${capacity.totalText}` : '在线')
                      : (share.stateDetail ?? '离线')}
                  </span>
                </div>
                {share.state !== 'Online' ? <span className="badge warn">离线</span> : null}
                <button className="small" onClick={() => onOpenShare(share.id)}>打开</button>
              </li>
            )
          })}
        </ul>
      </section>

      <section className="quick-grid">
        <button className="quick" onClick={() => onOpenView('recent')}><Icon name="clock" size={22} /><span>最近</span></button>
        <button className="quick" onClick={() => onOpenView('favorites')}><Icon name="star" size={22} /><span>收藏</span></button>
        <button className="quick" onClick={() => onOpenView('recycle')}><Icon name="trash" size={22} /><span>回收站</span></button>
        <button className="quick" onClick={() => onOpenView('transfers')}><Icon name="upload" size={22} /><span>传输</span></button>
      </section>

      <button className="primary block big" onClick={onUpload}>
        <Icon name="upload" size={18} />上传文件
      </button>

      <p className="muted small center">
        新设备默认只有查看和下载权限。上传、重命名、删除需要在电脑端管理页面单独开启。
      </p>
    </>
  )
}

/**
 * Turns the server's machine readable truncation reasons into a sentence a person can act on.
 *
 * "到达上限" on its own is not actionable: the user needs to know whether a whole share was only
 * partly scanned (so some files are simply missing from the list) or whether the scan was capped
 * while collecting (so the list is a true subset of the newest files). Those have different fixes,
 * so they get different wording.
 */
export function describeRecentTruncation(recent: RecentResponse): string {
  const scanLimited = (recent.truncatedReasons ?? [])
    .filter((reason) => reason.startsWith('scan_limit:'))
    .map((reason) => reason.slice('scan_limit:'.length))
  const shareLimited = (recent.truncatedReasons ?? [])
    .filter((reason) => reason.startsWith('per_share_limit:'))
    .map((reason) => reason.slice('per_share_limit:'.length))

  if (scanLimited.length > 0) {
    return `“${scanLimited.join('、')}”内的文件太多，本次扫描在读完之前就停下来了，`
      + '列表里可能缺少较新的文件。缩小目录范围或减少文件数量后可以看全。'
  }

  if (shareLimited.length > 0) {
    return `“${shareLimited.join('、')}”中最近修改的文件超过了单个共享的显示上限，`
      + '这里只列出最新的部分。更早的文件可以在对应目录里找到。'
  }

  return '文件较多，结果已到达服务器上限，这里只列出其中一部分。'
}

function RecentView({  recent,
  search,
  onOpen
}: {
  recent: RecentResponse | null
  search: string
  onOpen: (shareId: string, entry: Entry) => void
}) {
  if (!recent) return <div className="pad-lg center"><Spinner /></div>

  const items = search.trim()
    ? recent.items.filter((item) => item.entry.name.toLowerCase().includes(search.trim().toLowerCase()))
    : recent.items

  return (
    <section className="card">
      <div className="row">
        <h2 className="grow">最近修改</h2>
        <span className="muted small">{recent.windowDays} 天内</span>
      </div>
      {recent.truncated ? <Banner kind="warn">{describeRecentTruncation(recent)}</Banner> : null}
      {items.length === 0 ? <EmptyState icon="clock" title="没有最近修改的文件" /> : null}
      <ul className="list">
        {items.map((item) => (
          <li key={item.shareId + item.entry.path}>
            <span className={`entry-icon cat-${item.entry.category}`}>
              <Icon name={categoryIcon(item.entry.category, item.entry.isDirectory, item.entry.isLink)} size={22} />
            </span>
            <div className="entry-main" onClick={() => onOpen(item.shareId, item.entry)} role="button" tabIndex={0}
                 onKeyDown={(e) => { if (e.key === 'Enter') onOpen(item.shareId, item.entry) }}>
              <span className="entry-name">{item.entry.name}</span>
              <span className="entry-sub">{item.shareName} · {entrySizeText(item.entry)} · {formatRelative(item.entry.modifiedAt)}</span>
            </div>
          </li>
        ))}
      </ul>
    </section>
  )
}

function FavoritesView({
  favorites,
  onOpen,
  onRemove
}: {
  favorites: FavoriteItem[]
  onOpen: (favorite: FavoriteItem) => void
  onRemove: (favorite: FavoriteItem) => void
}) {
  return (
    <section className="card">
      <h2>收藏</h2>
      {favorites.length === 0 ? (
        <EmptyState icon="star" title="还没有收藏"
          hint="在文件的「…」菜单里可以收藏文件或文件夹。收藏按设备保存。" />
      ) : null}
      <ul className="list">
        {favorites.map((favorite) => {
          const broken = favorite.status !== 'ok'
          return (
            <li key={favorite.id}>
              <span className="entry-icon"><Icon name={favorite.isDirectory ? 'folder' : 'star'} size={22} /></span>
              <div className="entry-main" onClick={() => { if (!broken) onOpen(favorite) }} role="button" tabIndex={0}
                   onKeyDown={(e) => { if (e.key === 'Enter' && !broken) onOpen(favorite) }}>
                <span className="entry-name">{favorite.name}</span>
                <span className="entry-sub">
                  {favorite.shareName} / {favorite.path}
                  {broken ? ` · ${statusText(favorite.status)}` : ''}
                </span>
                {broken && favorite.statusDetail ? <span className="entry-sub">{favorite.statusDetail}</span> : null}
              </div>
              {broken ? <span className="badge warn">失效</span> : null}
              <button className="small danger" onClick={() => onRemove(favorite)}>移除</button>
            </li>
          )
        })}
      </ul>
    </section>
  )
}

// ------------------------------------------------------------------ small parts

function RailButton({ icon, label, active, onClick }: { icon: Parameters<typeof Icon>[0]['name']; label: string; active: boolean; onClick: () => void }) {
  return (
    <button className={`rail-btn${active ? ' active' : ''}`} onClick={onClick}>
      <Icon name={icon} size={18} /><span>{label}</span>
    </button>
  )
}

/**
 * Extensions on the phone.
 *
 * Only rendered with the ExtensionExecute capability. The phone can name an extension id, a command id and
 * arguments that must satisfy the command's declared schema — it can never supply a path, a working directory
 * or a command line, because the server resolves the entrypoint from the manifest approved on the PC.
 */
function ExtensionsPanel() {
  const [extensions, setExtensions] = useState<ExtensionDescriptor[]>([])
  const [tasks, setTasks] = useState<ExtensionTask[]>([])
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState<string | null>(null)

  const load = useCallback(async () => {
    setBusy(true)
    try {
      setExtensions((await extensionCommands()).extensions)
      setTasks((await extensionTasks()).tasks)
      setNotice(null)
    } catch (err) {
      setNotice(err instanceof ApiError ? err.message : '无法读取扩展。')
    } finally {
      setBusy(false)
    }
  }, [])

  useEffect(() => { void load() }, [load])

  const run = async (extension: ExtensionDescriptor, commandId: string) => {
    const command = extension.commands.find((c) => c.id === commandId)
    let args: unknown = {}
    if (command?.argumentsSchema) {
      const raw = window.prompt(
        `「${command.name}」的参数（JSON，必须符合扩展声明的 schema）`,
        JSON.stringify(defaultsFor(command.argumentsSchema), null, 2)
      )
      if (raw === null) return
      try {
        args = JSON.parse(raw)
      } catch {
        setNotice('参数不是合法的 JSON。')
        return
      }
    }

    setBusy(true)
    try {
      await extensionExecute(extension.id, commandId, args)
      setNotice('命令已提交，执行结果会显示在下方任务列表。')
      await load()
    } catch (err) {
      setNotice(err instanceof ApiError ? err.message : '命令未能执行。')
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="card">
      <h2>扩展命令</h2>
      {notice ? <Banner kind={notice.includes('无法') || notice.includes('不能') || notice.includes('不是') ? 'warn' : 'ok'} onDismiss={() => setNotice(null)}>{notice}</Banner> : null}
      <p className="muted small">
        扩展由电脑端安装并批准。这里只能运行扩展自己声明的命令，并且参数必须符合它声明的 schema；
        扩展无法在本机提供可执行文件路径或命令行。
      </p>

      <ul className="list">
        {extensions.map((extension) => (
          <li key={extension.id} className="entry-row">
            <div className="grow">
              <span className="entry-name">{extension.name} <span className="muted small">{extension.version}</span></span>
              <span className="entry-sub">{extension.publisher || '未知发布者'}</span>
            </div>
            {extension.commands.map((command) => (
              <button key={command.id} className="small" disabled={busy} onClick={() => void run(extension, command.id)}>
                {command.name}
              </button>
            ))}
          </li>
        ))}
        {extensions.length === 0 ? <li className="muted">电脑端还没有已批准的扩展。</li> : null}
      </ul>

      {tasks.length > 0 ? (
        <>
          <h3>任务记录</h3>
          <table className="grid">
            <thead><tr><th>命令</th><th>状态</th><th>退出码</th></tr></thead>
            <tbody>
              {tasks.slice(0, 20).map((task) => (
                <tr key={task.id}>
                  <td>{task.extensionName} · {task.commandId}</td>
                  <td>{taskLabel(task.state)}</td>
                  <td>{task.state === 'Succeeded' || task.state === 'Failed' ? task.exitCode : '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      ) : null}
    </section>
  )
}

/**
 * "本机状态": what this phone can see of the computer it is connected to.
 *
 * The base block answers the questions a user actually has — is it running, which address should I use, is
 * there a disk, what am I allowed to do here. The machine block (CPU, memory, throughput, how many phones are
 * online, recent warnings) is only present when this device holds the SystemView capability, and the server
 * omits it entirely otherwise: a phone without the capability does not receive the numbers and hide them, it
 * never receives them.
 */
function HostStatusPanel() {
  const [state, setState] = useState<HostStatus | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    setBusy(true)
    try {
      setState(await hostStatus())
      setError(null)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '无法读取电脑端状态。')
    } finally {
      setBusy(false)
    }
  }, [])

  useEffect(() => { void load() }, [load])

  if (error) return <section className="card"><h2>本机状态</h2><Banner kind="warn">{error}</Banner></section>
  if (!state) return <section className="card"><h2>本机状态</h2><div className="pad-lg center"><Spinner /></div></section>

  const s = state.status
  const storage = s.storage.available
    ? `${formatBytes(s.storage.freeBytes)} 可用 / ${formatBytes(s.storage.totalBytes)}（剩余 ${s.storage.freePercent.toFixed(0)}%）`
    : '共享存储当前不可用'

  return (
    <section className="card">
      <h2>本机状态</h2>
      {busy ? <div className="muted small">正在刷新…</div> : null}
      <table className="grid">
        <tbody>
          <tr><th>LanDisk 版本</th><td>{s.version}</td></tr>
          <tr><th>服务状态</th><td>{s.serviceState === 'running' ? '运行中' : s.serviceState}</td></tr>
          <tr><th>运行时长</th><td>{formatDuration(s.uptimeSeconds)}</td></tr>
          <tr><th>推荐访问地址</th><td>{s.recommendedOrigin ?? '—'}{s.recommendedKind === 'physical' ? '（物理地址）' : s.recommendedKind === 'stable' ? '（稳定名称）' : ''}</td></tr>
          <tr><th>HTTPS</th><td>{s.httpsEnabled ? '已启用' : '未启用'}</td></tr>
          <tr><th>共享</th><td>{s.shares.enabled} 个已启用 / 共 {s.shares.total} 个</td></tr>
          <tr><th>可用存储</th><td>{storage}</td></tr>
          <tr><th>当前传输</th><td>{s.transfers.active} 个进行中 · 今日 ↑ {formatBytes(s.transfers.todayUploadBytes)} / ↓ {formatBytes(s.transfers.todayDownloadBytes)}</td></tr>
          <tr><th>这台手机的权限</th><td>{s.you.level === 'ReadWrite' ? '可读写' : '只读'}{describePermissions(s.you.permissions)}</td></tr>
        </tbody>
      </table>

      {state.detailed && state.machine ? (
        <>
          <h3>机器负载</h3>
          <table className="grid">
            <tbody>
              <tr><th>进程 CPU</th><td>{state.machine.processCpuPercent.toFixed(1)}%</td></tr>
              <tr><th>系统 CPU</th><td>{state.machine.systemCpuPercent.toFixed(1)}%</td></tr>
              <tr><th>内存占用</th><td>{formatBytes(state.machine.memoryUsedBytes)}</td></tr>
              <tr><th>网络速率</th><td>↑ {formatBytes(state.machine.uploadBytesPerSecond)}/s · ↓ {formatBytes(state.machine.downloadBytesPerSecond)}/s</td></tr>
              <tr><th>在线设备</th><td>{state.machine.onlineDevices} 在线 · {state.machine.recentlyActiveDevices} 最近活动 · 共 {state.machine.totalDevices}</td></tr>
              <tr><th>VPN / 隧道</th><td>{state.machine.vpnOrTunnel ? '存在（可能影响稳定名称）' : '无'}</td></tr>
            </tbody>
          </table>
          {state.machine.problems.length > 0 ? (
            <>
              <h3>最近警告</h3>
              <ul className="list">
                {state.machine.problems.map((p, index) => (
                  <li key={p.at + index} className="entry-row">
                    <div className="grow">
                      <span className="entry-name">{p.action}</span>
                      <span className="entry-sub">{formatTime(p.at)} · {p.category}{p.detail ? ' · ' + p.detail : ''}</span>
                    </div>
                  </li>
                ))}
              </ul>
            </>
          ) : null}
        </>
      ) : (
        <p className="muted small">详细机器状态（CPU / 内存 / 网络速率 / 在线设备）需要在电脑端为这台手机授予「系统查看」能力。</p>
      )}
    </section>
  )
}

function describePermissions(permissions: Permissions): string {
  const parts = [
    permissions.writeFiles ? '文件写入' : null,
    permissions.installExtensions ? '扩展安装' : null,
    permissions.executeExtensions ? '扩展执行' : null,
    permissions.viewSystem ? '系统查看' : null
  ].filter(Boolean)
  return parts.length > 0 ? '（' + parts.join('、') + '）' : ''
}

function formatDuration(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) return '—'
  const days = Math.floor(seconds / 86400)
  const hours = Math.floor((seconds % 86400) / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)
  if (days > 0) return `${days} 天 ${hours} 小时`
  if (hours > 0) return `${hours} 小时 ${minutes} 分钟`
  return `${minutes} 分钟`
}

function defaultsFor(schema: Record<string, unknown>): Record<string, unknown> {
  const properties = (schema.properties ?? {}) as Record<string, { type?: string }>
  const result: Record<string, unknown> = {}
  for (const [key, value] of Object.entries(properties)) {
    result[key] = value?.type === 'boolean' ? false : value?.type === 'integer' || value?.type === 'number' ? 0 : ''
  }
  return result
}

function taskLabel(state: ExtensionTask['state']): string {
  switch (state) {
    case 'Queued': return '排队中'
    case 'Running': return '运行中'
    case 'Succeeded': return '成功'
    case 'Failed': return '失败'
    case 'Cancelled': return '已取消'
    default: return '超时'
  }
}

function TabButton({ icon, label, active, onClick }: { icon: Parameters<typeof Icon>[0]['name']; label: string; active: boolean; onClick: () => void }) {
  return (
    <button className={`tab-btn${active ? ' active' : ''}`} onClick={onClick}>
      <Icon name={icon} size={22} /><span>{label}</span>
    </button>
  )
}

function sortLabel(sort: string, descending: boolean): string {
  switch (sort) {
    case 'Size': return descending ? '大小 大→小' : '大小 小→大'
    case 'Modified': return descending ? '时间 新→旧' : '时间 旧→新'
    case 'Type': return descending ? '类型 Z→A' : '类型 A→Z'
    default: return descending ? '名称 Z→A' : '名称 A→Z'
  }
}

function statusText(status: FavoriteItem['status']): string {
  switch (status) {
    case 'missing': return '目标已不存在'
    case 'disabled': return '共享已关闭'
    case 'denied': return '没有权限'
    case 'offline': return '磁盘离线'
    case 'unreadable': return '暂时无法读取'
    default: return '可用'
  }
}

function uploadStateText(state: string): string {
  switch (state) {
    case 'queued': return '排队中'
    case 'hashing': return '校验中'
    case 'uploading': return '传输中'
    case 'processing': return '处理中'
    case 'committed': return '已提交'
    case 'failed': return '失败'
    case 'cancelled': return '已取消'
    case 'InProgress': return '进行中'
    case 'Committed': return '已提交'
    case 'Failed': return '失败'
    case 'Cancelled': return '已取消'
    case 'Processing': return '处理中'
    default: return state
  }
}

function parentPath(path: string): string {
  const index = path.lastIndexOf('/')
  return index <= 0 ? '' : path.slice(0, index)
}
