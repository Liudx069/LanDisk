using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LanDisk.Core.Configuration;
using LanDisk.Server;
using LanDisk.Server.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace LanDisk.Tests.Infrastructure;

/// <summary>
/// Boots the real LanDisk application (the same factory the published executable uses) on a
/// loopback port with an isolated data root and an isolated sandbox directory.
///
/// Everything the tests touch lives under <see cref="Root"/>; nothing outside the test sandbox
/// and the process data root is read or written.
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    private readonly List<HttpClient> _clients = [];

    public required WebApplication App { get; init; }
    public required LanDiskRuntime Runtime { get; init; }
    public required string Root { get; init; }
    public required string DataRoot { get; init; }
    public required string SandboxRoot { get; init; }
    public required string AdminToken { get; init; }
    public required string BaseUrl { get; init; }
    public required string HttpsBaseUrl { get; init; }

    /// <summary>Set when another host has taken over this one's directories, so neither deletes
    /// the shared root out from under the other.</summary>
    public bool SuppressCleanup { get; set; }

    /// <summary>Set when this host adopted the directories of a previous instance.</summary>
    private bool _ownsRoot = true;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<TestHost> CreateAsync(string? name = null, Action<HostOptions>? configure = null)
    {
        string label = (name ?? "case") + "-" + Guid.NewGuid().ToString("N")[..10];
        string root = Path.Combine(TestPaths.TempRoot, label);
        string dataRoot = Path.Combine(root, "data");
        string sandbox = Path.Combine(root, "sandbox");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(sandbox);

        var options = new HostOptions
        {
            DataRoot = dataRoot,
            HttpPort = 0,
            HttpsPort = 0,
            Quiet = true,
            ServeStaticFiles = false
        };
        configure?.Invoke(options);

        WebApplication app = LanDiskHost.Build([], options);
        await app.StartAsync();

        var runtime = app.Services.GetRequiredService<LanDiskRuntime>();
        string? http = runtime.BoundUrls.FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && u.Contains("127.0.0.1"));
        string? https = runtime.BoundUrls.FirstOrDefault(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && u.Contains("127.0.0.1"));

        if (http is null) throw new InvalidOperationException("测试主机没有绑定回环 HTTP 端口。");

        return new TestHost
        {
            App = app,
            Runtime = runtime,
            Root = root,
            DataRoot = dataRoot,
            SandboxRoot = sandbox,
            AdminToken = runtime.AdminBootstrapToken,
            BaseUrl = http.TrimEnd('/'),
            HttpsBaseUrl = (https ?? string.Empty).TrimEnd('/')
        };
    }

    /// <summary>
    /// Starts a brand new process-equivalent instance over the same data directory and sandbox.
    /// This is how "the paired state survives a server restart" is actually verified: the old host
    /// is stopped, a new one is built from the same files on a fresh port, and the previously
    /// issued cookie is replayed against it.
    /// </summary>
    public static async Task<TestHost> ReopenAsync(TestHost previous, Action<HostOptions>? configure = null)
    {
        var options = new HostOptions
        {
            DataRoot = previous.DataRoot,
            HttpPort = 0,
            HttpsPort = 0,
            Quiet = true,
            ServeStaticFiles = false
        };
        configure?.Invoke(options);

        previous.SuppressCleanup = true;

        WebApplication app = LanDiskHost.Build([], options);
        await app.StartAsync();

        var runtime = app.Services.GetRequiredService<LanDiskRuntime>();
        string http = runtime.BoundUrls.First(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && u.Contains("127.0.0.1"));
        string? https = runtime.BoundUrls.FirstOrDefault(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && u.Contains("127.0.0.1"));

        return new TestHost
        {
            App = app,
            Runtime = runtime,
            Root = previous.Root,
            DataRoot = previous.DataRoot,
            SandboxRoot = previous.SandboxRoot,
            AdminToken = runtime.AdminBootstrapToken,
            BaseUrl = http.TrimEnd('/'),
            HttpsBaseUrl = (https ?? string.Empty).TrimEnd('/'),
            _ownsRoot = true
        };
    }

    /// <summary>A cookie jar that can be shared between clients, which is how a browser session is
    /// carried across a simulated restart.</summary>
    public static CookieContainer NewCookieJar() => new();

    /// <summary>An HttpClient with its own cookie jar; each one models a distinct browser/device.
    /// Pass <paramref name="sharedCookies"/> to reuse an existing browser session.</summary>
    public HttpClient NewClient(bool acceptAnyCertificate = false, CookieContainer? sharedCookies = null)
    {
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = sharedCookies ?? new CookieContainer(),
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = acceptAnyCertificate
                ? (_, _, _, _) => true
                : null
        };

        var client = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.Add("X-LanDisk-Client", "test");
        _clients.Add(client);
        return client;
    }

    /// <summary>A client that does not manage cookies, so Set-Cookie headers can be inspected.</summary>
    public HttpClient NewCookieObservationClient()
    {
        var handler = new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false
        };

        var client = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.Add("X-LanDisk-Client", "test");
        _clients.Add(client);
        return client;
    }

    public HttpClient NewHttpsClient()
    {
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = false,
            // The test connects to the self signed certificate; the certificate content itself
            // is asserted separately. This is not a claim that a real client should skip trust.
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };

        var client = new HttpClient(handler) { BaseAddress = new Uri(HttpsBaseUrl), Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.Add("X-LanDisk-Client", "test");
        _clients.Add(client);
        return client;
    }

    // ---- assertions helpers -------------------------------------------------

    public string SandboxPath(string relative) => Path.Combine(SandboxRoot, relative.Replace('/', '\\'));

    public void WriteSandboxFile(string relative, string content, Encoding? encoding = null)
    {
        string path = SandboxPath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, encoding ?? new UTF8Encoding(false));
    }

    public void WriteSandboxBytes(string relative, byte[] content)
    {
        string path = SandboxPath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    public string ReadSandboxFile(string relative) => File.ReadAllText(SandboxPath(relative));

    public byte[] ReadSandboxBytes(string relative) => File.ReadAllBytes(SandboxPath(relative));

    public async ValueTask DisposeAsync()
    {
        foreach (HttpClient client in _clients) client.Dispose();
        try { await App.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token); } catch { }
        try { await App.DisposeAsync(); } catch { }
        if (_ownsRoot && !SuppressCleanup)
        {
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}

public static class TestPaths
{
    // Declaration order matters: static initialisers run top to bottom, so the repository root
    // must be resolved before anything that combines paths from it.
    public static readonly string RepoRoot = ResolveRepoRoot();

    /// <summary>All test artefacts live below the repository working tree, never in user folders.</summary>
    public static readonly string TempRoot = EnsureDirectory(Path.Combine(RepoRoot, ".testrun"));

    /// <summary>
    /// Machine readable evidence for this test run. Truncated once per test process so that
    /// consecutive runs never mix their results.
    /// </summary>
    public static readonly string EvidenceFile = PrepareEvidenceFile();

    private static string PrepareEvidenceFile()
    {
        string path = Path.Combine(RepoRoot, "dist", "test-evidence.tsv");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                $"# LanDisk 自动化测试证据{Environment.NewLine}" +
                $"# 运行开始: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}{Environment.NewLine}" +
                "# 列: 验收编号 <TAB> 时间 <TAB> 实际结果" + Environment.NewLine);
        }
        catch { }
        return path;
    }

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ResolveRepoRoot()
    {
        string baseDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(baseDir);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LanDisk.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? baseDir;
    }
}
