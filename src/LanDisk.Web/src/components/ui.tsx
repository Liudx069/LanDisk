import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Icon, Spinner } from './icons'

/**
 * A centred dialog.
 *
 * `viewer` switches it to the media/document overlay: an almost opaque surface on a much darker scrim, with
 * its own stacking level. The Owner's screenshot showed the file list and the bottom navigation reading
 * through a playing video, tinted red — the overlay was a 45% scrim on a panel that took its colour from the
 * theme, so whatever was behind it stayed legible. A viewer must not be a slightly darker page; it must be a
 * layer.
 */
export function Modal({ title, children, onClose, wide, viewer }: { title: string; children: ReactNode; onClose: () => void; wide?: boolean; viewer?: boolean }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  return (
    <div className={`modal-backdrop${viewer ? ' viewer-backdrop' : ''}`} onClick={(e) => { if (e.target === e.currentTarget) onClose() }}>
      <div className={`modal${wide ? ' modal-wide' : ''}${viewer ? ' viewer' : ''}`} role="dialog" aria-modal="true" aria-label={title}>
        <div className="modal-head">
          <h2>{title}</h2>
          <button className="icon-btn" onClick={onClose} aria-label="关闭"><Icon name="close" /></button>
        </div>
        <div className="modal-body">{children}</div>
      </div>
    </div>
  )
}

/**
 * Bottom sheet: the primary interaction surface on a phone, and a centred panel on wide screens.
 * It closes on backdrop tap, on Escape, and by dragging the handle down.
 */
export function Sheet({
  title,
  children,
  onClose,
  footer
}: {
  title: string
  children: ReactNode
  onClose: () => void
  footer?: ReactNode
}) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  return (
    <div className="sheet-backdrop" onClick={(e) => { if (e.target === e.currentTarget) onClose() }}>
      <div className="sheet" role="dialog" aria-modal="true" aria-label={title}>
        <div className="sheet-grip" aria-hidden="true" />
        <div className="sheet-head">
          <h2>{title}</h2>
          <button className="icon-btn" onClick={onClose} aria-label="关闭"><Icon name="close" /></button>
        </div>
        <div className="sheet-body">{children}</div>
        {footer ? <div className="sheet-foot">{footer}</div> : null}
      </div>
    </div>
  )
}

export function ConfirmDialog({
  title,
  message,
  confirmLabel = '确认',
  danger,
  busy,
  onConfirm,
  onCancel
}: {
  title: string
  message: ReactNode
  confirmLabel?: string
  danger?: boolean
  busy?: boolean
  onConfirm: () => void
  onCancel: () => void
}) {
  return (
    <Modal title={title} onClose={onCancel}>
      <div className="stack">
        <div>{message}</div>
        <div className="row">
          <button className={danger ? 'danger' : 'primary'} onClick={onConfirm} disabled={busy}>
            {busy ? <Spinner /> : null}{confirmLabel}
          </button>
          <button onClick={onCancel} disabled={busy}>取消</button>
        </div>
      </div>
    </Modal>
  )
}

export function NamePrompt({
  title,
  label,
  initial,
  confirmLabel = '确定',
  busy,
  selectBasename,
  onConfirm,
  onCancel
}: {
  title: string
  label: string
  initial: string
  confirmLabel?: string
  busy?: boolean
  /** Preselect the name without the extension, which is what people usually want to change. */
  selectBasename?: boolean
  onConfirm: (value: string) => void
  onCancel: () => void
}) {
  const [value, setValue] = useState(initial)
  const inputRef = useRef<HTMLInputElement>(null)

  useEffect(() => {
    const input = inputRef.current
    if (!input) return
    input.focus()
    if (selectBasename) {
      const dot = initial.lastIndexOf('.')
      if (dot > 0) input.setSelectionRange(0, dot)
      else input.select()
    } else {
      input.select()
    }
  }, [initial, selectBasename])

  return (
    <Modal title={title} onClose={onCancel}>
      <div className="stack">
        <label className="field">
          <span>{label}</span>
          <input
            ref={inputRef}
            type="text"
            value={value}
            onChange={(e) => setValue(e.target.value)}
            onKeyDown={(e) => { if (e.key === 'Enter' && value.trim() && !busy) onConfirm(value.trim()) }}
          />
        </label>
        <div className="row">
          <button className="primary" disabled={value.trim().length === 0 || busy} onClick={() => onConfirm(value.trim())}>
            {busy ? <Spinner /> : null}{confirmLabel}
          </button>
          <button onClick={onCancel} disabled={busy}>取消</button>
        </div>
      </div>
    </Modal>
  )
}

export function Banner({
  kind,
  children,
  onDismiss,
  action
}: {
  kind: 'info' | 'warn' | 'error' | 'ok'
  children: ReactNode
  onDismiss?: () => void
  action?: ReactNode
}) {
  return (
    <div className={`banner ${kind}`} role={kind === 'error' ? 'alert' : undefined}>
      <div className="banner-text">{children}</div>
      {action}
      {onDismiss ? <button className="icon-btn small" onClick={onDismiss} aria-label="关闭提示"><Icon name="close" size={16} /></button> : null}
    </div>
  )
}

export function ProgressBar({ value, max }: { value: number; max: number }) {
  const percent = max > 0 ? Math.min(100, Math.round((value / max) * 100)) : 0
  return (
    <div className="progress" role="progressbar" aria-valuenow={percent} aria-valuemin={0} aria-valuemax={100}>
      <i style={{ width: `${percent}%` }} />
    </div>
  )
}

/** Error text that keeps the technical detail one tap away instead of in the user's face. */
export function ErrorDetail({ detail }: { detail?: string }) {
  const [open, setOpen] = useState(false)
  if (!detail) return null
  return (
    <div className="error-detail">
      <button className="link-btn" onClick={() => setOpen((v) => !v)}>
        {open ? '收起详情' : '查看详情'}
      </button>
      {open ? <pre className="mono">{detail}</pre> : null}
    </div>
  )
}

export function Toggle({
  checked,
  onChange,
  label,
  hint,
  disabled
}: {
  checked: boolean
  onChange: (value: boolean) => void
  label: string
  hint?: string
  disabled?: boolean
}) {
  return (
    <label className={`toggle-row${disabled ? ' disabled' : ''}`}>
      <span className="toggle-text">
        <span>{label}</span>
        {hint ? <span className="muted">{hint}</span> : null}
      </span>
      <input
        type="checkbox"
        checked={checked}
        disabled={disabled}
        onChange={(e) => onChange(e.target.checked)}
      />
      <span className="toggle-track" aria-hidden="true"><span className="toggle-thumb" /></span>
    </label>
  )
}
