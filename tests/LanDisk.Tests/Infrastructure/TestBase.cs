using System.Runtime.InteropServices;
using System.Text.Json;
using LanDisk.Core.Model;
using LanDisk.Server;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>
/// One isolated LanDisk instance per test: its own data root, its own sandbox directory and its
/// own loopback port. Nothing here touches a real user directory or a real removable disk.
/// </summary>
public abstract class TestBase : IAsyncLifetime
{
    protected TestHost Host { get; private set; } = null!;
    protected LanDiskClient Admin { get; private set; } = null!;
    protected string ShareId { get; private set; } = string.Empty;

    /// <summary>In-memory evidence lines that the report generator reads.</summary>
    public static readonly List<string> Evidence = [];
    private static readonly object EvidenceGate = new();

    public virtual async Task InitializeAsync()
    {
        Host = await TestHost.CreateAsync(GetType().Name, ConfigureHost);
        Admin = new LanDiskClient(Host.NewClient());
        await Admin.AdminLoginAsync(Host.AdminToken);
        SeedSandbox(Host);
        JsonElement share = await Admin.CreateShareAsync(Host.SandboxRoot);
        ShareId = share.GetProperty("id").GetString()!;
    }

    public virtual async Task DisposeAsync()
    {
        await Host.DisposeAsync();
    }

    /// <summary>
    /// Hook for the few suites that need a different host shape. A test that has to show what a phone
    /// would see needs the server to bind a LAN address (and TLS), which stays off by default so every
    /// other suite keeps running on loopback only.
    /// </summary>
    protected virtual void ConfigureHost(HostOptions options) { }

    /// <summary>Hook for subclasses to populate the sandbox before the share is created.</summary>
    protected virtual void SeedSandbox(TestHost host) { }

    protected async Task<LanDiskClient> PairDeviceAsync(DeviceAccessLevel level = DeviceAccessLevel.ReadOnly, string name = "测试设备")
    {
        string token = await Admin.CreatePairingTokenAsync();
        var client = new LanDiskClient(Host.NewClient());
        JsonElement device = await client.RedeemAsync(token, name);
        string id = device.GetProperty("id").GetString()!;

        if (level == DeviceAccessLevel.ReadWrite)
            await Admin.PatchAsync<JsonElement>($"/api/admin/devices/{id}", new { level = "readWrite" });

        await client.MeAsync();
        return client;
    }

    /// <summary>
    /// Appends one machine readable evidence line for the delivery report. Public because helper
    /// kits that are not themselves test classes also need to record evidence.
    /// </summary>
    public static void Record(string caseId, string detail)
    {
        string line = $"{caseId}\t{DateTimeOffset.Now:HH:mm:ss}\t{detail}";
        lock (EvidenceGate)
        {
            Evidence.Add(line);
            try
            {
                // Machine readable evidence collected while the suite runs, consumed by the
                // delivery report. Kept inside the repository so nothing lands in user folders.
                File.AppendAllText(TestPaths.EvidenceFile, line + Environment.NewLine);
            }
            catch { }
        }
        Console.WriteLine("[EVIDENCE] " + line.Replace('\t', ' '));
    }

    protected static readonly JsonSerializerOptions Json = TestHost.Json;
}

/// <summary>Small Win32 helpers the tests need to build hostile file system shapes.</summary>
public static class TestFileSystem
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    public static bool TryCreateHardLink(string linkPath, string existingPath)
        => CreateHardLinkW(linkPath, existingPath, IntPtr.Zero);

    /// <summary>
    /// Creates a directory junction. Junctions do not require administrator rights, which makes
    /// them the practical way to test the reparse point refusal on any developer machine.
    /// </summary>
    public static bool TryCreateJunction(string junctionPath, string targetPath)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process is null) return false;
        process.WaitForExit(15000);
        return Directory.Exists(junctionPath);
    }
}
