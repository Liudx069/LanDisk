using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using LanDisk.Core.Model;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>F01 (directory presentation) and F02 (large directory).</summary>
public sealed class ListingTests : TestBase
{
    protected override void SeedSandbox(TestHost host)
    {
        host.WriteSandboxFile("中文 文件.txt", "内容");
        host.WriteSandboxFile("emoji-🎬-movie.mp4", "x");
        host.WriteSandboxFile("a#b$c%d&e'f(g)h+i,j;k=l@m.n", "x");
        host.WriteSandboxFile(new string('长', 120) + ".txt", "x");
        host.WriteSandboxFile("普通/子目录/深层.txt", "deep");
        Directory.CreateDirectory(host.SandboxPath("空目录"));
        host.WriteSandboxFile("1.txt", "1");
        host.WriteSandboxFile("2.txt", "2");
        host.WriteSandboxFile("10.txt", "10");
    }

    [Fact]
    public async Task F01_special_names_are_listed_verbatim()
    {
        var device = await PairDeviceAsync();
        JsonElement listing = (await device.BrowseAsync(ShareId, "")).Body;

        string[] names = listing.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!).ToArray();

        Assert.Contains("中文 文件.txt", names);
        Assert.Contains("emoji-🎬-movie.mp4", names);
        Assert.Contains("a#b$c%d&e'f(g)h+i,j;k=l@m.n", names);
        Assert.Contains(new string('长', 120) + ".txt", names);
        Assert.Contains("空目录", names);
        Assert.Contains("普通", names);

        JsonElement empty = listing.GetProperty("entries").EnumerateArray()
            .First(e => e.GetProperty("name").GetString() == "空目录");
        Assert.True(empty.GetProperty("isDirectory").GetBoolean());

        Record("F01", $"特殊名称条目数={names.Length}");
    }

    [Fact]
    public async Task F01_empty_directory_reports_zero_entries()
    {
        var device = await PairDeviceAsync();
        JsonElement listing = (await device.BrowseAsync(ShareId, "空目录")).Body;
        Assert.Equal(0, listing.GetProperty("total").GetInt32());
        Assert.Empty(listing.GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public async Task F01_natural_sort_orders_numbers_logically()
    {
        var device = await PairDeviceAsync();
        JsonElement listing = (await device.BrowseAsync(ShareId, "", sort: "name")).Body;

        string[] names = listing.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!)
            .Where(n => n.EndsWith(".txt") && n.Length <= 6)
            .ToArray();

        // Directories sort first, then files; the numeric part must order 1, 2, 10.
        int index1 = Array.IndexOf(names, "1.txt");
        int index2 = Array.IndexOf(names, "2.txt");
        int index10 = Array.IndexOf(names, "10.txt");
        Assert.True(index1 < index2 && index2 < index10, $"自然排序结果不符合预期: {string.Join(',', names)}");
    }

    [Fact]
    public async Task F01_breadcrumbs_describe_nesting()
    {
        var device = await PairDeviceAsync();
        JsonElement listing = (await device.BrowseAsync(ShareId, "普通/子目录")).Body;
        JsonElement[] crumbs = listing.GetProperty("breadcrumbs").EnumerateArray().ToArray();

        Assert.Equal(3, crumbs.Length);
        Assert.Equal("", crumbs[0].GetProperty("path").GetString());
        Assert.Equal("普通", crumbs[1].GetProperty("path").GetString());
        Assert.Equal("普通/子目录", crumbs[2].GetProperty("path").GetString());
    }

    [Fact]
    public async Task F01_filter_restricts_matches()
    {
        var device = await PairDeviceAsync();
        JsonElement listing = (await device.BrowseAsync(ShareId, "", filter: "普通")).Body;
        Assert.Equal(1, listing.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task F01_paging_slices_the_result()
    {
        var device = await PairDeviceAsync();
        JsonElement page = (await device.BrowseAsync(ShareId, "", sort: "name", offset: 0, limit: 2)).Body;

        Assert.Equal(2, page.GetProperty("entries").GetArrayLength());
        Assert.True(page.GetProperty("total").GetInt32() >= 9);
        Assert.True(page.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task F02_ten_thousand_entries_are_paged_without_truncation()
    {
        const int count = 10_000;
        string bulk = Host.SandboxPath("大量文件");
        Directory.CreateDirectory(bulk);

        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
        {
            using FileStream stream = File.Create(Path.Combine(bulk, $"f{i:D5}.bin"));
            stream.WriteByte((byte)(i % 251));
        }
        stopwatch.Stop();
        long createMs = stopwatch.ElapsedMilliseconds;

        var device = await PairDeviceAsync();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetTotalMemory(true);

        stopwatch.Restart();
        ApiResult<JsonElement> first = await device.BrowseAsync(ShareId, "大量文件", sort: "name", offset: 0, limit: 200);
        long firstMs = stopwatch.ElapsedMilliseconds;
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(count, first.Body.GetProperty("total").GetInt32());
        Assert.Equal(200, first.Body.GetProperty("entries").GetArrayLength());
        Assert.True(first.Body.GetProperty("hasMore").GetBoolean());

        stopwatch.Restart();
        ApiResult<JsonElement> last = await device.BrowseAsync(ShareId, "大量文件", sort: "name", offset: 9_800, limit: 200);
        long lastMs = stopwatch.ElapsedMilliseconds;
        Assert.Equal(200, last.Body.GetProperty("entries").GetArrayLength());
        Assert.False(last.Body.GetProperty("hasMore").GetBoolean());

        long after = GC.GetTotalMemory(false);

        string[] firstNames = first.Body.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!).ToArray();
        Assert.Equal("f00000.bin", firstNames[0]);
        Assert.Equal("f00199.bin", firstNames[199]);

        Record("F02", $"10000 项：创建 {createMs} ms，首页 {firstMs} ms（服务端扫描 {first.Body.GetProperty("scanMs").GetInt64()} ms），" +
                      $"末页 {lastMs} ms，托管堆变化 {(after - before) / 1024} KB");

        Assert.True(firstMs < 10_000, "首页响应时间异常");
    }
}
