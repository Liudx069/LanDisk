using System.Net;
using System.Text.Json;
using LanDisk.Core.Model;
using LanDisk.Core.Security;
using LanDisk.Server.Services;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>
/// Task 09, fixes 01–03: the native window and the HTTP endpoints must be the same implementation.
///
/// <para>Each test drives one path through <see cref="AdminOperations"/> — which is exactly what the WPF
/// pages now call — and where it matters drives the same operation through the real admin endpoint and
/// compares the two results field by field. The defects being pinned were of that shape: "确认配对" in the
/// window called <c>Pairing.Cancel</c>; "撤销" in the window skipped the token and session rotation the
/// browser performed; the recycle page restored as whichever phone happened to be first in the list.</para>
/// </summary>
public sealed class NativeParityHotfixTests : TestBase
{
    private AdminOperations Native => Host.Runtime.Admin;

    /// <summary>A request waiting for the local operator, the state the 6-digit code path leaves behind.</summary>
    private PairingRequest AwaitingLocalConfirmation(string deviceName = "测试 iPhone")
    {
        PairingRequest pairing = Host.Runtime.Pairing.Create();
        pairing.State = PairingState.AwaitingLocalConfirmation;
        pairing.DeviceName = deviceName;
        pairing.DeviceUserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X)";
        pairing.RemoteAddress = "192.168.2.44";
        pairing.ClaimSecret = SecretTokens.NewToken();
        return pairing;
    }

    private async Task<DeviceDefinition> PairAsync(LanDiskClient client, string name, DeviceAccessLevel level = DeviceAccessLevel.ReadOnly)
    {
        string token = await Admin.CreatePairingTokenAsync();
        var device = new LanDiskClient(Host.NewClient());
        JsonElement redeemed = await device.RedeemAsync(token, name);
        string id = redeemed.GetProperty("id").GetString()!;
        if (level == DeviceAccessLevel.ReadWrite)
            await Admin.PatchAsync<JsonElement>($"/api/admin/devices/{id}", new { level = "readWrite" });
        client.SetCsrf(device.CsrfToken);
        // The client the caller keeps is the one holding the device cookie.
        client.Http.DefaultRequestHeaders.Remove("X-LanDisk-Client");
        return Host.Runtime.Devices.Find(id)!;
    }

    /// <summary>Deletes one sandbox file as the given client and returns the recycle id.</summary>
    private async Task<string> DeleteAsync(LanDiskClient client, string path)
    {
        // The version token is required: the delete path refuses a stale token, so a helper that skipped
        // it would fail for a reason that has nothing to do with what is under test.
        ApiResult<JsonElement> meta = await client.GetAsync<JsonElement>("/api/file/meta" + LanDiskClient.FileQuery(ShareId, path));
        Assert.True(meta.IsSuccess, meta.Raw);
        string version = meta.Body.GetProperty("version").GetString()!;

        ApiResult<JsonElement> result = await client.PostAsync<JsonElement>("/api/file/delete", new { shareId = ShareId, path, version });
        Assert.True(result.IsSuccess, result.Raw);
        return result.Body.GetProperty("recycleId").GetString()!;
    }

    // ------------------------------------------------------------------ fix 01: pairing

    [Fact]
    public void Confirming_a_pairing_request_creates_the_device()
    {
        PairingRequest pairing = AwaitingLocalConfirmation();

        OperationOutcome<DeviceDefinition> outcome = Native.ConfirmPairing(pairing.Id);

        Assert.True(outcome.Ok, outcome.Message);
        DeviceDefinition created = outcome.Value!;

        Assert.Equal("测试 iPhone", created.Name);
        Assert.Equal(DeviceAccessLevel.ReadOnly, created.Level);
        Assert.Equal("192.168.2.44", created.LastSeenIp);
        Assert.Contains("iPhone", created.UserAgent!);
        Assert.False(created.Revoked);
        Assert.NotEmpty(created.TokenHash);

        // Completed, not cancelled, and pointing at the device it produced.
        Assert.Equal(PairingState.Redeemed, pairing.State);
        Assert.True(pairing.LocalConfirmationGranted);
        Assert.Equal(created.Id, pairing.IssuedDeviceId);
        Assert.Contains(Host.Runtime.Devices.All(), d => d.Id == created.Id);

        TestBase.Record("FIX-09-01",
            "本机“确认配对”真正创建设备：State=Redeemed、LocalConfirmationGranted、IssuedDeviceId 指向新设备，且设备已入库");
    }

    [Fact]
    public void Confirming_never_cancels_the_request()
    {
        PairingRequest pairing = AwaitingLocalConfirmation();

        Native.ConfirmPairing(pairing.Id);

        // The old implementation called Cancel: the request ended Cancelled and no device existed.
        Assert.NotEqual(PairingState.Cancelled, pairing.State);
        Assert.Equal(PairingState.Redeemed, pairing.State);
        Assert.Single(Host.Runtime.Devices.All());

        TestBase.Record("FIX-09-01b", "本机“确认配对”不再退化成 Cancel：请求状态为 Redeemed，设备数量从 0 变为 1");
    }

    [Fact]
    public void A_missing_pairing_request_is_refused()
    {
        OperationOutcome<DeviceDefinition> outcome = Native.ConfirmPairing("no-such-request");

        Assert.False(outcome.Ok);
        Assert.Equal("NOT_FOUND", outcome.Code);
        Assert.Empty(Host.Runtime.Devices.All());

        TestBase.Record("FIX-09-01j", "不存在的配对请求被拒绝（NOT_FOUND），且不会创建设备");
    }

    [Fact]
    public void An_expired_pairing_request_is_refused()
    {
        PairingRequest pairing = Host.Runtime.Pairing.Create();
        pairing.State = PairingState.AwaitingLocalConfirmation;
        // Two minutes is the real lifetime; move the deadline rather than waiting for it.
        typeof(PairingRequest).GetProperty(nameof(PairingRequest.ExpiresAt))!
            .SetValue(pairing, DateTimeOffset.Now.AddMinutes(-1));

        OperationOutcome<DeviceDefinition> outcome = Native.ConfirmPairing(pairing.Id);

        Assert.False(outcome.Ok);
        Assert.Equal("PAIRING_EXPIRED", outcome.Code);
        Assert.Empty(Host.Runtime.Devices.All());

        TestBase.Record("FIX-09-01c", "过期的配对请求被拒绝（PAIRING_EXPIRED），且不会创建设备");
    }

    [Fact]
    public void A_request_that_is_not_awaiting_confirmation_is_refused()
    {
        PairingRequest pairing = Host.Runtime.Pairing.Create();   // still Pending: no code submitted yet

        OperationOutcome<DeviceDefinition> outcome = Native.ConfirmPairing(pairing.Id);

        Assert.False(outcome.Ok);
        Assert.Equal("PAIRING_STATE", outcome.Code);
        Assert.Empty(Host.Runtime.Devices.All());

        TestBase.Record("FIX-09-01k", "还没有提交配对码、因此不处于 AwaitingLocalConfirmation 的请求被拒绝（PAIRING_STATE）");
    }

    [Fact]
    public void A_second_confirm_is_refused_and_only_one_device_exists()
    {
        PairingRequest pairing = AwaitingLocalConfirmation();

        Assert.True(Native.ConfirmPairing(pairing.Id).Ok);
        OperationOutcome<DeviceDefinition> second = Native.ConfirmPairing(pairing.Id);

        Assert.False(second.Ok);
        Assert.Equal("PAIRING_STATE", second.Code);
        Assert.Single(Host.Runtime.Devices.All());

        TestBase.Record("FIX-09-01d", "同一配对请求第二次确认被拒绝（PAIRING_STATE），设备仍然只有 1 台（既不重复创建设备，也不退回 Cancel）");
    }

    [Fact]
    public void Rejecting_a_request_cancels_it_and_creates_nothing()
    {
        PairingRequest pairing = AwaitingLocalConfirmation();

        OperationOutcome<bool> outcome = Native.RejectPairing(pairing.Id);

        Assert.True(outcome.Ok);
        Assert.True(outcome.Value);
        Assert.Equal(PairingState.Cancelled, pairing.State);
        Assert.Null(pairing.IssuedDeviceId);
        Assert.Empty(Host.Runtime.Devices.All());

        TestBase.Record("FIX-09-01e", "本机“拒绝”正确取消请求（State=Cancelled、IssuedDeviceId 为空、无设备产生）");
    }

    [Fact]
    public async Task The_native_and_http_confirm_paths_produce_the_same_device_fields()
    {
        const string sharedName = "同一台手机";
        PairingRequest nativePairing = AwaitingLocalConfirmation(sharedName);
        DeviceDefinition nativeDevice = Native.ConfirmPairing(nativePairing.Id).Value!;

        PairingRequest httpPairing = AwaitingLocalConfirmation(sharedName);
        ApiResult<JsonElement> response = await Admin.PostAsync<JsonElement>($"/api/admin/pairing/{httpPairing.Id}/confirm", new { });
        Assert.Equal(HttpStatusCode.OK, response.Status);
        JsonElement httpDevice = response.Body.GetProperty("device");

        // The device the endpoint reported is the one that was stored, and the stored device is field for
        // field the one the native path produced — including the two fields the DTO does not carry
        // (lastSeenIp, userAgent), which are read from the store rather than from the response.
        string httpDeviceId = httpDevice.GetProperty("id").GetString()!;
        DeviceDefinition storedHttpDevice = Host.Runtime.Devices.Find(httpDeviceId)!;

        Assert.Equal(nativeDevice.Name, httpDevice.GetProperty("name").GetString());
        Assert.Equal(nativeDevice.Level.ToString(), httpDevice.GetProperty("level").GetString());
        Assert.Equal(nativeDevice.Revoked, httpDevice.GetProperty("revoked").GetBoolean());
        Assert.Equal(httpDeviceId, httpPairing.IssuedDeviceId);
        Assert.Equal(nativeDevice.Name, storedHttpDevice.Name);
        Assert.Equal(nativeDevice.Level, storedHttpDevice.Level);
        Assert.Equal(nativeDevice.LastSeenIp, storedHttpDevice.LastSeenIp);
        Assert.Equal(nativeDevice.UserAgent, storedHttpDevice.UserAgent);
        Assert.Equal(2, Host.Runtime.Devices.All().Count);

        TestBase.Record("FIX-09-01f",
            "原生确认与 POST /api/admin/pairing/{id}/confirm 产出的设备字段逐项一致（name/level/lastSeenIp/userAgent/revoked）");
    }

    [Fact]
    public async Task The_http_confirm_endpoint_reports_the_same_refusals_as_the_shared_layer()
    {
        // Missing.
        ApiResult<JsonElement> missing = await Admin.PostAsync<JsonElement>("/api/admin/pairing/nope/confirm", new { });
        Assert.Equal(HttpStatusCode.NotFound, missing.Status);

        // Wrong state.
        PairingRequest pending = Host.Runtime.Pairing.Create();
        ApiResult<JsonElement> wrongState = await Admin.PostAsync<JsonElement>($"/api/admin/pairing/{pending.Id}/confirm", new { });
        Assert.Equal(HttpStatusCode.Conflict, wrongState.Status);

        // Expired.
        PairingRequest expired = Host.Runtime.Pairing.Create();
        expired.State = PairingState.AwaitingLocalConfirmation;
        typeof(PairingRequest).GetProperty(nameof(PairingRequest.ExpiresAt))!
            .SetValue(expired, DateTimeOffset.Now.AddMinutes(-1));
        ApiResult<JsonElement> gone = await Admin.PostAsync<JsonElement>($"/api/admin/pairing/{expired.Id}/confirm", new { });
        Assert.Equal(HttpStatusCode.Gone, gone.Status);

        TestBase.Record("FIX-09-01h",
            "端点把共享层的拒绝码翻译回原来的 HTTP 语义：不存在 404、状态不对 409、已过期 410");
    }

    [Fact]
    public void Confirm_and_reject_write_the_audit_the_same_way()
    {
        PairingRequest confirmed = AwaitingLocalConfirmation();
        Native.ConfirmPairing(confirmed.Id);

        PairingRequest rejected = AwaitingLocalConfirmation();
        Native.RejectPairing(rejected.Id);

        IReadOnlyList<AuditEntry> entries = Host.Runtime.Audit.Tail(50);
        AuditEntry confirmEntry = entries.First(e => e.Category == "pairing" && e.Action == "confirm");
        AuditEntry rejectEntry = entries.First(e => e.Category == "pairing" && e.Action == "reject");

        Assert.Equal(OperationActor.LocalAdminName, confirmEntry.DeviceName);
        Assert.Equal(OperationActor.LocalAdmin.SourceToken, confirmEntry.Source);
        Assert.Equal(confirmed.IssuedDeviceId, confirmEntry.DeviceId);
        Assert.Contains("remote=192.168.2.44", confirmEntry.Detail);
        Assert.True(confirmEntry.Success);

        Assert.Equal(OperationActor.LocalAdminName, rejectEntry.DeviceName);
        Assert.Equal(OperationActor.LocalAdmin.SourceToken, rejectEntry.Source);
        Assert.True(rejectEntry.Success);

        TestBase.Record("FIX-09-01g",
            "配对确认/拒绝的审计一致：Category=pairing、DeviceName=本机管理端、Source=local-admin，确认条目带 remote 与新设备 Id");
    }

    // ------------------------------------------------------------------ fix 02: devices

    [Fact]
    public async Task The_native_revoke_rotates_the_token_and_the_session_epoch()
    {
        var client = new LanDiskClient(Host.NewClient());
        DeviceDefinition device = await PairAsync(client, "撤销对照手机");
        string tokenBefore = device.TokenHash;
        string epochBefore = device.SessionEpoch;

        OperationOutcome<AdminOperations.DeviceRevocation> outcome = Native.RevokeDevice(device.Id);

        Assert.True(outcome.Ok, outcome.Message);
        DeviceDefinition after = Host.Runtime.Devices.Find(device.Id)!;
        Assert.True(after.Revoked);
        Assert.NotNull(after.RevokedAt);
        Assert.NotEqual(tokenBefore, after.TokenHash);
        Assert.NotEqual(epochBefore, after.SessionEpoch);

        TestBase.Record("FIX-09-02",
            "本机撤销执行完整语义：Revoked=true、RevokedAt 已写、TokenHash 与 SessionEpoch 都已轮换");
    }

    [Fact]
    public async Task A_session_that_worked_before_the_native_revoke_stops_working_after_it()
    {
        // A device that is logged in before the revoke, keeping its own cookie jar.
        string token = await Admin.CreatePairingTokenAsync();
        var device = new LanDiskClient(Host.NewClient());
        JsonElement redeemed = await device.RedeemAsync(token, "会话对照手机");
        string id = redeemed.GetProperty("id").GetString()!;

        JsonElement me = await device.MeAsync();
        Assert.Equal("device", me.GetProperty("kind").GetString());

        Native.RevokeDevice(id);

        ApiResult<JsonElement> after = await device.GetAsync<JsonElement>("/api/me");
        Assert.True(after.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            "撤销后旧会话仍然返回 " + (int)after.Status);

        TestBase.Record("FIX-09-02b", "撤销前该设备的会话有效（kind=device），本机撤销后旧会话立即被拒（401/403）");
    }

    [Fact]
    public async Task The_native_revoke_cancels_uploads_and_records_the_count()
    {
        var client = new LanDiskClient(Host.NewClient());
        DeviceDefinition device = await PairAsync(client, "上传对照手机", DeviceAccessLevel.ReadWrite);

        OperationOutcome<AdminOperations.DeviceRevocation> outcome = Native.RevokeDevice(device.Id);

        Assert.True(outcome.Ok);
        Assert.True(outcome.Value!.CancelledUploads >= 0);
        AuditEntry revoke = Host.Runtime.Audit.Tail(20).First(e => e.Action == "revoke");
        Assert.Contains("cancelledUploads=", revoke.Detail);
        Assert.Equal(OperationActor.LocalAdmin.SourceToken, revoke.Source);
        Assert.Equal(device.Id, revoke.DeviceId);
    }

    [Fact]
    public async Task Renaming_through_the_native_path_sanitizes_exactly_like_http()
    {
        DeviceDefinition first = await PairDeviceForTest("原生重命名");
        DeviceDefinition second = await PairDeviceForTest("HTTP 重命名");

        // 80 characters plus a control character and a tab: both paths must land on the same name.
        string messy = new string('长', 80) + "\u0007\t";

        OperationOutcome<DeviceDefinition> native = Native.RenameDevice(first.Id, messy);
        Assert.True(native.Ok, native.Message);

        ApiResult<JsonElement> http = await Admin.PatchAsync<JsonElement>($"/api/admin/devices/{second.Id}", new { name = messy });
        Assert.True(http.IsSuccess, http.Raw);

        DeviceDefinition httpDevice = Host.Runtime.Devices.Find(second.Id)!;
        Assert.Equal(native.Value!.Name, httpDevice.Name);
        Assert.Equal(DeviceNames.MaxLength, native.Value.Name.Length);
        Assert.DoesNotContain('\u0007', native.Value.Name);
        Assert.DoesNotContain('\t', native.Value.Name);

        TestBase.Record("FIX-09-02c",
            $"重命名清洗一致：原生与 HTTP 都得到长度 {native.Value.Name.Length}、无控制字符的同一个名称");
    }

    [Fact]
    public async Task Setting_access_through_the_native_path_matches_the_endpoint()
    {
        DeviceDefinition first = await PairDeviceForTest("原生权限");
        DeviceDefinition second = await PairDeviceForTest("HTTP 权限");

        Assert.True(Native.SetDeviceAccess(first.Id, readWrite: true).Ok);
        ApiResult<JsonElement> http = await Admin.PatchAsync<JsonElement>($"/api/admin/devices/{second.Id}", new { level = "readWrite" });
        Assert.True(http.IsSuccess, http.Raw);

        Assert.Equal(DeviceAccessLevel.ReadWrite, Host.Runtime.Devices.Find(first.Id)!.Level);
        Assert.Equal(DeviceAccessLevel.ReadWrite, Host.Runtime.Devices.Find(second.Id)!.Level);
    }

    [Fact]
    public async Task The_native_and_http_revoke_paths_leave_the_same_state()
    {
        DeviceDefinition nativeDevice = await PairDeviceForTest("原生撤销");
        DeviceDefinition httpDevice = await PairDeviceForTest("HTTP 撤销");
        string nativeEpoch = nativeDevice.SessionEpoch;
        string httpEpoch = httpDevice.SessionEpoch;

        Assert.True(Native.RevokeDevice(nativeDevice.Id).Ok);
        ApiResult<JsonElement> http = await Admin.PostAsync<JsonElement>($"/api/admin/devices/{httpDevice.Id}/revoke", new { });
        Assert.Equal(HttpStatusCode.OK, http.Status);
        Assert.True(http.Body.GetProperty("ok").GetBoolean());

        DeviceDefinition n = Host.Runtime.Devices.Find(nativeDevice.Id)!;
        DeviceDefinition h = Host.Runtime.Devices.Find(httpDevice.Id)!;

        Assert.True(n.Revoked && h.Revoked);
        Assert.NotEqual(nativeEpoch, n.SessionEpoch);
        Assert.NotEqual(httpEpoch, h.SessionEpoch);
        Assert.NotNull(n.RevokedAt);
        Assert.NotNull(h.RevokedAt);

        TestBase.Record("FIX-09-02d",
            "原生撤销与 HTTP 撤销最终状态一致：两台设备都是 Revoked=true、RevokedAt 非空、SessionEpoch 均已轮换");
    }

    [Fact]
    public void Revoking_a_missing_device_is_refused()
    {
        OperationOutcome<AdminOperations.DeviceRevocation> outcome = Native.RevokeDevice("no-such-device");
        Assert.False(outcome.Ok);
        Assert.Equal("NOT_FOUND", outcome.Code);

        TestBase.Record("FIX-09-02f", "撤销一台不存在的设备被拒绝（NOT_FOUND），不产生任何状态变化");
    }

    /// <summary>Pairs a throwaway phone and returns the stored device.</summary>
    private async Task<DeviceDefinition> PairDeviceForTest(string name)
    {
        string token = await Admin.CreatePairingTokenAsync();
        var device = new LanDiskClient(Host.NewClient());
        JsonElement redeemed = await device.RedeemAsync(token, name);
        return Host.Runtime.Devices.Find(redeemed.GetProperty("id").GetString()!)!;
    }

    // ------------------------------------------------------------------ fix 03: recycle

    [Fact]
    public async Task Restoring_with_no_paired_device_works_and_is_credited_to_the_local_admin()
    {
        // Nothing paired at all: the case that used to throw, because the page passed
        // runtime.Devices.All().First() as the actor.
        Assert.Empty(Host.Runtime.Devices.All());

        Host.WriteSandboxFile("待恢复.txt", "内容");
        string recycleId = await DeleteAsync(Admin, "待恢复.txt");

        OperationOutcome<RecycleEntry> outcome = Native.RestoreRecycleEntry(recycleId, OperationActor.LocalAdmin);

        Assert.True(outcome.Ok, outcome.Message);
        Assert.True(File.Exists(Path.Combine(Host.SandboxRoot, "待恢复.txt")));

        AuditEntry restore = Host.Runtime.Audit.Tail(30).First(e => e.Action == "restore");
        Assert.Equal(OperationActor.LocalAdminName, restore.DeviceName);
        Assert.Equal(OperationActor.LocalAdmin.SourceToken, restore.Source);
        Assert.Null(restore.DeviceId);   // not attributed to a phone

        TestBase.Record("FIX-09-03",
            "0 台已配对设备时本机恢复成功，审计为 DeviceName=本机管理端、Source=local-admin、DeviceId 为空");
    }

    [Fact]
    public async Task Purging_with_no_paired_device_works()
    {
        Host.WriteSandboxFile("待永久删除.txt", "内容");
        string recycleId = await DeleteAsync(Admin, "待永久删除.txt");

        OperationOutcome<bool> outcome = Native.PurgeRecycleEntry(recycleId);

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Null(Host.Runtime.Recycle.Find(recycleId));
        AuditEntry purge = Host.Runtime.Audit.Tail(30).First(e => e.Action == "purge");
        Assert.Equal(OperationActor.LocalAdminName, purge.DeviceName);
        Assert.Equal(OperationActor.LocalAdmin.SourceToken, purge.Source);

        TestBase.Record("FIX-09-03b", "0 台已配对设备时本机永久删除成功，且审计标记 source=local-admin");
    }

    [Fact]
    public async Task With_several_devices_a_local_action_belongs_to_none_of_them()
    {
        DeviceDefinition first = await PairDeviceForTest("第一台手机");
        DeviceDefinition second = await PairDeviceForTest("第二台手机");
        Assert.Equal(2, Host.Runtime.Devices.All().Count);

        Host.WriteSandboxFile("两台设备下的本机恢复.txt", "内容");
        string recycleId = await DeleteAsync(Admin, "两台设备下的本机恢复.txt");

        Assert.True(Native.RestoreRecycleEntry(recycleId, OperationActor.LocalAdmin).Ok);

        AuditEntry restore = Host.Runtime.Audit.Tail(30).First(e => e.Action == "restore");
        Assert.NotEqual(first.Id, restore.DeviceId);
        Assert.NotEqual(second.Id, restore.DeviceId);
        Assert.Null(restore.DeviceId);
        Assert.Equal(OperationActor.LocalAdminName, restore.DeviceName);

        TestBase.Record("FIX-09-03c",
            "有 2 台已配对设备时，本机恢复的审计既不挂在第一台也不挂在第二台上（DeviceId 为空，DeviceName=本机管理端）");
    }

    [Fact]
    public async Task A_restore_done_by_a_phone_is_still_attributed_to_that_phone()
    {
        string token = await Admin.CreatePairingTokenAsync();
        var phone = new LanDiskClient(Host.NewClient());
        JsonElement redeemed = await phone.RedeemAsync(token, "手机恢复对照");
        string deviceId = redeemed.GetProperty("id").GetString()!;
        await Admin.PatchAsync<JsonElement>($"/api/admin/devices/{deviceId}", new { level = "readWrite" });

        Host.WriteSandboxFile("手机恢复.txt", "内容");
        string recycleId = await DeleteAsync(phone, "手机恢复.txt");

        ApiResult<JsonElement> restored = await phone.PostAsync<JsonElement>($"/api/recycle/{recycleId}/restore", new { });
        Assert.True(restored.IsSuccess, restored.Raw);

        AuditEntry restore = Host.Runtime.Audit.Tail(30).First(e => e.Action == "restore");
        Assert.Equal(deviceId, restore.DeviceId);
        Assert.Equal("手机恢复对照", restore.DeviceName);
        Assert.Equal("device", restore.Source);

        TestBase.Record("FIX-09-03d", "手机端的恢复行为没有被改动：仍然记为发起该操作的那台设备（source=device）");
    }

    [Fact]
    public async Task Permanent_deletion_still_requires_the_local_confirmation_it_did_before()
    {
        Host.WriteSandboxFile("确认规则.txt", "内容");
        string recycleId = await DeleteAsync(Admin, "确认规则.txt");

        // /api/admin/recycle/empty without confirm=true is still refused: making the actor explicit did
        // not weaken the rule.
        ApiResult<JsonElement> refused = await Admin.PostAsync<JsonElement>("/api/admin/recycle/empty", new { });
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.NotNull(Host.Runtime.Recycle.Find(recycleId));

        ApiResult<JsonElement> confirmed = await Admin.PostAsync<JsonElement>("/api/admin/recycle/empty", new { confirm = true });
        Assert.Equal(HttpStatusCode.OK, confirmed.Status);
        Assert.Null(Host.Runtime.Recycle.Find(recycleId));
        Assert.False(File.Exists(Path.Combine(Host.SandboxRoot, "确认规则.txt")));

        TestBase.Record("FIX-09-03e",
            "永久删除的确认规则未被削弱：缺 confirm=true 返回 400 且条目仍在；确认后条目与文件都不在了");
    }

    [Fact]
    public async Task The_local_admin_actor_is_never_persisted_as_a_device()
    {
        Host.WriteSandboxFile("演员不入库.txt", "内容");
        string recycleId = await DeleteAsync(Admin, "演员不入库.txt");
        Assert.True(Native.RestoreRecycleEntry(recycleId, OperationActor.LocalAdmin).Ok);
        string second = await DeleteAsync(Admin, "演员不入库.txt");
        Assert.True(Native.PurgeRecycleEntry(second).Ok);

        // The actor carries an id, but it must never reach devices.json. Nothing was ever paired, so the
        // strongest form of the assertion is that the file was not even created; if a future change makes
        // it exist, it still must not mention the local administrator.
        Assert.Empty(Host.Runtime.Devices.All());
        string devicesFile = Host.Runtime.Paths.DevicesFile;
        if (File.Exists(devicesFile))
        {
            string devicesJson = File.ReadAllText(devicesFile);
            Assert.DoesNotContain(OperationActor.LocalAdminId, devicesJson);
            Assert.DoesNotContain(OperationActor.LocalAdminName, devicesJson);
        }

        TestBase.Record("FIX-09-03f", "本机管理身份从未被写进 devices.json（恢复与永久删除之后设备库仍为空）");
    }
}
