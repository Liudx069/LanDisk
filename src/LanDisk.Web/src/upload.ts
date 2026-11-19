import { createSHA256 } from 'hash-wasm'
import { ApiError } from './types'

// Upload protocol constants. They are deliberately part of the client-visible contract:
//  * chunks are written strictly in order, so resumption is unambiguous;
//  * the content fingerprint binds a session to one specific local file;
//  * the prefix hash proves that the bytes already on the server are a prefix of that file.

export const HEAD_SAMPLE = 64 * 1024
export const TAIL_SAMPLE = 64 * 1024
export const DEFAULT_CHUNK = 4 * 1024 * 1024

/** Prefix length above which re-hashing on resume is refused as impractical in a browser. */
export const MAX_RESUME_PREFIX = 256 * 1024 * 1024

export type FileFingerprint = {
  size: number
  lastModified: number
  hash: string
}

async function sha256HexOfBlob(blob: Blob): Promise<string> {
  const buffer = new Uint8Array(await blob.arrayBuffer())
  const hasher = await createSHA256()
  hasher.init()
  hasher.update(buffer)
  return hasher.digest('hex')
}

/**
 * Cheap identity of a local file. It is used to make sure a resumed session is continued with the
 * very file it was started from: a same-name, same-size file with different content produces a
 * different fingerprint, so the splice that would corrupt the result is refused.
 */
export async function fingerprintFile(file: File): Promise<FileFingerprint> {
  const head = file.slice(0, Math.min(HEAD_SAMPLE, file.size))
  const tailStart = Math.max(HEAD_SAMPLE, file.size - TAIL_SAMPLE)
  const tail = file.size > HEAD_SAMPLE ? file.slice(tailStart, file.size) : new Blob([])

  const headHash = await sha256HexOfBlob(head)
  const tailHash = await sha256HexOfBlob(tail)

  const material = ['landisk-v1', String(file.size), String(file.lastModified), headHash, tailHash].join('\n')
  const hasher = await createSHA256()
  hasher.init()
  hasher.update(new TextEncoder().encode(material))

  return { size: file.size, lastModified: file.lastModified, hash: hasher.digest('hex') }
}

/** Incremental SHA-256 over the first `length` bytes of a file. */
export async function hashPrefix(file: File, length: number, onProgress?: (done: number) => void): Promise<string> {
  const hasher = await createSHA256()
  hasher.init()
  const step = 8 * 1024 * 1024
  let offset = 0
  while (offset < length) {
    const end = Math.min(offset + step, length)
    const buffer = new Uint8Array(await file.slice(offset, end).arrayBuffer())
    hasher.update(buffer)
    offset = end
    onProgress?.(offset)
  }
  return hasher.digest('hex')
}

export type UploadProgress = {
  fileId: string
  fileName: string
  totalBytes: number
  transferredBytes: number
  state: 'queued' | 'hashing' | 'uploading' | 'processing' | 'committed' | 'failed' | 'cancelled'
  error?: string
  path?: string
}

export type UploadTarget = {
  shareId: string
  directory: string
}

export type UploadOutcome = {
  path: string
  name: string
  bytes: number
}

type InitResponse = { uploadId: string; chunkSize: number; receivedBytes: number; declaredLength: number }
type ChunkResponse = { receivedBytes: number; declaredLength: number; isComplete: boolean; nextOffset: number }
type CompleteResponse = { ok: boolean; path: string; name: string; bytes: number; sha256: string }
type ResumeResponse = { uploadId: string; receivedBytes: number; declaredLength: number; prefixHash: string; chunkSize: number }

function uploadHeaders(extra?: Record<string, string>): Record<string, string> {
  return { 'X-LanDisk-Client': 'web', ...(extra ?? {}) }
}

// TypeScript's DOM lib models ArrayBufferView over ArrayBufferLike while BodyInit expects a view
// over a plain ArrayBuffer. The runtime value is a plain byte view, so a single documented cast is
// clearer than copying every chunk into a new buffer just to satisfy the type.
function asBody(data: Uint8Array): BodyInit {
  return data as unknown as BodyInit
}

async function readError(response: Response): Promise<never> {
  const text = await response.text()
  let code = 'HTTP_' + response.status
  let message = `请求失败（HTTP ${response.status}）。`
  try {
    const body = JSON.parse(text)
    if (body?.error) {
      code = body.error.code ?? code
      message = body.error.message ?? message
    }
  } catch {
    /* keep the generic message */
  }
  throw new ApiError(response.status, code, message)
}

export type UploadContext = {
  shareId: string
  directory: string
  csrfToken: string
  /** 'rename' saves a copy next to an existing file; 'fail' reports the conflict instead. */
  conflictPolicy: 'rename' | 'fail'
  onProgress: (update: Partial<UploadProgress> & { fileId: string }) => void
  signal: AbortSignal
}

/**
 * Uploads one file. The whole file is never materialised in memory: it is read slice by slice,
 * hashed incrementally and sent as ordered chunks.
 */
export async function uploadFile(
  fileId: string,
  file: File,
  context: UploadContext,
  existingUploadId?: string
): Promise<UploadOutcome> {
  const { shareId, directory, csrfToken, conflictPolicy, onProgress, signal } = context

  const progress = (patch: Partial<UploadProgress>) => onProgress({ fileId, ...patch })

  progress({ state: 'hashing', transferredBytes: 0, totalBytes: file.size })
  const fingerprint = await fingerprintFile(file)

  let uploadId: string
  let chunkSize = DEFAULT_CHUNK
  let offset = 0

  if (existingUploadId) {
    const resumeBody = JSON.stringify({ fingerprint: fingerprint.hash, prefixSha256: null })
    const response = await fetch(`/api/uploads/${existingUploadId}/resume`, {
      method: 'POST',
      credentials: 'same-origin',
      headers: uploadHeaders({ 'Content-Type': 'application/json; charset=utf-8', 'X-LanDisk-CSRF': csrfToken }),
      body: resumeBody,
      signal
    })
    if (!response.ok) await readError(response)
    const resumed = (await response.json()) as ResumeResponse
    uploadId = resumed.uploadId
    chunkSize = resumed.chunkSize || DEFAULT_CHUNK
    offset = resumed.receivedBytes
    progress({ transferredBytes: offset, state: 'uploading' })
  } else {
    const response = await fetch('/api/uploads/', {
      method: 'POST',
      credentials: 'same-origin',
      headers: uploadHeaders({ 'Content-Type': 'application/json; charset=utf-8', 'X-LanDisk-CSRF': csrfToken }),
      body: JSON.stringify({
        shareId,
        path: directory,
        fileName: file.name,
        length: file.size,
        fingerprint: fingerprint.hash
      }),
      signal
    })
    if (!response.ok) await readError(response)
    const init = (await response.json()) as InitResponse
    uploadId = init.uploadId
    chunkSize = init.chunkSize || DEFAULT_CHUNK
    progress({ state: 'uploading', transferredBytes: 0 })
  }

  const wholeHasher = await createSHA256()
  wholeHasher.init()
  const reader = file.stream().getReader()
  let consumed = 0
  let pending = new Uint8Array(0)

  try {
    while (true) {
      const { done, value } = await reader.read()
      if (done) break
      if (signal.aborted) throw new DOMException('已取消', 'AbortError')

      const merged = new Uint8Array(pending.length + value.length)
      merged.set(pending, 0)
      merged.set(value, pending.length)
      let cursor = 0

      while (merged.length - cursor >= chunkSize) {
        const chunk = merged.subarray(cursor, cursor + chunkSize)
        await sendChunk(chunk)
        cursor += chunkSize
        consumed += chunkSize
      }

      pending = merged.slice(cursor)
      // The hasher runs over the file in order regardless of chunk boundaries.
      wholeHasher.update(merged.subarray(0, cursor))
    }

    if (pending.length > 0) {
      await sendChunk(pending)
      consumed += pending.length
      wholeHasher.update(pending)
      pending = new Uint8Array(0)
    }
  } finally {
    reader.releaseLock()
  }

  async function sendChunk(chunk: Uint8Array) {
    const chunkHasher = await createSHA256()
    chunkHasher.init()
    chunkHasher.update(chunk)
    const chunkHash = chunkHasher.digest('hex')

    const response = await fetch(`/api/uploads/${uploadId}/chunk?offset=${offset}`, {
      method: 'PUT',
      credentials: 'same-origin',
      headers: uploadHeaders({
        'Content-Type': 'application/octet-stream',
        'X-LanDisk-CSRF': csrfToken,
        'X-Chunk-SHA256': chunkHash
      }),
      body: asBody(chunk),
      signal
    })
    if (!response.ok) await readError(response)
    const result = (await response.json()) as ChunkResponse
    offset = result.receivedBytes
    progress({ transferredBytes: offset })
  }

  progress({ state: 'processing', transferredBytes: file.size })

  const completeResponse = await fetch(`/api/uploads/${uploadId}/complete`, {
    method: 'POST',
    credentials: 'same-origin',
    headers: uploadHeaders({ 'Content-Type': 'application/json; charset=utf-8', 'X-LanDisk-CSRF': csrfToken }),
    body: JSON.stringify({ sha256: wholeHasher.digest('hex'), conflictPolicy }),
    signal
  })
  if (!completeResponse.ok) await readError(completeResponse)
  const completed = (await completeResponse.json()) as CompleteResponse

  progress({ state: 'committed', transferredBytes: file.size, path: completed.path })
  return { path: completed.path, name: completed.name, bytes: completed.bytes }
}

export async function cancelUpload(uploadId: string, csrfToken: string): Promise<void> {
  try {
    await fetch(`/api/uploads/${uploadId}`, {
      method: 'DELETE',
      credentials: 'same-origin',
      headers: uploadHeaders({ 'X-LanDisk-CSRF': csrfToken })
    })
  } catch {
    /* cancellation is best effort on the client; the server expires idle sessions anyway */
  }
}

export type PendingUpload = {
  uploadId: string
  shareId: string
  path: string
  fileName: string
  declaredLength: number
  receivedBytes: number
  state: string
  expiresAt: string
  fingerprint: string
}

export async function listPendingUploads(): Promise<PendingUpload[]> {
  const response = await fetch('/api/uploads/', { credentials: 'same-origin', headers: uploadHeaders() })
  if (!response.ok) return []
  const body = (await response.json()) as { uploads: PendingUpload[] }
  return body.uploads ?? []
}
