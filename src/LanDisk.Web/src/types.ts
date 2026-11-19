// Shared API types. These mirror the JSON shapes the ASP.NET Core endpoints return.

export type ShareCapabilities = {
  canRead: boolean
  canUpload: boolean
  canRename: boolean
  canDelete: boolean
}

export type DeviceShare = ShareCapabilities & {
  id: string
  name: string
  state: string
  stateDetail?: string | null
}

export type AdminShare = {
  id: string
  name: string
  rootPath: string
  volumeGuidPath: string
  mountPoint: string
  capabilities: string
  enabled: boolean
  state: string
  stateDetail?: string | null
  createdAt: string
  note?: string | null
}

export type EntryCategory = 'folder' | 'image' | 'video' | 'audio' | 'document' | 'archive' | 'other'

export type Entry = {
  name: string
  path: string
  isDirectory: boolean
  length: number
  sizeText: string
  modifiedAt: string
  modifiedUtc: string
  createdAt: string
  version: string
  isHidden: boolean
  isLink: boolean
  contentKind: string
  category: EntryCategory
  mimeType: string
  canPreview: boolean
  canPlay: boolean
  hasThumbnail: boolean
}

export type Breadcrumb = { name: string; path: string }

export type SortField = 'Name' | 'Size' | 'Modified' | 'Type'
export type ViewMode = 'list' | 'grid'
export type BrowseScope = 'Folder' | 'FolderTree' | 'Share'

export type Listing = {
  shareId: string
  shareName: string
  shareState: string
  shareStateDetail?: string | null
  path: string
  breadcrumbs: Breadcrumb[]
  entries: Entry[]
  total: number
  offset: number
  limit: number
  hasMore: boolean
  scanMs: number
  scanned: number
  truncated: boolean
  sort: SortField
  descending: boolean
  scope: BrowseScope
  capabilities: ShareCapabilities
}

export type Preferences = {
  sort: SortField
  descending: boolean
  view: ViewMode
}

/**
 * What this phone may do *right now*. The server recomputes it from the live device record on every
 * request, so a change made on the Windows side shows up here at the next heartbeat — no re-pairing, and
 * also no waiting: both directions take effect immediately.
 */
export type Permissions = {
  writeFiles: boolean
  installExtensions: boolean
  executeExtensions: boolean
  viewSystem: boolean
  level: string
  lastSeenIp?: string
}

export type Presence = {
  state: 'online' | 'recently-active' | 'offline' | 'revoked'
  lastSeenAt: string
  lastSeenIp?: string
  onlineWindowSeconds: number
  recentlyActiveWindowSeconds: number
}

export type Me = {
  kind: 'device' | 'admin'
  isLoopback: boolean
  csrfToken: string
  device?: { id: string; name: string; level: string; createdAt: string; lastSeenAt: string; revoked: boolean }
  shares: DeviceShare[]
  permissions?: Permissions
  presence?: Presence
}

export type Heartbeat = {
  ok: boolean
  kind: 'device' | 'admin'
  serverTime: string
  permissions?: Permissions
  presence?: Presence
  /** Moves when the operator changes the phone's appearance; the app then re-reads the profile. */
  themeRevision?: number
  /**
   * Moves when the device's level or any of its capabilities change, so the app re-reads /api/me exactly
   * when something it is allowed to do has changed. Comparing a single boolean could not do that.
   */
  permissionRevision?: number
}

/** One side of the mobile appearance: colour roles, never CSS or script. */
export type MobileThemeProfile = {
  id: string
  name: string
  appearance: string
  roles: Record<string, string>
}

export type MobileTheme = {
  revision: number
  profileId: string
  /** True when the operator chose "follow the device": the phone then picks light or dark itself. */
  followsDevice?: boolean
  appearance?: string
  dark: MobileThemeProfile
  light: MobileThemeProfile
}

// ---- extensions ----

export type ExtensionCommandDescriptor = {
  id: string
  name: string
  description: string
  argumentsSchema: Record<string, unknown> | null
}

export type ExtensionDescriptor = {
  id: string
  name: string
  version: string
  publisher: string
  commands: ExtensionCommandDescriptor[]
}

export type ExtensionTask = {
  id: string
  extensionId: string
  extensionName: string
  commandId: string
  arguments: string | null
  deviceId: string
  deviceName: string
  state: 'Queued' | 'Running' | 'Succeeded' | 'Failed' | 'Cancelled' | 'TimedOut'
  startedAt: string
  finishedAt: string | null
  durationMs: number | null
  exitCode: number
  log: string | null
  error: string | null
}

export type ExtensionList = { extensions: ExtensionDescriptor[] }
export type ExtensionTasks = { tasks: ExtensionTask[] }

// ---- host status (what the phone may know about the computer it is talking to) ----

export type HostStatusBase = {
  version: string
  started: string
  uptimeSeconds: number
  serviceState: string
  recommendedOrigin: string | null
  recommendedKind: string
  stableOrigin: string | null
  physicalOrigin: string | null
  httpsEnabled: boolean
  lanEnabled: boolean
  mdnsRunning: boolean
  shares: { total: number; enabled: number }
  storage: { available: boolean; totalBytes: number; freeBytes: number; freePercent: number }
  transfers: { active: number; todayUploadBytes: number; todayDownloadBytes: number }
  you: { level: string; name: string; permissions: Permissions }
}

export type HostStatusMachine = {
  processCpuPercent: number
  systemCpuPercent: number
  memoryUsedBytes: number
  memoryTotalBytes: number
  uploadBytesPerSecond: number
  downloadBytesPerSecond: number
  onlineDevices: number
  recentlyActiveDevices: number
  totalDevices: number
  vpnOrTunnel: boolean
  problems: { at: string; category: string; action: string; detail: string | null; success: boolean }[]
}

/**
 * `detailed` is decided by the server: without the SystemView capability the machine numbers are absent from
 * the response entirely, rather than present and hidden by the app.
 */
export type HostStatus = {
  ok: boolean
  detailed: boolean
  status: HostStatusBase
  machine?: HostStatusMachine
}

export type RecentItem = {
  shareId: string
  shareName: string
  shareState: string
  entry: Entry
}

export type RecentResponse = {
  windowDays: number
  total: number
  offset: number
  limit: number
  hasMore: boolean
  truncated: boolean
  /** Machine readable reasons, e.g. "scan_limit:照片" or "per_share_limit:电影". */
  truncatedReasons: string[]
  perShare: { shareId: string; name: string; candidates: number; scanned: number; truncated: boolean }[]
  scanMs: number
  items: RecentItem[]
}

export type FavoriteItem = {
  id: string
  shareId: string
  shareName: string
  path: string
  name: string
  isDirectory: boolean
  addedAt: string
  status: 'ok' | 'missing' | 'disabled' | 'denied' | 'offline' | 'unreadable'
  statusDetail?: string | null
}

export type RecycleEntry = {
  id: string
  shareId: string
  shareName: string
  path: string
  name: string
  isDirectory: boolean
  length: number
  sizeText: string
  deletedAt: string
  deletedBy: string
  state: string
}

export type Transfer = {
  id: string
  direction: string
  shareId: string
  shareName: string
  path: string
  fileName: string
  totalBytes: number
  transferredBytes: number
  state: string
  deviceName: string
  startedAt: string
  updatedAt: string
  error?: string | null
}

export type StorageItem = {
  shareId: string
  name: string
  state: string
  stateDetail?: string | null
  totalBytes: number
  freeBytes: number
  usedBytes: number
  totalText: string
  freeText: string
}

export type BatchResultItem = {
  path: string
  ok: boolean
  errorCode?: string | null
  error?: string | null
  recycleId?: string | null
  isDirectory?: boolean
}

export type BatchResponse = {
  ok: boolean
  requested: number
  succeeded: number
  failed: number
  results: BatchResultItem[]
}

export type BatchPlan = {
  archiveName: string
  fileCount: number
  totalBytes: number
  totalSizeText: string
  truncated: boolean
  truncationReason?: string | null
  skipped: { path: string; errorCode?: string | null; error?: string | null }[]
}

export type AdminDevice = {
  id: string
  name: string
  level: string
  revoked: boolean
  createdAt: string
  lastSeenAt: string
  lastSeenIp?: string | null
  userAgent?: string | null
}

export type PairingRequest = {
  id: string
  shortCode: string
  state: string
  createdAt: string
  expiresAt: string
  deviceName?: string | null
  remoteAddress?: string | null
  userAgent?: string | null
  awaitingConfirmation: boolean
}

export type ApiErrorBody = { error: { code: string; message: string; extra?: unknown } }

export class ApiError extends Error {
  code: string
  status: number
  extra?: unknown
  /** Technical detail kept out of the primary message but available under "详情". */
  technical?: string

  constructor(status: number, code: string, message: string, extra?: unknown) {
    super(message)
    this.status = status
    this.code = code
    this.extra = extra
    this.technical = extra ? JSON.stringify(extra) : undefined
  }
}
