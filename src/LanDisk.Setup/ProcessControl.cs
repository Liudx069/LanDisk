using System.Diagnostics;
using System.Text.Json;

namespace LanDisk.Setup;

/// <summary>
/// Finding, stopping and starting the product.
///
/// <para>Replacing program files while the program is running is the classic way to produce a broken
/// installation, so the installer stops a running LanDisk first — and stops it the polite way: it asks
/// the running instance to shut itself down through its own loopback management API, which is the same
/// path the tray menu uses, so uploads are cancelled and the recycle bin is reconciled before the
/// process exits. Only if that does not work within a bounded time does it fall back to a window close,
/// and only if the caller allows it does it terminate the process.</para>
/// </summary>
public static class ProcessControl
{
    public static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(25);

    public static IReadOnlyList<Process> FindRunningLanDisk(string? installDir = null)
    {
        var found = new List<Process>();

        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                string name = process.ProcessName;
                if (!name.Equals("LanDisk", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("LanDisk.Server", StringComparison.OrdinalIgnoreCase)) continue;

                if (!string.IsNullOrEmpty(installDir))
                {
                    string? path = TryGetPath(process);
                    if (path is null || !SetupLayout.IsInside(path, installDir)) continue;
                }

                found.Add(process);
            }
            catch
            {
                // A process that cannot be inspected is not ours to stop.
            }
        }

        return found;
    }

    public static string? TryGetPath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch { return null; }
    }

    /// <summary>
    /// Stops every running instance. Returns the processes that are still alive afterwards.
    /// </summary>
    public static async Task<IReadOnlyList<Process>> StopAsync(
        string dataDir,
        string? installDir,
        bool allowForce,
        Action<string>? log = null)
    {
        IReadOnlyList<Process> running = FindRunningLanDisk(installDir);
        if (running.Count == 0) return [];

        log?.Invoke($"检测到正在运行的 LanDisk（{running.Count} 个进程），正在请求它安全退出…");

        // Preferred path: the product's own shutdown endpoint. It uses the admin token that the
        // running instance already wrote for the local user, so no credential is invented here.
        foreach (Process process in running)
        {
            string? adminUrl = await TryReadAdminUrlAsync(dataDir).ConfigureAwait(false);
            if (adminUrl is not null) await TryRequestShutdownAsync(adminUrl).ConfigureAwait(false);
        }

        DateTimeOffset deadline = DateTimeOffset.Now + GracefulStopTimeout;
        while (DateTimeOffset.Now < deadline)
        {
            await Task.Delay(500).ConfigureAwait(false);
            if (FindRunningLanDisk(installDir).Count == 0) return [];
        }

        foreach (Process process in FindRunningLanDisk(installDir))
        {
            try
            {
                if (process.CloseMainWindow()) log?.Invoke("已请求关闭 LanDisk 窗口。");
            }
            catch { }
        }

        await Task.Delay(3000).ConfigureAwait(false);
        IReadOnlyList<Process> remaining = FindRunningLanDisk(installDir);
        if (remaining.Count == 0) return [];

        if (!allowForce)
        {
            log?.Invoke("LanDisk 仍在运行，自动安装不会强制结束它。请手动退出后重试。");
            return remaining;
        }

        foreach (Process process in remaining)
        {
            try
            {
                log?.Invoke($"强制结束进程 {process.Id}（已等待 {GracefulStopTimeout.TotalSeconds:0} 秒仍未退出）。");
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                log?.Invoke("结束进程失败：" + ex.Message);
            }
        }

        await Task.Delay(1000).ConfigureAwait(false);
        return FindRunningLanDisk(installDir);
    }

    private static async Task<string?> TryReadAdminUrlAsync(string dataDir)
    {
        string path = SetupLayout.AdminUrlFileFor(dataDir);
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    string text = (await File.ReadAllTextAsync(path).ConfigureAwait(false)).Trim();
                    if (text.Length > 0) return text;
                }
            }
            catch { }

            await Task.Delay(250).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>
    /// Calls the shutdown endpoint using the token that is already in the admin URL fragment. The
    /// token is used locally and never written to the installer's log.
    /// </summary>
    private static async Task TryRequestShutdownAsync(string adminUrl)
    {
        try
        {
            int hash = adminUrl.IndexOf('#');
            string basePart = hash >= 0 ? adminUrl[..hash] : adminUrl;
            string token = string.Empty;
            if (hash >= 0)
            {
                string fragment = adminUrl[(hash + 1)..];
                foreach (string pair in fragment.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    int equals = pair.IndexOf('=');
                    if (equals > 0 && pair[..equals].Equals("token", StringComparison.OrdinalIgnoreCase))
                        token = pair[(equals + 1)..];
                }
            }

            var origin = new Uri(basePart);
            string root = origin.GetLeftPart(UriPartial.Authority);

            using var handler = new HttpClientHandler { CookieContainer = new System.Net.CookieContainer() };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

            if (token.Length > 0)
            {
                // POST /api/admin/session with the one-time token: the same handshake the browser does
                // on first load. The session cookie lands in this handler's cookie container.
                using var login = new HttpRequestMessage(HttpMethod.Post, root + "/api/admin/session");
                login.Headers.Add("X-LanDisk-Client", "setup");
                login.Content = new StringContent(
                    JsonSerializer.Serialize(new { token }), System.Text.Encoding.UTF8, "application/json");

                using HttpResponseMessage response = await client.SendAsync(login).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return;

                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                string? csrf = null;
                try
                {
                    using JsonDocument document = JsonDocument.Parse(body);
                    if (document.RootElement.TryGetProperty("csrfToken", out JsonElement element))
                        csrf = element.GetString();
                }
                catch { }

                using var shutdown = new HttpRequestMessage(HttpMethod.Post, root + "/api/admin/shutdown");
                shutdown.Headers.Add("X-LanDisk-Client", "setup");
                if (!string.IsNullOrEmpty(csrf)) shutdown.Headers.Add("X-LanDisk-CSRF", csrf);
                shutdown.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
                using HttpResponseMessage stopped = await client.SendAsync(shutdown).ConfigureAwait(false);
                return;
            }
        }
        catch
        {
            // Best effort: the caller falls back to asking the window to close.
        }
    }

    /// <summary>
    /// Opens the first-connection wizard on the running instance and returns the bootstrap URL, so the
    /// installer can show the same QR code the management page shows. Returns null when the instance
    /// is not reachable; the caller then simply opens the management page.
    /// </summary>
    public static async Task<string?> StartOnboardingAsync(string dataDir, Action<string>? log = null)
    {
        string? adminUrl = ReadAdminUrl(dataDir);
        if (adminUrl is null) return null;

        try
        {
            int hash = adminUrl.IndexOf('#');
            string basePart = hash >= 0 ? adminUrl[..hash] : adminUrl;
            string token = string.Empty;
            if (hash >= 0)
            {
                foreach (string pair in adminUrl[(hash + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    int equals = pair.IndexOf('=');
                    if (equals > 0 && pair[..equals].Equals("token", StringComparison.OrdinalIgnoreCase))
                        token = pair[(equals + 1)..];
                }
            }

            var origin = new Uri(basePart);
            string root = origin.GetLeftPart(UriPartial.Authority);

            using var handler = new HttpClientHandler { CookieContainer = new System.Net.CookieContainer() };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

            using var login = new HttpRequestMessage(HttpMethod.Post, root + "/api/admin/session");
            login.Headers.Add("X-LanDisk-Client", "setup");
            login.Content = new StringContent(
                JsonSerializer.Serialize(new { token }), System.Text.Encoding.UTF8, "application/json");
            using HttpResponseMessage session = await client.SendAsync(login).ConfigureAwait(false);
            if (!session.IsSuccessStatusCode) return null;

            string body = await session.Content.ReadAsStringAsync().ConfigureAwait(false);
            string? csrf = null;
            try
            {
                using JsonDocument document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("csrfToken", out JsonElement element)) csrf = element.GetString();
            }
            catch { }

            using var start = new HttpRequestMessage(HttpMethod.Post, root + "/api/admin/onboarding/start");
            start.Headers.Add("X-LanDisk-Client", "setup");
            if (!string.IsNullOrEmpty(csrf)) start.Headers.Add("X-LanDisk-CSRF", csrf);
            start.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await client.SendAsync(start).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            string startBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using JsonDocument result = JsonDocument.Parse(startBody);

            if (result.RootElement.TryGetProperty("lastError", out JsonElement lastError)
                && lastError.ValueKind == JsonValueKind.String
                && lastError.GetString() is { Length: > 0 } message)
            {
                log?.Invoke(message);
            }

            return result.RootElement.TryGetProperty("bootstrapUrl", out JsonElement url) ? url.GetString() : null;
        }
        catch (Exception ex)
        {
            log?.Invoke("无法开启首次连接向导：" + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Starts the product's own window.
    ///
    /// <para><paramref name="page"/> names the area to open (for example <c>连接 iPhone</c>). It is how the
    /// installer can bring up the pairing wizard without sending the user to a browser: this launches
    /// <c>LanDisk.exe</c> with <c>--page</c>, and the window starts maximized on that page.</para>
    /// </summary>
    public static bool StartShell(string installDir, string dataDir, Action<string>? log = null, string? page = null)
    {
        string exe = SetupLayout.ShellPathFor(installDir);
        if (!File.Exists(exe))
        {
            log?.Invoke("找不到 " + exe);
            return false;
        }

        try
        {
            var info = new ProcessStartInfo(exe)
            {
                WorkingDirectory = installDir,
                UseShellExecute = true
            };
            info.ArgumentList.Add("--data-dir");
            info.ArgumentList.Add(dataDir);
            if (!string.IsNullOrWhiteSpace(page))
            {
                info.ArgumentList.Add("--page");
                info.ArgumentList.Add(page!);
            }

            Process.Start(info);
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke("启动 LanDisk 失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>Waits until the product answers its own health endpoint, or the timeout expires.</summary>
    public static async Task<bool> WaitForReadinessAsync(int adminPort, TimeSpan timeout, Action<string>? log = null)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        DateTimeOffset deadline = DateTimeOffset.Now + timeout;

        while (DateTimeOffset.Now < deadline)
        {
            try
            {
                using HttpResponseMessage response =
                    await client.GetAsync($"http://127.0.0.1:{adminPort}/api/health").ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    using JsonDocument document = JsonDocument.Parse(body);
                    if (document.RootElement.TryGetProperty("ok", out JsonElement ok) && ok.GetBoolean()) return true;
                }
            }
            catch
            {
                // Not up yet.
            }

            await Task.Delay(400).ConfigureAwait(false);
        }

        log?.Invoke("等待 LanDisk 就绪超时。");
        return false;
    }

    /// <summary>The local management URL including its one-time token fragment, or null.</summary>
    public static string? ReadAdminUrl(string dataDir)
    {
        try
        {
            string path = SetupLayout.AdminUrlFileFor(dataDir);
            if (!File.Exists(path)) return null;
            string text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Opens the management page on the given view. The one-time token stays in the URL fragment, so
    /// it is never sent to the server as part of a request line.
    /// </summary>
    public static bool OpenUrl(string url, Action<string>? log = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke("打开浏览器失败：" + ex.Message);
            return false;
        }
    }
}
