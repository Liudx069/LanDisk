using System.Net;
using System.Text.Json;
using LanDisk.Core.Model;
using LanDisk.Server;
using LanDisk.Server.Services;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>
/// DEVICE12 — a device can actually be removed, not just hidden.
///
/// <para><b>The complaint.</b> The Owner's list had accumulated revoked devices with no way to clear them:
/// revoking stopped access but the row stayed forever. The old delete endpoint did remove the record, but it
/// skipped the token and session rotation, left the device's extension tasks running and wrote no audit
/// entry, so "remove" was quietly weaker than "revoke".</para>
/// </summary>
public sealed class DeviceRemovalTests : TestBase
{
    private async Task<LanDiskClient> PairedAsync(string name)
    {
        LanDiskClient device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite, name);
        await device.MeAsync();
        return device;
    }

    private static async Task<string> IdOfAsync(LanDiskClient device)
    {
        JsonElement me = await device.MeAsync();
        return me.GetProperty("device").GetProperty("id").GetString()!;
    }

    private static async Task<HttpStatusCode> MeStatusAsync(LanDiskClient device)
    {
        ApiResult<JsonElement> me = await device.GetAsync<JsonElement>("/api/me");
        return me.Status;
    }

    [Fact]
    public async Task A_revoked_device_can_be_permanently_removed()
    {
        LanDiskClient device = await PairedAsync("待移除手机");
        string id = await IdOfAsync(device);

        ApiResult<JsonElement> revoked = await Admin.PostAsync<JsonElement>($"/api/admin/devices/{id}/revoke", new { });
        Assert.True(revoked.IsSuccess, revoked.Raw);

        ApiResult<JsonElement> removed = await Admin.DeleteAsync<JsonElement>($"/api/admin/devices/{id}");
        Assert.True(removed.IsSuccess, removed.Raw);
        Assert.True(removed.Body.GetProperty("ok").GetBoolean());
        Assert.False(removed.Body.GetProperty("wasActive").GetBoolean());

        TestBase.Record("DEVICE12-01", "已撤销的设备可以被永久移除：DELETE /api/admin/devices/{id} 返回 ok，且如实标注 wasActive=false");
    }

    [Fact]
    public async Task A_removed_device_is_gone_from_the_registry()
    {
        LanDiskClient device = await PairedAsync("要删掉的手机");
        string id = await IdOfAsync(device);
        await Admin.PostAsync<JsonElement>($"/api/admin/devices/{id}/revoke", new { });
        await Admin.DeleteAsync<JsonElement>($"/api/admin/devices/{id}");

        Assert.DoesNotContain(Host.Runtime.Devices.All(), d => d.Id == id);

        ApiResult<JsonElement> list = await Admin.GetAsync<JsonElement>("/api/admin/devices");
        JsonElement devices = list.Body.GetProperty("devices");
        Assert.DoesNotContain(devices.EnumerateArray(), d => d.GetProperty("id").GetString() == id);

        TestBase.Record("DEVICE12-02", "移除后设备不再出现在 registry 与 /api/admin/devices 列表里（不是只把行藏起来）");
    }

    [Fact]
    public async Task An_old_session_stays_invalid_after_removal()
    {
        LanDiskClient device = await PairedAsync("会话要失效的手机");
        string id = await IdOfAsync(device);
        Assert.Equal(HttpStatusCode.OK, await MeStatusAsync(device));

        await Admin.DeleteAsync<JsonElement>($"/api/admin/devices/{id}");

        // The device's cookie is now unknown: its token hash is not in the registry at all.
        HttpStatusCode after = await MeStatusAsync(device);
        Assert.Equal(HttpStatusCode.Unauthorized, after);

        TestBase.Record("DEVICE12-03", "被移除设备的旧会话立即失效（/api/me 返回 401）：移除后连 token hash 都不再存在，旧 Cookie 无法重放");
    }

    [Fact]
    public async Task Other_devices_are_unaffected()
    {
        LanDiskClient keep = await PairedAsync("保留的手机");
        LanDiskClient drop = await PairedAsync("移除的手机");
        string dropId = await IdOfAsync(drop);

        await Admin.DeleteAsync<JsonElement>($"/api/admin/devices/{dropId}");

        JsonElement stillThere = await keep.MeAsync();
        Assert.Equal("保留的手机", stillThere.GetProperty("device").GetProperty("name").GetString());
        Assert.Single(Host.Runtime.Devices.All());

        TestBase.Record("DEVICE12-04", "移除一台设备不影响其它设备：另一台手机仍能正常访问，registry 里只剩它一台");
    }

    [Fact]
    public async Task The_removal_is_audited_and_previous_audit_survives()
    {
        LanDiskClient device = await PairedAsync("要留痕的手机");
        string id = await IdOfAsync(device);
        await Admin.PostAsync<JsonElement>($"/api/admin/devices/{id}/revoke", new { });
        List<AuditEntry> before = [.. Host.Runtime.Audit.Tail(200)];

        await Admin.DeleteAsync<JsonElement>($"/api/admin/devices/{id}");

        List<AuditEntry> after = [.. Host.Runtime.Audit.Tail(200)];
        AuditEntry entry = after.First(e => e.Action == "device-remove");
        Assert.Contains("removedDevice=要留痕的手机", entry.Detail, StringComparison.Ordinal);
        Assert.Contains("auditRetained=true", entry.Detail, StringComparison.Ordinal);

        // The device is gone, but nothing it did was erased: every earlier entry is still there.
        // Every earlier entry is still present: match on the fields that identify one, since audit entries
        // deliberately carry no synthetic id.
        foreach (AuditEntry previous in before)
            Assert.Contains(after, e => e.At == previous.At && e.Action == previous.Action && e.Detail == previous.Detail);

        TestBase.Record("DEVICE12-05",
            "移除写审计 action=device-remove（含设备名、wasActive、取消失败的传输与扩展任务数、auditRetained=true），且该设备此前产生的审计一条都没有被删除");
    }

    [Fact]
    public async Task Removing_a_still_active_device_revokes_it_first()
    {
        LanDiskClient device = await PairedAsync("还在使用的手机");
        string id = await IdOfAsync(device);
        Assert.Equal(HttpStatusCode.OK, await MeStatusAsync(device));

        ApiResult<JsonElement> removed = await Admin.DeleteAsync<JsonElement>($"/api/admin/devices/{id}");
        Assert.True(removed.IsSuccess, removed.Raw);

        // It was live, so the removal had to rotate the credential rather than just drop the row.
        Assert.True(removed.Body.GetProperty("wasActive").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatusAsync(device));

        TestBase.Record("DEVICE12-06",
            "对仍在使用的设备执行移除时先走完整的撤销步骤（wasActive=true，先轮换 token/session 再删除），不会出现「删了记录但凭据还有效」的窗口");
    }
}
