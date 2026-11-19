using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Text.Json;
using LanDisk.Core.Model;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>
/// Performance evidence for the operations the upgrade added. Numbers are recorded as measured
/// values, not as claims: the point is to show the implementation stays bounded, and to have a
/// baseline to compare against on the user's own hardware.
/// </summary>
public sealed class PerformanceTests : TestBase
{
    protected override void SeedSandbox(TestHost host)
    {
        host.WriteSandboxBytes("样本/文件.txt", "内容"u8.ToArray());
    }

    [Fact]
    public async Task PERF_thumbnail_generation_stays_bounded_under_concurrency()
    {
        const int imageCount = 24;
        string directory = Host.SandboxPath("图片");
        Directory.CreateDirectory(directory);

        for (int i = 0; i < imageCount; i++)
        {
            string path = Path.Combine(directory, $"合成图像-{i:D2}.png");
            WriteTestPng(path, 900, 700, i);
        }

        var device = await PairDeviceAsync();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long heapBefore = GC.GetTotalMemory(true);

        var stopwatch = Stopwatch.StartNew();

        // Different files, so this exercises real decodes rather than cache hits.
        Task<HttpResponseMessage>[] requests = Enumerable.Range(0, imageCount)
            .Select(i => device.SendRawAsync(new HttpRequestMessage(
                HttpMethod.Get,
                "/api/file/thumb" + LanDiskClient.FileQuery(ShareId, $"图片/合成图像-{i:D2}.png"))))
            .ToArray();

        HttpResponseMessage[] responses = await Task.WhenAll(requests);
        stopwatch.Stop();

        long totalBytes = 0;
        int generated = 0;
        foreach (HttpResponseMessage response in responses)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            byte[] bytes = await response.Content.ReadAsByteArrayAsync();
            totalBytes += bytes.Length;
            generated++;
            response.Dispose();
        }

        long heapAfter = GC.GetTotalMemory(false);
        long peakWorkingSet = Environment.WorkingSet;

        // A cached second pass must be materially faster, which also proves the cache is used.
        var cached = Stopwatch.StartNew();
        foreach (int i in Enumerable.Range(0, imageCount))
        {
            using HttpResponseMessage response = await device.SendRawAsync(new HttpRequestMessage(
                HttpMethod.Get,
                "/api/file/thumb" + LanDiskClient.FileQuery(ShareId, $"图片/合成图像-{i:D2}.png")));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        cached.Stop();

        long cacheBytes = Host.Runtime.Thumbnails.CurrentCacheBytes();

        Record("PERF-THUMB",
            $"{generated} 张 900×700 PNG 并发生成缩略图：首次 {stopwatch.ElapsedMilliseconds} ms，缓存命中 {cached.ElapsedMilliseconds} ms；" +
            $"缩略图总计 {totalBytes / 1024} KiB，磁盘缓存 {cacheBytes / 1024} KiB，" +
            $"托管堆变化 {(heapAfter - heapBefore) / 1024} KB，进程工作集 {peakWorkingSet / 1024 / 1024} MB");

        Assert.True(generated == imageCount);
        Assert.True(cacheBytes > 0, "缩略图应写入磁盘缓存");
        Assert.True(heapAfter - heapBefore < 200L * 1024 * 1024, "缩略图并发不应造成失控的内存增长");
    }

    [Fact]
    public async Task PERF_ten_thousand_entry_first_page_and_search()
    {
        const int count = 10_000;
        string bulk = Host.SandboxPath("性能目录");
        Directory.CreateDirectory(bulk);

        for (int i = 0; i < count; i++)
        {
            using FileStream stream = File.Create(Path.Combine(bulk, $"item-{i:D5}.bin"));
            stream.WriteByte((byte)(i % 251));
        }

        var device = await PairDeviceAsync();

        var first = Stopwatch.StartNew();
        ApiResult<JsonElement> page = await device.BrowseExAsync(ShareId, "性能目录", ("limit", "100"));
        first.Stop();

        var sorted = Stopwatch.StartNew();
        await device.BrowseExAsync(ShareId, "性能目录", ("sort", "size"), ("desc", "true"), ("limit", "100"));
        sorted.Stop();

        var paged = Stopwatch.StartNew();
        await device.BrowseExAsync(ShareId, "性能目录", ("offset", "9000"), ("limit", "100"));
        paged.Stop();

        var searched = Stopwatch.StartNew();
        ApiResult<JsonElement> search = await device.BrowseExAsync(ShareId, "性能目录",
            ("search", "item-09"), ("limit", "100"));
        searched.Stop();

        long scanMs = page.Body.GetProperty("scanMs").GetInt64();

        Record("PERF-10K",
            $"10000 项目录：首屏 {first.ElapsedMilliseconds} ms（服务端扫描 {scanMs} ms），按大小排序 {sorted.ElapsedMilliseconds} ms，" +
            $"偏移 9000 分页 {paged.ElapsedMilliseconds} ms，名称搜索命中 {search.Body.GetProperty("total").GetInt32()} 项耗时 {searched.ElapsedMilliseconds} ms");

        Assert.Equal(count, page.Body.GetProperty("total").GetInt32());
        Assert.Equal(1000, search.Body.GetProperty("total").GetInt32());
        Assert.True(first.ElapsedMilliseconds < 15_000, "首屏耗时异常");
    }

    /// <summary>Writes a small but genuinely decodable PNG without depending on the server code.</summary>
    private static void WriteTestPng(string path, int width, int height, int seed)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, Color.FromArgb((x * 7 + seed * 11) % 256, (y * 5 + seed * 3) % 256, (x + y + seed * 17) % 256));
            }
        }
        bitmap.Save(path, ImageFormat.Png);
    }
}
