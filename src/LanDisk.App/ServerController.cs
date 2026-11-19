using LanDisk.Core.Configuration;
using LanDisk.Core.Networking;
using LanDisk.Server;
using LanDisk.Server.Endpoints;
using LanDisk.Server.Services;
using LanDisk.Update;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LanDisk.App;

/// <summary>Lifecycle of the in-process server, as the window and tray report it.</summary>
internal enum ServerState
{
    /// <summary>Never started, or stopped cleanly.</summary>
    Idle,

    Starting,
    Running,
    Stopping,
    Stopped,

    /// <summary>Stopping did not complete cleanly; the process should still exit.</summary>
    StopFailed,

    Failed,

    /// <summary>A required port was already taken; the port number is reported.</summary>
    PortConflict
}

internal sealed record ServerStatus
{
    public ServerState State { get; init; } = ServerState.Stopped;

    public string Version { get; init; } = string.Empty;
    public string BuildCommit { get; init; } = string.Empty;
    public string DataRoot { get; init; } = string.Empty;
    public string? LoopbackUrl { get; init; }

    /// <summary>Everything Kestrel actually bound, in the order the host reported it.</summary>
    public IReadOnlyList<string> BoundUrls { get; init; } = [];

    /// <summary>Phone-facing candidates, best first, with loopback and virtual adapters ranked out.</summary>
    public IReadOnlyList<LanAddressCandidate> LanAddresses { get; init; } = [];

    public string? Error { get; init; }
    public string? ErrorCode { get; init; }

    /// <summary>The port that could not be bound, when the failure is a port conflict.</summary>
    public int? ConflictedPort { get; init; }

    public bool IsStarting => State == ServerState.Starting;
    public bool IsRunning => State == ServerState.Running;

    /// <summary>The URL the management page is opened at. The bootstrap token is never shown or copied.</summary>
    public string? AdminUrl => LoopbackUrl is null ? null : LoopbackUrl.TrimEnd('/') + "/admin";

    /// <summary>Deep link that makes the management page open the pairing tab and show a QR code.</summary>
    public string? PairUrl => LoopbackUrl is null ? null : LoopbackUrl.TrimEnd('/') + "/admin#pair";
}

/// <summary>
/// Owns the web host inside the desktop process.
///
/// <para>Running the server in-process (rather than as a child process) is what makes the tray menu's
/// "restart the service" instant and reliable, and it removes a whole class of orphaned-process
/// bugs: when the window exits, the listener is gone with it.</para>
///
/// <para>Two rules shape this class. First, <see cref="ServerState.Running"/> is only ever reported
/// after Kestrel has really bound its sockets <em>and</em> a loopback readiness probe has answered:
/// a host object that exists is not a service the phone can reach. Second, stopping is asynchronous
/// from end to end — <see cref="StopAsync"/> never blocks the caller, so the UI thread can await it
/// instead of waiting on it.</para>
/// </summary>
internal sealed class ServerController : IDisposable
{
    /// <summary>How long a graceful shutdown may take before the caller is told it failed.</summary>
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(20);

    private readonly string[] _args;
    private readonly object _gate = new();

    private WebApplication? _app;
    private Task? _runTask;
    private bool _suppressExitOnStop;

    public ServerController(string[] args) => _args = args;

    public event EventHandler<ServerStatus>? StatusChanged;

    public ServerStatus Status { get; private set; } = new();

    public AppPaths? Paths { get; private set; }

    /// <summary>
    /// The composition root the in-process server is running, once it exists. The native UI reads and
    /// acts through this same object graph, so it cannot drift from what the HTTP surface does.
    /// </summary>
    public LanDiskRuntime? Runtime { get; private set; }

    public bool IsRunning
    {
        get
        {
            lock (_gate) return _app is not null;
        }
    }

    /// <summary>Starts the host and only reports Running once it is genuinely reachable.</summary>
    public async Task<ServerStatus> StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_app is not null) return Status;
        }

        string dataRoot = ResolveDataRoot(_args);
        int[] ports = ResolvePorts(_args, dataRoot);

        Publish(new ServerStatus { State = ServerState.Starting, Version = Status.Version, DataRoot = Status.DataRoot });

        // Pre-flight the ports so a conflict can be named precisely. Kestrel's own bind error says
        // "address already in use" without telling the operator which of 8891/8892 fought back.
        PortConflict? unavailable = PortPreflight.FindConflict(ports);
        if (unavailable is not null)
        {
            var conflict = new ServerStatus
            {
                State = ServerState.PortConflict,
                Version = Status.Version,
                DataRoot = dataRoot,
                ConflictedPort = unavailable.Port,
                ErrorCode = "PORT_IN_USE",
                Error = unavailable.Message
            };
            Publish(conflict);
            return conflict;
        }

        try
        {
            AppPaths paths = AppPaths.ForDataRoot(dataRoot);
            Paths = paths;

            bool captureMode = _args.Any(a => a.Equals("--render-ui", StringComparison.OrdinalIgnoreCase));

            WebApplication app = LanDiskHost.Build(_args, new LanDisk.Server.HostOptions
            {
                DataRoot = dataRoot,
                Quiet = true,
                LogDirectory = paths.LogsDir,
                InstallRoot = UpdateLayoutRoot(),
                // 捕获模式下网络页面对一个**确定的**网卡夹具渲染（Ethernet 192.168.2.2 可用、
                // Hyper-V 172.24.0.1 被排除），这样 R2 的结果不依赖 Owner 现场接线，也不去读真实网卡。
                PhysicalLanSource = captureMode ? FixturePhysicalLan.Capture : null,
                // The shipped product publishes its stable .local name; the automated test host does
                // not, so tests never contend for UDP 5353.
                EnableMdns = true
            });

            var runtime = app.Services.GetRequiredService<LanDiskRuntime>();
            Runtime = runtime;

            // If the web host stops for a reason the shell did not initiate (the management page's
            // "退出 LanDisk", or a fatal host error), the window must go away too: a tray icon whose
            // server is dead is worse than no tray icon.
            app.Lifetime.ApplicationStopping.Register(() =>
            {
                bool suppressed;
                lock (_gate) suppressed = _suppressExitOnStop;
                if (!suppressed) ExitRequested?.Invoke(this, EventArgs.Empty);
            });

            await app.StartAsync(cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                _app = app;
                _runTask = app.WaitForShutdownAsync();
            }

            IReadOnlyList<string> bound = [.. runtime.BoundUrls];
            string? loopback = bound.FirstOrDefault(u => u.Contains("127.0.0.1", StringComparison.Ordinal))
                               ?? $"http://127.0.0.1:{ports[0]}";

            // Readiness probe: the sockets are bound, but "the phone can use it" also means the
            // request pipeline answers. Anything less must not be reported as Running.
            ReadinessResult ready = await ProbeReadinessAsync(loopback, cancellationToken).ConfigureAwait(false);
            if (!ready.Ok)
            {
                await StopCoreAsync().ConfigureAwait(false);
                var failed = new ServerStatus
                {
                    State = ServerState.Failed,
                    Version = Status.Version,
                    DataRoot = dataRoot,
                    ErrorCode = "READINESS_FAILED",
                    Error = "服务已绑定端口，但自检请求没有通过，因此没有进入运行状态。\n\n" + ready.Message
                };
                Publish(failed);
                return failed;
            }

            var status = new ServerStatus
            {
                State = ServerState.Running,
                Version = BuildInfo.Version,
                BuildCommit = BuildInfo.Commit,
                DataRoot = paths.DataRoot,
                LoopbackUrl = loopback,
                BoundUrls = bound,
                LanAddresses = LanInfo.RankForDevices()
            };
            Publish(status);
            return status;
        }
        catch (Exception ex)
        {
            await StopCoreAsync().ConfigureAwait(false);

            (string code, string message, int? port) = DescribeStartFailure(ex, ports);
            var status = new ServerStatus
            {
                State = port is null ? ServerState.Failed : ServerState.PortConflict,
                Version = Status.Version,
                DataRoot = dataRoot,
                ErrorCode = code,
                Error = message,
                ConflictedPort = port
            };
            Publish(status);
            return status;
        }
    }

    /// <summary>
    /// Stops the host. Never blocks the calling thread, never throws, and always leaves a terminal
    /// state behind so the caller can dispose and exit.
    /// </summary>
    public async Task<ServerStatus> StopAsync()
    {
        ServerStatus result = await StopCoreAsync().ConfigureAwait(false);
        Publish(result);
        return result;
    }

    private async Task<ServerStatus> StopCoreAsync()
    {
        WebApplication? app;
        Task? runTask;
        lock (_gate)
        {
            app = _app;
            runTask = _runTask;
            _app = null;
            _runTask = null;
            _suppressExitOnStop = true;
        }

        if (app is null)
        {
            return new ServerStatus { State = ServerState.Stopped, Version = Status.Version, DataRoot = Status.DataRoot };
        }

        Publish(new ServerStatus { State = ServerState.Stopping, Version = Status.Version, DataRoot = Status.DataRoot });

        var failures = new List<string>();
        try
        {
            using var timeout = new CancellationTokenSource(StopTimeout);
            await app.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            failures.Add($"优雅停止在 {StopTimeout.TotalSeconds:F0} 秒内没有完成");
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            failures.Add(ex.GetType().Name + ": " + ex.Message);
        }

        try
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            failures.Add("释放宿主失败：" + ex.Message);
        }

        if (runTask is not null)
        {
            try
            {
                await runTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // The listener is released by disposing the host; a lingering shutdown task is not
                // worth blocking the exit for, but it is worth reporting.
                failures.Add("关闭任务没有在 10 秒内结束");
            }
        }

        lock (_gate) _suppressExitOnStop = false;

        if (failures.Count == 0)
        {
            return new ServerStatus { State = ServerState.Stopped, Version = Status.Version, DataRoot = Status.DataRoot };
        }

        return new ServerStatus
        {
            State = ServerState.StopFailed,
            Version = Status.Version,
            DataRoot = Status.DataRoot,
            ErrorCode = "STOP_INCOMPLETE",
            Error = "服务停止过程中出现问题：" + string.Join("；", failures)
                    + "\n\n进程仍然可以退出；如果端口仍被占用，请重新启动 LanDisk。"
        };
    }

    public async Task<ServerStatus> RestartAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await WaitForPortsReleasedAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        return await StartAsync().ConfigureAwait(false);
    }

    /// <summary>Polite shutdown requested by the management page: stop and then exit the shell.</summary>
    public void RequestExit() => ExitRequested?.Invoke(this, EventArgs.Empty);

    public event EventHandler? ExitRequested;

    // ------------------------------------------------------------------ readiness

    private sealed record ReadinessResult(bool Ok, string Message);

    /// <summary>
    /// Asks the just-started instance about itself over loopback. A bound socket with a broken
    /// pipeline is exactly the case where the tray would otherwise show a healthy icon while the
    /// phone gets errors.
    /// </summary>
    private static async Task<ReadinessResult> ProbeReadinessAsync(string loopbackUrl, CancellationToken cancellationToken)
    {
        string url = loopbackUrl.TrimEnd('/') + "/api/health";
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        string last = "尚未收到响应。";

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using HttpResponseMessage response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    if (body.Contains("\"ok\":true", StringComparison.Ordinal))
                        return new ReadinessResult(true, "就绪。");
                    last = "健康检查返回了非 ok 的响应。";
                }
                else
                {
                    last = $"健康检查返回 HTTP {(int)response.StatusCode}。";
                }
            }
            catch (HttpRequestException ex)
            {
                last = "服务尚未就绪：" + ex.Message;
            }
            catch (TaskCanceledException)
            {
                last = "健康检查请求超时。";
            }

            try { await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        return new ReadinessResult(false, last);
    }

    // ------------------------------------------------------------------ failure description

    private static (string Code, string Message, int? Port) DescribeStartFailure(Exception ex, int[] ports)
    {
        bool addressInUse = ex is IOException io && (io.Message.Contains("address", StringComparison.OrdinalIgnoreCase)
                                                     || io.InnerException is System.Net.Sockets.SocketException)
                            || ex is System.Net.Sockets.SocketException;

        if (addressInUse)
        {
            int? busy = ports.FirstOrDefault(p => !IsPortFree(p));
            int port = busy ?? ports[0];
            return ("PORT_IN_USE",
                $"端口 {port} 已被其它程序占用，LanDisk 无法启动。\n\n"
                + "LanDisk 不会自动更换端口，因为手机或浏览器里保存的地址会因此失效。\n"
                + $"请关闭占用 {port} 的程序，或在管理页面的“网络”里显式改用其它端口后重启。",
                port);
        }

        if (ex is System.Security.Cryptography.CryptographicException)
        {
            return ("CERTIFICATE_ERROR",
                "HTTPS 证书无法加载或生成，LanDisk 无法启动。\n\n" + ex.Message,
                null);
        }

        if (ex is UnauthorizedAccessException)
        {
            return ("PERMISSION_DENIED",
                "LanDisk 没有权限绑定端口或写入数据目录。\n\n" + ex.Message,
                null);
        }

        return ("START_FAILED", ex.GetType().Name + "：" + ex.Message, null);
    }

    // ------------------------------------------------------------------ helpers

    private async Task WaitForPortsReleasedAsync(TimeSpan timeout)
    {
        int[] ports = ResolvePorts(_args, Paths?.DataRoot);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        while (stopwatch.Elapsed < timeout)
        {
            if (ports.All(IsPortFree)) return;
            await Task.Delay(200).ConfigureAwait(false);
        }
    }

    private static bool IsPortFree(int port) => PortPreflight.IsPortFree(port);

    /// <summary>
    /// The ports the host will actually try to bind: whatever settings.json says, overridden by an
    /// explicit command line flag. LanDisk never changes these on its own to dodge a conflict.
    /// </summary>
    private static int[] ResolvePorts(string[] args, string? dataRoot)
    {
        int http = PortPreflight.DefaultHttpPort;
        int https = PortPreflight.DefaultHttpsPort;

        try
        {
            if (dataRoot is not null)
            {
                AppSettings settings = JsonStore.Read(Path.Combine(dataRoot, "settings.json"), () => new AppSettings());
                if (settings.HttpPort > 0) http = settings.HttpPort;
                if (settings.HttpsPort > 0) https = settings.HttpsPort;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fall back to the defaults; a missing settings file is normal on a first run.
        }

        return PortPreflight.ResolveConfiguredPorts(http, https, args);
    }

    private void Publish(ServerStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    private static string ResolveDataRoot(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--data-dir" && i + 1 < args.Length) return Path.GetFullPath(args[i + 1]);
            if (args[i].StartsWith("--data-dir=", StringComparison.Ordinal)) return Path.GetFullPath(args[i]["--data-dir=".Length..]);
        }
        return AppPaths.DefaultDataRoot();
    }

    private static string UpdateLayoutRoot() => AppContext.BaseDirectory;

    /// <summary>
    /// Best-effort disposal for paths that cannot await. It is deliberately not the normal exit
    /// route: <see cref="StopAsync"/> is what the UI and tray call, so the UI thread never waits.
    /// </summary>
    public void Dispose()
    {
        try
        {
            StopCoreAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or TimeoutException)
        {
        }
    }
}
