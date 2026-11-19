import { heartbeat } from './api'
import type { Permissions, Presence } from './types'

/**
 * The phone's presence, owned by the application rather than by a screen.
 *
 * <h3>Why this is a module and not a component effect</h3>
 * The heartbeat used to live in a `useEffect` inside the device app's component tree, which meant anything
 * that remounted that subtree — opening the file viewer, switching to favourites, entering a transfer, going
 * fullscreen — could tear it down. The Owner's phone was in the foreground playing a video and the Windows
 * device page still said "0 online, 1 recently active", because presence had become a property of whichever
 * screen happened to be mounted.
 *
 * This module is started once and never stopped while the app is open. Screens can come and go; the phone's
 * "I am here" cannot.
 *
 * <h3>When it reports</h3>
 * - immediately when started, and again on `pageshow`, `focus` and `visibilitychange` becoming visible,
 * - immediately on any `noteActivity(...)` call (route mounted, viewer opened),
 * - every 15 s while the document is visible.
 *
 * A backgrounded Safari tab has its timers suspended, which is why the server's online window (45 s) is
 * wider than the interval. A hidden document therefore does not beat on the timer at all — but the moment it
 * becomes visible again it beats at once instead of waiting out the interval.
 *
 * <h3>Failure</h3>
 * A failed request is retried with exponential backoff rather than being swallowed, and any of the immediate
 * triggers resets the backoff and retries right away. That matters on a phone: the browser drops requests
 * when the tab is frozen, and the next `focus` must not be 60 seconds away.
 */

export type PresenceSnapshot = {
  permissions?: Permissions
  presence?: Presence
  themeRevision?: number
  /** Moves when the device's level or any capability changes; the app re-reads /api/me when it moves. */
  permissionRevision?: number
}

type Listener = (snapshot: PresenceSnapshot) => void

const listeners = new Set<Listener>()
let started = false
let timer: number | undefined
let current: PresenceSnapshot = {}
let consecutiveFailures = 0
let stopped = false

const IntervalMs = 15_000
const MaxBackoffMs = 60_000

/** Subscribes to heartbeat results. Returns an unsubscribe function. */
export function onPresence(listener: Listener): () => void {
  listeners.add(listener)
  if (current.permissions || current.presence) listener(current)
  return () => listeners.delete(listener)
}

export function presenceSnapshot(): PresenceSnapshot {
  return current
}

/**
 * Starts the heartbeat. Idempotent, and deliberately without a matching stop: the only thing that ends it is
 * the page going away.
 */
export function startPresence(): void {
  if (started) return
  started = true
  stopped = false

  if (typeof document !== 'undefined') {
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'visible') void beat('visibilitychange')
    })
  }
  if (typeof window !== 'undefined') {
    // `pageshow` also fires when Safari restores the page from the back/forward cache, where no other event
    // tells us the page is alive again.
    window.addEventListener('pageshow', () => void beat('pageshow'))
    window.addEventListener('focus', () => void beat('focus'))
  }

  void beat('start')
  schedule(IntervalMs)
}

/**
 * Records that the user did something the server should count as activity right now. Called when a route is
 * mounted and when the file viewer opens, so opening a video refreshes presence immediately instead of
 * waiting for the next tick.
 */
export function noteActivity(_reason: string): void {
  void beat('activity')
}

function schedule(delayMs: number): void {
  if (stopped || typeof window === 'undefined') return
  if (timer !== undefined) window.clearTimeout(timer)
  timer = window.setTimeout(() => {
    timer = undefined
    void beat('timer')
  }, delayMs)
}

async function beat(trigger: string): Promise<void> {
  if (stopped) return

  // The timer does not beat in a hidden tab: Safari suspends it anyway, and asking is noise. Every other
  // trigger is an explicit "the user is here", so it beats even if the document was hidden a moment ago.
  if (trigger === 'timer' && typeof document !== 'undefined' && document.visibilityState !== 'visible') {
    schedule(IntervalMs)
    return
  }

  try {
    const result = await heartbeat()
    consecutiveFailures = 0

    current = {
      permissions: result.permissions ?? current.permissions,
      presence: result.presence ?? current.presence,
      themeRevision: typeof result.themeRevision === 'number' ? result.themeRevision : current.themeRevision,
      permissionRevision:
        typeof result.permissionRevision === 'number' ? result.permissionRevision : current.permissionRevision
    }
    for (const listener of listeners) listener(current)
    schedule(IntervalMs)
  } catch {
    // Exponential backoff, capped. An explicit trigger resets it, so "come back to the tab" is never
    // punished for an earlier failure.
    consecutiveFailures++;
    const backoff = Math.min(MaxBackoffMs, 2000 * 2 ** Math.min(5, consecutiveFailures))
    schedule(trigger === 'timer' ? backoff : 1000)
  }
}

/** Test seam: forgets that the heartbeat was started, so an automated check can start it again. */
export function resetPresenceForTests(): void {
  if (timer !== undefined && typeof window !== 'undefined') window.clearTimeout(timer)
  timer = undefined
  started = false
  stopped = false
  consecutiveFailures = 0
  current = {}
  listeners.clear()
}
