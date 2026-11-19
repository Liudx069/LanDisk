using System.Net;
using System.Text.Json;
using LanDisk.Core.Model;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>Search and filter behaviour, including scope, combination and permission boundaries.</summary>
public sealed class SearchFilterTests : TestBase
{
    protected override void SeedSandbox(TestHost host)
    {
        host.WriteSandboxBytes("报告-2026.txt", new byte[100]);
        host.WriteSandboxBytes("报告-2025.txt", new byte[200]);
        host.WriteSandboxBytes("照片/假期.png", new byte[300]);
        host.WriteSandboxBytes("照片/风景.jpg", new byte[400]);
        host.WriteSandboxBytes("照片/说明.txt", new byte[500]);
        host.WriteSandboxBytes("视频/片段.mp4", new byte[6_000_000]);
        host.WriteSandboxBytes("音乐/歌曲.mp3", new byte[3_000_000]);
        host.WriteSandboxBytes("归档/包.zip", new byte[1_000_000]);
        host.WriteSandboxBytes("深层/2026/报告-深层.txt", new byte[600]);
        host.WriteSandboxBytes("其它/未知.xyz", new byte[700]);

        // The recursive search must descend into a directory whose own name does not match.
        host.WriteSandboxBytes("不匹配目录/报告-嵌套.txt", new byte[800]);

        DateTime now = DateTime.Now;
        SetTime(host, "报告-2026.txt", now.AddDays(-1));
        SetTime(host, "报告-2025.txt", now.AddDays(-400));
        SetTime(host, "照片/假期.png", now.AddDays(-10));
        SetTime(host, "其它/未知.xyz", now.AddDays(-3));
    }

    private static void SetTime(TestHost host, string relative, DateTime value)
    {
        try { File.SetLastWriteTime(host.SandboxPath(relative), value); } catch { }
    }

    // ---- scope ------------------------------------------------------------

    [Fact]
    public async Task SEARCH_current_folder_does_not_descend()
    {
        var device = await PairDeviceAsync();
        ApiResult<JsonElement> result = await device.BrowseExAsync(ShareId, "", ("search", "报告"), ("limit", "100"));

        string[] names = LanDiskClient.Names(result);
        Assert.Contains("报告-2026.txt", names);
        Assert.Contains("报告-2025.txt", names);
        Assert.DoesNotContain("报告-深层.txt", names);
        Assert.DoesNotContain("报告-嵌套.txt", names);

        Record("SEARCH", "默认范围只搜索当前文件夹");
    }

    [Fact]
    public async Task SEARCH_share_scope_descends_into_non_matching_directories()
    {
        var device = await PairDeviceAsync();
        ApiResult<JsonElement> result = await device.BrowseExAsync(ShareId, "",
            ("search", "报告"), ("scope", "share"), ("limit", "100"));

        string[] names = LanDiskClient.Names(result);
        Assert.Contains("报告-2026.txt", names);
        Assert.Contains("报告-深层.txt", names);
        // This is the case that fails if the recursion predicate is coupled to the name filter.
        Assert.Contains("报告-嵌套.txt", names);

        Record("SEARCH", $"共享范围递归搜索命中 {names.Length} 项，含名称不匹配目录内的文件");
    }

    [Fact]
    public async Task SEARCH_folder_tree_scope_is_relative_to_the_current_directory()
    {
        var device = await PairDeviceAsync();

        ApiResult<JsonElement> insidePhotos = await device.BrowseExAsync(ShareId, "照片",
            ("search", "."), ("scope", "foldertree"), ("limit", "100"));

        string[] names = LanDiskClient.Names(insidePhotos);
        Assert.All(names, n => Assert.True(n is "假期.png" or "风景.jpg" or "说明.txt", "越界结果: " + n));
        Assert.DoesNotContain("报告-2026.txt", names);

        Record("SEARCH", "文件夹树范围搜索限制在所选目录之下");
    }

    [Fact]
    public async Task SEARCH_matches_chinese_and_is_case_insensitive()
    {
        var device = await PairDeviceAsync();

        ApiResult<JsonElement> chinese = await device.BrowseExAsync(ShareId, "照片", ("search", "风景"));
        Assert.Contains("风景.jpg", LanDiskClient.Names(chinese));

        Host.WriteSandboxBytes("MixedCase.TXT", new byte[10]);
        ApiResult<JsonElement> mixed = await device.BrowseExAsync(ShareId, "", ("search", "mixedcase"));
        Assert.Contains("MixedCase.TXT", LanDiskClient.Names(mixed));

        Record("SEARCH", "中文与大小写不敏感匹配均生效");
    }

    // ---- filters ----------------------------------------------------------

    [Fact]
    public async Task FILTER_by_category_returns_only_that_category()
    {
        var device = await PairDeviceAsync();

        (string Category, string MustContain, string MustNotContain)[] cases =
        [
            ("image", "照片/假期.png", "视频/片段.mp4"),
            ("video", "视频/片段.mp4", "照片/假期.png"),
            ("audio", "音乐/歌曲.mp3", "照片/假期.png"),
            ("archive", "归档/包.zip", "照片/假期.png"),
            ("document", "照片/说明.txt", "照片/假期.png"),
            ("folder", "照片", "照片/假期.png")
        ];

        foreach ((string category, string mustContain, string mustNotContain) in cases)
        {
            ApiResult<JsonElement> result = await device.BrowseExAsync(ShareId, "",
                ("category", category), ("scope", "share"), ("limit", "500"));

            Assert.True(result.IsSuccess, $"分类筛选 {category} 失败: {result.Raw}");
            string[] names = LanDiskClient.Names(result);
            Assert.Contains(mustContain.Split('/')[^1], names);
            Assert.DoesNotContain(mustNotContain.Split('/')[^1], names);
        }

        Record("FILTER", $"六种类型筛选均只返回对应分类（{string.Join("、", cases.Select(c => c.Category))}）");
    }

    [Fact]
    public async Task FILTER_by_size_range_excludes_directories()
    {
        var device = await PairDeviceAsync();

        ApiResult<JsonElement> large = await device.BrowseExAsync(ShareId, "",
            ("scope", "share"), ("minSize", "1000000"), ("limit", "500"));

        string[] names = LanDiskClient.Names(large);
        Assert.Contains("片段.mp4", names);
        Assert.Contains("歌曲.mp3", names);
        Assert.Contains("包.zip", names);
        Assert.DoesNotContain("假期.png", names);

        // A size filter is meaningless for directories, so they are excluded rather than treated
        // as zero bytes.
        ApiResult<JsonElement> all = await device.BrowseExAsync(ShareId, "", ("scope", "share"), ("limit", "500"));
        Assert.Contains(all.Body.GetProperty("entries").EnumerateArray(), e => e.GetProperty("isDirectory").GetBoolean());

        JsonElement[] filtered = large.Body.GetProperty("entries").EnumerateArray().ToArray();
        Assert.DoesNotContain(filtered, e => e.GetProperty("isDirectory").GetBoolean());

        ApiResult<JsonElement> small = await device.BrowseExAsync(ShareId, "", ("maxSize", "1000"), ("limit", "500"));
        Assert.All(small.Body.GetProperty("entries").EnumerateArray(),
            e => Assert.True(e.GetProperty("length").GetInt64() <= 1000));

        Record("FILTER", "大小范围筛选生效，且排除文件夹");
    }

    [Fact]
    public async Task FILTER_by_modified_date_range()
    {
        var device = await PairDeviceAsync();

        string recent = DateTime.Now.AddDays(-5).ToString("yyyy-MM-dd");
        ApiResult<JsonElement> result = await device.BrowseExAsync(ShareId, "",
            ("scope", "share"), ("after", recent), ("limit", "500"));

        string[] names = LanDiskClient.Names(result);
        Assert.Contains("报告-2026.txt", names);
        Assert.Contains("未知.xyz", names);
        Assert.DoesNotContain("报告-2025.txt", names);

        string before = DateTime.Now.AddDays(-100).ToString("yyyy-MM-dd");
        ApiResult<JsonElement> older = await device.BrowseExAsync(ShareId, "",
            ("scope", "share"), ("before", before), ("limit", "500"));
        Assert.Contains("报告-2025.txt", LanDiskClient.Names(older));

        Record("FILTER", "修改日期范围筛选生效（按本地日期边界）");
    }

    [Fact]
    public async Task FILTER_combination_and_empty_result()
    {
        var device = await PairDeviceAsync();

        ApiResult<JsonElement> combination = await device.BrowseExAsync(ShareId, "",
            ("scope", "share"), ("search", "报告"), ("category", "document"),
            ("minSize", "150"), ("limit", "500"));

        string[] names = LanDiskClient.Names(combination);
        Assert.Contains("报告-2025.txt", names);
        Assert.DoesNotContain("报告-2026.txt", names);

        ApiResult<JsonElement> none = await device.BrowseExAsync(ShareId, "",
            ("scope", "share"), ("search", "绝对不存在的名字"), ("limit", "500"));
        Assert.Equal(0, none.Body.GetProperty("total").GetInt32());
        Assert.Empty(none.Body.GetProperty("entries").EnumerateArray());

        Record("FILTER", "多条件组合生效，无结果时返回空列表而非报错");
    }

    // ---- boundaries -------------------------------------------------------

    [Fact]
    public async Task SEARCH_never_leaks_another_share_or_the_internal_store()
    {
        // A second share exists but this device is only paired to the first one's data through the
        // share list; searching inside share one must never surface anything from share two.
        string other = Path.Combine(Host.Root, "other-share");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "报告-另一个共享.txt"), "secret");
        await Admin.CreateShareAsync(other, name: "另一个共享");

        var device = await PairDeviceAsync();
        ApiResult<JsonElement> result = await device.BrowseExAsync(ShareId, "",
            ("search", "报告"), ("scope", "share"), ("limit", "500"));

        string[] paths = result.Body.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("path").GetString()!).ToArray();

        Assert.DoesNotContain(paths, p => p.Contains("另一个共享", StringComparison.Ordinal));
        Assert.All(paths, p => Assert.DoesNotContain("..", p, StringComparison.Ordinal));

        // The internal store directory is skipped at every depth by the recursive walk.
        string store = Path.Combine(Host.SandboxPath("深层"), LanDisk.Core.Configuration.AppPaths.VolumeStoreName);
        Directory.CreateDirectory(store);
        File.WriteAllText(Path.Combine(store, "报告-内部.txt"), "internal");

        ApiResult<JsonElement> afterStore = await device.BrowseExAsync(ShareId, "",
            ("search", "报告"), ("scope", "share"), ("limit", "500"));
        Assert.DoesNotContain("报告-内部.txt", LanDiskClient.Names(afterStore));

        Record("SEARCH", "搜索不越出共享、不返回内部存储目录内容");
    }

    [Fact]
    public async Task SEARCH_of_a_disabled_share_is_refused()
    {
        var device = await PairDeviceAsync();
        await Admin.PatchAsync<JsonElement>($"/api/admin/shares/{ShareId}", new { enabled = false });

        ApiResult<JsonElement> result = await device.BrowseExAsync(ShareId, "", ("search", "报告"), ("scope", "share"));
        Assert.Equal(HttpStatusCode.Forbidden, result.Status);
        Assert.Equal("SHARE_DISABLED", result.ErrorCode);

        Record("SEARCH", "共享关闭后搜索与浏览一样被拒绝");
    }

    [Fact]
    public async Task SEARCH_reports_truncation_instead_of_silently_cutting_off()
    {
        // The seeded fixture has four entries matching "报告", so a two-result ceiling is reached.
        Host.Runtime.Settings.SearchMaxResults = 2;
        var device = await PairDeviceAsync();

        ApiResult<JsonElement> result = await device.BrowseExAsync(ShareId, "",
            ("search", "报告"), ("scope", "share"), ("limit", "500"));

        Assert.True(result.Body.GetProperty("truncated").GetBoolean(),
            "达到结果上限时必须明确标记 truncated");
        Assert.Equal(2, result.Body.GetProperty("total").GetInt32());

        Record("SEARCH", "达到上限时明确标记结果被截断，而不是假装完整");
    }

    [Fact]
    public async Task SEARCH_large_directory_stays_bounded()
    {
        string bulk = Host.SandboxPath("搜索大目录");
        Directory.CreateDirectory(bulk);
        for (int i = 0; i < 5_000; i++)
        {
            using FileStream stream = File.Create(Path.Combine(bulk, $"data-{i:D5}.bin"));
            stream.WriteByte(1);
        }

        var device = await PairDeviceAsync();
        ApiResult<JsonElement> result = await device.BrowseExAsync(ShareId, "搜索大目录",
            ("search", "data-00"), ("limit", "500"));

        // data-00000 … data-00999 match "data-00" exactly.
        Assert.Equal(1000, result.Body.GetProperty("total").GetInt32());
        Assert.Equal(500, result.Body.GetProperty("entries").GetArrayLength());
        Assert.True(result.Body.GetProperty("hasMore").GetBoolean());

        Record("SEARCH", $"5000 项目录中搜索命中 {result.Body.GetProperty("total").GetInt32()} 项，单页 {result.Body.GetProperty("entries").GetArrayLength()} 项");
    }
}
