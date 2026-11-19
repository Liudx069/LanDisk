import { useCallback, useEffect, useState } from 'react'
import { apiGet, apiPatch } from './api'
import type { Preferences, SortField, ViewMode } from './types'

const LOCAL_KEY = 'landisk.preferences'

function readLocal(): Preferences {
  try {
    const raw = localStorage.getItem(LOCAL_KEY)
    if (!raw) return { sort: 'Name', descending: false, view: 'list' }
    const parsed = JSON.parse(raw) as Partial<Preferences>
    return {
      sort: (parsed.sort as SortField) ?? 'Name',
      descending: parsed.descending ?? false,
      view: (parsed.view as ViewMode) ?? 'list'
    }
  } catch {
    return { sort: 'Name', descending: false, view: 'list' }
  }
}

/**
 * Sort and view preferences are remembered per browser session locally (so the UI never flickers
 * on load) and on the server per device (so they follow the device identity).
 */
export function usePreferences(): [Preferences, (patch: Partial<Preferences>) => void] {
  const [preferences, setPreferences] = useState<Preferences>(readLocal)

  useEffect(() => {
    let cancelled = false
    void (async () => {
      try {
        const remote = await apiGet<Preferences>('/api/preferences')
        if (cancelled) return
        setPreferences(remote)
        localStorage.setItem(LOCAL_KEY, JSON.stringify(remote))
      } catch {
        /* not paired yet, or offline; the local copy is good enough for now */
      }
    })()
    return () => { cancelled = true }
  }, [])

  const update = useCallback((patch: Partial<Preferences>) => {
    setPreferences((current) => {
      const next = { ...current, ...patch }
      localStorage.setItem(LOCAL_KEY, JSON.stringify(next))
      // Fire and forget: a failed sync must not block the UI, since the local value already applied.
      void apiPatch<Preferences>('/api/preferences', next).catch(() => undefined)
      return next
    })
  }, [])

  return [preferences, update]
}
