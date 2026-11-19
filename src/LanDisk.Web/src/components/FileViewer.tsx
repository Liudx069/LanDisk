import { useEffect, useRef, useState } from 'react'
import { ApiError, type Entry } from '../types'
import { apiGet, fileUrl } from '../api'
import { Banner, Modal } from './ui'
import { Icon, Spinner } from './icons'

const TEXT_EXTENSIONS = /\.(txt|log|md|csv|json|xml|ya?ml|ini|cfg|conf|cs|js|ts|tsx|jsx|py|java|c|h|cpp|go|rs|rb|php|sh|ps1|bat|cmd|sql|srt|vtt|ass|lrc|nfo)$/i

/**
 * The video element with the non-standard methods Safari exposes.
 *
 * <para>iOS Safari gives the element <c>webkitEnterFullscreen()</c> and does not implement
 * <c>requestFullscreen()</c> on it (until recently it was not available at all), while desktop Safari and
 * Chromium do the opposite. Calling whichever exists — and falling back to the native controls' own button
 * if neither does — is what makes one button work on the phone the product is actually used from.</para>
 */
type FullscreenVideo = HTMLVideoElement & {
  webkitEnterFullscreen?: () => void
  webkitExitFullscreen?: () => void
  webkitDisplayingFullscreen?: boolean
  webkitSupportsFullscreen?: boolean
}

/**
 * Preview / playback panel. Whether a media file actually plays is decided by the browser; when it
 * cannot, the user is told plainly and offered a download instead of a spinner that never resolves.
 */
export default function FileViewer({
  entry,
  shareId,
  capabilities,
  onClose,
  onRename,
  onDelete,
  onToggleFavorite,
  isFavorite
}: {
  entry: Entry
  shareId: string
  capabilities: { canRename: boolean; canDelete: boolean }
  onClose: () => void
  onRename: () => void
  onDelete: () => void
  onToggleFavorite: () => void
  isFavorite: boolean
}) {
  const [text, setText] = useState<{ text: string; truncated: boolean } | null>(null)
  const [textError, setTextError] = useState<string | null>(null)
  const [mediaError, setMediaError] = useState<string | null>(null)
  const [imageFailed, setImageFailed] = useState(false)
  const [isFullscreen, setIsFullscreen] = useState(false)
  const videoRef = useRef<FullscreenVideo | null>(null)

  const isText = TEXT_EXTENSIONS.test(entry.name) && entry.contentKind === 'SafeInline'
  const isImage = entry.category === 'image'
  const isPdf = /\.pdf$/i.test(entry.name)
  const isAudio = entry.category === 'audio'
  const isVideo = entry.category === 'video'
  const isActive = entry.contentKind === 'ActiveContent'

  /**
   * Fullscreen, via whichever route this browser actually provides.
   *
   * iPhone Safari: `webkitEnterFullscreen()` on the video element (the only one that works there).
   * Chromium / desktop Safari: `requestFullscreen()` on the element or its container.
   * Neither available: the video's own native controls still carry the platform's fullscreen button, so the
   * user is never worse off than before — the page just says so instead of pretending to have failed.
   */
  const toggleFullscreen = async () => {
    const video = videoRef.current
    if (!video) return

    try {
      if (isFullscreen) {
        if (typeof video.webkitExitFullscreen === 'function' && video.webkitDisplayingFullscreen) {
          video.webkitExitFullscreen()
        } else if (document.fullscreenElement) {
          await document.exitFullscreen()
        }
        setIsFullscreen(false)
        return
      }

      if (typeof video.webkitEnterFullscreen === 'function' && video.webkitSupportsFullscreen !== false) {
        video.webkitEnterFullscreen()
        setIsFullscreen(true)
        return
      }

      const target = video.parentElement ?? video
      if (typeof target.requestFullscreen === 'function') {
        await target.requestFullscreen()
        setIsFullscreen(true)
        return
      }

      setMediaError('这个浏览器不提供网页内的全屏入口；播放器自带的控制条上仍有全屏按钮。')
    } catch {
      setMediaError('进入全屏被浏览器拒绝了；播放器自带的控制条上仍有全屏按钮。')
    }
  }

  // Keep the label honest when the user leaves fullscreen with the platform's own control (the iOS video
  // overlay has a "Done" button that never goes through this component).
  useEffect(() => {
    if (!isVideo) return
    const onChange = () => setIsFullscreen(Boolean(document.fullscreenElement))
    document.addEventListener('fullscreenchange', onChange)
    return () => document.removeEventListener('fullscreenchange', onChange)
  }, [isVideo])

  useEffect(() => {
    if (!isText) return
    let cancelled = false
    void (async () => {
      try {
        const result = await apiGet<{ text: string; truncated: boolean }>(fileUrl('text', shareId, entry.path))
        if (!cancelled) setText({ text: result.text, truncated: result.truncated })
      } catch (err) {
        if (!cancelled) setTextError(err instanceof ApiError ? err.message : '无法预览该文本文件。')
      }
    })()
    return () => { cancelled = true }
  }, [isText, shareId, entry.path])

  const inlineUrl = fileUrl('content', shareId, entry.path)
  const downloadUrl = fileUrl('download', shareId, entry.path)

  function download() {
    // The browser owns the download; LanDisk can only report that it handed the bytes over.
    const link = document.createElement('a')
    link.href = downloadUrl
    link.rel = 'noopener'
    document.body.appendChild(link)
    link.click()
    link.remove()
  }

  return (
    <Modal title={entry.name} onClose={onClose} wide viewer>
      <div className="stack">
        <div className="row viewer-meta">
          <span className="muted">{entry.sizeText} · {entry.mimeType}</span>
          <span className="spacer" />
          <button className="icon-btn" onClick={onToggleFavorite} aria-label={isFavorite ? '取消收藏' : '收藏'}>
            <Icon name={isFavorite ? 'starFilled' : 'star'} size={18} />
          </button>
        </div>

        {isImage && !imageFailed ? (
          <div className="preview-box">
            <img src={inlineUrl} alt={entry.name} onError={() => setImageFailed(true)} />
          </div>
        ) : null}

        {isImage && imageFailed ? (
          <Banner kind="warn">图片无法显示，可能格式不被浏览器支持。可以下载后用其它应用打开。</Banner>
        ) : null}

        {isPdf ? (
          <div className="preview-box pdf-box">
            <div className="muted">PDF 预览依赖浏览器的内置阅读器。若没有显示，请在新标签页打开或下载。</div>
            <a className="btn" href={inlineUrl} target="_blank" rel="noreferrer">在新标签页打开 PDF</a>
          </div>
        ) : null}

        {isText ? (
          <div className="preview-box">
            {textError ? <div className="muted">{textError}</div> : null}
            {!textError && !text ? <Spinner label="正在读取…" /> : null}
            {text ? (
              <>
                {text.truncated ? <div className="muted small">文件较大，仅显示开头部分。完整内容请下载查看。</div> : null}
                <pre>{text.text}</pre>
              </>
            ) : null}
          </div>
        ) : null}

        {isAudio ? (
          <audio controls preload="metadata" src={inlineUrl} onError={() => setMediaError('浏览器无法播放该音频格式。')} />
        ) : null}

        {isVideo ? (
          <div className="video-shell">
            <video
              ref={videoRef}
              controls
              playsInline
              preload="metadata"
              src={inlineUrl}
              onError={() => setMediaError('浏览器无法直接播放该视频（容器或编码不受支持）。')}
            />
            {/* An explicit fullscreen control.
                iOS renders its own overlay only while the video is playing and hides it again after a few
                seconds, which is why the owner reported "no fullscreen button" even though the file played:
                the control existed in the browser's chrome, not in the page. This button is part of the
                page, and it uses whichever fullscreen route the platform actually offers. Nothing about the
                playback pipeline changes — the same <video> element keeps decoding MP4/H.264 exactly as it
                already did on the real device. */}
            <button
              className="video-fullscreen"
              onClick={() => void toggleFullscreen()}
              aria-label={isFullscreen ? '退出全屏' : '全屏播放'}
              title={isFullscreen ? '退出全屏' : '全屏播放'}
            >
              <Icon name={isFullscreen ? 'close' : 'expand'} size={18} />
              {isFullscreen ? '退出全屏' : '全屏'}
            </button>
          </div>
        ) : null}

        {mediaError ? (
          <Banner kind="warn">
            {mediaError} 这不代表文件损坏——本版本不做服务器转码，请下载后用其它播放器打开。
          </Banner>
        ) : null}

        {isActive ? (
          <Banner kind="warn">
            这是可执行或主动内容文件（HTML、SVG 或脚本）。为避免脚本在本应用页面中运行，只提供下载，不提供内联预览。
          </Banner>
        ) : null}

        {!isImage && !isText && !isPdf && !isAudio && !isVideo && !isActive ? (
          <div className="muted">该类型没有内联预览，请下载后查看。</div>
        ) : null}

        <div className="row">
          <button className="primary" onClick={download}><Icon name="download" size={18} />下载</button>
          {capabilities.canRename ? <button onClick={onRename}><Icon name="rename" size={18} />重命名</button> : null}
          {capabilities.canDelete ? <button className="danger" onClick={onDelete}><Icon name="trash" size={18} />删除</button> : null}
        </div>

        <p className="muted small">
          「下载」是把文件交给浏览器下载。本应用无法得知设备是否已经把它保存到本地存储中。
        </p>
      </div>
    </Modal>
  )
}
