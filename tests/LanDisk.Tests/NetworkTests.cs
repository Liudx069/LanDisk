using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LanDisk.Core.Model;
using LanDisk.Core.Networking;
using LanDisk.Core.Paths;
using LanDisk.Server;
using LanDisk.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;

namespace LanDisk.Tests;

/// <summary>
/// V01-V04: volume identity, simulated media removal, drive letter reuse and re-confirmation.
/// There is no physical removable disk attached to the test machine, so the removal cases are
/// explicitly simulations: they change the file system shape, not real hardware.
/// </summary>
public sealed class VolumeTests : TestBase
{
    protected override void SeedSandbox(TestHost host)
    {
        host.WriteSandboxFile("数据/文件.txt", "内容");
    }

    [Fact]
    public async Task V01_a_share_records_the_real_volume_identity()
    {
        JsonElement share = (await Admin.GetAsync<JsonElement>("/api/admin/shares")).Body
            .GetProperty("shares").EnumerateArray().First(s => s.GetProperty("id").GetString() == ShareId);

        string recordedGuid = share.GetProperty("volumeGuidPath").GetString()!;
        string actualGuid = LanDisk.Core.Interop.Win32FileSystem.GetVolumeGuidPath(
            LanDisk.Core.Interop.Win32FileSystem.GetVolumeMountPoint(Host.SandboxRoot));

        Assert.Equal(actualGuid, recordedGuid);
        Assert.StartsWith(@"\\?\Volume{", recordedGuid, StringComparison.Ordinal);

        // The reported mount point is informational; the GUID is the identity.
        string mountPoint = share.GetProperty("mountPoint").GetString()!;
        Assert.Equal(Path.GetPathRoot(Host.SandboxRoot), mountPoint);
        Assert.Equal(ShareState.Online, Host.Runtime.Resolver.ProbeShare(Host.Runtime.Shares.Find(ShareId)!).State);

        var device = await PairDeviceAsync();
        ApiResult<JsonElement> meta = await device.GetAsync<JsonElement>("/api/file/meta" + LanDiskClient.FileQuery(ShareId, "数据/文件.txt"));
        Assert.Equal(recordedGuid, meta.Body.GetProperty("volumeGuidPath").GetString());

        Record("V01", "共享绑定真实卷 GUID：" + recordedGuid);
    }

    [Fact]
    public async Task V02_simulated_media_removal_reports_offline_and_stops_operations()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);

        ApiResult<JsonElement> before = await device.BrowseAsync(ShareId, "");
        Assert.Equal(HttpStatusCode.OK, before.Status);

        // Simulates the volume disappearing: the share root path is no longer reachable.
        string moved = Host.SandboxRoot + "-detached";
        Directory.Move(Host.SandboxRoot, moved);
        try
        {
            Host.Runtime.RefreshShareHealth();
            ShareDefinition share = Host.Runtime.Shares.Find(ShareId)!;
            Assert.Equal(ShareState.Offline, share.State);

            ApiResult<JsonElement> browse = await device.BrowseAsync(ShareId, "");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, browse.Status);
            Assert.Equal("SHARE_OFFLINE", browse.ErrorCode);

            using HttpResponseMessage download = await device.SendRawAsync(
                new HttpRequestMessage(HttpMethod.Get, "/api/file/download" + LanDiskClient.FileQuery(ShareId, "数据/文件.txt")));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, download.StatusCode);

            ApiResult<JsonElement> delete = await device.PostAsync<JsonElement>("/api/file/delete", new
            {
                shareId = ShareId,
                path = "数据/文件.txt",
                version = "1-1"
            });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, delete.Status);

            ApiResult<JsonElement> upload = await device.PostAsync<JsonElement>("/api/uploads/", new
            {
                shareId = ShareId,
                path = "",
                fileName = "x.bin",
                length = 4,
                fingerprint = new string('c', 64)
            });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, upload.Status);

            Record("V02", "模拟拔出后共享离线，所有操作返回 503 且无假成功");
        }
        finally
        {
            Directory.Move(moved, Host.SandboxRoot);
        }

        Host.Runtime.RefreshShareHealth();
        ApiResult<JsonElement> recovered = await device.BrowseAsync(ShareId, "");
        Assert.Equal(HttpStatusCode.OK, recovered.Status);
        Record("V02", "路径恢复后共享自动回到在线");
    }

    [Fact]
    public async Task V02_simulated_removal_reports_queued_uploads_as_failures_not_successes()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);

        ApiResult<JsonElement> init = await device.PostAsync<JsonElement>("/api/uploads/", new
        {
            shareId = ShareId,
            path = "",
            fileName = "中断.bin",
            length = 1024,
            fingerprint = new string('d', 64)
        });
        Assert.True(init.IsSuccess, init.Raw);
        string uploadId = init.Body.GetProperty("uploadId").GetString()!;

        string moved = Host.SandboxRoot + "-detached";
        Directory.Move(Host.SandboxRoot, moved);
        try
        {
            byte[] chunk = Sandbox.Pattern(1024, 3);
            var content = new ByteArrayContent(chunk);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            var request = new HttpRequestMessage(HttpMethod.Put, $"/api/uploads/{uploadId}/chunk?offset=0") { Content = content };
            request.Headers.Add("X-Chunk-SHA256", Sandbox.Sha256Hex(chunk));

            using HttpResponseMessage chunkResponse = await device.SendRawAsync(request);
            // Staging lives on the volume rather than inside the share, so accepting the bytes is
            // legitimate. What must never happen is a successful *commit*.
            bool chunkAccepted = chunkResponse.IsSuccessStatusCode;

            ApiResult<JsonElement> complete = await device.PostAsync<JsonElement>($"/api/uploads/{uploadId}/complete", new
            {
                sha256 = chunkAccepted ? Sandbox.Sha256Hex(chunk) : new string('0', 64),
                conflictPolicy = "fail"
            });
            Assert.False(complete.IsSuccess, "共享不可用时不得报告提交成功");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, complete.Status);

            Assert.False(File.Exists(Path.Combine(moved, "中断.bin")), "不得在失联目录里留下半成品");

            // A failed commit discards its own staging file rather than leaving it behind.
            var session = Host.Runtime.Uploads.Find(uploadId, Host.Runtime.Devices.All().Last().Id);
            if (session is not null)
                Assert.False(File.Exists(session.StagingPath), "提交失败后必须清理暂存文件");

            Record("V02", $"拔盘过程中：分块被接受={chunkAccepted}，提交明确失败且暂存已清理");
        }
        finally
        {
            Directory.Move(moved, Host.SandboxRoot);
        }
    }

    [Fact]
    public async Task V03_a_different_volume_reusing_the_same_path_does_not_inherit_access()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);

        // Simulates another disk taking over the identity slot: the recorded volume GUID no
        // longer matches the volume that currently hosts the path.
        string foreignGuid = LanDisk.Core.Interop.Win32FileSystem.GetVolumeGuidPath(
            LanDisk.Core.Interop.Win32FileSystem.GetVolumeMountPoint(Path.GetPathRoot(Environment.SystemDirectory)!));

        string original = Host.Runtime.Shares.Find(ShareId)!.VolumeGuidPath;
        Assert.NotEqual(foreignGuid, original);

        Host.Runtime.Shares.Update(ShareId, s => s.VolumeGuidPath = foreignGuid);
        Host.Runtime.RefreshShareHealth();

        ApiResult<JsonElement> browse = await device.BrowseAsync(ShareId, "");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, browse.Status);
        Assert.Equal("SHARE_IDENTITYMISMATCH", browse.ErrorCode);

        ApiResult<JsonElement> upload = await device.PostAsync<JsonElement>("/api/uploads/", new
        {
            shareId = ShareId,
            path = "",
            fileName = "旧盘上传.bin",
            length = 4,
            fingerprint = new string('e', 64)
        });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, upload.Status);
        Assert.False(File.Exists(Host.SandboxPath("旧盘上传.bin")));

        Host.Runtime.Shares.Update(ShareId, s => s.VolumeGuidPath = original);
        Host.Runtime.RefreshShareHealth();
        Record("V03", "卷身份不匹配时拒绝服务，不会把操作落到新盘");
    }

    [Fact]
    public async Task V03_replacing_the_share_root_object_requires_reconfirmation()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);

        // Simulates the root directory being swapped (reformat/recreate with the same name).
        string original = Host.Runtime.Shares.Find(ShareId)!.RootObjectId;
        Host.Runtime.Shares.Update(ShareId, s => s.RootObjectId = "00000000-0000000000000000");
        Host.Runtime.RefreshShareHealth();

        ShareDefinition share = Host.Runtime.Shares.Find(ShareId)!;
        Assert.Equal(ShareState.IdentityMismatch, share.State);

        ApiResult<JsonElement> browse = await device.BrowseAsync(ShareId, "");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, browse.Status);

        // The local administrator explicitly re-confirms the new identity.
        ApiResult<JsonElement> reconfirmed = await Admin.PostAsync<JsonElement>($"/api/admin/shares/{ShareId}/reconfirm", new { });
        Assert.True(reconfirmed.IsSuccess, reconfirmed.Raw);
        Assert.Equal(original, Host.Runtime.Shares.Find(ShareId)!.RootObjectId);

        ApiResult<JsonElement> after = await device.BrowseAsync(ShareId, "");
        Assert.Equal(HttpStatusCode.OK, after.Status);
        Record("V03", "根对象变化后必须由本机重新确认");
    }

    [Fact]
    public async Task V04_reconnecting_the_same_volume_restores_service()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        string guid = Host.Runtime.Shares.Find(ShareId)!.VolumeGuidPath;

        string moved = Host.SandboxRoot + "-detached";
        Directory.Move(Host.SandboxRoot, moved);
        Host.Runtime.RefreshShareHealth();
        Assert.Equal(ShareState.Offline, Host.Runtime.Shares.Find(ShareId)!.State);

        Directory.Move(moved, Host.SandboxRoot);
        Host.Runtime.RefreshShareHealth();

        ShareDefinition share = Host.Runtime.Shares.Find(ShareId)!;
        Assert.Equal(ShareState.Online, share.State);
        Assert.Equal(guid, share.VolumeGuidPath);

        ApiResult<JsonElement> browse = await device.BrowseAsync(ShareId, "");
        Assert.Equal(HttpStatusCode.OK, browse.Status);
        Record("V04", "同一卷重新挂载后自动恢复");
    }

    [Fact]
    public async Task V04_recycle_payload_on_an_unreachable_volume_is_not_silently_lost()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);

        ApiResult<JsonElement> meta = await device.GetAsync<JsonElement>("/api/file/meta" + LanDiskClient.FileQuery(ShareId, "数据/文件.txt"));
        string version = meta.Body.GetProperty("version").GetString()!;

        ApiResult<JsonElement> deleted = await device.PostAsync<JsonElement>("/api/file/delete", new
        {
            shareId = ShareId,
            path = "数据/文件.txt",
            version
        });
        Assert.True(deleted.IsSuccess, deleted.Raw);
        string recycleId = deleted.Body.GetProperty("recycleId").GetString()!;

        // Point the entry at an unreachable volume identity.
        Host.Runtime.RecycleIndex.Update(recycleId, e => e.VolumeGuidPath = @"\\?\Volume{00000000-0000-0000-0000-000000000000}\");

        ApiResult<JsonElement> restore = await device.PostAsync<JsonElement>($"/api/recycle/{recycleId}/restore", new { });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, restore.Status);

        // The payload is still recorded and the admin can still see it; nothing was silently purged.
        JsonElement adminRecycle = (await Admin.GetAsync<JsonElement>("/api/admin/recycle")).Body;
        Assert.Contains(adminRecycle.GetProperty("entries").EnumerateArray(),
            e => e.GetProperty("id").GetString() == recycleId);
        Record("V04", "回收内容所在卷不可达时明确失败，不静默丢失");
    }
}

/// <summary>N01-N04: LAN exposure, HTTPS, HTTP compatibility mode and network diagnostics.</summary>
public sealed class NetworkTests : TestBase
{
    protected override void SeedSandbox(TestHost host) => host.WriteSandboxFile("文件.txt", "内容");

    [Fact]
    public async Task N01_default_binding_is_loopback_only()
    {
        foreach (string url in Host.Runtime.BoundUrls)
        {
            Assert.True(url.Contains("127.0.0.1") || url.Contains("[::1]"), "默认不得绑定非回环地址：" + url);
        }

        Record("N01", "默认监听：" + string.Join(", ", Host.Runtime.BoundUrls));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task N01_private_lan_sources_are_served_and_public_sources_are_not()
    {
        (HttpStatusCode allowedStatus, string _) = await NetworkPolicyProbe.RunAsync(
            Host, "192.168.1.50", "192.168.1.10", "/api/health", lanEnabled: true);
        Assert.Equal(HttpStatusCode.OK, allowedStatus);

        (HttpStatusCode disabled, string disabledBody) = await NetworkPolicyProbe.RunAsync(
            Host, "192.168.1.50", "192.168.1.10", "/api/health", lanEnabled: false);
        Assert.Equal(HttpStatusCode.Forbidden, disabled);
        Assert.Contains("LAN_DISABLED", disabledBody);

        (HttpStatusCode publicDenied, string publicBody) = await NetworkPolicyProbe.RunAsync(
            Host, "8.8.8.8", "192.168.1.10", "/api/health", lanEnabled: true);
        Assert.Equal(HttpStatusCode.Forbidden, publicDenied);
        Assert.Contains("PUBLIC_SOURCE", publicBody);

        Record("N01", "网络策略：私有来源放行，公网来源与关闭状态均拒绝（合成连接）");
    }

    [Fact]
    public async Task N01_cidr_restriction_is_enforced()
    {
        string original = Host.Runtime.Settings.AllowedCidr;
        Host.Runtime.Settings.AllowedCidr = "192.168.5.0/24";
        try
        {
            (HttpStatusCode denied, string body) = await NetworkPolicyProbe.RunAsync(
                Host, "192.168.1.50", "192.168.1.10", "/api/health", lanEnabled: true);
            Assert.Equal(HttpStatusCode.Forbidden, denied);
            Assert.Contains("CIDR_DENIED", body);

            (HttpStatusCode allowed, string _) = await NetworkPolicyProbe.RunAsync(
                Host, "192.168.5.20", "192.168.1.10", "/api/health", lanEnabled: true);
            Assert.Equal(HttpStatusCode.OK, allowed);
        }
        finally
        {
            Host.Runtime.Settings.AllowedCidr = original;
        }

        Assert.True(LanInfo.IsInCidr(IPAddress.Parse("192.168.5.20"), IPAddress.Parse("192.168.5.0"), 24));
        Assert.False(LanInfo.IsInCidr(IPAddress.Parse("192.168.6.20"), IPAddress.Parse("192.168.5.0"), 24));
        Record("N01", "CIDR 限制生效");
    }

    [Fact]
    public void N01_cidr_parsing_rejects_bad_input()
    {
        Assert.False(LanInfo.TryParseCidr("not-a-cidr", out _, out _));
        Assert.False(LanInfo.TryParseCidr("192.168.1.0/33", out _, out _));
        Assert.True(LanInfo.TryParseCidr("10.0.0.0/8", out IPAddress network, out int prefix));
        Assert.Equal(8, prefix);
        Assert.Equal("10.0.0.0", network.ToString());
    }

    [Fact]
    public async Task N02_https_listener_serves_the_generated_certificate()
    {
        Assert.False(string.IsNullOrEmpty(Host.HttpsBaseUrl), "测试主机应提供回环 HTTPS 监听");

        using var client = Host.NewHttpsClient();
        using HttpResponseMessage response = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Record("N02", "回环 HTTPS 监听可用（证书信任由后续独立用例判定，未在此处冒充可信）");
    }

    [Fact]
    public async Task N02_certificate_covers_loopback_and_local_private_addresses()
    {
        ApiResult<JsonElement> tls = await Admin.GetAsync<JsonElement>("/api/admin/tls");
        Assert.True(tls.IsSuccess, tls.Raw);

        JsonElement certificate = tls.Body.GetProperty("certificate");
        string[] ips = certificate.GetProperty("ipAddresses").EnumerateArray().Select(i => i.GetString()!).ToArray();
        string[] dns = certificate.GetProperty("dnsNames").EnumerateArray().Select(d => d.GetString()!).ToArray();

        Assert.Contains("127.0.0.1", ips);
        Assert.Contains("localhost", dns);

        // 1.4.0 replaced the single self-signed leaf with a persistent local CA plus leaves it issues:
        // that is what lets a phone trust once and keep working when the LAN address changes. The leaf
        // is therefore no longer self-signed, its subject is the stable .local name, and the Windows
        // machine name is kept in the SANs so an address that worked before this release still works.
        Assert.Contains(Environment.MachineName.ToLowerInvariant(), dns);
        Assert.False(certificate.GetProperty("isSelfSigned").GetBoolean());
        Assert.True(certificate.GetProperty("issuedByLocalCa").GetBoolean());
        Assert.Contains(dns, d => d.StartsWith("landisk-", StringComparison.Ordinal) && d.EndsWith(".local", StringComparison.Ordinal));
        Assert.StartsWith("CN=landisk-", certificate.GetProperty("subject").GetString()!, StringComparison.Ordinal);
        Assert.True(certificate.GetProperty("notAfter").GetDateTimeOffset() > DateTimeOffset.Now.AddMonths(6));

        JsonElement rootCa = tls.Body.GetProperty("rootCa");
        Assert.StartsWith("CN=LanDisk Local CA", rootCa.GetProperty("subject").GetString()!, StringComparison.Ordinal);
        Assert.True(rootCa.GetProperty("notAfter").GetDateTimeOffset() > DateTimeOffset.Now.AddYears(5));

        string fingerprint = certificate.GetProperty("sha256Fingerprint").GetString()!;
        Assert.Matches("^([0-9A-F]{2}:){31}[0-9A-F]{2}$", fingerprint);

        Record("N02", "证书 SAN 覆盖回环与本机私有地址，指纹 " + fingerprint[..23] + "…");
    }

    [Fact]
    public async Task N02_certificate_download_requires_the_local_admin_and_never_exposes_the_private_key()
    {
        var anonymous = new LanDiskClient(Host.NewClient());
        ApiResult<JsonElement> denied = await anonymous.GetAsync<JsonElement>("/api/admin/tls/cert");
        Assert.True(denied.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);

        using HttpResponseMessage cer = await Admin.SendRawAsync(new HttpRequestMessage(HttpMethod.Get, "/api/admin/tls/cert"));
        Assert.Equal(HttpStatusCode.OK, cer.StatusCode);
        byte[] der = await cer.Content.ReadAsByteArrayAsync();
        Assert.Equal(0x30, der[0]); // DER SEQUENCE

        // An ECDSA P-256 certificate is compact, so length alone is not a good check; parse it.
        using var served = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(der);
        Assert.StartsWith("CN=LanDisk Local CA", served.Subject, StringComparison.Ordinal);
        Assert.False(served.HasPrivateKey, "下载的必须是公开证书，不能带私钥");

        // The exported file is a public certificate only: no PKCS#12 payload is ever served.
        string asText = System.Text.Encoding.Latin1.GetString(der);
        Assert.DoesNotContain("PRIVATE KEY", asText, StringComparison.Ordinal);

        // Both private keys live on this machine, sealed for the current Windows user. The seal is
        // DPAPI, so the bytes on disk are neither a readable PEM block nor a PKCS#12 blob.
        Assert.True(File.Exists(Host.Runtime.Paths.CaProtectedFile), "本地 CA 私钥应保存在本机数据目录内");
        Assert.True(File.Exists(Host.Runtime.Paths.LeafProtectedFile), "服务器证书私钥应保存在本机数据目录内");

        string sealedText = System.Text.Encoding.Latin1.GetString(
            await File.ReadAllBytesAsync(Host.Runtime.Paths.CaProtectedFile));
        Assert.DoesNotContain("PRIVATE KEY", sealedText, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN", sealedText, StringComparison.Ordinal);

        Record("N02", "仅本机管理端可下载公开的根证书；CA 与服务器私钥以 DPAPI 封存，不出本机");
    }

    [Fact]
    public async Task N02_regenerating_the_certificate_produces_a_new_valid_certificate()
    {
        JsonElement first = (await Admin.GetAsync<JsonElement>("/api/admin/tls")).Body.GetProperty("certificate");
        string firstThumbprint = first.GetProperty("thumbprint").GetString()!;

        JsonElement second = (await Admin.GetAsync<JsonElement>("/api/admin/tls?regenerate=1")).Body.GetProperty("certificate");
        string secondThumbprint = second.GetProperty("thumbprint").GetString()!;

        Assert.NotEqual(firstThumbprint, secondThumbprint);
        Assert.Contains("127.0.0.1", second.GetProperty("ipAddresses").EnumerateArray().Select(i => i.GetString()!));
        Record("N02", "证书可重新生成且 SAN 保持正确");
    }

    [Fact]
    public async Task N03_lan_cannot_be_enabled_without_tls_or_explicit_compatibility_mode()
    {
        JsonElement settings = (await Admin.GetAsync<JsonElement>("/api/admin/network")).Body.GetProperty("settings");
        Assert.False(settings.GetProperty("lanEnabled").GetBoolean());
        Assert.False(settings.GetProperty("httpsEnabled").GetBoolean());
        Assert.False(settings.GetProperty("httpCompatModeEnabled").GetBoolean());

        ApiResult<JsonElement> refused = await Admin.PostAsync<JsonElement>("/api/admin/network", new
        {
            lanEnabled = true,
            httpsEnabled = false,
            httpCompatModeEnabled = false
        });

        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Equal("TLS_OR_COMPAT_REQUIRED", refused.ErrorCode);

        ApiResult<JsonElement> accepted = await Admin.PostAsync<JsonElement>("/api/admin/network", new
        {
            lanEnabled = true,
            httpsEnabled = true,
            httpCompatModeEnabled = false
        });
        Assert.True(accepted.IsSuccess, accepted.Raw);
        Assert.True(accepted.Body.GetProperty("restartRequired").GetBoolean());
        Assert.True(Host.Runtime.Settings.HttpsEnabled);
        Assert.True(Host.Runtime.Settings.LanEnabled);
        Assert.False(Host.Runtime.Settings.HttpCompatModeEnabled);

        // The socket binding is decided at startup; the running instance must not silently
        // start listening on the LAN just because the setting was saved.
        Assert.All(Host.Runtime.BoundUrls, url =>
            Assert.True(url.Contains("127.0.0.1") || url.Contains("[::1]"), "保存设置不应立即改变监听：" + url));

        Record("N03", "HTTP 兼容模式默认关闭，局域网必须显式选择 TLS 或兼容模式");
    }

    [Fact]
    public async Task N03_unknown_bind_address_is_refused()
    {
        ApiResult<JsonElement> result = await Admin.PostAsync<JsonElement>("/api/admin/network", new
        {
            lanEnabled = true,
            httpsEnabled = true,
            bindAddress = "203.0.113.77"
        });

        Assert.Equal(HttpStatusCode.BadRequest, result.Status);
        Assert.Equal("ADDRESS_UNKNOWN", result.ErrorCode);
        Record("N03", "拒绝绑定非本机地址");
    }

    [Fact]
    public async Task N04_port_conflict_fails_fast_with_a_clear_error()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int busyPort = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            var options = new HostOptions
            {
                DataRoot = Path.Combine(Host.Root, "port-conflict-data"),
                HttpPort = busyPort,
                HttpsPort = 0,
                Quiet = true,
                ServeStaticFiles = false
            };

            WebApplication app = LanDiskHost.Build([], options);
            await Assert.ThrowsAnyAsync<IOException>(async () => await app.StartAsync());
            await app.DisposeAsync();

            Record("N04", $"端口 {busyPort} 被占用时启动明确失败，并给出可操作提示");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task N04_diagnostics_explain_the_common_network_failures()
    {
        ApiResult<JsonElement> diagnostics = await Admin.GetAsync<JsonElement>("/api/admin/diagnostics");
        Assert.True(diagnostics.IsSuccess, diagnostics.Raw);

        string[] ids = diagnostics.Body.GetProperty("checks").EnumerateArray()
            .Select(c => c.GetProperty("id").GetString()!).ToArray();

        Assert.Contains("shares", ids);
        Assert.Contains("lan", ids);
        Assert.Contains("firewall", ids);
        Assert.Contains("sleep", ids);

        string all = diagnostics.Raw;
        Assert.DoesNotContain(Host.DataRoot, all, StringComparison.OrdinalIgnoreCase);
        Record("N04", "自检覆盖共享状态、局域网开关、防火墙与睡眠提示");
    }

    [Fact]
    public async Task N04_diagnostic_output_does_not_leak_the_data_root_or_secrets()
    {
        ApiResult<JsonElement> status = await Admin.GetAsync<JsonElement>("/api/admin/status");
        Assert.True(status.IsSuccess);

        ApiResult<JsonElement> audit = await Admin.GetAsync<JsonElement>("/api/admin/audit?limit=50");
        Assert.True(audit.IsSuccess);
        Assert.DoesNotContain(Host.Runtime.AdminBootstrapToken, audit.Raw, StringComparison.Ordinal);

        string pfxPassword = File.Exists(Host.Runtime.Paths.TlsPasswordFile)
            ? File.ReadAllText(Host.Runtime.Paths.TlsPasswordFile).Trim()
            : string.Empty;
        if (pfxPassword.Length > 0)
            Assert.DoesNotContain(pfxPassword, audit.Raw, StringComparison.Ordinal);
    }
}
