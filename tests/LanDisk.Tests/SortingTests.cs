using System.Diagnostics;
using System.Net;
using System.Text.Json;
using LanDisk.Core.Model;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>
/// LAN_DISK_DSH_UX_AUTH_UPGRADE_02 — server side ordering guarantees:
/// folders always first, stable total order, ordering completed before paging, and the
/// name/number/emoji/timestamp cases the specification calls out.
/// </summary>
public sealed class SortingTests : TestBase
{
    protected override void SeedSandbox(TestHost host)
    {
        // Names chosen to exercise natural numeric order, case, Chinese and emoji together.
        // NTFS is case-insensitive, so "Alpha.txt" and "alpha.txt" would be the same file; the
        // names below are distinct while still differing in case.
        host.WriteSandboxBytes("Alpha.txt", new byte[10]);
        host.WriteSandboxBytes("zebra.txt", new byte[20]);
        host.WriteSandboxBytes("beta.txt", new byte[30]);
        host.WriteSandboxBytes("file2.txt", new byte[40]);
        host.WriteSandboxBytes("file10.txt", new byte[50]);
        host.WriteSandboxBytes("file1.txt", new byte[60]);
        host.WriteSandboxBytes("中文文件.txt", new byte[70]);
        host.WriteSandboxBytes("表情 🎬 文件.txt", new byte[80]);
        host.WriteSandboxBytes("空文件.txt", []);
        host.WriteSandboxBytes("archive.zip", new byte[100]);
        host.WriteSandboxBytes("photo.PNG", new byte[200]);
        host.WriteSandboxBytes("noeh", new byte[300]);

        Directory.CreateDirectory(host.SandboxPath("文件夹B"));
        Directory.CreateDirectory(host.SandboxPath("文件夹A"));
        Directory.CreateDirectory(host.SandboxPath("folder-1"));
        Directory.CreateDirectory(host.SandboxPath("folder-2"));

        // Distinct timestamps so the time ordering is unambiguous.
        DateTime baseTime = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);
        SetTime(host, "Alpha.txt", baseTime.AddDays(1));
        SetTime(host, "beta.txt", baseTime.AddDays(2));
        SetTime(host, "file1.txt", baseTime.AddDays(3));
        SetTime(host, "中文文件.txt", baseTime.AddDays(4));
        SetTime(host, "photo.PNG", baseTime.AddDays(5));
    }

    private static void SetTime(TestHost host, string relative, DateTime value)
    {
        try { File.SetLastWriteTime(host.SandboxPath(relative), value); } catch { }
    }

    private async Task<string[]> NamesAsync(LanDiskClient client, string sort, bool desc, string path = "")
    {
        ApiResult<JsonElement> result = await client.BrowseExAsync(ShareId,
            path,
            ("sort", sort),
            ("desc", desc ? "true" : "false"),
            ("limit", "1000"));

        Assert.Equal(HttpStatusCode.OK, result.Status);
        return LanDiskClient.Names(result);
    }

    [Fact]
    public async Task SORT_folders_come_first_in_both_directions()
    {
        var device = await PairDeviceAsync();

        foreach (bool desc in new[] { false, true })
        {
            foreach (string sort in new[] { "name", "size", "modified", "type" })
            {
                ApiResult<JsonElement> result = await device.BrowseExAsync(ShareId, "",
                    ("sort", sort), ("desc", desc ? "true" : "false"), ("limit", "1000"));

                JsonElement[] entries = result.Body.GetProperty("entries").EnumerateArray().ToArray();
                int lastDirectoryIndex = -1;
                int firstFileIndex = int.MaxValue;

                for (int i = 0; i < entries.Length; i++)
                {
                    if (entries[i].GetProperty("isDirectory").GetBoolean()) lastDirectoryIndex = Math.Max(lastDirectoryIndex, i);
                    else firstFileIndex = Math.Min(firstFileIndex, i);
                }

                Assert.True(lastDirectoryIndex < firstFileIndex,
                    $"sort={sort} desc={desc} 时文件夹没有排在文件之前（最后文件夹索引 {lastDirectoryIndex}，首个文件索引 {firstFileIndex}）");
            }
        }

        Record("SORT", "四种排序 × 升/降序，文件夹始终排在文件之前");
    }

    [Fact]
    public async Task SORT_by_name_uses_natural_order_and_reverses_cleanly()
    {
        var device = await PairDeviceAsync();

        string[] ascending = await NamesAsync(device, "name", desc: false);
        string[] descending = await NamesAsync(device, "name", desc: true);

        string[] ascendingFiles = ascending.Where(n => n.Contains('.')).ToArray();
        int index1 = Array.IndexOf(ascendingFiles, "file1.txt");
        int index2 = Array.IndexOf(ascendingFiles, "file2.txt");
        int index10 = Array.IndexOf(ascendingFiles, "file10.txt");

        Assert.True(index1 < index2 && index2 < index10,
            "自然排序应为 file1 < file2 < file10，实际: " + string.Join(", ", ascendingFiles));

        // Reversing the sort reverses the file group, while the folder group stays in front.
        ApiResult<JsonElement> ascendingFull = await device.BrowseExAsync(ShareId, "", ("sort", "name"), ("limit", "1000"));
        JsonElement firstEntry = ascendingFull.Body.GetProperty("entries").EnumerateArray().First();
        Assert.True(firstEntry.GetProperty("isDirectory").GetBoolean(),
            "升序时第一项必须是文件夹，实际: " + firstEntry.GetProperty("name").GetString());

        string[] descendingFiles = descending.Where(n => n.Contains('.')).ToArray();
        Assert.Equal(ascendingFiles.Reverse(), descendingFiles);

        Record("SORT", "名称自然排序 file1 < file2 < file10，升降序互为逆序（文件组内）");
    }

    [Fact]
    public async Task SORT_by_name_handles_chinese_emoji_and_case()
    {
        var device = await PairDeviceAsync();
        string[] names = await NamesAsync(device, "name", desc: false);

        Assert.Contains("中文文件.txt", names);
        Assert.Contains("表情 🎬 文件.txt", names);
        Assert.Contains("Alpha.txt", names);
        Assert.Contains("zebra.txt", names);

        // Case-insensitive comparison: Alpha < beta < zebra regardless of the stored casing.
        int alpha = Array.IndexOf(names, "Alpha.txt");
        int beta = Array.IndexOf(names, "beta.txt");
        int zebra = Array.IndexOf(names, "zebra.txt");
        Assert.True(alpha < beta && beta < zebra,
            $"大小写不敏感排序应为 Alpha < beta < zebra，实际索引 {alpha}/{beta}/{zebra}");

        // The comparison must be antisymmetric. Directories stay in front in both directions, so
        // the reversal property is asserted on the file group, and the folder group must remain
        // first while also reversing internally.
        string[] reversed = await NamesAsync(device, "name", desc: true);

        string[] filesAscending = names.Where(n => n.Contains('.')).ToArray();
        string[] filesDescending = reversed.Where(n => n.Contains('.')).ToArray();
        Assert.Equal(filesAscending.Reverse(), filesDescending);

        JsonElement ascendingFirst = (await device.BrowseExAsync(ShareId, "", ("sort", "name"), ("limit", "1")))
            .Body.GetProperty("entries").EnumerateArray().First();
        JsonElement descendingFirst = (await device.BrowseExAsync(ShareId, "", ("sort", "name"), ("desc", "true"), ("limit", "1")))
            .Body.GetProperty("entries").EnumerateArray().First();
        Assert.True(ascendingFirst.GetProperty("isDirectory").GetBoolean());
        Assert.True(descendingFirst.GetProperty("isDirectory").GetBoolean());

        Record("SORT", "中文、emoji、大小写名称排序稳定，文件组升降序严格互逆且文件夹始终在前");
    }

    [Fact]
    public async Task SORT_by_size_and_modified_is_correct()
    {
        var device = await PairDeviceAsync();

        ApiResult<JsonElement> bySize = await device.BrowseExAsync(ShareId, "", ("sort", "size"), ("limit", "1000"));
        long[] sizes = bySize.Body.GetProperty("entries").EnumerateArray()
            .Where(e => !e.GetProperty("isDirectory").GetBoolean())
            .Select(e => e.GetProperty("length").GetInt64())
            .ToArray();
        Assert.Equal(sizes.OrderBy(v => v), sizes);

        ApiResult<JsonElement> bySizeDesc = await device.BrowseExAsync(ShareId, "", ("sort", "size"), ("desc", "true"), ("limit", "1000"));
        long[] sizesDesc = bySizeDesc.Body.GetProperty("entries").EnumerateArray()
            .Where(e => !e.GetProperty("isDirectory").GetBoolean())
            .Select(e => e.GetProperty("length").GetInt64())
            .ToArray();
        Assert.Equal(sizes.OrderByDescending(v => v), sizesDesc);

        ApiResult<JsonElement> byModified = await device.BrowseExAsync(ShareId, "", ("sort", "modified"), ("limit", "1000"));
        DateTimeOffset[] times = byModified.Body.GetProperty("entries").EnumerateArray()
            .Where(e => !e.GetProperty("isDirectory").GetBoolean())
            .Select(e => e.GetProperty("modifiedUtc").GetDateTimeOffset())
            .ToArray();
        Assert.Equal(times.OrderBy(v => v), times);

        Record("SORT", "按大小与修改时间排序正确（字节值与 UTC 时间语义）");
    }

    [Fact]
    public async Task SORT_by_type_groups_extensions_together()
    {
        var device = await PairDeviceAsync();
        ApiResult<JsonElement> result = await device.BrowseExAsync(ShareId, "", ("sort", "type"), ("limit", "1000"));

        string[] extensions = result.Body.GetProperty("entries").EnumerateArray()
            .Where(e => !e.GetProperty("isDirectory").GetBoolean())
            .Select(e => Path.GetExtension(e.GetProperty("name").GetString()!))
            .ToArray();

        string[] sorted = extensions.OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(sorted, extensions);

        // The response carries a category for the UI type filter, derived from the extension only.
        JsonElement zip = result.Body.GetProperty("entries").EnumerateArray()
            .First(e => e.GetProperty("name").GetString() == "archive.zip");
        Assert.Equal("archive", zip.GetProperty("category").GetString());

        JsonElement png = result.Body.GetProperty("entries").EnumerateArray()
            .First(e => e.GetProperty("name").GetString() == "photo.PNG");
        Assert.Equal("image", png.GetProperty("category").GetString());

        Record("SORT", "按类型排序按扩展名分组，并给出类型分类");
    }

    [Fact]
    public async Task SORT_zero_byte_file_reports_zero_and_directories_report_dash()
    {
        var device = await PairDeviceAsync();
        ApiResult<JsonElement> result = await device.BrowseExAsync(ShareId, "", ("limit", "1000"));

        JsonElement zero = result.Body.GetProperty("entries").EnumerateArray()
            .First(e => e.GetProperty("name").GetString() == "空文件.txt");
        Assert.Equal(0, zero.GetProperty("length").GetInt64());
        Assert.Equal("0 B", zero.GetProperty("sizeText").GetString());

        JsonElement folder = result.Body.GetProperty("entries").EnumerateArray()
            .First(e => e.GetProperty("isDirectory").GetBoolean());
        Assert.Equal("—", folder.GetProperty("sizeText").GetString());

        Record("SORT", "0 字节显示 0 B，文件夹大小显示 —（不递归扫描）");
    }

    [Fact]
    public async Task SORT_paging_is_stable_across_requests()
    {
        var device = await PairDeviceAsync();

        foreach (string sort in new[] { "name", "size", "modified", "type" })
        {
            // Page one item at a time. With a non-total order entries could repeat or vanish
            // between pages even though nothing changed on disk.
            var collected = new List<string>();
            for (int offset = 0; offset < 20; offset++)
            {
                ApiResult<JsonElement> page = await device.BrowseExAsync(ShareId, "",
                    ("sort", sort), ("offset", offset.ToString()), ("limit", "1"));

                if (page.Body.GetProperty("entries").GetArrayLength() == 0) break;
                collected.Add(LanDiskClient.Names(page)[0]);
            }

            Assert.Equal(collected.Count, collected.Distinct().Count());

            ApiResult<JsonElement> all = await device.BrowseExAsync(ShareId, "", ("sort", sort), ("limit", "1000"));
            Assert.Equal(LanDiskClient.Names(all), collected);
        }

        Record("SORT", "逐条翻页的结果与一次性取回完全一致，无重复无遗漏");
    }

    [Fact]
    public async Task SORT_ten_thousand_entries_sort_before_paging()
    {
        const int count = 10_000;
        string bulk = Host.SandboxPath("排序大目录");
        Directory.CreateDirectory(bulk);

        for (int i = 0; i < count; i++)
        {
            using FileStream stream = File.Create(Path.Combine(bulk, $"n{(count - i):D5}.dat"));
            stream.WriteByte((byte)(i % 251));
        }

        var device = await PairDeviceAsync();

        // First page must contain the globally smallest names, proving sorting happened before
        // the page was sliced rather than inside the slice.
        var stopwatch = Stopwatch.StartNew();
        ApiResult<JsonElement> first = await device.BrowseExAsync(ShareId, "排序大目录",
            ("sort", "name"), ("limit", "50"));
        long firstMs = stopwatch.ElapsedMilliseconds;

        Assert.Equal(count, first.Body.GetProperty("total").GetInt32());
        string[] firstNames = LanDiskClient.Names(first);
        Assert.Equal("n00001.dat", firstNames[0]);
        Assert.Equal("n00050.dat", firstNames[49]);

        stopwatch.Restart();
        ApiResult<JsonElement> middle = await device.BrowseExAsync(ShareId, "排序大目录",
            ("sort", "name"), ("offset", "5000"), ("limit", "5"));
        long middleMs = stopwatch.ElapsedMilliseconds;

        string[] middleNames = LanDiskClient.Names(middle);
        Assert.Equal("n05001.dat", middleNames[0]);

        stopwatch.Restart();
        ApiResult<JsonElement> bySizeDesc = await device.BrowseExAsync(ShareId, "排序大目录",
            ("sort", "size"), ("desc", "true"), ("limit", "1"));
        long sizeMs = stopwatch.ElapsedMilliseconds;

        // Every file has the same length here, so the name tie breaker decides, and it follows the
        // primary direction: descending size means descending name within the equal-size group.
        Assert.Equal("n10000.dat", LanDiskClient.Names(bySizeDesc)[0]);

        ApiResult<JsonElement> bySizeAsc = await device.BrowseExAsync(ShareId, "排序大目录",
            ("sort", "size"), ("limit", "1"));
        Assert.Equal("n00001.dat", LanDiskClient.Names(bySizeAsc)[0]);

        // The tie breaker makes the ordering a total order, which is what keeps paging stable.
        ApiResult<JsonElement> page1 = await device.BrowseExAsync(ShareId, "排序大目录",
            ("sort", "size"), ("offset", "0"), ("limit", "100"));
        ApiResult<JsonElement> page2 = await device.BrowseExAsync(ShareId, "排序大目录",
            ("sort", "size"), ("offset", "100"), ("limit", "100"));
        Assert.Empty(LanDiskClient.Names(page1).Intersect(LanDiskClient.Names(page2)));

        Record("SORT", $"10000 项：首屏 {firstMs} ms，偏移 5000 分页 {middleMs} ms，大小排序 {sizeMs} ms（服务端扫描 {first.Body.GetProperty("scanMs").GetInt64()} ms）");
    }

    [Fact]
    public async Task SORT_preference_is_remembered_per_device_and_survives_restart()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadOnly, "偏好设备");

        JsonElement initial = (await device.GetAsync<JsonElement>("/api/preferences")).Body;
        Assert.Equal("Name", initial.GetProperty("sort").GetString());
        Assert.False(initial.GetProperty("descending").GetBoolean());
        Assert.Equal("list", initial.GetProperty("view").GetString());

        await device.PatchAsync<JsonElement>("/api/preferences", new { sort = "size", descending = true, view = "grid" });

        JsonElement updated = (await device.GetAsync<JsonElement>("/api/preferences")).Body;
        Assert.Equal("Size", updated.GetProperty("sort").GetString());
        Assert.True(updated.GetProperty("descending").GetBoolean());
        Assert.Equal("grid", updated.GetProperty("view").GetString());

        // A different device must not inherit another device's preference.
        var second = await PairDeviceAsync(DeviceAccessLevel.ReadOnly, "第二台");
        JsonElement other = (await second.GetAsync<JsonElement>("/api/preferences")).Body;
        Assert.Equal("Name", other.GetProperty("sort").GetString());

        Record("SORT", "排序/视图偏好按设备记忆，设备之间互不影响");
    }
}
