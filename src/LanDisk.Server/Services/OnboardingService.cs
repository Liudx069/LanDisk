using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using LanDisk.Core.Configuration;
using LanDisk.Core.Networking;
using LanDisk.Core.Security;

namespace LanDisk.Server.Services;

public sealed record OnboardingStatus(
    bool Active,
    string? BootstrapId,
    string? BootstrapUrl,
    DateTimeOffset? ExpiresAt,
    bool Trusted,
    DateTimeOffset? TrustedAt,
    string HostName,
    string HttpsUrl,
    IReadOnlyList<string> Addresses,
    IReadOnlyList<string> Listeners,
    string? LastError,
    int Port);

/// <summary>
/// The first-connection wizard's HTTP side.
///
/// <para><b>What this is allowed to be.</b> A phone that has never seen LanDisk cannot fetch anything
/// over HTTPS from it yet — that is the whole problem being solved — so a small plain-HTTP window
/// exists while the wizard is open. Everything served through it is public by construction: a static
/// page, the root CA's public certificate, an unsigned configuration profile containing only that
/// certificate, and a boolean "has the phone trusted us yet". No admin API, no file API, no cookies,
/// no token, no device secret, no share name. The listener stops the moment the wizard closes.</para>
///
/// <para><b>Why the pairing token is not here.</b> Handing a pairing token to the phone through an
/// unauthenticated HTTP page would undermine the pairing it is meant to protect. The phone is instead
/// sent back to the computer's screen, which shows the one-time pairing QR over the HTTPS origin once
/// the trust step is confirmed.</para>
/// </summary>
public sealed class OnboardingService : IDisposable
{
    public const int DefaultBootstrapPort = 8890;
    public static readonly TimeSpan BootstrapLifetime = TimeSpan.FromMinutes(20);

    private readonly AppPaths _paths;
    private readonly Func<byte[]?> _caCertificate;
    private readonly Func<string> _hostName;
    private readonly Func<int> _httpsPort;
    private readonly Func<IReadOnlyList<string>> _addresses;
    private readonly Action<string>? _log;
    private readonly bool _allowLoopback;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private readonly List<TcpListener> _tcpListeners = [];
    private readonly List<Task> _acceptTasks = [];
    private string _bootstrapId = string.Empty;
    private DateTimeOffset _expiresAt;
    private readonly List<string> _listeners = [];

    public OnboardingService(
        AppPaths paths,
        Func<byte[]?> caCertificate,
        Func<string> hostName,
        Func<int> httpsPort,
        Func<IReadOnlyList<string>> addresses,
        bool allowLoopback = false,
        Action<string>? log = null)
    {
        _paths = paths;
        _caCertificate = caCertificate;
        _hostName = hostName;
        _httpsPort = httpsPort;
        _addresses = addresses;
        _allowLoopback = allowLoopback;
        _log = log;
    }

    public bool IsActive { get; private set; }

    public string? LastError { get; private set; }

    public bool Trusted { get; private set; }

    public DateTimeOffset? TrustedAt { get; private set; }

    public int Port { get; private set; } = DefaultBootstrapPort;

    public string? BootstrapId { get { lock (_gate) { return IsActive ? _bootstrapId : null; } } }

    public DateTimeOffset? ExpiresAt { get { lock (_gate) { return IsActive ? _expiresAt : null; } } }

    /// <summary>Number of HTTP requests served; reconciliation evidence for the acceptance script.</summary>
    public long ServedRequests { get; private set; }

    public IReadOnlyList<string> Listeners { get { lock (_gate) { return _listeners.ToArray(); } } }

    public OnboardingStatus Status()
    {
        (string primaryAddress, _) = BestAddress();
        string hostName = _hostName();
        int httpsPort = _httpsPort();
        string? id = BootstrapId;

        return new OnboardingStatus(
            IsActive,
            id,
            IsActive && !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(primaryAddress)
                ? $"http://{primaryAddress}:{Port}/ios-setup/{id}"
                : null,
            ExpiresAt,
            Trusted,
            TrustedAt,
            hostName,
            $"https://{hostName}:{httpsPort}/",
            _addresses(),
            Listeners,
            LastError,
            Port);
    }

    /// <summary>
    /// Opens the bootstrap window. The id is short-lived and single-purpose: it identifies this one
    /// wizard session and cannot be used to reach anything else.
    ///
    /// <para>A new session starts from a clean slate, so the connectivity hint from a previous attempt is
    /// dropped here. Calling this while a session is already open changes nothing — including the hint,
    /// which describes the phone that is currently pairing.</para>
    /// </summary>
    public OnboardingStatus Start(int port = DefaultBootstrapPort)
    {
        lock (_gate)
        {
            if (IsActive) return Status();

            Port = port;
            Trusted = false;
            TrustedAt = null;
            LastError = null;
            _bootstrapId = SecretTokens.NewShortId();
            _expiresAt = DateTimeOffset.Now.Add(BootstrapLifetime);
            _verifiedHttpsHost = null;
            _verifiedHttpsAt = null;
        }

        if (_caCertificate() is null)
        {
            LastError = "本地根 CA 尚未生成，无法开始首次连接向导。";
            return Status();
        }

        var endpoints = new List<IPEndPoint>();
        foreach (string address in _addresses())
        {
            if (!IPAddress.TryParse(address, out IPAddress? ip)) continue;
            if (!LanInfo.IsPrivate(ip)) continue;
            endpoints.Add(new IPEndPoint(ip, Port));
        }
        if (_allowLoopback) endpoints.Add(new IPEndPoint(IPAddress.Loopback, Port));

        if (endpoints.Count == 0)
        {
            LastError = "没有可用的局域网地址，无法在局域网上提供首次连接引导。请先连接 Wi-Fi 或网线。";
            _log?.Invoke(LastError);
            return Status();
        }

        try
        {
            // One listener per private address, and no listener anywhere else. Binding the wildcard
            // and relying on the firewall would mean the bootstrap page answers on whatever interface
            // happens to be up, which is exactly what "Private / LocalSubnet only" rules out.
            var listeners = new List<TcpListener>();
            foreach (IPEndPoint endpoint in endpoints)
            {
                var listener = new TcpListener(endpoint.Address, endpoint.Port);
                listener.Server.ExclusiveAddressUse = false;
                listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                listener.Start();
                listeners.Add(listener);
            }

            var cts = new CancellationTokenSource();
            lock (_gate)
            {
                _listeners.Clear();
                _listeners.AddRange(endpoints.Select(e => e.Address + ":" + Port));
                _tcpListeners.AddRange(listeners);
                _cts = cts;
                IsActive = true;
                foreach (TcpListener listener in listeners)
                {
                    _acceptTasks.Add(Task.Run(() => AcceptLoopAsync(listener, cts.Token)));
                }
            }

            _log?.Invoke($"首次连接向导已开启：http://{BestAddress().Address}:{Port}/ios-setup/{BootstrapId}（仅私网地址可访问，{(int)BootstrapLifetime.TotalMinutes} 分钟后自动关闭）");
            return Status();
        }
        catch (Exception ex)
        {
            foreach (TcpListener listener in _tcpListeners)
            {
                try { listener.Stop(); } catch { }
            }
            _tcpListeners.Clear();

            LastError = ex is SocketException se && se.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? $"端口 {Port} 已被占用，无法开启首次连接向导。LanDisk 不会自动改用其它端口，请关闭占用该端口的程序后重试。"
                : "无法开启首次连接向导：" + ex.Message;
            _log?.Invoke(LastError);
            return Status();
        }
    }

    /// <summary>
    /// Closes the plain-HTTP bootstrap window and releases 8890.
    ///
    /// <para>The verified-host hint deliberately survives this. The host closes the window as soon as the
    /// phone has trusted the CA — that is the listener's whole purpose — while the pairing QR is still
    /// ahead of the user. Clearing the hint here would throw away the only measured fact we have about
    /// which address that phone can reach, exactly when it is about to be needed.</para>
    /// </summary>
    public void Stop(string reason)
    {
        CancellationTokenSource? cts;
        List<TcpListener> listeners;
        Task[] accepts;

        lock (_gate)
        {
            if (!IsActive && _tcpListeners.Count == 0) return;
            cts = _cts;
            listeners = [.. _tcpListeners];
            accepts = [.. _acceptTasks];
            _tcpListeners.Clear();
            _acceptTasks.Clear();
            _cts = null;
            IsActive = false;
            _bootstrapId = string.Empty;
            _listeners.Clear();
        }

        try { cts?.Cancel(); } catch { }
        foreach (TcpListener listener in listeners)
        {
            try { listener.Stop(); } catch { }
        }
        try { Task.WaitAll(accepts, TimeSpan.FromSeconds(2)); } catch { }
        try { cts?.Dispose(); } catch { }

        _log?.Invoke("首次连接向导已关闭：" + reason);
    }

    /// <summary>Records that a device completed the HTTPS trust step.</summary>
    public void MarkTrusted(string? detail)
    {
        Trusted = true;
        TrustedAt = DateTimeOffset.Now;
        _log?.Invoke("检测到 iPhone 已完成证书完全信任（HTTPS 探测成功）" + (string.IsNullOrWhiteSpace(detail) ? "" : "：" + detail));
    }

    /// <summary>
    /// How long a verified HTTPS host stays usable as this wizard's connectivity hint.
    ///
    /// <para>It has to outlive the plain-HTTP bootstrap window on purpose. The host closes 8890 the moment
    /// the phone has trusted the CA, and the pairing QR is scanned after that — so a hint that died with
    /// the listener would arrive exactly too late to be useful. Twenty minutes is the same budget the
    /// bootstrap window itself gets.</para>
    /// </summary>
    public static readonly TimeSpan VerifiedHostLifetime = TimeSpan.FromMinutes(20);

    private string? _verifiedHttpsHost;
    private DateTimeOffset? _verifiedHttpsAt;

    /// <summary>
    /// The HTTPS host a real client reached during <i>this</i> wizard session, recorded from the Host
    /// header of a trust probe that arrived over TLS. Null when nothing was recorded or the hint has
    /// aged out.
    ///
    /// <para><b>What it is for.</b> A phone that completed the probe has proved three things at once: it
    /// can reach this machine at that host, the TLS handshake succeeded, and the certificate was accepted.
    /// Under a VPN/TUN the stable <c>.local</c> name is exactly the host that fails, so the pairing link
    /// is ordered by what the phone actually used instead of by a guess about the network.</para>
    ///
    /// <para><b>What it is not.</b> It is not persisted anywhere: it describes one connection attempt, not
    /// the installation. It never contains credentials, and it is only ever set from the stable name of
    /// this installation or from an address the interface classifier currently accepts.</para>
    /// </summary>
    public string? VerifiedHttpsHost
    {
        get
        {
            lock (_gate)
            {
                if (_verifiedHttpsHost is null) return null;
                if (_verifiedHttpsAt is { } at && DateTimeOffset.Now - at > VerifiedHostLifetime)
                {
                    _verifiedHttpsHost = null;
                    _verifiedHttpsAt = null;
                    return null;
                }
                return _verifiedHttpsHost;
            }
        }
    }

    /// <summary>When the currently held hint was recorded, or null when there is none.</summary>
    public DateTimeOffset? VerifiedHttpsAt
    {
        get { lock (_gate) { return _verifiedHttpsHost is null ? null : _verifiedHttpsAt; } }
    }

    /// <summary>
    /// Records "a phone reached us at this host over HTTPS". Returns false — and records nothing — when the
    /// host is not one this installation is allowed to advertise.
    ///
    /// <para>The allowed set is deliberately tiny: the stable name of this installation, or an address the
    /// physical-interface classifier accepts right now. A forged <c>Host</c> header, a public name, a
    /// VPN/TUN address, a Hyper-V switch address or the 198.18.0.0/15 benchmark range can therefore never
    /// steer the pairing QR somewhere else. Names are compared case-insensitively; addresses are parsed
    /// and compared as addresses, never as strings.</para>
    /// </summary>
    public bool RecordVerifiedHttpsHost(string? host)
    {
        string? normalized = NormalizeVerifiedHost(host);
        if (normalized is null)
        {
            _log?.Invoke("忽略了一个不属于本机合法地址的 HTTPS 探测 Host：" + (string.IsNullOrWhiteSpace(host) ? "(空)" : host));
            return false;
        }

        lock (_gate)
        {
            _verifiedHttpsHost = normalized;
            _verifiedHttpsAt = DateTimeOffset.Now;
        }

        _log?.Invoke($"已记录本次真正可达的 HTTPS 地址：{normalized}（配对二维码会优先使用它）");
        return true;
    }

    /// <summary>
    /// Forgets the hint. Called when a wizard session ends for good — a new session starting, or a pairing
    /// that has just succeeded — and never merely because the 8890 listener closed.
    /// </summary>
    public void ClearVerifiedHttpsHost(string reason)
    {
        bool had;
        lock (_gate)
        {
            had = _verifiedHttpsHost is not null;
            _verifiedHttpsHost = null;
            _verifiedHttpsAt = null;
        }

        if (had) _log?.Invoke("已清除本次的连接地址提示：" + reason);
    }

    /// <summary>
    /// The one place a Host header is turned into a hint. Returns the canonical form of an allowed host, or
    /// null. Kept separate from <see cref="RecordVerifiedHttpsHost"/> so the rule can be tested directly.
    /// </summary>
    public string? NormalizeVerifiedHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;

        string candidate = host.Trim();
        if (candidate.Length is 0 or > 253) return null;
        if (candidate.StartsWith('[') && candidate.EndsWith(']')) candidate = candidate[1..^1];

        // A Host header is one name or one address: never a URL, a path, credentials or a port list.
        if (candidate.Contains('/') || candidate.Contains('\\') || candidate.Contains('@')
            || candidate.Contains(' ') || candidate.Contains(':'))
        {
            return null;
        }

        string stable = _hostName();
        if (!string.IsNullOrWhiteSpace(stable) && string.Equals(candidate, stable, StringComparison.OrdinalIgnoreCase))
            return stable;

        // Every other name is refused outright: the Windows machine name, localhost, a DNS suffix, or
        // anything an attacker puts in the header.
        if (!IPAddress.TryParse(candidate, out IPAddress? ip)) return null;

        // Loopback is a legitimate probe result (a browser on this very machine) but it is never a place a
        // phone can go, so it is recorded and then deliberately never ranked first by the pairing links.
        if (IPAddress.IsLoopback(ip)) return ip.ToString();

        foreach (string address in _addresses())
        {
            if (IPAddress.TryParse(address, out IPAddress? allowed) && allowed.Equals(ip)) return allowed.ToString();
        }

        return null;
    }

    /// <summary>True when the bootstrap id in a request matches the live session.</summary>
    public bool MatchesBootstrapId(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return false;

        string current;
        lock (_gate) { current = _bootstrapId; }
        if (string.IsNullOrEmpty(current)) return false;

        // Hash both sides first so the comparison is fixed-length and cannot leak the id's length.
        byte[] left = SHA256.HashData(Encoding.ASCII.GetBytes(candidate));
        byte[] right = SHA256.HashData(Encoding.ASCII.GetBytes(current));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    private (string Address, string Reason) BestAddress()
    {
        // The bootstrap QR must point at an address the phone can actually open, so it uses the same
        // physical-interface rule as mDNS and the certificate.
        AdapterVerdict? best = PhysicalLanSelector.Best();
        return best is null ? (string.Empty, "没有可用的物理局域网地址") : (best.Address, best.Reason);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException)
            {
                if (token.IsCancellationRequested) return;
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, token), token);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;

            try
            {
                NetworkStream stream = client.GetStream();

                if (DateTimeOffset.Now > _expiresAt)
                {
                    await WriteAsync(stream, 410, "text/plain; charset=utf-8",
                        "本次连接向导已过期。请在电脑上重新点击“连接 iPhone”。", token).ConfigureAwait(false);
                    return;
                }

                (string method, string path, _) = await ReadRequestAsync(stream, token).ConfigureAwait(false);
                if (string.IsNullOrEmpty(method))
                {
                    await WriteAsync(stream, 400, "text/plain; charset=utf-8", "请求无法解析。", token).ConfigureAwait(false);
                    return;
                }

                if (method is not ("GET" or "HEAD"))
                {
                    await WriteAsync(stream, 405, "text/plain; charset=utf-8", "只支持 GET。", token).ConfigureAwait(false);
                    return;
                }

                ServedRequests++;
                await RouteAsync(stream, method, path, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Invoke("首次连接向导请求处理失败：" + ex.Message);
            }
        }
    }

    private async Task RouteAsync(NetworkStream stream, string method, string path, CancellationToken token)
    {
        string clean = path.Split('?')[0].TrimEnd('/');
        string[] segments = clean.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (clean.Length == 0)
        {
            await WriteAsync(stream, 200, "text/plain; charset=utf-8", "LanDisk onboarding bootstrap.\n", token).ConfigureAwait(false);
            return;
        }

        if (segments.Length < 1 || !segments[0].Equals("ios-setup", StringComparison.OrdinalIgnoreCase))
        {
            await WriteAsync(stream, 404, "text/plain; charset=utf-8", "未知路径。", token).ConfigureAwait(false);
            return;
        }

        // /ios-setup/<id>/...
        if (segments.Length < 2 || !MatchesBootstrapId(segments[1]))
        {
            await WriteAsync(stream, 404, "text/plain; charset=utf-8",
                "该连接向导已失效。请在电脑上重新点击“连接 iPhone”获取新的二维码。", token).ConfigureAwait(false);
            return;
        }

        string action = segments.Length >= 3 ? segments[2].ToLowerInvariant() : string.Empty;

        switch (action)
        {
            case "":
                await WriteAsync(stream, 200, "text/html; charset=utf-8", BootstrapPage(), token, method).ConfigureAwait(false);
                return;

            case "landisk.mobileconfig":
            {
                byte[]? ca = _caCertificate();
                if (ca is null)
                {
                    await WriteAsync(stream, 500, "text/plain; charset=utf-8", "本地 CA 证书不可用。", token).ConfigureAwait(false);
                    return;
                }

                await WriteBytesAsync(stream, 200, "application/x-apple-aspen-config",
                    Encoding.UTF8.GetBytes(MobileConfig(ca)), "lanDisk-ca.mobileconfig", token, method)
                    .ConfigureAwait(false);
                return;
            }

            case "landisk-ca.cer":
            case "landisk-root.cer":
            {
                byte[]? ca = _caCertificate();
                if (ca is null)
                {
                    await WriteAsync(stream, 500, "text/plain; charset=utf-8", "本地 CA 证书不可用。", token).ConfigureAwait(false);
                    return;
                }

                await WriteBytesAsync(stream, 200, "application/pkix-cert", ca, "landisk-ca.cer", token, method).ConfigureAwait(false);
                return;
            }

            case "status":
            {
                // Deliberately tiny and non-sensitive: the page needs to know one boolean.
                string json = "{\"trusted\":" + (Trusted ? "true" : "false")
                              + ",\"active\":" + (IsActive ? "true" : "false")
                              + ",\"hostName\":\"" + Json(_hostName()) + "\""
                              + ",\"httpsPort\":" + _httpsPort()
                              + "}";
                await WriteAsync(stream, 200, "application/json; charset=utf-8", json, token).ConfigureAwait(false);
                return;
            }

            default:
                await WriteAsync(stream, 404, "text/plain; charset=utf-8", "未知路径。", token).ConfigureAwait(false);
                return;
        }
    }

    // ------------------------------------------------------------------ content

    public string BootstrapPage()
    {
        string host = _hostName();
        int httpsPort = _httpsPort();
        string hostHtml = Html(host);
        string caUrl = $"/ios-setup/{BootstrapId}/landisk-ca.cer";
        string profileUrl = $"/ios-setup/{BootstrapId}/landisk.mobileconfig";
        string statusUrl = $"/ios-setup/{BootstrapId}/status";
        string bootstrapId = BootstrapId ?? string.Empty;

        // The trust probe is an HTTPS request, so it can only be attempted against a name the phone can
        // resolve and a certificate it already trusts. The stable name is tried first — it survives an
        // address change — and every physical LAN address is a fallback for networks that drop mDNS.
        var probeHosts = new List<string> { host };
        foreach (string address in _addresses())
        {
            if (!probeHosts.Contains(address, StringComparer.OrdinalIgnoreCase)) probeHosts.Add(address);
        }

        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1,viewport-fit=cover\">");
        sb.AppendLine("<meta name=\"referrer\" content=\"no-referrer\">");
        sb.AppendLine("<title>连接 LanDisk</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(":root{color-scheme:light dark}body{font:16px/1.6 -apple-system,system-ui,'PingFang SC',sans-serif;margin:0;padding:24px;max-width:640px}");
        sb.AppendLine("h1{font-size:22px;margin:0 0 4px}h2{font-size:17px;margin:28px 0 8px}");
        sb.AppendLine("ol{padding-left:22px}li{margin:6px 0}.muted{opacity:.7;font-size:14px}");
        sb.AppendLine("a.btn{display:block;text-align:center;padding:16px;margin:14px 0;border-radius:12px;background:#0a84ff;color:#fff;text-decoration:none;font-weight:600}");
        sb.AppendLine(".box{border:1px solid rgba(128,128,128,.35);border-radius:12px;padding:14px;margin:14px 0}");
        sb.AppendLine(".ok{color:#0a8a3a;font-weight:600}.wait{opacity:.75}");
        sb.AppendLine(".done{border:1px solid #0a8a3a;border-radius:12px;padding:16px;margin:16px 0;background:rgba(10,138,58,.08)}");
        sb.AppendLine("</style></head><body>");
        sb.AppendLine("<h1>连接 LanDisk</h1>");
        sb.AppendLine($"<p class=\"muted\">这台电脑的稳定名称：<strong>{hostHtml}</strong></p>");

        sb.AppendLine("<h2>第 1 步 · 安装 LanDisk 安全证书</h2>");
        sb.AppendLine($"<a class=\"btn\" href=\"{profileUrl}\">安装 LanDisk 安全证书</a>");
        sb.AppendLine($"<p class=\"muted\">如果描述文件方式不可用，可改用 <a href=\"{caUrl}\">下载证书文件 (.cer)</a>。</p>");

        sb.AppendLine("<h2>第 2 步 · 在 iPhone 上安装</h2>");
        sb.AppendLine("<ol><li>弹出“已下载描述文件”时，点<strong>关闭</strong>，然后打开 <strong>设置</strong>。</li>");
        sb.AppendLine("<li>设置 → 通用 → VPN与设备管理 → 安装刚才下载的 LanDisk 描述文件。</li>");
        sb.AppendLine("<li>设置 → 通用 → 关于本机 → 证书信任设置 → 打开对 <strong>LanDisk Local CA</strong> 的<strong>完全信任</strong>。</li></ol>");
        sb.AppendLine("<div class=\"box\">苹果要求由你本人在设备上确认这一步，LanDisk 不能也不会替你自动完成；" +
                      "只安装描述文件、不打开“完全信任”时，Safari 仍然会报证书错误。</div>");

        // The page now finishes on its own: it keeps probing over HTTPS every other second, so the user
        // never has to come back and press anything. Only the Apple-mandated trust switch is left.
        sb.AppendLine("<h2>第 3 步 · 这一步会自动完成</h2>");
        sb.AppendLine("<p id=\"state\" class=\"wait\">正在等待“完全信任”打开…（完成第 2 步后无需任何操作，本页会自己发现）</p>");
        sb.AppendLine("<div id=\"done\" class=\"done\" style=\"display:none\">");
        sb.AppendLine("<div class=\"ok\">✅ 安全连接已建立</div>");
        sb.AppendLine("<p class=\"muted\" style=\"margin:8px 0 0\">这台 iPhone 已经能用 HTTPS 访问 LanDisk。" +
                      "现在请看电脑上的 LanDisk 窗口，那里会显示配对二维码。</p>");
        sb.AppendLine("</div>");
        sb.AppendLine("<p><button id=\"retry\" class=\"btn\" style=\"border:0;width:100%;font-size:16px;background:#5b6878\">立即再检测一次</button></p>");

        sb.AppendLine("<h2>之后</h2>");
        sb.AppendLine("<p class=\"muted\">信任完成后回到电脑上，窗口里会自动出现一次性配对二维码；用 iPhone 相机扫描即可完成配对。" +
                      $"以后直接访问 <code>https://{hostHtml}:{httpsPort}/</code> 即可，地址变化也不需要重新信任证书。</p>");

        sb.AppendLine("<script>");
        sb.AppendLine("(function(){");
        sb.AppendLine($"var httpsPort={httpsPort}, hosts={System.Text.Json.JsonSerializer.Serialize(probeHosts.ToArray())}, " +
                      $"bootstrapId={System.Text.Json.JsonSerializer.Serialize(bootstrapId)}, " +
                      $"statusUrl={System.Text.Json.JsonSerializer.Serialize(statusUrl)};");
        sb.AppendLine("var state=document.getElementById('state');");
        sb.AppendLine("var done=document.getElementById('done');");
        sb.AppendLine("var retry=document.getElementById('retry');");
        sb.AppendLine("var probing=false, stopped=false, hostIndex=0;");
        sb.AppendLine("function showDone(){ stopped=true; state.className='ok'; state.textContent='已确认 HTTPS 可用。'; " +
                      "done.style.display='block'; retry.style.display='none'; }");
        sb.AppendLine("function status(){");
        sb.AppendLine("  fetch(statusUrl,{cache:'no-store'}).then(function(r){return r.json()}).then(function(s){");
        sb.AppendLine("    if(s.trusted){ showDone(); }");
        sb.AppendLine("  }).catch(function(){ /* the status endpoint is best effort */ });");
        sb.AppendLine("}");
        sb.AppendLine("function probe(){");
        sb.AppendLine("  if(stopped||probing) return;");
        sb.AppendLine("  probing=true;");
        sb.AppendLine("  var host=hosts[hostIndex % hosts.length];");
        sb.AppendLine("  hostIndex++;");
        sb.AppendLine("  var img=new Image();var settled=false;");
        sb.AppendLine("  function finish(){ if(!settled){ settled=true; probing=false; status(); } }");
        sb.AppendLine("  img.onload=finish; img.onerror=finish;");
        sb.AppendLine("  img.src='https://'+host+':'+httpsPort+'/api/onboarding/trust-probe?b='+encodeURIComponent(bootstrapId)+'&t='+Date.now();");
        sb.AppendLine("  setTimeout(function(){ if(!settled){ settled=true; probing=false; status(); } },4000);");
        sb.AppendLine("}");
        sb.AppendLine("retry.addEventListener('click',function(){ stopped=false; state.className='wait'; " +
                      "state.textContent='正在检查…'; probe(); });");
        sb.AppendLine("status();");
        sb.AppendLine("probe();");
        sb.AppendLine("setInterval(function(){ if(!stopped) probe(); }, 1500);");
        sb.AppendLine("setInterval(function(){ if(!stopped) status(); }, 1500);");
        sb.AppendLine("window.addEventListener('pageshow',function(){ if(!stopped){ status(); probe(); } });");
        sb.AppendLine("})();");
        sb.AppendLine("</script></body></html>");
        return sb.ToString();
    }

    /// <summary>
    /// An unsigned configuration profile carrying only the public root certificate. It deliberately
    /// sets no VPN, proxy, MDM or other device policy, and has a unique identifier per installation.
    /// iOS will label it "未签名"; that is the truth and it is not dressed up as anything else.
    /// </summary>
    public string MobileConfig(byte[] caDer)
    {
        string base64 = Convert.ToBase64String(caDer, Base64FormattingOptions.InsertLineBreaks);
        string uuid = DeterministicUuid(caDer);
        string name = Html(_hostName());

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">");
        sb.AppendLine("<plist version=\"1.0\"><dict>");
        sb.AppendLine("  <key>PayloadContent</key><array><dict>");
        sb.AppendLine("    <key>PayloadCertificateFileName</key><string>landisk-ca.cer</string>");
        sb.AppendLine("    <key>PayloadDescription</key><string>LanDisk 本地根证书</string>");
        sb.AppendLine("    <key>PayloadDisplayName</key><string>LanDisk Local CA</string>");
        sb.AppendLine($"    <key>PayloadIdentifier</key><string>local.landisk.ca.{uuid}</string>");
        sb.AppendLine("    <key>PayloadType</key><string>com.apple.security.root</string>");
        sb.AppendLine($"    <key>PayloadUUID</key><string>{uuid}</string>");
        sb.AppendLine("    <key>PayloadVersion</key><integer>1</integer>");
        sb.AppendLine("    <key>PayloadContent</key><data>");
        sb.AppendLine(base64);
        sb.AppendLine("    </data>");
        sb.AppendLine("  </dict></array>");
        sb.AppendLine("  <key>PayloadDescription</key><string>只包含 LanDisk 本地根证书，用于让 Safari 信任这台电脑的 HTTPS。</string>");
        sb.AppendLine($"  <key>PayloadDisplayName</key><string>LanDisk ({name})</string>");
        sb.AppendLine($"  <key>PayloadIdentifier</key><string>local.landisk.profile.{uuid}</string>");
        sb.AppendLine("  <key>PayloadRemovalDisallowed</key><false/>");
        sb.AppendLine("  <key>PayloadType</key><string>Configuration</string>");
        sb.AppendLine($"  <key>PayloadUUID</key><string>{uuid}</string>");
        sb.AppendLine("  <key>PayloadVersion</key><integer>1</integer>");
        sb.AppendLine("</dict></plist>");
        return sb.ToString();
    }

    private static string DeterministicUuid(byte[] caDer)
    {
        byte[] hash = SHA256.HashData(caDer);
        return string.Format("{0:x2}{1:x2}{2:x2}{3:x2}-{4:x2}{5:x2}-{6:x2}{7:x2}-{8:x2}{9:x2}-{10:x2}{11:x2}{12:x2}{13:x2}{14:x2}{15:x2}",
            hash[0], hash[1], hash[2], hash[3], hash[4], hash[5], hash[6], hash[7],
            hash[8], hash[9], hash[10], hash[11], hash[12], hash[13], hash[14], hash[15]);
    }

    // ------------------------------------------------------------------ tiny HTTP

    private static async Task<(string Method, string Path, string Version)> ReadRequestAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[8192];
        int total = 0;

        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), token).ConfigureAwait(false);
            if (read <= 0) break;
            total += read;

            string soFar = Encoding.ASCII.GetString(buffer, 0, total);
            if (soFar.Contains("\r\n\r\n", StringComparison.Ordinal)) break;
            if (soFar.Contains('\n') && total > 4096) break;
        }

        if (total == 0) return (string.Empty, string.Empty, string.Empty);

        string head = Encoding.ASCII.GetString(buffer, 0, total);
        string firstLine = head.Split('\n')[0].Trim();
        string[] parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return (string.Empty, string.Empty, string.Empty);

        return (parts[0].ToUpperInvariant(), parts[1], parts.Length > 2 ? parts[2] : "HTTP/1.0");
    }

    private static async Task WriteAsync(Stream stream, int status, string contentType, string body, CancellationToken token, string method = "GET")
    {
        byte[] payload = Encoding.UTF8.GetBytes(body);
        await WriteBytesAsync(stream, status, contentType, payload, null, token, method).ConfigureAwait(false);
    }

    private static async Task WriteBytesAsync(
        Stream stream, int status, string contentType, byte[] payload, string? fileName,
        CancellationToken token, string method = "GET")
    {
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n");
        head.Append("Content-Type: ").Append(contentType).Append("\r\n");
        head.Append("Content-Length: ").Append(payload.Length).Append("\r\n");
        head.Append("Cache-Control: no-store, no-cache, must-revalidate\r\n");
        head.Append("Pragma: no-cache\r\n");
        head.Append("X-Content-Type-Options: nosniff\r\n");
        head.Append("Referrer-Policy: no-referrer\r\n");
        head.Append("Connection: close\r\n");
        if (fileName is not null) head.Append("Content-Disposition: attachment; filename=\"").Append(fileName).Append("\"\r\n");
        head.Append("\r\n");

        byte[] headerBytes = Encoding.ASCII.GetBytes(head.ToString());
        await stream.WriteAsync(headerBytes, token).ConfigureAwait(false);
        if (!method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            await stream.WriteAsync(payload, token).ConfigureAwait(false);
        }
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        404 => "Not Found",
        405 => "Method Not Allowed",
        410 => "Gone",
        500 => "Internal Server Error",
        _ => "OK"
    };

    private static string Html(string value) => value
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string Json(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public void Dispose() => Stop("服务退出");
}
