using LanDisk.Core.Model;
using LanDisk.Server.Services;

namespace LanDisk.Server.Endpoints;

/// <summary>
/// What a paired phone may know about the machine it is talking to.
///
/// <para><b>Why a second, smaller surface.</b> The operations dashboard is an administrator view on loopback;
/// a phone must not be handed the same thing. This endpoint answers "is the computer I am connected to
/// healthy, and what am I allowed to do here" with the base set, and adds the machine-level numbers — CPU,
/// memory, throughput, how many phones are online, recent warnings — only for a device that has been granted
/// <see cref="DeviceCapability.SystemView"/>. Without that capability the response simply does not contain
/// them; it is not a matter of the app hiding fields it received.</para>
/// </summary>
public static class HostStatusEndpoints
{
    public static void MapHostStatusEndpoints(this WebApplication app)
    {
        app.MapGet("/api/host-status", (HttpContext context, LanDiskRuntime runtime) =>
        {
            RequestSession session = context.Session();
            if (session.Device is null && session.Admin is null)
                return ApiHelpers.Fail(StatusCodes.Status401Unauthorized, "NOT_AUTHENTICATED", "尚未配对。");

            OperationsSnapshot snapshot = runtime.Metrics.Snapshot();
            bool detailed = session.IsAdmin || session.Permissions.ViewSystem;
            string? recommended = runtime.Admin.RecommendedHttpsOrigin();

            object Base() => new
            {
                version = snapshot.System.Version,
                started = snapshot.System.StartedAt,
                uptimeSeconds = (long)snapshot.System.UptimeSeconds,
                serviceState = "running",
                recommendedOrigin = recommended,
                recommendedKind = runtime.Admin.RecommendedHostKind,
                stableOrigin = runtime.Admin.StableHttpsOrigin(),
                physicalOrigin = runtime.Admin.PhysicalHttpsOrigin(),
                httpsEnabled = runtime.Settings.HttpsEnabled,
                lanEnabled = runtime.Settings.LanEnabled,
                mdnsRunning = runtime.Mdns is { IsRunning: true },
                shares = new { total = snapshot.Storage.ShareCount, enabled = snapshot.Storage.EnabledShareCount },
                storage = new
                {
                    // The phone is not told "0 B free" when there is simply nothing mounted: that reads as
                    // "the machine has no disk", which is a different and alarming statement.
                    available = snapshot.Storage.ShareCount > 0,
                    totalBytes = snapshot.Storage.TotalBytes,
                    freeBytes = snapshot.Storage.FreeBytes,
                    freePercent = snapshot.Storage.FreePercent
                },
                transfers = new
                {
                    active = snapshot.Transfers.ActiveCount,
                    todayUploadBytes = snapshot.Transfers.TodayUploadBytes,
                    todayDownloadBytes = snapshot.Transfers.TodayDownloadBytes
                },
                // The caller's own authority, so the phone can show "what I may do here" without guessing.
                you = new
                {
                    level = session.Device?.Level.ToString() ?? "LocalAdmin",
                    permissions = session.Permissions,
                    name = session.Device?.Name ?? OperationActor.LocalAdminName
                }
            };

            if (!detailed) return Results.Json(new { ok = true, detailed = false, status = Base() }, ApiHelpers.Json);

            return Results.Json(new
            {
                ok = true,
                detailed = true,
                status = Base(),
                machine = new
                {
                    processCpuPercent = snapshot.System.ProcessCpuPercent,
                    systemCpuPercent = snapshot.System.SystemCpuPercent,
                    memoryUsedBytes = snapshot.System.WorkingSetBytes,
                    memoryTotalBytes = 0L,
                    uploadBytesPerSecond = snapshot.Network.UploadBytesPerSecond,
                    downloadBytesPerSecond = snapshot.Network.DownloadBytesPerSecond,
                    onlineDevices = snapshot.Devices.Online,
                    recentlyActiveDevices = snapshot.Devices.RecentlyActive,
                    totalDevices = snapshot.Devices.Total,
                    vpnOrTunnel = snapshot.Network.VpnOrTunnel,
                    problems = snapshot.Warnings.Take(5).Select(p => new
                    {
                        at = p.At,
                        category = p.Category,
                        action = p.Action,
                        detail = p.Detail,
                        success = p.Success
                    })
                }
            }, ApiHelpers.Json);
        });
    }
}
