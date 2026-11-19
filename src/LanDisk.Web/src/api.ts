import { ApiError, type ApiErrorBody } from './types'

// The CSRF token is delivered to the paired client and echoed back in a custom header on every
// write. A cross-site page cannot read it and cannot set the header without a CORS preflight,
// which this server never grants.
let csrfToken = ''

export function setCsrf(token: string | undefined | null) {
  if (token) csrfToken = token
}

export function getCsrf(): string {
  return csrfToken
}

/// The server mirrors the CSRF token into a script-readable cookie so a page reload can pick it
/// back up without an extra round trip before the first write.
export function refreshCsrfFromCookie(): string {
  const match = /(?:^|;\s*)landisk_csrf=([^;]+)/.exec(document.cookie)
  if (match) csrfToken = decodeURIComponent(match[1])
  return csrfToken
}

function headers(extra?: Record<string, string>): Record<string, string> {
  const result: Record<string, string> = { 'X-LanDisk-Client': 'web', ...(extra ?? {}) }
  if (csrfToken) result['X-LanDisk-CSRF'] = csrfToken
  return result
}

async function parse<T>(response: Response): Promise<T> {
  const text = await response.text()
  let payload: unknown = undefined
  if (text) {
    try {
      payload = JSON.parse(text)
    } catch {
      payload = undefined
    }
  }

  if (!response.ok) {
    const body = payload as ApiErrorBody | undefined
    const code = body?.error?.code ?? 'HTTP_' + response.status
    const message = body?.error?.message ?? `请求失败（HTTP ${response.status}）。`
    throw new ApiError(response.status, code, message, body?.error?.extra)
  }

  return payload as T
}

export async function apiGet<T>(url: string): Promise<T> {
  const response = await fetch(url, { method: 'GET', credentials: 'same-origin', headers: headers() })
  return parse<T>(response)
}

/**
 * The phone's lightweight "still here" call.
 *
 * It does three things at once and costs one tiny request: it keeps the device's presence honest (the
 * server treats every authenticated request as evidence the phone is awake), it returns the authority the
 * phone currently has, and it reports a theme revision so a change made on the Windows side is picked up
 * without any extra polling. Safari suspends timers in a backgrounded tab, which is why the server's
 * "online" window (45 s) is wider than the heartbeat interval (15 s).
 */
export async function heartbeat(): Promise<import('./types').Heartbeat> {
  return apiSend<import('./types').Heartbeat>('/api/heartbeat', 'POST', {})
}

/** The machine the phone is connected to, as far as this phone is allowed to see it. */
export async function hostStatus(): Promise<import('./types').HostStatus> {
  return apiGet<import('./types').HostStatus>('/api/host-status')
}

export async function mobileTheme(): Promise<import('./types').MobileTheme> {
  // `cache: 'no-store'` plus a changing query parameter: the header alone is not enough on iOS Safari, which
  // will happily reuse a response from the back/forward cache or a service worker. A stale theme profile is
  // exactly why the Owner's phone kept its previous appearance after the operator changed it.
  const response = await fetch(`/api/mobile-theme?_=${Date.now()}`, {
    method: 'GET',
    credentials: 'same-origin',
    cache: 'no-store',
    headers: headers()
  })
  return parse<import('./types').MobileTheme>(response)
}

// ---- extensions (only reachable with ExtensionInstall / ExtensionExecute) ----

export async function extensionCommands(): Promise<import('./types').ExtensionList> {
  return apiGet<import('./types').ExtensionList>('/api/extensions/commands')
}

export async function extensionExecute(extensionId: string, commandId: string, args: unknown): Promise<{ task: import('./types').ExtensionTask }> {
  return apiSend<{ task: import('./types').ExtensionTask }>('/api/extensions/execute', 'POST', {
    extensionId,
    commandId,
    arguments: args
  })
}

export async function extensionTasks(): Promise<import('./types').ExtensionTasks> {
  return apiGet<import('./types').ExtensionTasks>('/api/extensions/tasks')
}

export async function apiSend<T>(url: string, method: string, body?: unknown): Promise<T> {
  const response = await fetch(url, {
    method,
    credentials: 'same-origin',
    headers: headers(body === undefined ? undefined : { 'Content-Type': 'application/json; charset=utf-8' }),
    body: body === undefined ? undefined : JSON.stringify(body)
  })
  return parse<T>(response)
}

export const apiPost = <T,>(url: string, body?: unknown) => apiSend<T>(url, 'POST', body)
export const apiPatch = <T,>(url: string, body?: unknown) => apiSend<T>(url, 'PATCH', body)
export const apiDelete = <T,>(url: string) => apiSend<T>(url, 'DELETE')

/**
 * Uploads a file as a raw request body with progress reporting.
 *
 * `fetch` cannot report upload progress, and an update package is large enough that a silent
 * multi-second stall would look like a hang, so this one call uses XMLHttpRequest. The CSRF header
 * and the same-origin credential rule are applied exactly as in `apiSend`.
 */
export function apiUpload<T>(url: string, file: File, onProgress?: (sent: number, total: number) => void): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    const request = new XMLHttpRequest()
    request.open('POST', url, true)
    request.withCredentials = true
    request.setRequestHeader('X-LanDisk-Client', 'web')
    request.setRequestHeader('Content-Type', 'application/octet-stream')
    if (csrfToken) request.setRequestHeader('X-LanDisk-CSRF', csrfToken)
    request.setRequestHeader('X-LanDisk-Package-Name', encodeURIComponent(file.name))

    request.upload.onprogress = (event) => {
      if (event.lengthComputable && onProgress) onProgress(event.loaded, event.total)
    }

    request.onerror = () => reject(new ApiError(0, 'NETWORK', '网络中断，上传未完成。'))
    request.onabort = () => reject(new ApiError(0, 'CANCELLED', '上传已取消。'))
    request.ontimeout = () => reject(new ApiError(0, 'TIMEOUT', '上传超时。'))

    request.onload = () => {
      let payload: unknown = undefined
      if (request.responseText) {
        try {
          payload = JSON.parse(request.responseText)
        } catch {
          payload = undefined
        }
      }

      if (request.status >= 200 && request.status < 300) {
        resolve(payload as T)
        return
      }

      const body = payload as ApiErrorBody | undefined
      reject(new ApiError(
        request.status,
        body?.error?.code ?? 'HTTP_' + request.status,
        body?.error?.message ?? `上传失败（HTTP ${request.status}）。`,
        body?.error?.extra
      ))
    }

    request.send(file)
  })
}

export function fileUrl(kind: 'content' | 'download' | 'text' | 'meta' | 'thumb', shareId: string, path: string): string {
  const params = new URLSearchParams({ shareId, path })
  if (kind === 'content') params.set('disposition', 'inline')
  return `/api/file/${kind}?${params.toString()}`
}

export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return '—'
  if (bytes === 0) return '0 B'
  const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit += 1
  }
  return unit === 0 ? `${bytes} B` : `${value.toFixed(value < 10 ? 2 : 1)} ${units[unit]}`
}

/** Entry size text that mirrors the server: directories have no scanned size. */
export function entrySizeText(entry: { isDirectory: boolean; length: number; sizeText?: string }): string {
  if (entry.isDirectory) return '—'
  return entry.sizeText ?? formatBytes(entry.length)
}

export function formatTime(value: string): string {
  if (!value) return '—'
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return '—'
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`
}

/** Compact relative time for list rows; falls back to an absolute date for older items. */
export function formatRelative(value: string): string {
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return '—'
  const diff = Date.now() - date.getTime()
  const minutes = Math.floor(diff / 60000)
  if (minutes < 1) return '刚刚'
  if (minutes < 60) return `${minutes} 分钟前`
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours} 小时前`
  const days = Math.floor(hours / 24)
  if (days < 7) return `${days} 天前`
  return formatTime(value)
}

export function isSecureContextForPwa(): boolean {
  return typeof window !== 'undefined' && (window.isSecureContext || location.hostname === 'localhost' || location.hostname === '127.0.0.1')
}
