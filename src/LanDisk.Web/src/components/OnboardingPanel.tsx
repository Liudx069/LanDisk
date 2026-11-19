import { useCallback, useEffect, useRef, useState } from 'react'
import QRCode from 'qrcode'
import { apiGet, apiPost } from '../api'
import { Banner } from './ui'

type CertificateView = {
  subject?: string | null
  notAfter?: string | null
  dnsNames?: string[]
  ipAddresses?: string[]
  rootCaSha256?: string | null
  rootCaSubject?: string | null
  rootCaNotAfter?: string | null
  leafReason?: string | null
}

type OnboardingState = {
  active: boolean
  bootstrapId?: string | null
  bootstrapUrl?: string | null
  expiresAt?: string | null
  trusted: boolean
  trustedAt?: string | null
  hostName: string
  httpsUrl: string
  addresses: string[]
  listeners: string[]
  port: number
  lastError?: string | null
  stableNameAvailable: boolean
  mdnsError?: string | null
  certificate?: CertificateView | null
}

/**
 * The "connect an iPhone" wizard.
 *
 * It is two QR codes on purpose. The first one is a plain-HTTP address that carries no secret at all:
 * it serves the public root certificate and the instructions for the trust step Apple requires the
 * user to perform personally. Only once the phone has provably completed that step does the second
 * — pairing — QR code appear, and that one lives on the HTTPS origin. Putting the pairing token in the
 * HTTP page would have been more convenient and would have handed the token to anyone on the LAN.
 */
export default function OnboardingPanel({ onError, onNotice }: { onError: (message: string) => void; onNotice: (message: string) => void }) {
  const [state, setState] = useState<OnboardingState | null>(null)
  const [bootstrapQr, setBootstrapQr] = useState('')
  const [pairingQr, setPairingQr] = useState('')
  const [pairingUrls, setPairingUrls] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const timer = useRef<number | null>(null)

  const load = useCallback(async () => {
    try {
      setState(await apiGet<OnboardingState>('/api/admin/onboarding'))
    } catch (error) {
      onError(error instanceof Error ? error.message : '无法读取首次连接向导状态')
    }
  }, [onError])

  useEffect(() => {
    void load()
    timer.current = window.setInterval(() => { void load() }, 3000)
    return () => { if (timer.current !== null) window.clearInterval(timer.current) }
  }, [load])

  useEffect(() => {
    if (!state?.bootstrapUrl) { setBootstrapQr(''); return }
    void QRCode.toDataURL(state.bootstrapUrl, { width: 440, margin: 1, errorCorrectionLevel: 'M' })
      .then(setBootstrapQr).catch(() => setBootstrapQr(''))
  }, [state?.bootstrapUrl])

  const makePairing = useCallback(async () => {
    try {
      const created = await apiPost<{ urls: string[] }>('/api/admin/pairing', {})
      setPairingUrls(created.urls ?? [])
      const preferred = created.urls?.[0] ?? ''
      setPairingQr(preferred ? await QRCode.toDataURL(preferred, { width: 440, margin: 1, errorCorrectionLevel: 'M' }) : '')
    } catch (error) {
      onError(error instanceof Error ? error.message : '无法生成配对二维码')
    }
  }, [onError])

  // Step 3 of the wizard is the pairing QR code, and it only appears after the phone has proven the
  // HTTPS trust step. That ordering is the whole security argument of this page.
  useEffect(() => {
    if (state?.trusted && pairingQr === '') void makePairing()
  }, [state?.trusted, pairingQr, makePairing])

  const start = async () => {
    setBusy(true)
    try {
      const started = await apiPost<{ active: boolean; bootstrapUrl?: string | null; lastError?: string | null }>('/api/admin/onboarding/start', {})
      if (started.lastError) onError(started.lastError)
      else onNotice('已开启首次连接向导。请用 iPhone 相机扫描下方二维码。')
      await load()
    } catch (error) {
      onError(error instanceof Error ? error.message : '无法开启首次连接向导')
    } finally {
      setBusy(false)
    }
  }

  const stop = async () => {
    setBusy(true)
    try {
      await apiPost('/api/admin/onboarding/stop', {})
      setPairingQr('')
      setPairingUrls([])
      await load()
    } catch (error) {
      onError(error instanceof Error ? error.message : '无法关闭首次连接向导')
    } finally {
      setBusy(false)
    }
  }

  const resetCa = async () => {
    if (!window.confirm('重置本地根证书后，所有已安装 LanDisk 证书的 iPhone 都必须重新安装并重新开启“完全信任”。\n\n共享目录与已配对设备不受影响。确定继续吗？')) return
    setBusy(true)
    try {
      const result = await apiPost<{ warning: string }>('/api/admin/onboarding/reset-ca?confirm=yes', {})
      onNotice(result.warning)
      setPairingQr('')
      await load()
    } catch (error) {
      onError(error instanceof Error ? error.message : '无法重置本地根证书')
    } finally {
      setBusy(false)
    }
  }

  if (!state) return <div className="card"><h2>连接 iPhone</h2><p className="muted">正在读取状态…</p></div>

  const certificate = state.certificate

  return (
    <div className="card">
      <h2>连接 iPhone</h2>

      <p className="muted">
        稳定名称：<span className="mono">{state.hostName}</span>
        {state.stableNameAvailable
          ? '（已通过 mDNS 发布，手机可以用这个名称长期访问）'
          : '（稳定名称当前不可用，将在下方回退为局域网地址）'}
      </p>
      {!state.stableNameAvailable && state.mdnsError && (
        <Banner kind="warn">
          {state.mdnsError}　稳定名称不可用时仍可用当前局域网地址访问：<span className="mono">{state.httpsUrl}</span>。
          地址变化后可能需要重新配对，但不需要重新信任证书。
        </Banner>
      )}

      {state.lastError && <Banner kind="error">{state.lastError}</Banner>}

      <div className="row" style={{ gap: 8, flexWrap: 'wrap' }}>
        <button className="primary" onClick={() => void start()} disabled={busy || state.active}>开始首次连接向导</button>
        <button className="small" onClick={() => void stop()} disabled={busy || !state.active}>结束向导（关闭 {state.port} 端口）</button>
        <button className="small" onClick={() => void load()} disabled={busy}>刷新</button>
      </div>

      {state.active && (
        <>
          <h3>第 1 步 · 用 iPhone 扫描这个二维码</h3>
          <p className="muted">
            这个页面走的是明文 HTTP（<span className="mono">:{state.port}</span>），只提供公开证书与安装说明，
            <strong>不包含任何令牌、配对密钥或文件接口</strong>。仅在你自己的局域网内、向导开启期间可访问，
            向导关闭后端口立即停止监听。
          </p>
          {bootstrapQr
            ? <img src={bootstrapQr} alt="首次连接二维码" style={{ width: 220, height: 220 }} />
            : <p className="muted">正在生成二维码…</p>}
          <p className="mono small">{state.bootstrapUrl}</p>
          {state.expiresAt && <p className="muted">该链接有效至 {new Date(state.expiresAt).toLocaleTimeString()}，过期后需要重新开始向导。</p>}
          <ol>
            <li>iPhone 用相机扫描上面的二维码，Safari 会打开 LanDisk 的引导页。</li>
            <li>点“安装 LanDisk 安全证书”，然后在 <strong>设置 → 通用 → VPN与设备管理</strong> 中安装描述文件。</li>
            <li>在 <strong>设置 → 通用 → 关于本机 → 证书信任设置</strong> 中打开对 <strong>LanDisk Local CA</strong> 的“完全信任”。</li>
            <li>回到 Safari 点“继续”。这一步苹果要求由你本人确认，LanDisk 无法也不会代你完成。</li>
          </ol>
        </>
      )}

      <h3>第 2 步 · 确认 HTTPS 已受信任</h3>
      {state.trusted
        ? <Banner kind="ok">已检测到手机成功通过 HTTPS 访问（{state.trustedAt ? new Date(state.trustedAt).toLocaleTimeString() : '刚刚'}）。可以继续配对。</Banner>
        : <p className="muted">还没有检测到。完成上面的完全信任后，回到引导页点“继续”即可。</p>}

      <h3>第 3 步 · 扫描配对二维码</h3>
      {!state.trusted && <p className="muted">HTTPS 信任确认后，这里才会显示配对二维码。真正的一次性配对令牌只通过 HTTPS 传递。</p>}
      {state.trusted && (
        <>
          {pairingQr
            ? <img src={pairingQr} alt="配对二维码" style={{ width: 220, height: 220 }} />
            : <p className="muted">正在生成配对二维码…</p>}
          {pairingUrls.length > 0 && (
            <p className="muted">首选地址：<span className="mono">{pairingUrls[0]}</span></p>
          )}
          <button className="small" onClick={() => void makePairing()} disabled={busy}>重新生成配对二维码</button>
        </>
      )}

      <h3>证书架构</h3>
      <table className="grid">
        <tbody>
          <tr><th>本地根 CA</th><td className="mono">{certificate?.rootCaSubject ?? '—'}</td></tr>
          <tr><th>根 CA 指纹（SHA-256）</th><td className="mono small">{certificate?.rootCaSha256 ?? '—'}</td></tr>
          <tr><th>根 CA 有效期至</th><td>{certificate?.rootCaNotAfter ? new Date(certificate.rootCaNotAfter).toLocaleDateString() : '—'}</td></tr>
          <tr><th>服务器证书主体</th><td className="mono">{certificate?.subject ?? '—'}</td></tr>
          <tr><th>服务器证书 SAN</th><td className="mono small">{[...(certificate?.dnsNames ?? []), ...(certificate?.ipAddresses ?? [])].join('、') || '—'}</td></tr>
          <tr><th>服务器证书有效期至</th><td>{certificate?.notAfter ? new Date(certificate.notAfter).toLocaleDateString() : '—'}</td></tr>
          <tr><th>本次签发原因</th><td>{certificate?.leafReason ?? '—'}</td></tr>
        </tbody>
      </table>
      <p className="muted">
        根 CA 只在首次安装时生成一次：换网络、换 IP、服务器证书到期续签、LanDisk 升级都不会更换它，
        因此手机只需要信任一次。只有用户主动重置时才会更换。
      </p>
      <button className="small danger" onClick={() => void resetCa()} disabled={busy}>重置本地根证书（需要手机重新信任）</button>

      <h3>这一步之后</h3>
      <p className="muted">
        配对完成后，iPhone 会记住这个安全地址，之后打开即自动登录。共享、权限、端口与配对记录在升级
        （.lupd）后都不会改变，也不需要重新做上面的任何一步。
      </p>
    </div>
  )
}
