using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using LanDisk.Core.Model;
using LanDisk.Server.Services;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>
/// AUTH17 / ONLINE17 — permissions that change while the phone is still connected, and a definition of
/// "online" that is a measurement rather than a guess.
///
/// <para><b>What was wrong.</b> The session cookie carried the device record resolved at pairing time, and
/// the mobile UI only ever read its capabilities once. A phone that was read-only when it paired kept its
/// buttons, and a phone that was granted write access saw nothing change until it re-paired — while the
/// server itself was already re-reading the record. The missing pieces were (a) honouring the live record
/// everywhere the decision is consumed, in particular for a transfer that is already running, and (b) giving
/// the phone a cheap way to ask "what may I do now".</para>
///
/// <para><b>What "online" means here.</b> Not "a device exists" and not "the row is not revoked": every
/// authenticated request is evidence, presence is classified from the last such evidence (45 s online,
/// 5 minutes recently active — wider than the phone's 15 s heartbeat because Safari suspends background
/// timers), and a revoked device is never online.</para>
/// </summary>
public sealed class ProductizationPermissionTests : TestBase
{
    protected override void SeedSandbox(TestHost host) => host.WriteSandboxFile("已有.txt", "内容");

    // ------------------------------------------------------------------ helpers

    private static async Task<ApiResult<JsonElement>> ChunkAsync(LanDiskClient device, string uploadId, long offset, byte[] chunk)
    {
        var content = new ByteArrayContent(chunk);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/uploads/{uploadId}/chunk?offset={offset}") { Content = content };
        request.Headers.Add("X-Chunk-SHA256", Sandbox.Sha256Hex(chunk));

        using HttpResponseMessage response = await device.SendRawAsync(request);
        string raw = await response.Content.ReadAsStringAsync();
        JsonElement parsed = default;
        try { if (!string.IsNullOrWhiteSpace(raw)) parsed = JsonSerializer.Deserialize<JsonElement>(raw, TestHost.Json); } catch { }
        return new ApiResult<JsonElement>(response.StatusCode, parsed, raw);
    }

    private async Task<ApiResult<JsonElement>> InitUploadAsync(LanDiskClient device, string fileName, byte[] content)
    {
        return await device.PostAsync<JsonElement>("/api/uploads/", new
        {
            shareId = ShareId,
            path = "",
            fileName,
            length = (long)content.Length,
            fingerprint = Sandbox.Fingerprint(content.Length, 1_700_000_000_000, content)
        });
    }

    private async Task SetLevelAsync(DeviceDefinition device, bool readWrite)
    {
        OperationOutcome<DeviceDefinition> outcome = Host.Runtime.Admin.SetDeviceAccess(device.Id, readWrite);
        Assert.True(outcome.Ok, outcome.Message);
        await Task.CompletedTask;
    }

    // ------------------------------------------------------------------ AUTH17-01

    [Fact]
    public async Task A_read_only_device_cannot_upload()
    {
        LanDiskClient device = await PairDeviceAsync(DeviceAccessLevel.ReadOnly, "只读手机");
        byte[] content = [1, 2, 3, 4];

        ApiResult<JsonElement> init = await InitUploadAsync(device, "只读.txt", content);

        Assert.Equal(HttpStatusCode.Forbidden, init.Status);
        Assert.Equal("PERMISSION_DENIED", init.ErrorCode);

        TestBase.Record("AUTH17-01", "只读设备无法上传：POST /api/uploads/ 返回 403 PERMISSION_DENIED，手机端据此禁用上传入口");
    }

    // ------------------------------------------------------------------ AUTH17-02 / 03

    [Fact]
    public async Task Raising_the_level_takes_effect_on_the_existing_session_without_re_pairing()
    {
        LanDiskClient device = await PairDeviceAsync(DeviceAccessLevel.ReadOnly, "升级手机");
        DeviceDefinition record = Host.Runtime.Devices.All().Single();
        byte[] content = [9, 8, 7, 6, 5];

        Assert.Equal(HttpStatusCode.Forbidden, (await InitUploadAsync(device, "升级.txt", content)).Status);

        // The operator flips it on the Windows side. The phone keeps its cookie: no re-pairing.
        await SetLevelAsync(record, readWrite: true);

        ApiResult<JsonElement> init = await InitUploadAsync(device, "升级.txt", content);
        Assert.True(init.IsSuccess, "提升权限后同一会话必须立刻可以上传：" + init.Raw);
        string uploadId = init.Body.GetProperty("uploadId").GetString()!;

        ApiResult<JsonElement> chunk = await ChunkAsync(device, uploadId, 0, content);
        Assert.True(chunk.IsSuccess, chunk.Raw);
        ApiResult<JsonElement> complete = await device.PostAsync<JsonElement>($"/api/uploads/{uploadId}/complete",
            new { sha256 = Sandbox.Sha256Hex(content), conflictPolicy = "fail" });
        Assert.True(complete.IsSuccess, complete.Raw);
        Assert.True(File.Exists(Host.SandboxPath("升级.txt")));

        TestBase.Record("AUTH17-02",
            "只读 → 读写后**同一会话**（不重新配对、不重启）立即上传成功：初始化、分块、提交全部通过，文件真实落盘");
    }

    [Fact]
    public async Task Lowering_the_level_blocks_the_next_write_on_the_existing_session()
    {
        LanDiskClient device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite, "降级手机");
        DeviceDefinition record = Host.Runtime.Devices.All().Single();

        byte[] first = [1, 1, 1];
        ApiResult<JsonElement> ok = await InitUploadAsync(device, "降级-之前.txt", first);
        Assert.True(ok.IsSuccess, ok.Raw);

        await SetLevelAsync(record, readWrite: false);

        byte[] second = [2, 2, 2];
        ApiResult<JsonElement> denied = await InitUploadAsync(device, "降级-之后.txt", second);
        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        Assert.Equal("PERMISSION_DENIED", denied.ErrorCode);

        // Rename is the same decision through a different endpoint.
        ApiResult<JsonElement> rename = await device.PostAsync<JsonElement>("/api/file/rename",
            new { shareId = ShareId, path = "已有.txt", newName = "改名.txt" });
        Assert.Equal(HttpStatusCode.Forbidden, rename.Status);

        // And raising it back restores the ability without touching the session.
        await SetLevelAsync(record, readWrite: true);
        Assert.True((await InitUploadAsync(device, "降级-恢复.txt", second)).IsSuccess);

        TestBase.Record("AUTH17-03",
            "读写 → 只读后同一会话的下一次写入立即被拒（上传初始化与重命名都 403），再改回读写后无需重配对即恢复");
    }

    // ------------------------------------------------------------------ AUTH17-04

    [Fact]
    public async Task A_running_upload_is_cancelled_when_write_access_is_withdrawn()
    {
        LanDiskClient device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite, "传输中降级手机");
        DeviceDefinition record = Host.Runtime.Devices.All().Single();

        byte[] content = new byte[4096];
        for (int i = 0; i < content.Length; i++) content[i] = (byte)(i % 251);

        ApiResult<JsonElement> init = await InitUploadAsync(device, "传输中.txt", content);
        Assert.True(init.IsSuccess, init.Raw);
        string uploadId = init.Body.GetProperty("uploadId").GetString()!;

        // First half lands.
        ApiResult<JsonElement> firstChunk = await ChunkAsync(device, uploadId, 0, content[..2048]);
        Assert.True(firstChunk.IsSuccess, firstChunk.Raw);

        // The operator takes write access away mid-transfer.
        await SetLevelAsync(record, readWrite: false);

        ApiResult<JsonElement> secondChunk = await ChunkAsync(device, uploadId, 2048, content[2048..]);
        Assert.Equal(HttpStatusCode.Forbidden, secondChunk.Status);
        Assert.Equal("UPLOAD_PERMISSION_REVOKED", secondChunk.ErrorCode);

        // The session is gone rather than merely refused, so nothing can be published later either.
        Assert.DoesNotContain(uploadId, Host.Runtime.Uploads.ListForDevice(record.Id).Select(u => u.Id));
        ApiResult<JsonElement> complete = await device.PostAsync<JsonElement>($"/api/uploads/{uploadId}/complete",
            new { sha256 = Sandbox.Sha256Hex(content), conflictPolicy = "fail" });
        Assert.False(complete.IsSuccess);
        Assert.False(File.Exists(Host.SandboxPath("传输中.txt")), "被降级取消的上传不得留下文件");

        TestBase.Record("AUTH17-04",
            "降级时正在进行的上传被取消：后续分块 403 UPLOAD_PERMISSION_REVOKED，上传会话从服务端移除，提交被拒且目标文件不存在（不会“悄悄写完”）");
    }

    // ------------------------------------------------------------------ AUTH17-05

    [Fact]
    public async Task The_phone_can_always_read_its_current_permissions()
    {
        LanDiskClient device = await PairDeviceAsync(DeviceAccessLevel.ReadOnly, "读权限手机");
        DeviceDefinition record = Host.Runtime.Devices.All().Single();

        JsonElement before = await device.MeAsync();
        Assert.False(before.GetProperty("permissions").GetProperty("writeFiles").GetBoolean());
        Assert.False(before.GetProperty("permissions").GetProperty("viewSystem").GetBoolean());
        Assert.False(before.GetProperty("shares")[0].GetProperty("canUpload").GetBoolean());

        await SetLevelAsync(record, readWrite: true);
        OperationOutcome<DeviceDefinition> granted = Host.Runtime.Admin.SetDeviceCapabilities(record.Id, ["SystemView"]);
        Assert.True(granted.Ok, granted.Message);

        // Same session, same cookie: the next read reports the new authority.
        JsonElement after = await device.MeAsync();
        Assert.True(after.GetProperty("permissions").GetProperty("writeFiles").GetBoolean());
        Assert.True(after.GetProperty("permissions").GetProperty("viewSystem").GetBoolean());
        Assert.True(after.GetProperty("shares")[0].GetProperty("canUpload").GetBoolean());

        // The heartbeat returns the same view, which is what the mobile UI polls.
        ApiResult<JsonElement> beat = await device.PostAsync<JsonElement>("/api/heartbeat", new { });
        Assert.True(beat.IsSuccess, beat.Raw);
        Assert.True(beat.Body.GetProperty("permissions").GetProperty("writeFiles").GetBoolean());

        TestBase.Record("AUTH17-05",
            "权限 UI 刷新能拿到服务器当前状态：同一会话下 /api/me 与 /api/heartbeat 都返回最新的 writeFiles/viewSystem 与共享 canUpload，手机无需重新配对即可刷新按钮可用性");
    }

    // ------------------------------------------------------------------ AUTH17-06

    [Fact]
    public async Task Permission_changes_never_rotate_the_session_epoch()
    {
        LanDiskClient device = await PairDeviceAsync(DeviceAccessLevel.ReadOnly, "epoch 手机");
        DeviceDefinition record = Host.Runtime.Devices.All().Single();
        string epoch = record.SessionEpoch;

        await SetLevelAsync(record, readWrite: true);
        OperationOutcome<DeviceDefinition> caps = Host.Runtime.Admin.SetDeviceCapabilities(record.Id, ["FileWrite", "NetworkManage"]);
        Assert.True(caps.Ok, caps.Message);
        await SetLevelAsync(record, readWrite: false);
        Host.Runtime.Admin.SetDeviceCapabilities(record.Id, []);

        Assert.Equal(epoch, Host.Runtime.Devices.Find(record.Id)!.SessionEpoch);
        // The session still works: a rotated epoch would have invalidated it.
        Assert.True((await device.MeAsync()).GetProperty("kind").GetString() == "device");

        TestBase.Record("AUTH17-06",
            "普通权限修改（读写开关、Capability 授予与收回）都不轮换 SessionEpoch：多次修改后 epoch 不变，旧会话继续有效；只有撤销才轮换");
    }

    [Fact]
    public async Task Revoking_a_device_still_kills_its_session_immediately()
    {
        LanDiskClient device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite, "被撤销手机");
        DeviceDefinition record = Host.Runtime.Devices.All().Single();
        string epoch = record.SessionEpoch;

        OperationOutcome<AdminOperations.DeviceRevocation> revoked = Host.Runtime.Admin.RevokeDevice(record.Id);
        Assert.True(revoked.Ok, revoked.Message);

        Assert.NotEqual(epoch, Host.Runtime.Devices.Find(record.Id)!.SessionEpoch);
        ApiResult<JsonElement> me = await device.GetAsync<JsonElement>("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.Status);

        TestBase.Record("AUTH17-07",
            "撤销仍然立即生效：SessionEpoch 轮换、token 哈希轮换，旧 cookie 的下一次 /api/me 直接 401（权限收紧与撤销是两件事）");
    }

    // ------------------------------------------------------------------ ONLINE17-01 .. 06

    [Fact]
    public async Task Any_authenticated_request_counts_as_activity()
    {
        LanDiskClient device = await PairDeviceAsync(DeviceAccessLevel.ReadOnly, "活动手机");
        DeviceDefinition record = Host.Runtime.Devices.All().Single();

        Host.Runtime.Devices.Touch(record.Id, null, DateTimeOffset.Now.AddMinutes(-10));
        Assert.Equal("offline", DeviceActivity.ToWire(DeviceActivity.Classify(Host.Runtime.Devices.Find(record.Id)!, DateTimeOffset.Now)));

        await device.GetAsync<JsonElement>("/api/browse?shareId=" + ShareId);

        DevicePresence presence = DeviceActivity.Classify(Host.Runtime.Devices.Find(record.Id)!, DateTimeOffset.Now);
        Assert.Equal(DevicePresence.Online, presence);

        TestBase.Record("ONLINE17-01",
            "任何认证 API 请求都更新 LastSeen：把最后活动时间人为置为 10 分钟前后，一次普通 /api/browse 就让该设备重新判定为 online");
    }

    [Fact]
    public async Task The_heartbeat_updates_activity_and_reports_presence()
    {
        LanDiskClient device = await PairDeviceAsync(DeviceAccessLevel.ReadOnly, "心跳手机");
        DeviceDefinition record = Host.Runtime.Devices.All().Single();

        Host.Runtime.Devices.Touch(record.Id, null, DateTimeOffset.Now.AddMinutes(-10));
        ApiResult<JsonElement> beat = await device.PostAsync<JsonElement>("/api/heartbeat", new { });

        Assert.True(beat.IsSuccess, beat.Raw);
        Assert.Equal("online", beat.Body.GetProperty("presence").GetProperty("state").GetString());
        Assert.Equal("online", DeviceActivity.ToWire(DeviceActivity.Classify(Host.Runtime.Devices.Find(record.Id)!, DateTimeOffset.Now)));

        TestBase.Record("ONLINE17-02",
            "POST /api/heartbeat 更新 LastSeen 并返回 presence.state=online（手机每 15 秒一次；服务端每分钟才落盘一次，不会每个心跳写磁盘）");
    }

    [Fact]
    public void Presence_thresholds_are_exactly_the_documented_ones()
    {
        var device = new DeviceDefinition { Id = "d", Name = "阈值手机" };
        DateTimeOffset now = DateTimeOffset.Now;

        device.LastSeenAt = now.AddSeconds(-44);
        Assert.Equal(DevicePresence.Online, DeviceActivity.Classify(device, now));           // ONLINE17-03

        device.LastSeenAt = now.AddSeconds(-46);
        Assert.Equal(DevicePresence.RecentlyActive, DeviceActivity.Classify(device, now));   // ONLINE17-04
        device.LastSeenAt = now.AddMinutes(-4.9);
        Assert.Equal(DevicePresence.RecentlyActive, DeviceActivity.Classify(device, now));

        device.LastSeenAt = now.AddMinutes(-5.1);
        Assert.Equal(DevicePresence.Offline, DeviceActivity.Classify(device, now));          // ONLINE17-05

        device.LastSeenAt = now;
        Assert.Equal(DevicePresence.Online, DeviceActivity.Classify(device, now));
        device.Revoked = true;
        Assert.Equal(DevicePresence.Revoked, DeviceActivity.Classify(device, now));          // ONLINE17-06

        TestBase.Record("ONLINE17-03", "45 秒以内判定为 online（44 秒 online，46 秒 recently-active，阈值有测试钉住）");
        TestBase.Record("ONLINE17-04", "最近活动区间正确：45 秒~5 分钟为 recently-active（含 4.9 分钟边界）");
        TestBase.Record("ONLINE17-05", "超过 5 分钟为 offline（5.1 分钟）");
        TestBase.Record("ONLINE17-06", "被撤销的设备永远不是 online：即使 LastSeenAt 是此刻，也判定为 revoked");

        Assert.Equal("recently-active", DeviceActivity.ToWire(DevicePresence.RecentlyActive));
    }

    [Fact]
    public async Task Device_presence_and_capabilities_are_visible_to_the_windows_side()
    {
        LanDiskClient device = await PairDeviceAsync(DeviceAccessLevel.ReadOnly, "看板手机");
        DeviceDefinition record = Host.Runtime.Devices.All().Single();
        await device.PostAsync<JsonElement>("/api/heartbeat", new { });

        ApiResult<JsonElement> list = await Admin.GetAsync<JsonElement>("/api/admin/devices");
        Assert.True(list.IsSuccess, list.Raw);
        JsonElement row = list.Body.GetProperty("devices").EnumerateArray().Single();
        Assert.Equal("online", row.GetProperty("presence").GetProperty("state").GetString());
        Assert.Equal("None", row.GetProperty("capabilities").GetString());
        Assert.Empty(row.GetProperty("capabilityList").EnumerateArray());

        ApiResult<JsonElement> granted = await Admin.PatchAsync<JsonElement>($"/api/admin/devices/{record.Id}",
            new { capabilities = new[] { "SystemView", "ExtensionExecute" } });
        Assert.True(granted.IsSuccess, granted.Raw);
        Assert.Equal(2, granted.Body.GetProperty("capabilities").EnumerateArray().Count());

        AuditEntry? entry = Host.Runtime.Audit.Tail(50).FirstOrDefault(e => e.Action == "capabilities");
        Assert.NotNull(entry);
        Assert.Contains("SystemView", entry!.Detail!, StringComparison.Ordinal);

        TestBase.Record("AUTH17-05b",
            "Windows 侧设备列表带 presence 与 capabilities：HTTP PATCH 可独立授予 SystemView/ExtensionExecute，返回完整能力集合，并写入 action=capabilities 审计");
    }
}
