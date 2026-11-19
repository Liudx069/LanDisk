using System.Diagnostics;
using System.Net.NetworkInformation;
using LanDisk.Core.Model;
using LanDisk.Core.Networking;

namespace LanDisk.Server.Services;

/// <summary>
/// Live numbers for the operations dashboard: what this process is doing, what the machine's link is doing,
/// how much has moved today, and whether anything is wrong.
///
/// <para><b>Why it is not just "/api/health" with more fields.</b> Health answers "is this installation
/// usable"; the dashboard answers "what is happening right now". They have different audiences (the updater
/// versus a human) and different shapes (a verdict versus a time series), so they are separate surfaces that
/// share the same runtime.</para>
///
/// <para><b>Cost.</b> Sampling is done on demand and remembered: the dashboard polls every couple of seconds
/// and gets a freshly computed delta, while the CPU and network counters are only read when someone is
/// actually looking. A closed dashboard costs nothing.</para>
/// </summary>
public sealed class RuntimeMetrics
{
    private readonly LanDiskRuntime _runtime;
    private readonly object _gate = new();

    private DateTimeOffset _lastSampleAt = DateTimeOffset.Now;
    private TimeSpan _lastProcessCpu = Process.GetCurrentProcess().TotalProcessorTime;
    private (ulong SystemIdle, ulong SystemKernel, ulong SystemUser) _lastSystem;
    private long _lastBytesSent;
    private long _lastBytesReceived;

    private long _requests;
    private long _failures;
    private DateTimeOffset _countersSince = DateTimeOffset.Now;
    private readonly List<(DateTimeOffset At, double Up, double Down)> _recent = [];

    public RuntimeMetrics(LanDiskRuntime runtime)
    {
        _runtime = runtime;
        ReadSystemTimes(out _lastSystem.SystemIdle, out _lastSystem.SystemKernel, out _lastSystem.SystemUser);
        ReadLinkBytes(out _lastBytesSent, out _lastBytesReceived);
    }

    /// <summary>Counts one served request; called for every response, so it must stay trivially cheap.</summary>
    public void NoteRequest(int statusCode)
    {
        lock (_gate)
        {
            _requests++;
            if (statusCode >= 400) _failures++;
        }
    }

    /// <summary>
    /// Appends a synthetic network-rate sample to the five-minute chart.
    ///
    /// <para><b>Capture-only seam.</b> A capture of an idle scratch install measures a flat, near-zero link, so
    /// the chart would prove nothing about its own layout. The UI capture feeds this a plausible series so the
    /// dashboard screenshot shows a real, non-trivial polyline. It is not part of normal operation and never
    /// runs in the shipped product.</para>
    /// </summary>
    public void InjectSample(double uploadBytesPerSecond, double downloadBytesPerSecond)
    {
        lock (_gate)
        {
            _recent.Add((DateTimeOffset.Now, uploadBytesPerSecond, downloadBytesPerSecond));
            DateTimeOffset cutoff = DateTimeOffset.Now.AddMinutes(-5);
            _recent.RemoveAll(sample => sample.At < cutoff);
        }
    }

    /// <summary>
    /// The dashboard payload. Every number is either a measurement taken here or a value the runtime already
    /// owns; nothing is estimated and nothing is invented for the chart.
    /// </summary>
    public OperationsSnapshot Snapshot()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        double processCpu, systemCpu, up, down;
        long requests, failures;

        lock (_gate)
        {
            double seconds = Math.Max(0.2, (now - _lastSampleAt).TotalSeconds);
            Process process = Process.GetCurrentProcess();
            TimeSpan cpu = process.TotalProcessorTime;
            processCpu = Math.Clamp((cpu - _lastProcessCpu).TotalMilliseconds / (seconds * 1000 * Environment.ProcessorCount) * 100, 0, 100);
            _lastProcessCpu = cpu;
            _lastSampleAt = now;

            ReadSystemTimes(out ulong idle, out ulong kernel, out ulong user);
            // KernelTime already includes IdleTime on Windows, so the busy fraction is (total - idle) over
            // total — never "kernel + user over kernel + user + idle", which double-counts idle. The delta
            // math is a pure function so it is testable (OPS17-01..04) and cannot produce NaN, negative or
            // >100 values.
            systemCpu = SystemCpuPercent(_lastSystem.SystemIdle, _lastSystem.SystemKernel, _lastSystem.SystemUser, idle, kernel, user);
            _lastSystem = (idle, kernel, user);

            ReadLinkBytes(out long sent, out long received);
            up = Math.Max(0, (sent - _lastBytesSent) / seconds);
            down = Math.Max(0, (received - _lastBytesReceived) / seconds);
            _lastBytesSent = sent;
            _lastBytesReceived = received;

            requests = _requests;
            failures = _failures;

            _recent.Add((now, up, down));
            DateTimeOffset cutoff = now.AddMinutes(-5);
            _recent.RemoveAll(sample => sample.At < cutoff);
        }

        IReadOnlyList<DeviceDefinition> devices = _runtime.Devices.All();
        int online = devices.Count(d => DeviceActivity.Classify(d, now) == DevicePresence.Online);
        int recentlyActive = devices.Count(d => DeviceActivity.Classify(d, now) == DevicePresence.RecentlyActive);

        IReadOnlyList<TransferRecord> transfers = _runtime.Transfers.Recent(500);
        List<TransferRecord> active = [.. transfers.Where(t => t.State == TransferState.InProgress)];
        List<TransferRecord> today = [.. transfers.Where(t => t.StartedAt.Date == now.Date)];

        List<ShareDefinition> shares = [.. _runtime.Shares.All()];
        long totalBytes = 0, freeBytes = 0;
        foreach (ShareDefinition share in shares)
        {
            (long total, long free) = VolumeCapacity(share.RootPath);
            totalBytes += total;
            freeBytes += free;
        }

        bool vpn = _runtime.VpnOrTunnelDetected(out string vpnDetails);
        List<AuditEntry> recentProblems = [.. _runtime.Audit.Tail(200)
            .Where(e => !e.Success || e.Category is "error")
            .TakeLast(8)];

        double[] upSeries;
        double[] downSeries;
        lock (_gate)
        {
            upSeries = [.. _recent.Select(s => s.Up)];
            downSeries = [.. _recent.Select(s => s.Down)];
        }

        return new OperationsSnapshot
        {
            GeneratedAt = now,
            System = new OperationsSnapshot.SystemLoad(
                UptimeSeconds: (now - _runtime.StartedAt).TotalSeconds,
                StartedAt: _runtime.StartedAt,
                Version: LanDisk.Server.Endpoints.BuildInfo.Version,
                ProcessCpuPercent: Math.Round(processCpu, 1),
                SystemCpuPercent: Math.Round(systemCpu, 1),
                WorkingSetBytes: Process.GetCurrentProcess().WorkingSet64,
                MachineName: Environment.MachineName,
                ProcessorCount: Environment.ProcessorCount),
            Network = new OperationsSnapshot.NetworkLoad(
                UploadBytesPerSecond: Math.Round(up, 1),
                DownloadBytesPerSecond: Math.Round(down, 1),
                UploadSeries: upSeries,
                DownloadSeries: downSeries,
                AcceptedAddresses: _runtime.LanAddresses(),
                VpnOrTunnel: vpn,
                VpnDetails: vpnDetails,
                HttpsEnabled: _runtime.Settings.HttpsEnabled,
                StableName: _runtime.Identity.HostName,
                MdnsRunning: _runtime.Mdns is { IsRunning: true },
                RecommendedOrigin: _runtime.Admin.RecommendedHttpsOrigin() ?? string.Empty,
                RecommendedKind: _runtime.Admin.RecommendedHostKind,
                StableOrigin: _runtime.Admin.StableHttpsOrigin(),
                PhysicalOrigin: _runtime.Admin.PhysicalHttpsOrigin()),
            Devices = new OperationsSnapshot.DeviceLoad(
                Total: devices.Count,
                Online: online,
                RecentlyActive: recentlyActive,
                Offline: devices.Count - online - recentlyActive,
                OnlineWindowSeconds: (int)DeviceActivity.OnlineWindow.TotalSeconds),
            Storage = new OperationsSnapshot.StorageLoad(
                ShareCount: shares.Count,
                EnabledShareCount: shares.Count(s => s.Enabled),
                TotalBytes: totalBytes,
                FreeBytes: freeBytes,
                UsedBytes: Math.Max(0, totalBytes - freeBytes),
                FreePercent: totalBytes > 0 ? Math.Round(freeBytes * 100.0 / totalBytes, 1) : 0),
            Transfers = new OperationsSnapshot.TransferLoad(
                ActiveCount: active.Count,
                ActiveUploads: active.Count(t => t.Direction == TransferDirection.Upload),
                ActiveDownloads: active.Count(t => t.Direction == TransferDirection.Download),
                TodayUploadBytes: today.Where(t => t.Direction == TransferDirection.Upload).Sum(t => t.TransferredBytes),
                TodayDownloadBytes: today.Where(t => t.Direction == TransferDirection.Download).Sum(t => t.TransferredBytes),
                TodayUploadCount: today.Count(t => t.Direction == TransferDirection.Upload),
                TodayDownloadCount: today.Count(t => t.Direction == TransferDirection.Download),
                Active: [.. active.Take(10).Select(t => new OperationsSnapshot.ActiveTransfer(
                    t.Id, t.Direction.ToString(), t.ShareName, t.RelativePath, t.DeviceName,
                    t.TransferredBytes, t.TotalBytes, t.StartedAt))]),
            Requests = new OperationsSnapshot.RequestLoad(requests, failures, _countersSince),
            Warnings = [.. recentProblems.Select(e => new OperationsSnapshot.OperationProblem(
                e.At, e.Category, e.Action, Redact(e.Detail), e.Success))]
        };
    }

    /// <summary>
    /// Audit details are written for the local operator, and the dashboard is another place they are shown.
    /// Anything that looks like a credential is masked here so a screenshot of the dashboard can be shared
    /// without leaking one.
    /// </summary>
    internal static string? Redact(string? detail)
    {
        if (string.IsNullOrEmpty(detail)) return detail;
        string masked = System.Text.RegularExpressions.Regex.Replace(
            detail, @"(token|secret|password|passwd|pwd|code|sha256)=([^\s,;]+)",
            "$1=***", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return masked;
    }

    /// <summary>
    /// System CPU percentage from two GetSystemTimes samples.
    ///
    /// <para><b>Why not kernel + user / (kernel + user + idle).</b> On Windows, <c>KernelTime</c> is the time
    /// spent in the kernel <i>including</i> the idle thread, so that formula double-counts idle and understates
    /// the CPU. The correct busy fraction is <c>(kernel + user - idle) / (kernel + user)</c>, where the total
    /// denominator is kernel + user (idle is already inside kernel).</para>
    ///
    /// <para>Pure and clamped: a counter that went backwards (another process sampled the machine, or the
    /// kernel wrapped) yields 0 rather than a negative or NaN value, and no input can exceed 100.</para>
    /// </summary>
    public static double SystemCpuPercent(ulong prevIdle, ulong prevKernel, ulong prevUser, ulong idle, ulong kernel, ulong user)
    {
        double kernelDelta = kernel >= prevKernel ? kernel - prevKernel : 0;
        double userDelta = user >= prevUser ? user - prevUser : 0;
        double idleDelta = idle >= prevIdle ? idle - prevIdle : 0;

        double total = kernelDelta + userDelta;
        if (total <= 0) return 0;

        double busy = Math.Max(0, total - idleDelta);
        return Math.Clamp(busy / total * 100, 0, 100);
    }

    private static (long Total, long Free) VolumeCapacity(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return (0, 0);
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return (0, 0);
            return (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static void ReadLinkBytes(out long sent, out long received)
    {
        sent = 0;
        received = 0;
        try
        {
            foreach (AdapterVerdict candidate in PhysicalLanSelector.PhysicalAddresses())
            {
                NetworkInterface? nic = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.GetIPProperties().UnicastAddresses
                        .Any(u => u.Address.ToString() == candidate.Address));
                if (nic is null) continue;
                IPv4InterfaceStatistics stats = nic.GetIPv4Statistics();
                sent += stats.BytesSent;
                received += stats.BytesReceived;
            }
        }
        catch
        {
            // The counters are a nicety; a machine that refuses to report them still gets a dashboard.
        }
    }

    private static void ReadSystemTimes(out ulong idle, out ulong kernel, out ulong user)
    {
        idle = 0;
        kernel = 0;
        user = 0;
        try
        {
            if (GetSystemTimes(out long idleTime, out long kernelTime, out long userTime))
            {
                idle = (ulong)idleTime;
                kernel = (ulong)kernelTime;
                user = (ulong)userTime;
            }
        }
        catch
        {
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
}
