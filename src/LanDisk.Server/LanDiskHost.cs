using System.Net;
using System.Net.Sockets;
using LanDisk.Core.Configuration;
using LanDisk.Core.Networking;
using LanDisk.Core.Security;
using LanDisk.Core.Storage;
using LanDisk.Server.Endpoints;
using LanDisk.Server.Services;
using LanDisk.Update;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace LanDisk.Server;

public sealed class HostOptions
{
    public string? DataRoot { get; set; }
    public int? HttpPort { get; set; }
    public int? HttpsPort { get; set; }
    public bool? LanEnabled { get; set; }
    public bool? HttpsEnabled { get; set; }
    public bool? HttpCompatModeEnabled { get; set; }
    public string? LanBindAddress { get; set; }
    /// <summary>
    /// Where the physical-network reading comes from. Null means "enumerate the real machine", which is what
    /// every normal launch does. The UI capture supplies a deterministic fixture so the network page can be
    /// verified against a known adapter set without depending on (or touching) the machine's real network.
    /// </summary>
    public Func<int, Core.Networking.PhysicalLanSnapshot>? PhysicalLanSource { get; set; }

    /// <summary>
    /// Test-only bounded download throttling (delay ms per chunk, chunk bytes). Honoured only when
    /// <c>LANDISK_TEST_MODE=1</c> is also set, so a shipped installation can never enable it.
    /// </summary>
    public (int DelayMs, int ChunkBytes) DownloadThrottle { get; set; }

    /// <summary>
    /// Reads the test-only throttle from the environment, refusing it unless the explicit test-mode flag is
    /// present. Returns (0,0) — no throttling — for every production launch.
    /// </summary>
    public static (int DelayMs, int ChunkBytes) ReadTestDownloadThrottle()
    {
        bool testMode = string.Equals(Environment.GetEnvironmentVariable("LANDISK_TEST_MODE"), "1", StringComparison.Ordinal);
        string? rawDelay = Environment.GetEnvironmentVariable("LANDISK_TEST_DOWNLOAD_THROTTLE_MS");
        string? rawChunk = Environment.GetEnvironmentVariable("LANDISK_TEST_DOWNLOAD_CHUNK_BYTES");
        if (string.IsNullOrWhiteSpace(rawDelay)) return default;
        if (!testMode)
        {
            Console.WriteLine("  警告：设置了下载节流环境变量但未设置 LANDISK_TEST_MODE=1 —— 按未启用处理（节流仅限测试）。");
            return default;
        }
        if (!int.TryParse(rawDelay, out int delay) || delay <= 0) return default;
        int chunk = 0;
        if (!string.IsNullOrWhiteSpace(rawChunk) && int.TryParse(rawChunk, out int parsedChunk) && parsedChunk > 0) chunk = parsedChunk;
        return (delay, chunk);
    }

    public bool Quiet { get; set; }
    public bool ServeStaticFiles { get; set; } = true;

    /// <summary>
    /// Directory the product was installed into. Defaults to the running assembly's directory; the
    /// Windows shell and the tests set it explicitly so an update never touches the wrong tree.
    /// </summary>
    public string? InstallRoot { get; set; }

    /// <summary>
    /// When set, the host also writes its log to this directory. The Windows shell runs without a
    /// console, so the file log is the only place its diagnostics can land.
    /// </summary>
    public string? LogDirectory { get; set; }

    /// <summary>
    /// Publish the stable <c>.local</c> name over mDNS. Off by default so an automated test host does
    /// not contend for UDP 5353; the shipped entry points turn it on explicitly.
    /// </summary>
    public bool EnableMdns { get; set; }

    /// <summary>Path to <c>LanDisk.ExtensionHost.exe</c>. Null resolves to the running directory.</summary>
    public string? ExtensionHostPath { get; set; }

    /// <summary>
    /// Also bind the first-connection bootstrap to loopback. Only tests need this: a real user reaches
    /// the wizard from the phone, never from the machine itself.
    /// </summary>
    public bool AllowBootstrapLoopback { get; set; }
}

/// <summary>
/// Builds the LanDisk web application. The published executable and the automated test suite use
/// this same factory so the tests exercise the shipping object graph, pipeline order and bindings.
/// </summary>
public static class LanDiskHost
{
    public static WebApplication Build(string[] args, HostOptions? options = null)
    {
        options ??= new HostOptions();

        string dataRoot = options.DataRoot
                          ?? GetArg(args, "--data-dir")
                          ?? AppPaths.DefaultDataRoot();
        AppPaths paths = AppPaths.ForDataRoot(dataRoot);

        AppSettings settings = JsonStore.Read(paths.SettingsFile, () => new AppSettings());

        if (options.HttpPort is not null) settings.HttpPort = options.HttpPort.Value;
        if (options.HttpsPort is not null) settings.HttpsPort = options.HttpsPort.Value;
        if (options.LanEnabled is not null) settings.LanEnabled = options.LanEnabled.Value;
        if (options.HttpsEnabled is not null) settings.HttpsEnabled = options.HttpsEnabled.Value;
        if (options.HttpCompatModeEnabled is not null) settings.HttpCompatModeEnabled = options.HttpCompatModeEnabled.Value;
        if (options.LanBindAddress is not null) settings.LanBindAddress = options.LanBindAddress;

        string? portArg = GetArg(args, "--port");
        if (portArg is not null && int.TryParse(portArg, out int parsedPort)) settings.HttpPort = parsedPort;

        string? httpsPortArg = GetArg(args, "--https-port");
        if (httpsPortArg is not null && int.TryParse(httpsPortArg, out int parsedHttpsPort)) settings.HttpsPort = parsedHttpsPort;

            string adminToken = LoadOrCreateAdminToken(paths);
        var runtime = new LanDiskRuntime(paths, settings, adminToken, options.InstallRoot,
            options.AllowBootstrapLoopback, options.Quiet ? null : message => Console.WriteLine("  " + message),
            extensionHostPath: options.ExtensionHostPath,
            physicalLanSource: options.PhysicalLanSource,
            downloadThrottle: options.DownloadThrottle.DelayMs > 0 ? options.DownloadThrottle : HostOptions.ReadTestDownloadThrottle());

        // Reconcile anything the previous run left half finished before serving traffic.
        ReconcileReport reconcile = runtime.Recycle.Reconcile(runtime.Shares.All());
        runtime.Uploads.Reconcile();
        runtime.RefreshShareHealth();

        // Adopt the version now on disk and surface the outcome of the last update attempt (if any)
        // so the admin page can say "completed" or "failed and rolled back" rather than guessing.
        runtime.Updates.Reconcile();

        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });
        if (!string.IsNullOrWhiteSpace(options.LogDirectory))
        {
            builder.Logging.AddProvider(new FileLoggerProvider(options.LogDirectory));
        }
        builder.Logging.SetMinimumLevel(options.Quiet
            ? LogLevel.Warning
            : settings.VerboseLogging ? LogLevel.Debug : LogLevel.Information);

        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
            o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
        });

        builder.Services.AddSingleton(runtime);
        builder.Services.AddHostedService<MaintenanceService>();

        int httpPort = settings.HttpPort;
        int httpsPort = settings.HttpsPort;

        // Port 0 means "pick a free port"; resolve it once so that the IPv4 and IPv6 loopback
        // listeners share the same number.
        if (httpPort == 0) httpPort = FindFreePort();
        if (httpsPort == 0) httpsPort = FindFreePort();

        // Remember what was actually bound. Settings keeps the configured value (which may legitimately be 0),
        // so anything that has to restore this instance — the updater's restart and the health check that
        // judges it — must be told the real endpoints rather than an unresolved 0.
        runtime.BoundHttpPort = httpPort;
        runtime.BoundHttpsPort = httpsPort;

        var listenTargets = new List<(IPAddress Address, int Port, bool Https)>
        {
            // Plain HTTP on loopback first: the local management page is a secure context there
            // (loopback is treated as trustworthy) and does not depend on a trusted certificate.
            (IPAddress.Loopback, httpPort, false),
            (IPAddress.IPv6Loopback, httpPort, false),
            (IPAddress.Loopback, httpsPort, true)
        };

        bool lanHttp = settings.LanEnabled && !settings.HttpsEnabled && settings.HttpCompatModeEnabled;
        bool lanHttps = settings.LanEnabled && settings.HttpsEnabled;

        if (lanHttps || lanHttp)
        {
            // Physical interfaces only: the server must not listen on a Hyper-V switch or a VPN tunnel
            // address, because binding there advertises a reachability that does not exist and (for the
            // tunnel) can make the product depend on the VPN's routing.
            List<string> physical = [.. runtime.LanAddresses()];
            if (!string.IsNullOrWhiteSpace(settings.LanBindAddress))
            {
                List<string> pinned = [.. physical.Where(a => a == settings.LanBindAddress)];
                if (pinned.Count > 0)
                {
                    physical = pinned;
                }
                else
                {
                    // The pinned address is gone: DHCP moved the machine, the adapter was disabled, or the
                    // address was typed while on another network. Binding nothing would leave only loopback
                    // and the failure used to be completely silent (no log line, no UI hint) — the owner saw
                    // exactly that on 2026-11-10 and spent hours on it. Keep LAN reachable on the current
                    // physical address and record a warning the UI surfaces.
                    runtime.NoteBindAddressFallback(settings.LanBindAddress);
                }
            }

            foreach (string address in physical)
            {
                if (!IPAddress.TryParse(address, out IPAddress? ip)) continue;
                if (lanHttps) listenTargets.Add((ip, httpsPort, true));
                if (lanHttp) listenTargets.Add((ip, httpPort, false));
            }
        }

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = Math.Max(settings.MaxChunkBytes, 8 * 1024 * 1024);
            kestrel.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(3);

            foreach ((IPAddress address, int port, bool https) in listenTargets.Distinct())
            {
                kestrel.Listen(address, port, listen =>
                {
                    listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1;
                    if (https)
                    {
                        // The leaf is resolved through a per-connection selector, not captured once at
                        // startup: when the machine moves to a different network LanDisk re-issues the
                        // leaf, and the running listener must pick it up without a restart (a restart
                        // would be exactly the "you must redo something" experience this design exists
                        // to remove). The root CA behind it never changes.
                        ServerCertificate initial = runtime.EnsureCertificates();
                        listen.UseHttps(httpsOptions =>
                        {
                            httpsOptions.ServerCertificate = initial.Leaf;
                            httpsOptions.ServerCertificateSelector = (_, _) =>
                                runtime.CurrentServerCertificate ?? initial.Leaf;
                        });
                    }
                });
            }
        });

        WebApplication app = builder.Build();

        var allowedHosts = new AllowedHosts([]);
        foreach ((IPAddress address, _, _) in listenTargets)
        {
            allowedHosts.Add(address.ToString());
        }
        if (!string.IsNullOrWhiteSpace(settings.LanBindAddress)) allowedHosts.Add(settings.LanBindAddress);

        // The stable name is a first-class way to reach this machine: it is the address a phone keeps,
        // and rejecting it at the Host check would make the whole stable-name design unusable.
        allowedHosts.Add(runtime.Identity.HostName);

        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseMiddleware<HostValidationMiddleware>(allowedHosts);
        app.UseMiddleware<NetworkPolicyMiddleware>();
        app.UseMiddleware<SessionMiddleware>();
        app.UseMiddleware<CsrfMiddleware>();

        // Deliberately small, and deliberately includes the build commit: "which build is this?"
        // is the first question asked about a machine that is misbehaving, and the answer is not a
        // secret. The detailed self check lives at /api/admin/health behind the loopback guard.
        app.MapGet("/api/health", () => Results.Json(new
        {
            ok = true,
            product = UpdateManifest.ProductName,
            version = BuildInfo.Version,
            buildCommit = BuildInfo.Commit,
            buildId = BuildInfo.BuildId,
            startedAt = runtime.StartedAt
        }, ApiHelpers.Json));

        app.MapPairEndpoints();
        app.MapFileEndpoints();
        app.MapLibraryEndpoints();
        app.MapUploadEndpoints();
        app.MapRecycleEndpoints();
        app.MapAdminEndpoints();
        app.MapUpdateEndpoints();
        app.MapOnboardingEndpoints();
        app.MapHostStatusEndpoints();
        app.MapExtensionEndpoints();

        if (options.ServeStaticFiles)
        {
            string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            if (Directory.Exists(wwwroot))
            {
                var fileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(wwwroot);
                app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
                app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = fileProvider,
                    ServeUnknownFileTypes = false,
                    OnPrepareResponse = ctx =>
                    {
                        ctx.Context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                    }
                });
            }
        }

        app.MapFallback(async context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = new { code = "NOT_FOUND", message = "接口不存在。" }
                }, ApiHelpers.Json);
                return;
            }

            string indexPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
            if (File.Exists(indexPath))
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.Headers["Cache-Control"] = "no-cache";
                await context.Response.SendFileAsync(indexPath);
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync("LanDisk 前端资源缺失。请使用发布包，或先运行 tools/Build-Web.ps1 生成 wwwroot。");
            }
        });

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var feature = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            runtime.BoundUrls.Clear();
            if (feature is not null)
            {
                foreach (string url in feature.Addresses) runtime.BoundUrls.Add(url.Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1"));
            }

            // Publishing the stable name is best effort by design: on a network that forbids multicast
            // the product still works over the current address, and the computer's own screen says so.
            if (options.EnableMdns && settings.LanEnabled)
            {
                if (runtime.StartMdns(out string? mdnsError))
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1));
                        runtime.Mdns?.AnnounceAgain();
                    });
                }
                else if (!options.Quiet)
                {
                    Console.WriteLine("  稳定名称不可用（mDNS 未能启动）：" + mdnsError);
                    Console.WriteLine("  将以当前局域网地址提供访问；手机侧改用地址二维码即可。");
                }
            }

            string loopback = runtime.BoundUrls.FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && u.Contains("127.0.0.1"))
                              ?? runtime.BoundUrls.FirstOrDefault(u => u.Contains("127.0.0.1"))
                              ?? $"http://127.0.0.1:{httpPort}";
            string adminUrl = loopback.TrimEnd('/') + "/admin#token=" + adminToken;

            try
            {
                File.WriteAllText(paths.AdminUrlFile, adminUrl + Environment.NewLine);
            }
            catch { }

            if (!options.Quiet)
            {
                Console.WriteLine();
                Console.WriteLine("LanDisk 已启动。");
                Console.WriteLine("  数据目录: " + paths.DataRoot);
                Console.WriteLine("  稳定名称: " + runtime.Identity.HostName);
                Console.WriteLine("  监听地址: " + string.Join(", ", runtime.BoundUrls));
                Console.WriteLine("  本机管理: " + adminUrl);
                Console.WriteLine("  共享数量: " + runtime.Shares.All().Count + "，设备数量: " + runtime.Devices.All().Count(d => !d.Revoked));
                if (reconcile.Committed > 0 || reconcile.Failed > 0 || reconcile.OrphanedCleanups > 0)
                    Console.WriteLine($"  回收站核对: 已确认 {reconcile.Committed}，失败 {reconcile.Failed}，清理孤立暂存 {reconcile.OrphanedCleanups}");
                if (settings.LanEnabled)
                    Console.WriteLine("  局域网: 已开启" + (settings.HttpsEnabled ? "（HTTPS）" : "（HTTP 兼容模式，未加密）"));
                else
                    Console.WriteLine("  局域网: 未开启（仅本机可访问）。在管理页面中显式开启后重启生效。");
                Console.WriteLine("  按 Ctrl+C 退出。");
                Console.WriteLine();
            }
        });

        app.Lifetime.ApplicationStopping.Register(() =>
        {
            runtime.StopMdns();
            runtime.Onboarding.Stop("服务退出");
        });

        return app;
    }

    private static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == name && i + 1 < args.Length) return args[i + 1];
            if (args[i].StartsWith(name + "=", StringComparison.Ordinal)) return args[i][(name.Length + 1)..];
        }
        return null;
    }

    private static string LoadOrCreateAdminToken(AppPaths paths)
    {
        try
        {
            if (File.Exists(paths.AdminTokenFile))
            {
                string existing = File.ReadAllText(paths.AdminTokenFile).Trim();
                if (existing.Length >= 32) return existing;
            }
        }
        catch { }

        string token = SecretTokens.NewToken();
        try
        {
            Directory.CreateDirectory(paths.SecretsDir);
            File.WriteAllText(paths.AdminTokenFile, token);
        }
        catch { }
        return token;
    }

    /// <summary>
    /// Picks a port that is free right now.
    ///
    /// <para><b>Why this retries.</b> Asking the OS for a free port and then binding it later is a race: on a
    /// busy machine — and especially when many test hosts start at once — something else can take the number
    /// in between, and the launch then fails for a reason that has nothing to do with the product. Retrying a
    /// few times with a freshly probed port removes that failure mode. An explicitly configured port is never
    /// retried: if the operator asked for 8892 and 8892 is taken, that must still fail loudly.</para>
    /// </summary>
    private static int FindFreePort()
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();

                // Probe again to make sure the pair we are about to hand to Kestrel is still free; a port that
                // survived this check is very unlikely to be stolen in the microseconds before the bind.
                var confirm = new TcpListener(IPAddress.Loopback, port);
                try
                {
                    confirm.Start();
                    confirm.Stop();
                    return port;
                }
                catch (SocketException)
                {
                    try { confirm.Stop(); } catch { }
                }
            }
            catch (SocketException)
            {
                try { listener.Stop(); } catch { }
            }
        }

        throw new InvalidOperationException("无法找到可用端口：连续 8 次探测都被占用。");
    }
}

/// <summary>Periodic housekeeping: share health, expired uploads, stale pairing requests.</summary>
public sealed class MaintenanceService : BackgroundService
{
    private readonly LanDiskRuntime _runtime;
    private readonly ILogger<MaintenanceService> _logger;

    public MaintenanceService(LanDiskRuntime runtime, ILogger<MaintenanceService> logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var healthTimer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        var cleanupTimer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        var networkTimer = new PeriodicTimer(TimeSpan.FromSeconds(30));

        Task health = Task.Run(async () =>
        {
            while (await healthTimer.WaitForNextTickAsync(stoppingToken))
            {
                try { _runtime.RefreshShareHealth(); }
                catch (Exception ex) { _logger.LogDebug(ex, "共享健康检查失败"); }
            }
        }, stoppingToken);

        Task cleanup = Task.Run(async () =>
        {
            while (await cleanupTimer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    _runtime.Pairing.Prune();
                    _runtime.Uploads.Reconcile();
                    // Presence timestamps are updated in memory on every request; this is where they reach
                    // the disk, so a 15 second heartbeat costs one small write per minute instead of four.
                    _runtime.Devices.FlushActivity();
                }
                catch (Exception ex) { _logger.LogDebug(ex, "清理任务失败"); }
            }
        }, stoppingToken);

        // Keeps the stable name pointing at the address the machine actually has now, and closes the
        // onboarding window when it has served its purpose or timed out.
        Task network = Task.Run(async () =>
        {
            while (await networkTimer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    _runtime.RefreshNetworkIdentity();

                    OnboardingStatus status = _runtime.Onboarding.Status();
                    bool expired = status.ExpiresAt is { } expiry && DateTimeOffset.Now > expiry;
                    if (_runtime.Onboarding.IsActive && status.Trusted) _runtime.Onboarding.Stop("手机已完成信任");
                    else if (_runtime.Onboarding.IsActive && expired) _runtime.Onboarding.Stop("引导窗口超时");
                }
                catch (Exception ex) { _logger.LogDebug(ex, "网络身份检查失败"); }
            }
        }, stoppingToken);

        try
        {
            await Task.WhenAll(health, cleanup, network);
        }
        catch (OperationCanceledException) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Cancel every in-flight upload so no staging file is left behind and no partial file
        // is ever presented as complete.
        foreach (var session in _runtime.Uploads.ListForDevice("*").ToList())
        {
            _runtime.Uploads.Cancel(session, "服务退出");
        }
        await base.StopAsync(cancellationToken);
    }
}
