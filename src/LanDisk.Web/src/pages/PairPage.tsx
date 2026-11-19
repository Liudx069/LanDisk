import { useCallback, useEffect, useRef, useState } from 'react'
import { apiGet, apiPost, refreshCsrfFromCookie, setCsrf } from '../api'
import { ApiError } from '../types'

type Phase = 'idle' | 'pairing' | 'awaiting' | 'done' | 'error'

type RedeemResponse = {
  status: string
  device?: { id: string; name: string }
  requestId?: string
  claimSecret?: string
  deviceName?: string
}

export default function PairPage() {
  const [phase, setPhase] = useState<Phase>('idle')
  const [message, setMessage] = useState('')
  const [code, setCode] = useState('')
  const [deviceName, setDeviceName] = useState('')
  const [attemptedToken, setAttemptedToken] = useState(false)
  const pollRef = useRef<number | null>(null)

  const finish = useCallback((name: string) => {
    setPhase('done')
    setMessage(`已与“${name}”配对成功。正在进入文件列表…`)
    setCsrf(localStorage.getItem('landisk.csrf') ?? '')
    window.setTimeout(() => {
      history.replaceState(null, '', '/')
      location.assign('/')
    }, 900)
  }, [])

  const redeemToken = useCallback(async (token: string) => {
    setPhase('pairing')
    setMessage('正在使用配对链接…')
    try {
      const result = await apiPost<RedeemResponse>('/api/pair/redeem', {
        token,
        deviceName: deviceName.trim() || undefined
      })
      setCsrf(localStorage.getItem('landisk.csrf') ?? '')
      finish(result.device?.name ?? '设备')
    } catch (err) {
      setPhase('error')
      setMessage(err instanceof ApiError ? err.message : '配对失败，请重新扫码或输入配对码。')
    }
  }, [deviceName, finish])

  // A device that is already paired never sees this page for long: it is sent straight to the app.
  useEffect(() => {
    if (attemptedToken) return
    let cancelled = false
    void (async () => {
      try {
        refreshCsrfFromCookie()
        const me = await apiGet<{ kind: string }>('/api/me')
        if (!cancelled && me?.kind) {
          setPhase('done')
          setMessage('这台设备已经配对，正在进入文件列表…')
          window.setTimeout(() => { history.replaceState(null, '', '/'); location.assign('/') }, 400)
        }
      } catch {
        /* not paired yet, which is the normal case for this page */
      }
    })()
    return () => { cancelled = true }
  }, [attemptedToken])

  // The token arrives in the URL fragment so it never reaches the server access log.
  useEffect(() => {
    if (attemptedToken) return
    const match = /(?:^|[#&])t=([^&]+)/.exec(location.hash)
    if (!match) return
    setAttemptedToken(true)
    history.replaceState(null, '', '/pair')
    void redeemToken(decodeURIComponent(match[1]))
  }, [attemptedToken, redeemToken])

  useEffect(() => () => {
    if (pollRef.current !== null) window.clearInterval(pollRef.current)
  }, [])

  async function submitCode() {
    const trimmed = code.replace(/\D/g, '')
    if (trimmed.length !== 6) {
      setPhase('error')
      setMessage('请输入 6 位数字配对码。')
      return
    }

    setPhase('pairing')
    setMessage('已提交配对码，等待电脑端确认…')

    try {
      const started = await apiPost<RedeemResponse>('/api/pair/code', {
        shortCode: trimmed,
        deviceName: deviceName.trim() || undefined
      })

      if (!started.requestId || !started.claimSecret) {
        setPhase('error')
        setMessage('服务器未返回配对请求信息。')
        return
      }

      setPhase('awaiting')
      setMessage('请在这台电脑的 LanDisk 管理页面点击“确认配对”。')

      const requestId = started.requestId
      const claimSecret = started.claimSecret
      let attempts = 0

      pollRef.current = window.setInterval(async () => {
        attempts += 1
        if (attempts > 120) {
          if (pollRef.current !== null) window.clearInterval(pollRef.current)
          setPhase('error')
          setMessage('等待本机确认超时，请重新生成配对码。')
          return
        }
        try {
          const status = await apiPost<RedeemResponse>('/api/pair/status', { requestId, claimSecret })
          if (status.status === 'paired') {
            if (pollRef.current !== null) window.clearInterval(pollRef.current)
            finish(status.device?.name ?? '设备')
          } else if (status.status === 'expired' || status.status === 'cancelled') {
            if (pollRef.current !== null) window.clearInterval(pollRef.current)
            setPhase('error')
            setMessage('配对请求已失效，请在电脑端重新生成。')
          }
        } catch (err) {
          if (pollRef.current !== null) window.clearInterval(pollRef.current)
          setPhase('error')
          setMessage(err instanceof ApiError ? err.message : '查询配对状态失败。')
        }
      }, 2000)
    } catch (err) {
      setPhase('error')
      setMessage(err instanceof ApiError ? err.message : '配对失败。')
    }
  }

  const busy = phase === 'pairing' || phase === 'awaiting'

  return (
    <div className="app">
      <header className="top">
        <h1>LanDisk 设备配对</h1>
      </header>

      <div className="card">
        <p className="muted">
          在电脑上打开 LanDisk，点击“本机管理”页面里的“添加设备”，然后扫描二维码，
          或把 6 位配对码输入到下面。配对完成后本机还需要在管理页面确认。
        </p>

        <label className="field">
          <span>设备名称（可选，用于在电脑端识别这台手机）</span>
          <input
            type="text"
            value={deviceName}
            placeholder="例如：我的 iPhone"
            onChange={(e) => setDeviceName(e.target.value)}
          />
        </label>

        <label className="field">
          <span>6 位配对码</span>
          <input
            type="text"
            inputMode="numeric"
            autoComplete="one-time-code"
            maxLength={6}
            value={code}
            placeholder="000000"
            onChange={(e) => setCode(e.target.value.replace(/\D/g, '').slice(0, 6))}
            onKeyDown={(e) => { if (e.key === 'Enter' && !busy) void submitCode() }}
          />
        </label>

        <button className="primary block" disabled={busy} onClick={() => void submitCode()}>
          {busy ? '处理中…' : '提交配对码'}
        </button>
      </div>

      {message && (
        <div className={`banner ${phase === 'error' ? 'error' : phase === 'done' ? 'ok' : 'info'}`}>
          {message}
        </div>
      )}

      <div className="card">
        <h2>说明</h2>
        <ul className="muted" style={{ paddingLeft: 18, margin: 0 }}>
          <li>新配对的设备默认只有“查看和下载”权限，上传、重命名、删除需要电脑端单独开启。</li>
          <li>配对码 2 分钟内有效，只能使用一次。</li>
          <li>电脑端可以随时撤销这台设备；撤销后本机已下载的文件不会被远程删除。</li>
        </ul>
      </div>
    </div>
  )
}
