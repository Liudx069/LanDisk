import type { ReactNode } from 'react'

export type IconName =
  | 'folder' | 'file' | 'image' | 'video' | 'audio' | 'archive' | 'document' | 'link' | 'warning'
  | 'search' | 'sort' | 'filter' | 'grid' | 'list' | 'more' | 'back' | 'close' | 'upload'
  | 'download' | 'rename' | 'trash' | 'restore' | 'star' | 'starFilled' | 'clock' | 'home'
  | 'server' | 'check' | 'chevronRight' | 'chevronDown' | 'refresh' | 'logout' | 'info' | 'qr'
  | 'expand'

const PATHS: Record<IconName, string> = {
  folder: 'M3 6.5A1.5 1.5 0 0 1 4.5 5h3.6c.4 0 .8.16 1.06.44L10.4 6.7h9.1A1.5 1.5 0 0 1 21 8.2v9.3a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 17.5v-11Z',
  file: 'M6 2.5h7.2L19 8.3v13.2H6V2.5Zm7 1.2V9h5.3',
  image: 'M4 5h16v14H4V5Zm2 10 3.5-4 3 3.4L16 11l3 4H6Zm9-7a1.5 1.5 0 1 1 0 3 1.5 1.5 0 0 1 0-3Z',
  video: 'M4 6h11v12H4V6Zm12 4 4-2.5v9L16 14v-4Z',
  audio: 'M9 18V7l9-2v11M9 18a2.5 2.5 0 1 1-5 0 2.5 2.5 0 0 1 5 0Zm9-2a2.5 2.5 0 1 1-5 0 2.5 2.5 0 0 1 5 0Z',
  archive: 'M5 4h14v4H5V4Zm1.5 4h11v12h-11V8Zm4 2.5h3v2h-3v-2Z',
  document: 'M7 3h7l4 4v14H7V3Zm7 0v4h4M9.5 12h6M9.5 15h6M9.5 18h4',
  link: 'M10 13a5 5 0 0 0 7 0l2-2a5 5 0 0 0-7-7l-1 1m-1 8a5 5 0 0 1-7 0 5 5 0 0 1 0-7l2-2a5 5 0 0 1 7 0',
  warning: 'M12 3 2 20h20L12 3Zm0 6v5m0 3v.5',
  search: 'M11 4a7 7 0 1 0 0 14 7 7 0 0 0 0-14Zm5 12 4.5 4.5',
  sort: 'M7 4v16m0 0-3-3m3 3 3-3M17 20V4m0 0-3 3m3-3 3 3',
  filter: 'M3 5h18l-7 8v6l-4 2v-8L3 5Z',
  grid: 'M4 4h7v7H4V4Zm9 0h7v7h-7V4ZM4 13h7v7H4v-7Zm9 0h7v7h-7v-7Z',
  list: 'M4 6h16M4 12h16M4 18h16',
  more: 'M12 6.5a1.6 1.6 0 1 1 0-3.2 1.6 1.6 0 0 1 0 3.2Zm0 7.1a1.6 1.6 0 1 1 0-3.2 1.6 1.6 0 0 1 0 3.2Zm0 7.1a1.6 1.6 0 1 1 0-3.2 1.6 1.6 0 0 1 0 3.2Z',
  back: 'M15 5l-7 7 7 7',
  close: 'M6 6l12 12M18 6 6 18',
  upload: 'M12 20V6m0 0-5 5m5-5 5 5M4 4h16',
  download: 'M12 4v14m0 0-5-5m5 5 5-5M4 20h16',
  rename: 'M4 20h16M6 16 16 6l3 3L9 19H6v-3Z',
  trash: 'M5 7h14M9 7V5h6v2m-8 0 1 13h8l1-13M10 11v6m4-6v6',
  restore: 'M4 12a8 8 0 1 0 3-6.2M4 4v5h5',
  star: 'M12 3.6l2.6 5.4 5.9.8-4.3 4.1 1.1 5.9L12 17l-5.3 2.8 1.1-5.9L3.5 9.8l5.9-.8L12 3.6Z',
  starFilled: 'M12 3.6l2.6 5.4 5.9.8-4.3 4.1 1.1 5.9L12 17l-5.3 2.8 1.1-5.9L3.5 9.8l5.9-.8L12 3.6Z',
  clock: 'M12 4a8 8 0 1 0 0 16 8 8 0 0 0 0-16Zm0 3.5V12l3.2 2',
  home: 'M4 11 12 4l8 7v9h-6v-6H10v6H4v-9Z',
  server: 'M4 5h16v5H4V5Zm0 9h16v5H4v-5Zm2.5-6.5v.01M6.5 16.5v.01',
  check: 'M5 12.5 10 17.5 19 7',
  chevronRight: 'M9 6l6 6-6 6',
  chevronDown: 'M6 9l6 6 6-6',
  refresh: 'M20 12a8 8 0 1 1-2.3-5.7M20 4v5h-5',
  logout: 'M15 4h4v16h-4M11 8l-4 4 4 4M7 12h9',
  info: 'M12 4a8 8 0 1 0 0 16 8 8 0 0 0 0-16Zm0 3.5v.01M12 11v5',
  qr: 'M4 4h6v6H4V4Zm10 0h6v6h-6V4ZM4 14h6v6H4v-6Zm10 3h3v3h-3v-3Zm3-3h3v3h-3v-3Z',
  expand: 'M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5'
}

export function Icon({ name, size = 20, className }: { name: IconName; size?: number; className?: string }) {
  return (
    <svg
      className={className}
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.7"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
    >
      <path d={PATHS[name]} />
    </svg>
  )
}

/** Maps a file category to the icon shown in lists and grids. */
export function categoryIcon(category: string, isDirectory: boolean, isLink: boolean): IconName {
  if (isLink) return 'link'
  if (isDirectory) return 'folder'
  switch (category) {
    case 'image': return 'image'
    case 'video': return 'video'
    case 'audio': return 'audio'
    case 'archive': return 'archive'
    case 'document': return 'document'
    default: return 'file'
  }
}

export function Spinner({ label }: { label?: string }) {
  return (
    <span className="spinner-wrap">
      <span className="spinner" aria-hidden="true" />
      {label ? <span className="muted">{label}</span> : null}
    </span>
  )
}

export function EmptyState({ icon, title, hint, action }: { icon: IconName; title: string; hint?: ReactNode; action?: ReactNode }) {
  return (
    <div className="empty">
      <Icon name={icon} size={34} />
      <div className="empty-title">{title}</div>
      {hint ? <div className="muted">{hint}</div> : null}
      {action}
    </div>
  )
}
