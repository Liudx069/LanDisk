using System.Net;
using System.Text.Json;
using LanDisk.Core.Model;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>Recent, favourites, thumbnails and storage information.</summary>
public sealed class LibraryTests : TestBase
{
    private static readonly byte[] SmallPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    protected override void SeedSandbox(TestHost host)
    {
        host.WriteSandboxBytes("图像/红点.png", SmallPng);
        host.WriteSandboxFile("图像/说明.txt", "not an image");
        host.WriteSandboxBytes("文档/报告.txt", new byte[10]);
        host.WriteSandboxBytes("视频/短片.mp4", new byte[1000]);

        DateTime now = DateTime.Now;
        SetTime(host, "文档/报告.txt", now.AddDays(-1));
        SetTime(host, "图像/红点.png", now.AddDays(-2));
        SetTime(host, "视频/短片.mp4", now.AddDays(-3));
    }

    private static void SetTime(TestHost host, string relative, DateTime value)
    {
        try { File.SetLastWriteTime(host.SandboxPath(relative), value); } catch { }
    }

    // ---- recent -----------------------------------------------------------

    [Fact]
    public async Task RECENT_lists_files_most_recently_modified_first()
    {
        var device = await PairDeviceAsync();
        ApiResult<JsonElement> result = await device.GetAsync<JsonElement>("/api/recent?limit=50");

        Assert.Equal(HttpStatusCode.OK, result.Status);
        JsonElement[] items = result.Body.GetProperty("items").EnumerateArray().ToArray();
        Assert.NotEmpty(items);

        DateTimeOffset[] times = items
            .Select(i => i.GetProperty("entry").GetProperty("modifiedUtc").GetDateTimeOffset())
            .ToArray();
        Assert.Equal(times.OrderByDescending(t => t), times);

        // Directories are never part of "recent"; only files are.
        Assert.All(items, i => Assert.False(i.GetProperty("entry").GetProperty("isDirectory").GetBoolean()));

        Record("RECENT", $"最近文件返回 {items.Length} 项，按修改时间倒序且只含文件");
    }

    [Fact]
    public async Task RECENT_respects_the_configured_window_and_pages()
    {
        Host.Runtime.Settings.RecentWindowDays = 2;
        var device = await PairDeviceAsync();

        ApiResult<JsonElement> result = await device.GetAsync<JsonElement>("/api/recent?limit=1&offset=0");
        Assert.Equal(2, result.Body.GetProperty("windowDays").GetInt32());

        DateTimeOffset cutoff = DateTimeOffset.Now.AddDays(-3);
        JsonElement[] items = result.Body.GetProperty("items").EnumerateArray().ToArray();
        Assert.All(items, i => Assert.True(
            i.GetProperty("entry").GetProperty("modifiedUtc").GetDateTimeOffset() >= cutoff,
            "窗口之外的文件不应出现"));

        Record("RECENT", "最近文件的窗口天数与分页参数生效");
    }

    [Fact]
    public async Task RECENT_does_not_expose_shares_the_device_cannot_read()
    {
        string other = Path.Combine(Host.Root, "hidden-share");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "秘密.txt"), "secret");
        await Admin.CreateShareAsync(other, name: "隐藏共享");

        var device = await PairDeviceAsync();
        await Admin.PatchAsync<JsonElement>(
            $"/api/admin/shares/{(await Admin.GetAsync<JsonElement>("/api/admin/shares")).Body.GetProperty("shares").EnumerateArray().First(s => s.GetProperty("name").GetString() == "隐藏共享").GetProperty("id").GetString()}",
            new { enabled = false });

        ApiResult<JsonElement> result = await device.GetAsync<JsonElement>("/api/recent?limit=100");
        string[] paths = result.Body.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("entry").GetProperty("path").GetString()!).ToArray();

        Assert.DoesNotContain("秘密.txt", paths);
        Record("RECENT", "最近文件不包含被关闭/未授权的共享");
    }

    // ---- favourites -------------------------------------------------------

    [Fact]
    public async Task FAVORITES_add_list_and_remove()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadOnly);

        ApiResult<JsonElement> added = await device.PostAsync<JsonElement>("/api/favorites", new
        {
            shareId = ShareId,
            path = "文档/报告.txt"
        });
        Assert.True(added.IsSuccess, added.Raw);
        string id = added.Body.GetProperty("id").GetString()!;

        // Adding the same target twice is idempotent rather than producing duplicates.
        ApiResult<JsonElement> again = await device.PostAsync<JsonElement>("/api/favorites", new
        {
            shareId = ShareId,
            path = "文档/报告.txt"
        });
        Assert.True(again.Body.GetProperty("alreadyExists").GetBoolean());

        ApiResult<JsonElement> listed = await device.GetAsync<JsonElement>("/api/favorites");
        JsonElement[] items = listed.Body.GetProperty("items").EnumerateArray().ToArray();
        Assert.Single(items);
        Assert.Equal("报告.txt", items[0].GetProperty("name").GetString());
        Assert.Equal("ok", items[0].GetProperty("status").GetString());

        ApiResult<JsonElement> removed = await device.DeleteAsync<JsonElement>($"/api/favorites/{id}");
        Assert.True(removed.IsSuccess, removed.Raw);
        Assert.Empty((await device.GetAsync<JsonElement>("/api/favorites")).Body.GetProperty("items").EnumerateArray());

        Record("FAVORITE", "收藏新增（幂等）、列出与删除均正确");
    }

    [Fact]
    public async Task FAVORITES_report_broken_targets_without_granting_access()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadOnly);

        ApiResult<JsonElement> added = await device.PostAsync<JsonElement>("/api/favorites", new
        {
            shareId = ShareId,
            path = "视频/短片.mp4"
        });
        Assert.True(added.IsSuccess, added.Raw);

        // The target disappears underneath the bookmark.
        File.Delete(Host.SandboxPath("视频/短片.mp4"));
        JsonElement missing = (await device.GetAsync<JsonElement>("/api/favorites")).Body
            .GetProperty("items").EnumerateArray().First();
        Assert.Equal("missing", missing.GetProperty("status").GetString());

        // Restore the file, then disable the share: the bookmark must report as unusable rather
        // than silently serving stale access.
        Host.WriteSandboxBytes("视频/短片.mp4", new byte[1000]);
        await Admin.PatchAsync<JsonElement>($"/api/admin/shares/{ShareId}", new { enabled = false });

        JsonElement disabled = (await device.GetAsync<JsonElement>("/api/favorites")).Body
            .GetProperty("items").EnumerateArray().First();
        Assert.Equal("disabled", disabled.GetProperty("status").GetString());

        Record("FAVORITE", "收藏在目标消失或共享关闭时显示失效状态");
    }

    [Fact]
    public async Task FAVORITES_are_per_device()
    {
        var first = await PairDeviceAsync(DeviceAccessLevel.ReadOnly, "设备一");
        var second = await PairDeviceAsync(DeviceAccessLevel.ReadOnly, "设备二");

        await first.PostAsync<JsonElement>("/api/favorites", new { shareId = ShareId, path = "文档/报告.txt" });

        Assert.Single((await first.GetAsync<JsonElement>("/api/favorites")).Body.GetProperty("items").EnumerateArray());
        Assert.Empty((await second.GetAsync<JsonElement>("/api/favorites")).Body.GetProperty("items").EnumerateArray());

        Record("FAVORITE", "收藏按设备隔离");
    }

    [Fact]
    public async Task FAVORITES_of_a_device_cannot_be_deleted_by_another_device()
    {
        var first = await PairDeviceAsync(DeviceAccessLevel.ReadOnly, "设备一");
        var second = await PairDeviceAsync(DeviceAccessLevel.ReadOnly, "设备二");

        string id = (await first.PostAsync<JsonElement>("/api/favorites", new { shareId = ShareId, path = "文档/报告.txt" }))
            .Body.GetProperty("id").GetString()!;

        ApiResult<JsonElement> attempt = await second.DeleteAsync<JsonElement>($"/api/favorites/{id}");
        Assert.Equal(HttpStatusCode.Forbidden, attempt.Status);

        Record("FAVORITE", "设备不能删除其它设备的收藏");
    }

    // ---- thumbnails -------------------------------------------------------

    [Fact]
    public async Task THUMBNAIL_is_generated_for_an_image_and_reused_from_cache()
    {
        var device = await PairDeviceAsync();

        using HttpResponseMessage first = await device.SendRawAsync(new HttpRequestMessage(
            HttpMethod.Get, "/api/file/thumb" + LanDiskClient.FileQuery(ShareId, "图像/红点.png")));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("image/jpeg", first.Content.Headers.ContentType?.MediaType);
        byte[] bytes = await first.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 100);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);

        // Second request must be served from the on-disk cache and produce identical bytes.
        using HttpResponseMessage second = await device.SendRawAsync(new HttpRequestMessage(
            HttpMethod.Get, "/api/file/thumb" + LanDiskClient.FileQuery(ShareId, "图像/红点.png")));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(bytes, await second.Content.ReadAsByteArrayAsync());

        Assert.True(Host.Runtime.Thumbnails.CurrentCacheBytes() > 0, "缩略图应写入本机缓存");

        Record("THUMBNAIL", $"PNG 缩略图生成成功（{bytes.Length} 字节 JPEG）并命中磁盘缓存");
    }

    [Fact]
    public async Task THUMBNAIL_rejects_non_images_and_active_content()
    {
        var device = await PairDeviceAsync();

        using HttpResponseMessage text = await device.SendRawAsync(new HttpRequestMessage(
            HttpMethod.Get, "/api/file/thumb" + LanDiskClient.FileQuery(ShareId, "图像/说明.txt")));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, text.StatusCode);

        Host.WriteSandboxFile("图像/矢量.svg", "<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>");
        using HttpResponseMessage svg = await device.SendRawAsync(new HttpRequestMessage(
            HttpMethod.Get, "/api/file/thumb" + LanDiskClient.FileQuery(ShareId, "图像/矢量.svg")));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, svg.StatusCode);

        Record("THUMBNAIL", "非图片与 SVG 主动内容不生成缩略图");
    }

    [Fact]
    public async Task THUMBNAIL_requires_authentication_and_a_readable_share()
    {
        var anonymous = new LanDiskClient(Host.NewClient());
        ApiResult<JsonElement> denied = await anonymous.GetAsync<JsonElement>(
            "/api/file/thumb" + LanDiskClient.FileQuery(ShareId, "图像/红点.png"));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.Status);

        var device = await PairDeviceAsync();
        ApiResult<JsonElement> traversal = await device.GetAsync<JsonElement>(
            "/api/file/thumb" + LanDiskClient.FileQuery(ShareId, "../../etc/passwd"));
        Assert.Equal(HttpStatusCode.BadRequest, traversal.Status);

        Record("THUMBNAIL", "缩略图接口与其它文件接口一样要求认证并经过路径校验");
    }

    // ---- storage ----------------------------------------------------------

    [Fact]
    public async Task STORAGE_reports_volume_capacity_for_visible_shares()
    {
        var device = await PairDeviceAsync();
        ApiResult<JsonElement> result = await device.GetAsync<JsonElement>("/api/storage");

        Assert.Equal(HttpStatusCode.OK, result.Status);
        JsonElement item = result.Body.GetProperty("items").EnumerateArray().First();
        Assert.True(item.GetProperty("totalBytes").GetInt64() > 0);
        Assert.True(item.GetProperty("freeBytes").GetInt64() >= 0);
        Assert.Matches(@"^(—|\d+(\.\d+)? (B|KiB|MiB|GiB|TiB))$", item.GetProperty("freeText").GetString()!);

        Record("STORAGE", $"容量信息：{item.GetProperty("freeText").GetString()} 可用 / {item.GetProperty("totalText").GetString()} 总计");
    }
}
