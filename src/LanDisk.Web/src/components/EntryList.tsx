import { useCallback, useEffect, useRef, useState } from 'react'
import { entrySizeText, fileUrl, formatRelative } from '../api'
import type { Entry, ViewMode } from '../types'
import { Icon, categoryIcon } from './icons'

type EntryListProps = {
  entries: Entry[]
  shareId: string
  view: ViewMode
  selectionMode: boolean
  selected: Set<string>
  onOpen: (entry: Entry) => void
  onToggleSelect: (entry: Entry) => void
  onEnterSelection: (entry: Entry) => void
  onMore: (entry: Entry) => void
}

/** Long press turns a tap into a selection gesture, which is what people expect on a phone. */
function useLongPress(onLongPress: () => void, onTap: () => void, delay = 450) {
  const timer = useRef<number | null>(null)
  const fired = useRef(false)

  const clear = useCallback(() => {
    if (timer.current !== null) {
      window.clearTimeout(timer.current)
      timer.current = null
    }
  }, [])

  useEffect(() => clear, [clear])

  return {
    onPointerDown: (event: React.PointerEvent) => {
      if (event.button !== 0) return
      fired.current = false
      clear()
      timer.current = window.setTimeout(() => {
        fired.current = true
        onLongPress()
      }, delay)
    },
    onPointerUp: () => {
      clear()
      if (!fired.current) onTap()
    },
    onPointerLeave: clear,
    onPointerCancel: clear,
    onContextMenu: (event: React.MouseEvent) => {
      // Desktop equivalent of long press, and it stops the browser menu from covering the row.
      event.preventDefault()
      clear()
      onLongPress()
    }
  }
}

function Thumbnail({ entry, shareId, size }: { entry: Entry; shareId: string; size: number }) {
  const [failed, setFailed] = useState(false)
  const icon = categoryIcon(entry.category, entry.isDirectory, entry.isLink)

  if (!entry.hasThumbnail || failed) {
    return (
      <span className={`entry-icon cat-${entry.category}`}>
        <Icon name={icon} size={size} />
      </span>
    )
  }

  return (
    <span className="entry-thumb">
      <img
        src={fileUrl('thumb', shareId, entry.path)}
        alt=""
        loading="lazy"
        decoding="async"
        onError={() => setFailed(true)}
      />
    </span>
  )
}

function EntryRow({
  entry,
  shareId,
  selectionMode,
  isSelected,
  onOpen,
  onToggleSelect,
  onEnterSelection,
  onMore
}: {
  entry: Entry
  shareId: string
  selectionMode: boolean
  isSelected: boolean
  onOpen: (entry: Entry) => void
  onToggleSelect: (entry: Entry) => void
  onEnterSelection: (entry: Entry) => void
  onMore: (entry: Entry) => void
}) {
  const handlers = useLongPress(
    () => (selectionMode ? onToggleSelect(entry) : onEnterSelection(entry)),
    () => (selectionMode ? onToggleSelect(entry) : onOpen(entry))
  )

  return (
    <li className={`entry${isSelected ? ' selected' : ''}`} data-name={entry.name}>
      {selectionMode ? (
        <span className={`checkbox${isSelected ? ' on' : ''}`} aria-hidden="true">
          {isSelected ? <Icon name="check" size={16} /> : null}
        </span>
      ) : null}

      <div className="entry-tap" {...handlers} role="button" tabIndex={0}
           onKeyDown={(e) => {
             if (e.key === 'Enter' || e.key === ' ') {
               e.preventDefault()
               if (selectionMode) onToggleSelect(entry)
               else onOpen(entry)
             }
           }}>
        <Thumbnail entry={entry} shareId={shareId} size={26} />
        <span className="entry-main">
          <span className="entry-name" title={entry.name}>{entry.name}</span>
          <span className="entry-sub">
            {entry.isDirectory ? '文件夹' : entrySizeText(entry)}
            <span className="dot">·</span>
            {formatRelative(entry.modifiedAt)}
            {entry.isLink ? <><span className="dot">·</span>链接（不跟随）</> : null}
          </span>
        </span>
        <span className="entry-col col-modified muted">{formatRelative(entry.modifiedAt)}</span>
        <span className="entry-col col-type muted">{entry.isDirectory ? '文件夹' : typeLabel(entry)}</span>
        <span className="entry-col col-size muted">{entrySizeText(entry)}</span>
      </div>

      {!selectionMode ? (
        <button className="icon-btn entry-more" aria-label={`${entry.name} 的更多操作`}
                onClick={(e) => { e.stopPropagation(); onMore(entry) }}>
          <Icon name="more" size={18} />
        </button>
      ) : null}
    </li>
  )
}

function EntryCard({
  entry,
  shareId,
  selectionMode,
  isSelected,
  onOpen,
  onToggleSelect,
  onEnterSelection,
  onMore
}: {
  entry: Entry
  shareId: string
  selectionMode: boolean
  isSelected: boolean
  onOpen: (entry: Entry) => void
  onToggleSelect: (entry: Entry) => void
  onEnterSelection: (entry: Entry) => void
  onMore: (entry: Entry) => void
}) {
  const handlers = useLongPress(
    () => (selectionMode ? onToggleSelect(entry) : onEnterSelection(entry)),
    () => (selectionMode ? onToggleSelect(entry) : onOpen(entry))
  )

  return (
    <li className={`card-entry${isSelected ? ' selected' : ''}`}>
      <div className="card-tap" {...handlers} role="button" tabIndex={0}
           onKeyDown={(e) => {
             if (e.key === 'Enter' || e.key === ' ') {
               e.preventDefault()
               if (selectionMode) onToggleSelect(entry)
               else onOpen(entry)
             }
           }}>
        <Thumbnail entry={entry} shareId={shareId} size={34} />
        {selectionMode ? (
          <span className={`checkbox overlay${isSelected ? ' on' : ''}`} aria-hidden="true">
            {isSelected ? <Icon name="check" size={14} /> : null}
          </span>
        ) : null}
        <span className="card-name" title={entry.name}>{entry.name}</span>
        <span className="card-sub muted">{entry.isDirectory ? '文件夹' : entrySizeText(entry)}</span>
      </div>
      {!selectionMode ? (
        <button className="icon-btn card-more" aria-label={`${entry.name} 的更多操作`}
                onClick={(e) => { e.stopPropagation(); onMore(entry) }}>
          <Icon name="more" size={16} />
        </button>
      ) : null}
    </li>
  )
}

function typeLabel(entry: Entry): string {
  switch (entry.category) {
    case 'image': return '图片'
    case 'video': return '视频'
    case 'audio': return '音频'
    case 'archive': return '压缩包'
    case 'document': return '文档'
    default: {
      const dot = entry.name.lastIndexOf('.')
      return dot > 0 ? entry.name.slice(dot + 1).toUpperCase() : '文件'
    }
  }
}

export default function EntryList({
  entries,
  shareId,
  view,
  selectionMode,
  selected,
  onOpen,
  onToggleSelect,
  onEnterSelection,
  onMore
}: EntryListProps) {
  if (view === 'grid') {
    return (
      <ul className="grid-entries">
        {entries.map((entry) => (
          <EntryCard
            key={entry.path}
            entry={entry}
            shareId={shareId}
            selectionMode={selectionMode}
            isSelected={selected.has(entry.path)}
            onOpen={onOpen}
            onToggleSelect={onToggleSelect}
            onEnterSelection={onEnterSelection}
            onMore={onMore}
          />
        ))}
      </ul>
    )
  }

  return (
    <ul className="entries">
      <li className="entries-head" aria-hidden="true">
        <span className="entry-tap">
          <span className="entry-icon" />
          <span className="entry-main">名称</span>
          <span className="entry-col col-modified">修改时间</span>
          <span className="entry-col col-type">类型</span>
          <span className="entry-col col-size">大小</span>
        </span>
      </li>
      {entries.map((entry) => (
        <EntryRow
          key={entry.path}
          entry={entry}
          shareId={shareId}
          selectionMode={selectionMode}
          isSelected={selected.has(entry.path)}
          onOpen={onOpen}
          onToggleSelect={onToggleSelect}
          onEnterSelection={onEnterSelection}
          onMore={onMore}
        />
      ))}
    </ul>
  )
}
