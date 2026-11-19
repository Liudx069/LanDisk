using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using LanDisk.Core.Model;
using LanDisk.Core.State;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>R02 (recycle and restore), R03 (failure handling and crash consistency), R04 (purge).</summary>
public sealed class RecycleTests : TestBase
{
    protected override void SeedSandbox(TestHost host)
    {
        host.WriteSandboxBytes("文件.bin", Sandbox.Pattern(50_000, seed: 91));
        host.WriteSandboxFile("文档/说明.txt", "内容");
        host.WriteSandboxFile("文档/子目录/深层.txt", "深层内容");
        host.WriteSandboxFile("中文 名称.txt", "中文内容");
    }

    private async Task<string> VersionOf(LanDiskClient device, string path)
    {
        ApiResult<JsonElement> meta = await device.GetAsync<JsonElement>("/api/file/meta" + LanDiskClient.FileQuery(ShareId, path));
        Assert.True(meta.IsSuccess, meta.Raw);
        return meta.Body.GetProperty("version").GetString()!;
    }

    private async Task<JsonElement> DeleteAsync(LanDiskClient device, string path)
    {
        string version = await VersionOf(device, path);
        ApiResult<JsonElement> result = await device.PostAsync<JsonElement>("/api/file/delete", new
        {
            shareId = ShareId,
            path,
            version
        });
        Assert.True(result.IsSuccess, result.Raw);
        return result.Body;
    }

    [Fact]
    public async Task R02_deleted_file_is_recoverable_with_identical_content()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        byte[] before = Host.ReadSandboxBytes("文件.bin");
        string hashBefore = Convert.ToHexStringLower(SHA256.HashData(before));

        JsonElement deletion = await DeleteAsync(device, "文件.bin");
        string recycleId = deletion.GetProperty("recycleId").GetString()!;

        Assert.False(File.Exists(Host.SandboxPath("文件.bin")));
        Assert.True(File.Exists(Host.SandboxPath("文档/说明.txt")), "删除一个文件不能影响其他文件");

        JsonElement listing = (await device.BrowseAsync(ShareId, "")).Body;
        Assert.DoesNotContain("文件.bin", listing.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!).ToArray());

        JsonElement recycle = (await device.GetAsync<JsonElement>("/api/recycle")).Body;
        JsonElement entry = recycle.GetProperty("entries").EnumerateArray()
            .First(e => e.GetProperty("id").GetString() == recycleId);
        Assert.Equal("文件.bin", entry.GetProperty("name").GetString());

        ApiResult<JsonElement> restored = await device.PostAsync<JsonElement>($"/api/recycle/{recycleId}/restore", new { });
        Assert.True(restored.IsSuccess, restored.Raw);

        byte[] after = Host.ReadSandboxBytes("文件.bin");
        Assert.Equal(hashBefore, Convert.ToHexStringLower(SHA256.HashData(after)));
        Record("R02", "文件删除后恢复，内容哈希一致");
    }

    [Fact]
    public async Task R02_directory_tree_is_recoverable_intact()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        string hashBefore = Sandbox.Sha256Hex(await File.ReadAllBytesAsync(Host.SandboxPath("文档/子目录/深层.txt")));

        await DeleteAsync(device, "文档");

        Assert.False(Directory.Exists(Host.SandboxPath("文档")));

        JsonElement recycle = (await device.GetAsync<JsonElement>("/api/recycle")).Body;
        string id = recycle.GetProperty("entries").EnumerateArray()
            .First(e => e.GetProperty("name").GetString() == "文档")
            .GetProperty("id").GetString()!;

        ApiResult<JsonElement> restore = await device.PostAsync<JsonElement>($"/api/recycle/{id}/restore", new { });
        Assert.True(restore.IsSuccess, restore.Raw);

        Assert.True(File.Exists(Host.SandboxPath("文档/说明.txt")));
        Assert.True(File.Exists(Host.SandboxPath("文档/子目录/深层.txt")));
        Assert.Equal(hashBefore, Sandbox.Sha256Hex(await File.ReadAllBytesAsync(Host.SandboxPath("文档/子目录/深层.txt"))));
        Record("R02", "目录树删除后可完整恢复");
    }

    [Fact]
    public async Task R02_restore_conflict_is_refused_and_an_alternate_name_works()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        JsonElement deletion = await DeleteAsync(device, "中文 名称.txt");
        string id = deletion.GetProperty("recycleId").GetString()!;

        // Something new occupies the original name.
        Host.WriteSandboxFile("中文 名称.txt", "新占用");

        ApiResult<JsonElement> conflict = await device.PostAsync<JsonElement>($"/api/recycle/{id}/restore", new { });
        Assert.Equal(HttpStatusCode.Conflict, conflict.Status);
        Assert.Equal("新占用", Host.ReadSandboxFile("中文 名称.txt"));

        ApiResult<JsonElement> alternate = await device.PostAsync<JsonElement>($"/api/recycle/{id}/restore", new
        {
            alternateName = "已恢复的副本.txt"
        });

        Assert.True(alternate.IsSuccess, alternate.Raw);
        Assert.Equal("中文内容", Host.ReadSandboxFile("已恢复的副本.txt"));
        Assert.Equal("新占用", Host.ReadSandboxFile("中文 名称.txt"));
        Record("R02", "恢复冲突不覆盖，可另存恢复");
    }

    [Fact]
    public async Task R03_recycle_failure_never_degrades_into_a_permanent_delete()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        string version = await VersionOf(device, "文件.bin");

        // Hold the file with no sharing so the move into the recycle store cannot succeed.
        using (FileStream hold = new(Host.SandboxPath("文件.bin"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            ApiResult<JsonElement> result = await device.PostAsync<JsonElement>("/api/file/delete", new
            {
                shareId = ShareId,
                path = "文件.bin",
                version
            });

            Assert.False(result.IsSuccess, "回收失败时不得报告成功");
        }

        Assert.True(File.Exists(Host.SandboxPath("文件.bin")), "回收失败后原文件必须保留");
        Assert.Equal(50_000, new FileInfo(Host.SandboxPath("文件.bin")).Length);

        JsonElement recycle = (await device.GetAsync<JsonElement>("/api/recycle")).Body;
        Assert.DoesNotContain(recycle.GetProperty("entries").EnumerateArray(),
            e => e.GetProperty("name").GetString() == "文件.bin" && e.GetProperty("state").GetString() == "Committed");

        Record("R03", "回收失败时拒绝删除，原文件保留");
    }

    [Fact]
    public void R03_reconciler_marks_a_completed_move_as_committed()
    {
        // Simulates the crash window: the payload exists and the original is gone, but the
        // process died before the index was updated from Staged to Committed.
        TestHost host = Host;
        string source = host.SandboxPath("crash-source.bin");
        File.WriteAllBytes(source, Sandbox.Pattern(2048, 5));

        var index = new RecycleIndex(host.Runtime.Paths.RecycleIndexFile);
        var locator = new LanDisk.Core.Storage.VolumeStoreLocator(host.Runtime.Paths);
        var resolver = new LanDisk.Core.Paths.PathResolver(host.Runtime.Paths);
        var operations = new LanDisk.Core.Storage.FileOperations(resolver);
        var audit = new AuditLog(host.Runtime.Paths.AuditFile, 500);
        var service = new LanDisk.Core.Storage.RecycleService(index, locator, resolver, operations, audit);

        LanDisk.Core.Storage.VolumeStore store = locator.Locate(source);
        string entryId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(store.RecycleDir, entryId));
        string payload = Path.Combine(store.RecycleDir, entryId, "data");
        File.Move(source, payload);

        ShareDefinition share = host.Runtime.Shares.All().First();
        string volumeGuid = LanDisk.Core.Interop.Win32FileSystem.GetVolumeGuidPath(
            LanDisk.Core.Interop.Win32FileSystem.GetVolumeMountPoint(payload));

        index.Add(new RecycleEntry
        {
            Id = entryId,
            ShareId = share.Id,
            OriginalRelativePath = "crash-source.bin",
            DisplayName = "crash-source.bin",
            IsDirectory = false,
            Length = 2048,
            VolumeGuidPath = volumeGuid,
            StoredRelativePath = Path.Combine(entryId, "data"),
            State = RecycleState.Staged,
            DeletedByDeviceName = "test"
        });

        LanDisk.Core.Storage.ReconcileReport report = service.Reconcile(host.Runtime.Shares.All());

        Assert.Equal(1, report.Committed);
        Assert.Equal(RecycleState.Committed, index.Find(entryId)!.State);
        Record("R03", "崩溃窗口中对账为 Committed");
    }

    [Fact]
    public void R03_reconciler_discards_an_orphan_payload_when_the_original_still_exists()
    {
        TestHost host = Host;
        string source = host.SandboxPath("orphan.bin");
        File.WriteAllBytes(source, Sandbox.Pattern(1024, 6));

        var index = new RecycleIndex(host.Runtime.Paths.RecycleIndexFile);
        var locator = new LanDisk.Core.Storage.VolumeStoreLocator(host.Runtime.Paths);
        var resolver = new LanDisk.Core.Paths.PathResolver(host.Runtime.Paths);
        var operations = new LanDisk.Core.Storage.FileOperations(resolver);
        var audit = new AuditLog(host.Runtime.Paths.AuditFile, 500);
        var service = new LanDisk.Core.Storage.RecycleService(index, locator, resolver, operations, audit);

        LanDisk.Core.Storage.VolumeStore store = locator.Locate(source);
        string entryId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(store.RecycleDir, entryId));
        string payload = Path.Combine(store.RecycleDir, entryId, "data");
        File.Copy(source, payload);

        ShareDefinition share = host.Runtime.Shares.All().First();
        string volumeGuid = LanDisk.Core.Interop.Win32FileSystem.GetVolumeGuidPath(
            LanDisk.Core.Interop.Win32FileSystem.GetVolumeMountPoint(payload));

        index.Add(new RecycleEntry
        {
            Id = entryId,
            ShareId = share.Id,
            OriginalRelativePath = "orphan.bin",
            DisplayName = "orphan.bin",
            IsDirectory = false,
            Length = 1024,
            VolumeGuidPath = volumeGuid,
            StoredRelativePath = Path.Combine(entryId, "data"),
            State = RecycleState.Staged,
            DeletedByDeviceName = "test"
        });

        LanDisk.Core.Storage.ReconcileReport report = service.Reconcile(host.Runtime.Shares.All());

        Assert.Equal(1, report.OrphanedCleanups);
        Assert.True(File.Exists(source), "对账绝不能删除用户仍然拥有的原始文件");
        Assert.False(File.Exists(payload));
        Record("R03", "对账清理孤立暂存，保留用户原文件");
    }

    [Fact]
    public async Task R04_only_the_local_admin_can_purge()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        JsonElement deletion = await DeleteAsync(device, "文件.bin");
        string id = deletion.GetProperty("recycleId").GetString()!;

        // There is no device facing purge endpoint at all.
        ApiResult<JsonElement> attempt = await device.PostAsync<JsonElement>($"/api/recycle/{id}/purge", new { });
        Assert.True(attempt.Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.MethodNotAllowed,
            "手机端不应存在永久清空接口：" + attempt.Status);

        // The local admin can purge, and it is limited to the indexed object.
        ApiResult<JsonElement> purge = await Admin.PostAsync<JsonElement>($"/api/admin/recycle/{id}/purge", new { });
        Assert.True(purge.IsSuccess, purge.Raw);

        JsonElement recycle = (await device.GetAsync<JsonElement>("/api/recycle")).Body;
        Assert.DoesNotContain(recycle.GetProperty("entries").EnumerateArray(),
            e => e.GetProperty("id").GetString() == id);
        Record("R04", "永久清空仅本机管理端可用");
    }

    [Fact]
    public async Task R04_empty_requires_explicit_confirmation()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        await DeleteAsync(device, "文件.bin");
        await DeleteAsync(device, "中文 名称.txt");

        ApiResult<JsonElement> withoutConfirm = await Admin.PostAsync<JsonElement>("/api/admin/recycle/empty", new { });
        Assert.Equal(HttpStatusCode.BadRequest, withoutConfirm.Status);
        Assert.Equal("CONFIRM_REQUIRED", withoutConfirm.ErrorCode);

        ApiResult<JsonElement> confirmed = await Admin.PostAsync<JsonElement>("/api/admin/recycle/empty", new { confirm = true });
        Assert.True(confirmed.IsSuccess, confirmed.Raw);
        Assert.Equal(2, confirmed.Body.GetProperty("purged").GetInt32());

        JsonElement recycle = (await device.GetAsync<JsonElement>("/api/recycle")).Body;
        Assert.Empty(recycle.GetProperty("entries").EnumerateArray());
        Record("R04", "永久清空需要二次确认");
    }

    [Fact]
    public async Task R04_device_without_delete_permission_cannot_restore()
    {
        var writer = await PairDeviceAsync(DeviceAccessLevel.ReadWrite, "写入设备");
        JsonElement deletion = await DeleteAsync(writer, "文件.bin");
        string id = deletion.GetProperty("recycleId").GetString()!;

        // A share that no longer grants delete must not allow restoring through it either.
        await Admin.PatchAsync<JsonElement>($"/api/admin/shares/{ShareId}", new { allowDelete = false });

        var second = await PairDeviceAsync(DeviceAccessLevel.ReadWrite, "第二个设备");
        ApiResult<JsonElement> restore = await second.PostAsync<JsonElement>($"/api/recycle/{id}/restore", new { });

        Assert.Equal(HttpStatusCode.Forbidden, restore.Status);
        Record("R04", "缺少删除权限时不能恢复");
    }

    [Fact]
    public async Task R02_recycle_listing_hides_entries_from_shares_the_device_cannot_delete_in()
    {
        var writer = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        await DeleteAsync(writer, "文件.bin");

        await Admin.PatchAsync<JsonElement>($"/api/admin/shares/{ShareId}", new { allowDelete = false });
        JsonElement recycle = (await writer.GetAsync<JsonElement>("/api/recycle")).Body;
        Assert.Empty(recycle.GetProperty("entries").EnumerateArray());

        // The local admin still sees everything.
        JsonElement adminRecycle = (await Admin.GetAsync<JsonElement>("/api/admin/recycle")).Body;
        Assert.NotEmpty(adminRecycle.GetProperty("entries").EnumerateArray());
        Record("R02", "回收站列表按当前权限过滤");
    }

    [Fact]
    public async Task R02_delete_requires_the_delete_capability()
    {
        var readOnly = await PairDeviceAsync(DeviceAccessLevel.ReadOnly);
        string version = await VersionOf(readOnly, "文件.bin");

        ApiResult<JsonElement> result = await readOnly.PostAsync<JsonElement>("/api/file/delete", new
        {
            shareId = ShareId,
            path = "文件.bin",
            version
        });

        Assert.Equal(HttpStatusCode.Forbidden, result.Status);
        Assert.True(File.Exists(Host.SandboxPath("文件.bin")));
        Record("R02", "只读设备不能删除");
    }
}
