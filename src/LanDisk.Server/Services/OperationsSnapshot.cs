using LanDisk.Core.Model;

namespace LanDisk.Server.Services;

/// <summary>
/// The operations dashboard payload, as a typed model.
///
/// <para><b>Why typed rather than an anonymous object.</b> Both the HTTP endpoint and the native Windows
/// page show these numbers, and the page runs in this process. With a named type the two surfaces cannot
/// drift (a renamed field breaks the build instead of silently rendering "—"), and the shape can be asserted
/// in a test without spinning up an HTTP client.</para>
/// </summary>
public sealed record OperationsSnapshot
{
    public DateTimeOffset GeneratedAt { get; init; }
    public required SystemLoad System { get; init; }
    public required NetworkLoad Network { get; init; }
    public required DeviceLoad Devices { get; init; }
    public required StorageLoad Storage { get; init; }
    public required TransferLoad Transfers { get; init; }
    public required RequestLoad Requests { get; init; }
    public IReadOnlyList<OperationProblem> Warnings { get; init; } = [];

    public sealed record SystemLoad(
        double UptimeSeconds,
        DateTimeOffset StartedAt,
        string Version,
        double ProcessCpuPercent,
        double SystemCpuPercent,
        long WorkingSetBytes,
        string MachineName,
        int ProcessorCount);

    public sealed record NetworkLoad(
        double UploadBytesPerSecond,
        double DownloadBytesPerSecond,
        IReadOnlyList<double> UploadSeries,
        IReadOnlyList<double> DownloadSeries,
        IReadOnlyList<string> AcceptedAddresses,
        bool VpnOrTunnel,
        string VpnDetails,
        bool HttpsEnabled,
        string StableName,
        bool MdnsRunning,
        /// <summary>The address to recommend; never the stable name when it has not been measured reachable.</summary>
        string RecommendedOrigin,
        /// <summary>How the recommendation was chosen: verified / physical / stable.</summary>
        string RecommendedKind,
        /// <summary>The stable <c>.local</c> origin, shown separately and never presented as "verified".</summary>
        string StableOrigin,
        /// <summary>The best physical LAN origin, or null.</summary>
        string? PhysicalOrigin);

    public sealed record DeviceLoad(int Total, int Online, int RecentlyActive, int Offline, int OnlineWindowSeconds);

    public sealed record StorageLoad(int ShareCount, int EnabledShareCount, long TotalBytes, long FreeBytes, long UsedBytes, double FreePercent);

    public sealed record TransferLoad(
        int ActiveCount,
        int ActiveUploads,
        int ActiveDownloads,
        long TodayUploadBytes,
        long TodayDownloadBytes,
        int TodayUploadCount,
        int TodayDownloadCount,
        IReadOnlyList<ActiveTransfer> Active);

    public sealed record ActiveTransfer(
        string Id,
        string Direction,
        string Share,
        string Path,
        string Device,
        long Transferred,
        long Total,
        DateTimeOffset StartedAt);

    public sealed record RequestLoad(long Total, long Failures, DateTimeOffset Since);

    public sealed record OperationProblem(DateTimeOffset At, string Category, string Action, string? Detail, bool Success);

    /// <summary>Everything the dashboard needs to decide a colour, in one place.</summary>
    public string OverallTone
    {
        get
        {
            if (Warnings.Any(w => !w.Success && w.Category is "error" or "storage")) return "danger";
            if (Warnings.Count > 0) return "warning";
            return Devices.Online > 0 || Transfers.ActiveCount > 0 ? "success" : "info";
        }
    }

    /// <summary>One sentence for the page header, so the user does not have to read four tiles to know.</summary>
    public string Summary => Warnings.Count == 0
        ? $"运行正常 · {Devices.Online} 台设备在线 · {Transfers.ActiveCount} 个传输进行中"
        : $"有 {Warnings.Count} 条需要注意的记录 · {Devices.Online} 台设备在线";
}

/// <summary>A single device row for the presence-aware device list (shared by the page and the API).</summary>
public sealed record DeviceRow(
    DeviceDefinition Device,
    DevicePresence Presence,
    DevicePermissions Permissions,
    IReadOnlyList<string> Capabilities)
{
    public string PresenceWire => DeviceActivity.ToWire(Presence);
}
