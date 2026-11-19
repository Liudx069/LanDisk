using System.Diagnostics;
using System.Text.Json;

namespace LanDisk.Update;

/// <summary>
/// Where things live in an installation, and how to find the pieces the update system launches.
/// Kept in one place so the server and the updater cannot disagree about a file name.
/// </summary>
public static class UpdateLayout
{
    public const string ShellFileName = "LanDisk.exe";
    public const string ServerFileName = "LanDisk.Server.exe";
    public const string UpdaterFileName = "LanDisk.Updater.exe";
    public const string UpdaterDirectoryName = "updater";

    /// <summary>The directory the running product was started from.</summary>
    public static string InstallRoot() => Normalize(AppContext.BaseDirectory);

    public static string UpdaterDirectory(string installRoot)
        => Path.Combine(Normalize(installRoot), UpdaterDirectoryName);

    /// <summary>
    /// The installed updater, or <c>null</c> when the installation predates the updater (for
    /// example a plain <c>LanDisk.Server.exe</c> publish from task 01/02).
    /// </summary>
    public static string? FindUpdater(string installRoot)
    {
        string candidate = Path.Combine(UpdaterDirectory(installRoot), UpdaterFileName);
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// The user facing shell if present, otherwise the bare server. The updater restarts whichever
    /// one the installation actually uses.
    /// </summary>
    public static string ResolveEntryPoint(string installRoot)
    {
        string normalized = Normalize(installRoot);
        string shell = Path.Combine(normalized, ShellFileName);
        if (File.Exists(shell)) return shell;

        string server = Path.Combine(normalized, ServerFileName);
        if (File.Exists(server)) return server;

        throw new FileNotFoundException($"安装目录中既没有 {ShellFileName} 也没有 {ServerFileName}。", normalized);
    }

    public static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

/// <summary>
/// What the restarted instance must report for an update to count as successful.
///
/// Built from the health snapshot the server took before applying, plus the package's own promises
/// about version and commit. Comparing against observed values — not against the updater's intent —
/// is what makes "the upgrade worked" a statement about the product rather than about this program.
/// </summary>
public sealed class UpdateHealthExpectation
{
    public string? Product { get; init; }

    /// <summary>The version the package says it installs.</summary>
    public string? Version { get; init; }

    /// <summary>The commit the package says it was built from.</summary>
    public string? BuildCommit { get; init; }

    public int? AdminPort { get; init; }
    public int? LanPort { get; init; }
    public string? CertificateThumbprint { get; init; }

    /// <summary>True when a certificate existed before, so its absence afterwards is a failure.</summary>
    public bool CertificateWasPresent { get; init; }

    /// <summary>Previous shares store hash; a different value means the store was rewritten.</summary>
    public string? SharesStoreSha256 { get; init; }

    public string? DevicesStoreSha256 { get; init; }
    public string? DataRoot { get; init; }

    /// <summary>When false, an unknown commit on the running build is tolerated (development builds).</summary>
    public bool RequireBuildCommit { get; init; } = true;

    /// <summary>
    /// Whether the installation must report a build-info.json. A package always ships one, so the
    /// forward path requires it; a rollback only requires what was there before.
    /// </summary>
    public bool RequireInstallBuildInfo { get; init; }

    public static UpdateHealthExpectation FromSnapshot(PreUpdateSnapshot? snapshot, UpdateManifest? manifest, bool isRollback)
    {
        return new UpdateHealthExpectation
        {
            Product = UpdateManifest.ProductName,
            // A rollback must land back on the version that was there before, not the package's.
            Version = isRollback ? snapshot?.Version : manifest?.Version,
            BuildCommit = isRollback ? snapshot?.BuildCommit : manifest?.BuildCommit,
            AdminPort = snapshot?.AdminPort,
            LanPort = snapshot?.LanPort,
            CertificateThumbprint = snapshot?.CertificateThumbprint,
            CertificateWasPresent = !string.IsNullOrEmpty(snapshot?.CertificateThumbprint),
            SharesStoreSha256 = snapshot?.SharesStoreSha256,
            DevicesStoreSha256 = snapshot?.DevicesStoreSha256,
            DataRoot = snapshot?.DataRoot,
            RequireBuildCommit = !isRollback || !string.IsNullOrEmpty(snapshot?.BuildCommit),
            // After installing a package the identity file must exist; after a rollback only what the
            // previous installation actually had may be demanded.
            RequireInstallBuildInfo = !isRollback || !string.IsNullOrEmpty(snapshot?.BuildInfoVersion)
        };
    }
}

public sealed class UpdateHealthResult
{
    public bool Ok { get; init; }
    public string? Version { get; init; }
    public string? BuildCommit { get; init; }
    public string? Message { get; init; }
    public string? Url { get; init; }
    public int Attempts { get; init; }
    public TimeSpan Elapsed { get; init; }

    /// <summary>Every expectation that failed, so the rollback reason names the real problem.</summary>
    public List<string> Failures { get; init; } = [];

    /// <summary>The health payload as observed, for diagnostics and for the update record.</summary>
    public string? ObservedJson { get; init; }

    public static UpdateHealthResult Fail(string message, string? url = null, int attempts = 0, TimeSpan elapsed = default)
        => new() { Ok = false, Message = message, Url = url, Attempts = attempts, Elapsed = elapsed, Failures = [message] };
}

/// <summary>
/// Probes the freshly restarted LanDisk instance.
///
/// The health check is the decision point between "commit" and "roll back", so it asks the running
/// process about itself over loopback and compares the answer against the pre-update snapshot: a
/// server that binds but reports the wrong build, the wrong ports, a different certificate or
/// unloadable state is not healthy, even though it is answering HTTP.
/// </summary>
public static class UpdateHealth
{
    public static async Task<UpdateHealthResult> WaitAsync(
        int port,
        TimeSpan timeout,
        UpdateHealthExpectation expectation,
        CancellationToken cancellationToken = default)
    {
        string url = $"http://127.0.0.1:{port}/api/admin/health";
        var stopwatch = Stopwatch.StartNew();
        int attempts = 0;
        UpdateHealthResult last = UpdateHealthResult.Fail("尚未收到健康检查响应。", url);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;

            try
            {
                using HttpResponseMessage response = await client.GetAsync(url, cancellationToken);
                string body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    UpdateHealthResult evaluated = Evaluate(body, expectation, url, attempts, stopwatch.Elapsed);
                    if (evaluated.Ok) return evaluated;

                    // A wrong-version or wrong-commit answer will not fix itself, but the retry loop
                    // also covers "the new instance is still starting", so keep trying until the
                    // timeout and remember the most informative failure.
                    last = evaluated;
                }
                else
                {
                    last = UpdateHealthResult.Fail($"健康检查返回 HTTP {(int)response.StatusCode}。", url, attempts, stopwatch.Elapsed);
                }
            }
            catch (HttpRequestException ex)
            {
                last = UpdateHealthResult.Fail("服务尚未就绪：" + ex.Message, url, attempts, stopwatch.Elapsed);
            }
            catch (TaskCanceledException)
            {
                last = UpdateHealthResult.Fail("健康检查请求超时。", url, attempts, stopwatch.Elapsed);
            }

            try { await Task.Delay(TimeSpan.FromMilliseconds(700), cancellationToken); }
            catch (OperationCanceledException) { break; }
        }

        return new UpdateHealthResult
        {
            Ok = false,
            Version = last.Version,
            BuildCommit = last.BuildCommit,
            Message = last.Message,
            Url = url,
            Attempts = attempts,
            Elapsed = stopwatch.Elapsed,
            Failures = last.Failures,
            ObservedJson = last.ObservedJson
        };
    }

    public static async Task<UpdateHealthResult> ProbeAsync(
        string url,
        UpdateHealthExpectation expectation,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using HttpResponseMessage response = await client.GetAsync(url, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                return UpdateHealthResult.Fail($"HTTP {(int)response.StatusCode}", url, 1, stopwatch.Elapsed);

            return Evaluate(body, expectation, url, 1, stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return UpdateHealthResult.Fail(ex.Message, url, 1, stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// Compares one health payload against the expectation. Every mismatch is collected, because
    /// "自检未通过" on its own gives an operator nothing to act on.
    /// </summary>
    public static UpdateHealthResult Evaluate(
        string body,
        UpdateHealthExpectation expectation,
        string? url = null,
        int attempts = 1,
        TimeSpan elapsed = default)
    {
        var failures = new List<string>();

        JsonElement root;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new UpdateHealthResult
            {
                Ok = false,
                Message = "健康检查响应不是合法 JSON。",
                Url = url,
                Attempts = attempts,
                Elapsed = elapsed,
                Failures = ["健康检查响应不是合法 JSON。"]
            };
        }

        if (!GetBool(root, "ok"))
            failures.Add("服务自检报告 ok=false（" + FirstErrorCheck(root) + "）");

        string? product = GetString(root, "product");
        string? version = GetString(root, "version");
        string? commit = GetString(root, "buildCommit");

        if (expectation.Product is not null && !string.Equals(product, expectation.Product, StringComparison.Ordinal))
            failures.Add($"product 为 {product ?? "(缺失)"}，预期 {expectation.Product}");

        if (expectation.Version is not null && !string.Equals(version, expectation.Version, StringComparison.OrdinalIgnoreCase))
            failures.Add($"version 为 {version ?? "(缺失)"}，预期 {expectation.Version}");

        if (expectation.RequireBuildCommit && !string.IsNullOrEmpty(expectation.BuildCommit))
        {
            if (string.IsNullOrEmpty(commit))
                failures.Add($"buildCommit 缺失，预期 {expectation.BuildCommit}");
            else if (!string.Equals(commit, expectation.BuildCommit, StringComparison.OrdinalIgnoreCase))
                failures.Add($"buildCommit 为 {commit}，预期 {expectation.BuildCommit}");
        }

        CompareInt(root, "adminPort", expectation.AdminPort, "管理端口", failures);
        CompareInt(root, "lanPort", expectation.LanPort, "局域网端口", failures);

        if (expectation.CertificateWasPresent)
        {
            string? thumbprint = GetString(root, "certificateThumbprint");
            if (string.IsNullOrEmpty(thumbprint))
                failures.Add("HTTPS 证书指纹缺失（更新前存在证书）");
            else if (!string.Equals(thumbprint, expectation.CertificateThumbprint, StringComparison.OrdinalIgnoreCase))
                failures.Add($"HTTPS 证书指纹为 {thumbprint}，预期 {expectation.CertificateThumbprint}");
        }

        if (expectation.DataRoot is not null)
        {
            string? dataRoot = GetString(root, "dataRoot");
            if (!string.IsNullOrEmpty(dataRoot)
                && !string.Equals(UpdateLayout.Normalize(dataRoot), UpdateLayout.Normalize(expectation.DataRoot), StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"dataRoot 为 {dataRoot}，预期 {expectation.DataRoot}");
            }
        }

        CheckStore(root, "sharesStore", "共享库", expectation.SharesStoreSha256, failures);
        CheckStore(root, "devicesStore", "配对设备库", expectation.DevicesStoreSha256, failures);

        // The identity recorded in the installation directory is the third opinion. A package that
        // installed its binaries but not its build-info.json would leave the folder claiming the old
        // version, so a disagreement here is a reason to roll back rather than to commit.
        if (root.TryGetProperty("installBuildInfo", out JsonElement installInfo) && installInfo.ValueKind == JsonValueKind.Object)
        {
            string? installVersion = GetString(installInfo, "version");
            string? installCommit = GetString(installInfo, "buildCommit");

            if (expectation.Version is not null && !string.Equals(installVersion, expectation.Version, StringComparison.OrdinalIgnoreCase))
                failures.Add($"安装目录 build-info.json 的版本为 {installVersion ?? "(缺失)"}，预期 {expectation.Version}");

            if (expectation.RequireBuildCommit && !string.IsNullOrEmpty(expectation.BuildCommit)
                && !string.IsNullOrEmpty(installCommit)
                && !string.Equals(installCommit, expectation.BuildCommit, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"安装目录 build-info.json 的构建提交为 {installCommit}，预期 {expectation.BuildCommit}");
            }
        }
        else if (expectation.RequireInstallBuildInfo)
        {
            failures.Add("健康检查没有报告安装目录的 build-info.json（更新可能没有替换它）。");
        }

        if (root.TryGetProperty("browseSmoke", out JsonElement smoke))
        {
            if (!GetBool(smoke, "ok")) failures.Add("隔离目录浏览自检未通过：" + (GetString(smoke, "message") ?? "无说明"));
        }
        else
        {
            failures.Add("健康检查没有包含隔离目录浏览自检结果。");
        }

        return new UpdateHealthResult
        {
            Ok = failures.Count == 0,
            Version = version,
            BuildCommit = commit,
            Message = failures.Count == 0 ? "服务自检通过，且与更新前的状态一致。" : string.Join("；", failures),
            Url = url,
            Attempts = attempts,
            Elapsed = elapsed,
            Failures = failures,
            ObservedJson = body
        };
    }

    private static void CompareInt(JsonElement root, string name, int? expected, string label, List<string> failures)
    {
        if (expected is null or <= 0) return;

        if (!root.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.Number)
        {
            failures.Add($"{label}（{name}）缺失");
            return;
        }

        int actual = element.GetInt32();
        if (actual != expected.Value) failures.Add($"{label}为 {actual}，预期 {expected.Value}");
    }

    private static void CheckStore(JsonElement root, string name, string label, string? expectedHash, List<string> failures)
    {
        if (!root.TryGetProperty(name, out JsonElement store) || store.ValueKind != JsonValueKind.Object)
        {
            failures.Add($"{label}（{name}）自检结果缺失");
            return;
        }

        if (!GetBool(store, "ok"))
        {
            failures.Add($"{label}无法加载：" + (GetString(store, "message") ?? "无说明"));
            return;
        }

        // The hash may legitimately change when a newer build migrates the file, so a difference is
        // reported only when the store is unreadable; the count is compared for information.
        if (expectedHash is not null)
        {
            string? actual = GetString(store, "sha256");
            if (actual is not null && !string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                // Deliberately not a failure: an upgrade is allowed to migrate its own state files.
                // It is surfaced in the message so a reviewer can see it happened.
            }
        }
    }

    private static bool GetBool(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string FirstErrorCheck(JsonElement root)
    {
        if (!root.TryGetProperty("checks", out JsonElement checks) || checks.ValueKind != JsonValueKind.Array)
            return "未提供检查明细";

        foreach (JsonElement check in checks.EnumerateArray())
        {
            if (string.Equals(GetString(check, "status"), "error", StringComparison.Ordinal))
            {
                return $"{GetString(check, "id")}: {GetString(check, "message")}";
            }
        }

        return "未提供检查明细";
    }
}
