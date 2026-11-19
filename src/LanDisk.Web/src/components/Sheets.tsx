import { useState } from 'react'
import type { BrowseScope, Entry, EntryCategory, SortField } from '../types'
import { Icon, categoryIcon } from './icons'
import { Sheet } from './ui'

export type FilterState = {
  category: EntryCategory | 'all'
  scope: BrowseScope
  dateFrom: string
  dateTo: string
  sizePreset: SizePreset
}

export type SizePreset = 'any' | 'tiny' | 'small' | 'medium' | 'large'

export const defaultFilters: FilterState = {
  category: 'all',
  scope: 'Folder',
  dateFrom: '',
  dateTo: '',
  sizePreset: 'any'
}

export const sizePresetBytes: Record<SizePreset, { min?: number; max?: number; label: string }> = {
  any: { label: '不限' },
  tiny: { max: 1024 * 1024, label: '小于 1 MiB' },
  small: { min: 1024 * 1024, max: 100 * 1024 * 1024, label: '1 – 100 MiB' },
  medium: { min: 100 * 1024 * 1024, max: 1024 * 1024 * 1024, label: '100 MiB – 1 GiB' },
  large: { min: 1024 * 1024 * 1024, label: '大于 1 GiB' }
}

const CATEGORIES: { value: EntryCategory | 'all'; label: string }[] = [
  { value: 'all', label: '全部' },
  { value: 'folder', label: '文件夹' },
  { value: 'image', label: '图片' },
  { value: 'video', label: '视频' },
  { value: 'audio', label: '音频' },
  { value: 'document', label: '文档' },
  { value: 'archive', label: '压缩包' },
  { value: 'other', label: '其它' }
]

const SORT_OPTIONS: { field: SortField; desc: boolean; label: string }[] = [
  { field: 'Name', desc: false, label: '名称 A → Z' },
  { field: 'Name', desc: true, label: '名称 Z → A' },
  { field: 'Modified', desc: true, label: '修改时间 新 → 旧' },
  { field: 'Modified', desc: false, label: '修改时间 旧 → 新' },
  { field: 'Type', desc: false, label: '类型 A → Z' },
  { field: 'Type', desc: true, label: '类型 Z → A' },
  { field: 'Size', desc: true, label: '大小 大 → 小' },
  { field: 'Size', desc: false, label: '大小 小 → 大' }
]

export function SortSheet({
  sort,
  descending,
  onPick,
  onClose
}: {
  sort: SortField
  descending: boolean
  onPick: (field: SortField, desc: boolean) => void
  onClose: () => void
}) {
  return (
    <Sheet title="排序方式" onClose={onClose}>
      <ul className="option-list">
        {SORT_OPTIONS.map((option) => {
          const active = option.field === sort && option.desc === descending
          return (
            <li key={option.label}>
              <button className={`option${active ? ' active' : ''}`} onClick={() => { onPick(option.field, option.desc); onClose() }}>
                <span>{option.label}</span>
                {active ? <Icon name="check" size={18} /> : null}
              </button>
            </li>
          )
        })}
      </ul>
    </Sheet>
  )
}

export function FilterSheet({
  filters,
  onChange,
  onReset,
  onClose,
  resultCount
}: {
  filters: FilterState
  onChange: (next: FilterState) => void
  onReset: () => void
  onClose: () => void
  resultCount: number | null
}) {
  const [draft, setDraft] = useState<FilterState>(filters)
  const patch = (value: Partial<FilterState>) => setDraft((current) => ({ ...current, ...value }))

  return (
    <Sheet
      title="筛选"
      onClose={onClose}
      footer={
        <div className="row">
          <button className="primary" onClick={() => { onChange(draft); onClose() }}>
            应用{resultCount !== null ? `（${resultCount} 项）` : ''}
          </button>
          <button onClick={() => { setDraft(defaultFilters); onReset(); onClose() }}>清除</button>
        </div>
      }
    >
      <div className="stack">
        <section>
          <h3>类型</h3>
          <div className="chips">
            {CATEGORIES.map((category) => (
              <button
                key={category.value}
                className={`chip${draft.category === category.value ? ' on' : ''}`}
                onClick={() => patch({ category: category.value })}
              >
                {category.label}
              </button>
            ))}
          </div>
        </section>

        <section>
          <h3>搜索范围</h3>
          <div className="chips">
            <button className={`chip${draft.scope === 'Folder' ? ' on' : ''}`} onClick={() => patch({ scope: 'Folder' })}>当前文件夹</button>
            <button className={`chip${draft.scope === 'FolderTree' ? ' on' : ''}`} onClick={() => patch({ scope: 'FolderTree' })}>含子文件夹</button>
            <button className={`chip${draft.scope === 'Share' ? ' on' : ''}`} onClick={() => patch({ scope: 'Share' })}>整个共享</button>
          </div>
          <p className="muted small">选择后需要输入名称关键词才会触发跨文件夹搜索。</p>
        </section>

        <section>
          <h3>修改日期</h3>
          <div className="row">
            <label className="field grow">
              <span>从</span>
              <input type="date" value={draft.dateFrom} onChange={(e) => patch({ dateFrom: e.target.value })} />
            </label>
            <label className="field grow">
              <span>到</span>
              <input type="date" value={draft.dateTo} onChange={(e) => patch({ dateTo: e.target.value })} />
            </label>
          </div>
        </section>

        <section>
          <h3>大小</h3>
          <div className="chips">
            {(Object.keys(sizePresetBytes) as SizePreset[]).map((preset) => (
              <button
                key={preset}
                className={`chip${draft.sizePreset === preset ? ' on' : ''}`}
                onClick={() => patch({ sizePreset: preset })}
              >
                {sizePresetBytes[preset].label}
              </button>
            ))}
          </div>
        </section>
      </div>
    </Sheet>
  )
}

export function EntryActionsSheet({
  entry,
  canRename,
  canDelete,
  isFavorite,
  favoriteBusy,
  onOpen,
  onDownload,
  onRename,
  onToggleFavorite,
  onDelete,
  onSelect,
  onClose
}: {
  entry: Entry
  canRename: boolean
  canDelete: boolean
  isFavorite: boolean
  favoriteBusy: boolean
  onOpen: () => void
  onDownload: () => void
  onRename: () => void
  onToggleFavorite: () => void
  onDelete: () => void
  onSelect: () => void
  onClose: () => void
}) {
  return (
    <Sheet title={entry.name} onClose={onClose}>
      <div className="actions-head">
        <span className={`entry-icon cat-${entry.category}`}><Icon name={categoryIcon(entry.category, entry.isDirectory, entry.isLink)} size={28} /></span>
        <span className="muted">
          {entry.isDirectory ? '文件夹' : entry.sizeText}
          {entry.isDirectory ? '' : ` · ${entry.mimeType}`}
        </span>
      </div>

      <ul className="option-list">
        {!entry.isDirectory ? (
          <li><button className="option" onClick={onOpen}><Icon name="search" size={18} /><span>预览或播放</span></button></li>
        ) : null}
        {!entry.isDirectory ? (
          <li><button className="option" onClick={onDownload}><Icon name="download" size={18} /><span>下载</span></button></li>
        ) : null}
        <li>
          <button className="option" onClick={onToggleFavorite} disabled={favoriteBusy}>
            <Icon name={isFavorite ? 'starFilled' : 'star'} size={18} />
            <span>{isFavorite ? '取消收藏' : '收藏'}</span>
          </button>
        </li>
        {canRename ? (
          <li><button className="option" onClick={onRename}><Icon name="rename" size={18} /><span>重命名</span></button></li>
        ) : null}
        <li><button className="option" onClick={onSelect}><Icon name="check" size={18} /><span>选择</span></button></li>
        {canDelete ? (
          <li>
            <button className="option danger" onClick={onDelete}>
              <Icon name="trash" size={18} /><span>删除（移入回收站）</span>
            </button>
          </li>
        ) : null}
      </ul>

      <p className="muted small">
        删除会把对象移动到 LanDisk 自己的回收站，可以在这里的「回收站」中恢复，不是 Windows 系统回收站。
      </p>
    </Sheet>
  )
}
