using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanDisk.Core.Model;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>Batch ZIP download (streaming, partial failure reporting) and batch delete.</summary>
public sealed class BatchTests : TestBase
{
    protected override void SeedSandbox(TestHost host)
    {
        host.WriteSandboxBytes("单文件.bin", Sandbox.Pattern(20_000, 1));
        host.WriteSandboxBytes("目录/一.txt", Encoding.UTF8.GetBytes("first"));
        host.WriteSandboxBytes("目录/二.txt", Encoding.UTF8.GetBytes("second"));
        host.WriteSandboxBytes("目录/子目录/三.txt", Encoding.UTF8.GetBytes("third"));
        host.WriteSandboxBytes("中文 名称.txt", Encoding.UTF8.GetBytes("中文内容"));
        host.WriteSandboxBytes("重复/a.txt", Encoding.UTF8.GetBytes("a"));
    }

    private async Task<string> VersionOf(LanDiskClient client, string path)
    {
        ApiResult<JsonElement> meta = await client.GetAsync<JsonElement>("/api/file/meta" + LanDiskClient.FileQuery(ShareId, path));
        Assert.True(meta.IsSuccess, meta.Raw);
        return meta.Body.GetProperty("version").GetString()!;
    }

    private static object[] Items(params string[] paths)
        => paths.Select(p => (object)new { path = p, version = (string?)null }).ToArray();

    // ---- plan -------------------------------------------------------------

    [Fact]
    public async Task BATCH_plan_reports_files_bytes_and_skips()
    {
        var device = await PairDeviceAsync();

        ApiResult<JsonElement> plan = await device.PostAsync<JsonElement>("/api/batch/plan", new
        {
            shareId = ShareId,
            items = Items("单文件.bin", "目录", "不存在的文件.txt")
        });

        Assert.True(plan.IsSuccess, plan.Raw);
        Assert.Equal(4, plan.Body.GetProperty("fileCount").GetInt32()); // 1 + 3 inside 目录
        Assert.True(plan.Body.GetProperty("totalBytes").GetInt64() > 20_000);

        JsonElement[] skipped = plan.Body.GetProperty("skipped").EnumerateArray().ToArray();
        Assert.Single(skipped);
        Assert.Equal("不存在的文件.txt", skipped[0].GetProperty("path").GetString());

        Record("BATCH", $"计划：{plan.Body.GetProperty("fileCount").GetInt32()} 个文件 / {plan.Body.GetProperty("totalSizeText").GetString()}，跳过 {skipped.Length} 项");
    }

    [Fact]
    public async Task BATCH_plan_refuses_path_traversal()
    {
        var device = await PairDeviceAsync();
        ApiResult<JsonElement> plan = await device.PostAsync<JsonElement>("/api/batch/plan", new
        {
            shareId = ShareId,
            items = Items("../../Windows/win.ini")
        });

        Assert.True(plan.IsSuccess);
        JsonElement skipped = plan.Body.GetProperty("skipped").EnumerateArray().Single();
        Assert.StartsWith("PATH_", skipped.GetProperty("errorCode").GetString()!);
        Assert.Equal(0, plan.Body.GetProperty("fileCount").GetInt32());

        Record("BATCH", "批量计划拒绝路径越界项且不泄漏内容");
    }

    // ---- zip --------------------------------------------------------------

    [Fact]
    public async Task BATCH_zip_contains_every_requested_file_with_correct_bytes()
    {
        var device = await PairDeviceAsync();

        using HttpResponseMessage response = await device.SendRawAsync(new HttpRequestMessage(HttpMethod.Post, "/api/batch/download")
        {
            Content = JsonContent(new { shareId = ShareId, items = Items("单文件.bin", "目录", "中文 名称.txt") })
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);

        byte[] archive = await response.Content.ReadAsByteArrayAsync();
        using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);

        Assert.Equal(5, zip.Entries.Count); // 单文件 + 目录内 3 个 + 中文名称
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(Host.ReadSandboxBytes("单文件.bin"))),
            HashEntry(zip, "单文件.bin"));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("third"))), HashEntry(zip, "目录/子目录/三.txt"));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("中文内容"))), HashEntry(zip, "中文 名称.txt"));

        Record("BATCH", $"ZIP 含 {zip.Entries.Count} 个条目，逐项 SHA256 与源文件一致");
    }

    [Fact]
    public async Task BATCH_zip_includes_a_manifest_for_skipped_items()
    {
        var device = await PairDeviceAsync();

        using HttpResponseMessage response = await device.SendRawAsync(new HttpRequestMessage(HttpMethod.Post, "/api/batch/download")
        {
            Content = JsonContent(new
            {
                shareId = ShareId,
                items = Items("单文件.bin", "缺失的目录", "../../outside.txt")
            })
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("2", response.Headers.GetValues("X-LanDisk-Archive-Skipped").Single());

        byte[] archive = await response.Content.ReadAsByteArrayAsync();
        using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);

        ZipArchiveEntry manifest = zip.Entries.First(e => e.Name.Contains("未包含"));
        using var reader = new StreamReader(manifest.Open(), Encoding.UTF8);
        string text = await reader.ReadToEndAsync();

        Assert.Contains("缺失的目录", text);
        Assert.Contains("outside.txt", text);
        Assert.Contains("未能加入归档", text);

        Record("BATCH", "归档内含未包含项目清单，逐项说明原因");
    }

    [Fact]
    public async Task BATCH_zip_of_a_directory_keeps_the_tree_structure()
    {
        var device = await PairDeviceAsync();

        using HttpResponseMessage response = await device.SendRawAsync(new HttpRequestMessage(HttpMethod.Post, "/api/batch/download")
        {
            Content = JsonContent(new { shareId = ShareId, items = Items("目录") })
        });

        byte[] archive = await response.Content.ReadAsByteArrayAsync();
        using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);

        string[] names = zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "目录/一.txt", "目录/二.txt", "目录/子目录/三.txt" }, names);

        Record("BATCH", "整目录打包保留目录结构，递归进入子目录");
    }

    [Fact]
    public async Task BATCH_zip_excludes_the_internal_store_directory()
    {
        string store = Path.Combine(Host.SandboxPath("目录"), LanDisk.Core.Configuration.AppPaths.VolumeStoreName);
        Directory.CreateDirectory(store);
        File.WriteAllText(Path.Combine(store, "秘密.txt"), "internal");

        var device = await PairDeviceAsync();
        using HttpResponseMessage response = await device.SendRawAsync(new HttpRequestMessage(HttpMethod.Post, "/api/batch/download")
        {
            Content = JsonContent(new { shareId = ShareId, items = Items("目录") })
        });

        byte[] archive = await response.Content.ReadAsByteArrayAsync();
        using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);

        Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains(LanDisk.Core.Configuration.AppPaths.VolumeStoreName, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(zip.Entries, e => e.Name.Contains("秘密"));

        Record("BATCH", "递归打包跳过 LanDisk 内部存储目录");
    }

    [Fact]
    public async Task BATCH_zip_fails_cleanly_when_nothing_is_readable()
    {
        var device = await PairDeviceAsync();

        using HttpResponseMessage response = await device.SendRawAsync(new HttpRequestMessage(HttpMethod.Post, "/api/batch/download")
        {
            Content = JsonContent(new { shareId = ShareId, items = Items("不存在A", "不存在B") })
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("NOTHING_TO_ARCHIVE", body);

        Record("BATCH", "全部项目不可读时返回明确错误，不生成空归档");
    }

    [Fact]
    public async Task BATCH_zip_requires_read_permission_and_authentication()
    {
        var anonymous = new LanDiskClient(Host.NewClient());
        using HttpResponseMessage anonymousResponse = await anonymous.SendRawAsync(new HttpRequestMessage(HttpMethod.Post, "/api/batch/download")
        {
            Content = JsonContent(new { shareId = ShareId, items = Items("单文件.bin") })
        });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        await Admin.PatchAsync<JsonElement>($"/api/admin/shares/{ShareId}", new { enabled = false });
        var device = await PairDeviceAsync();
        using HttpResponseMessage disabled = await device.SendRawAsync(new HttpRequestMessage(HttpMethod.Post, "/api/batch/download")
        {
            Content = JsonContent(new { shareId = ShareId, items = Items("单文件.bin") })
        });
        Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode);

        Record("BATCH", "批量下载与单文件下载的认证/授权要求一致");
    }

    // ---- batch delete -----------------------------------------------------

    [Fact]
    public async Task BATCH_delete_moves_items_to_the_recycle_bin_and_reports_each_one()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);

        var items = new List<object>
        {
            new { path = "单文件.bin", version = await VersionOf(device, "单文件.bin") },
            new { path = "中文 名称.txt", version = await VersionOf(device, "中文 名称.txt") },
            new { path = "不存在的文件.txt", version = (string?)"1-1" }
        };

        ApiResult<JsonElement> result = await device.PostAsync<JsonElement>("/api/batch/delete", new { shareId = ShareId, items });

        Assert.True(result.IsSuccess, result.Raw);
        Assert.Equal(3, result.Body.GetProperty("requested").GetInt32());
        Assert.Equal(2, result.Body.GetProperty("succeeded").GetInt32());
        Assert.Equal(1, result.Body.GetProperty("failed").GetInt32());
        Assert.False(result.Body.GetProperty("ok").GetBoolean());

        JsonElement[] results = result.Body.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(2, results.Count(r => r.GetProperty("ok").GetBoolean()));
        Assert.Equal(1, results.Count(r => !r.GetProperty("ok").GetBoolean()));

        Assert.False(File.Exists(Host.SandboxPath("单文件.bin")));

        // Everything is recoverable from the application recycle bin.
        JsonElement recycle = (await device.GetAsync<JsonElement>("/api/recycle")).Body;
        string[] names = recycle.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!).ToArray();
        Assert.Contains("单文件.bin", names);
        Assert.Contains("中文 名称.txt", names);

        Record("BATCH", "批量删除逐项返回结果，成功项进入回收站，失败项如实报告");
    }

    [Fact]
    public async Task BATCH_delete_requires_delete_permission()
    {
        var readOnly = await PairDeviceAsync(DeviceAccessLevel.ReadOnly);
        ApiResult<JsonElement> result = await readOnly.PostAsync<JsonElement>("/api/batch/delete", new
        {
            shareId = ShareId,
            items = new[] { new { path = "单文件.bin", version = "1-1" } }
        });

        Assert.Equal(HttpStatusCode.Forbidden, result.Status);
        Assert.True(File.Exists(Host.SandboxPath("单文件.bin")));

        Record("BATCH", "只读设备不能执行批量删除");
    }

    [Fact]
    public async Task BATCH_delete_rejects_a_stale_version_instead_of_deleting_the_wrong_object()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        string version = await VersionOf(device, "单文件.bin");

        await Task.Delay(20);
        Host.WriteSandboxBytes("单文件.bin", Sandbox.Pattern(30_000, 9));

        ApiResult<JsonElement> result = await device.PostAsync<JsonElement>("/api/batch/delete", new
        {
            shareId = ShareId,
            items = new[] { new { path = "单文件.bin", version } }
        });

        JsonElement item = result.Body.GetProperty("results").EnumerateArray().Single();
        Assert.False(item.GetProperty("ok").GetBoolean());
        Assert.Equal("PATH_OBJECTCHANGED", item.GetProperty("errorCode").GetString());
        Assert.True(File.Exists(Host.SandboxPath("单文件.bin")));

        Record("BATCH", "批量删除沿用对象版本校验，过期版本被拒绝");
    }

    [Fact]
    public async Task BATCH_delete_enforces_the_item_cap()
    {
        Host.Runtime.Settings.BatchDeleteMaxItems = 2;
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);

        ApiResult<JsonElement> result = await device.PostAsync<JsonElement>("/api/batch/delete", new
        {
            shareId = ShareId,
            items = new[]
            {
                new { path = "a", version = "1-1" },
                new { path = "b", version = "1-1" },
                new { path = "c", version = "1-1" }
            }
        });

        Assert.Equal(HttpStatusCode.Conflict, result.Status);
        Assert.Contains("最多删除", result.ErrorMessage);

        Record("BATCH", "批量删除条目数上限生效");
    }

    // ---- helpers ----------------------------------------------------------

    private static StringContent JsonContent(object value)
        => new(JsonSerializer.Serialize(value, TestHost.Json), Encoding.UTF8, "application/json");

    private static string HashEntry(ZipArchive zip, string name)
    {
        ZipArchiveEntry entry = zip.Entries.First(e => e.FullName == name);
        using Stream stream = entry.Open();
        using var sha = SHA256.Create();
        return Convert.ToHexStringLower(sha.ComputeHash(stream));
    }
}
