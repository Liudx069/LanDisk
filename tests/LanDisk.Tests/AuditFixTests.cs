using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanDisk.Core.Model;
using LanDisk.Core.Storage;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>
/// LAN_DISK_WINDOWS_APP_UPDATER_03 —— 外部审核两项问题的专项测试。
///
/// FIX-01: 批量 ZIP 的票据只保存“共享 + 相对路径 + 身份”，真正流式打开每个条目之前
///         必须重新经过统一的 PathResolver、卷身份与对象身份检查。
/// FIX-02: “最近”不得因为单次固定页大小而静默丢失候选，截断必须准确且带原因。
///
/// 这些用例写在修复之前，用来先证明问题存在。
/// </summary>
public sealed class AuditFixTests : TestBase
{
    protected override void SeedSandbox(TestHost host)
    {
        host.WriteSandboxBytes("普通文件.txt", Encoding.UTF8.GetBytes("hello"));
        host.WriteSandboxBytes("目录/内部一.txt", Encoding.UTF8.GetBytes("one"));
        host.WriteSandboxBytes("目录/内部二.txt", Encoding.UTF8.GetBytes("two"));
    }

    // ==================================================================== FIX-01

    private static StringContent JsonBody(object value)
        => new(JsonSerializer.Serialize(value, TestHost.Json), Encoding.UTF8, "application/json");

    /// <summary>Issues a download ticket for the given share relative paths.</summary>
    private async Task<JsonElement> IssueTicket(LanDiskClient device, params string[] paths)
    {
        ApiResult<JsonElement> result = await device.PostAsync<JsonElement>("/api/batch/ticket", new
        {
            shareId = ShareId,
            items = paths.Select(p => new { path = p, version = (string?)null }).ToArray()
        });
        Assert.True(result.IsSuccess, "签发下载票据失败: " + result.Raw);
        return result.Body;
    }

    private static async Task<(HttpStatusCode Status, string Body)> FetchTicket(LanDiskClient device, string url)
    {
        using HttpResponseMessage response = await device.SendRawAsync(new HttpRequestMessage(HttpMethod.Get, url));
        string body = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult().Length > 0
            ? await response.Content.ReadAsStringAsync()
            : string.Empty;
        return (response.StatusCode, body);
    }

    [Fact]
    public async Task FIX01_normal_batch_zip_still_works_after_the_change()
    {
        var device = await PairDeviceAsync();
        JsonElement ticket = await IssueTicket(device, "普通文件.txt", "目录");

        using HttpResponseMessage response = await device.SendRawAsync(
            new HttpRequestMessage(HttpMethod.Get, ticket.GetProperty("url").GetString()!));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        byte[] archive = await response.Content.ReadAsByteArrayAsync();
        using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);

        Assert.Equal(3, zip.Entries.Count);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("hello"))),
            HashEntry(zip, "普通文件.txt"));

        Record("FIX01", "正常批量 ZIP 仍可用，内容哈希正确");
    }

    [Fact]
    public async Task FIX01_file_replaced_after_ticket_is_rejected_with_changed_since_selection()
    {
        var device = await PairDeviceAsync();
        JsonElement ticket = await IssueTicket(device, "普通文件.txt");

        // 票据签发后，同路径的文件被替换成不同的对象
        await Task.Delay(20);
        Host.WriteSandboxBytes("普通文件.txt", Encoding.UTF8.GetBytes("REPLACED-CONTENT-IS-LONGER"));

        using HttpResponseMessage response = await device.SendRawAsync(
            new HttpRequestMessage(HttpMethod.Get, ticket.GetProperty("url").GetString()!));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("CHANGED_SINCE_SELECTION", body);
        Assert.DoesNotContain("REPLACED-CONTENT", body);

        Record("FIX01", "票据后文件被替换 -> 409 CHANGED_SINCE_SELECTION，未读取新对象");
    }

    [Fact]
    public async Task FIX01_file_deleted_after_ticket_is_rejected()
    {
        var device = await PairDeviceAsync();
        JsonElement ticket = await IssueTicket(device, "普通文件.txt");

        File.Delete(Host.SandboxPath("普通文件.txt"));

        using HttpResponseMessage response = await device.SendRawAsync(
            new HttpRequestMessage(HttpMethod.Get, ticket.GetProperty("url").GetString()!));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("CHANGED_SINCE_SELECTION", await response.Content.ReadAsStringAsync());

        Record("FIX01", "票据后文件被删除 -> 409，不产生半成品归档");
    }

    [Fact]
    public async Task FIX01_volume_identity_change_after_ticket_is_rejected()
    {
        var device = await PairDeviceAsync();
        JsonElement ticket = await IssueTicket(device, "普通文件.txt");

        // 模拟同盘符换盘：登记的卷身份与当前卷不一致
        string original = Host.Runtime.Shares.Find(ShareId)!.VolumeGuidPath;
        Host.Runtime.Shares.Update(ShareId, s => s.VolumeGuidPath = @"\\?\Volume{00000000-0000-0000-0000-000000000000}\");

        try
        {
            using HttpResponseMessage response = await device.SendRawAsync(
                new HttpRequestMessage(HttpMethod.Get, ticket.GetProperty("url").GetString()!));

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            string body = await response.Content.ReadAsStringAsync();
            Assert.Contains("CHANGED_SINCE_SELECTION", body);

            Record("FIX01", "票据后卷身份变化 -> 409，不把新盘的内容当作原对象");
        }
        finally
        {
            Host.Runtime.Shares.Update(ShareId, s => s.VolumeGuidPath = original);
        }
    }

    [Fact]
    public async Task FIX01_permission_revoked_after_ticket_is_rejected()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        JsonElement ticket = await IssueTicket(device, "普通文件.txt");

        // 票据签发后本机关闭共享
        await Admin.PatchAsync<JsonElement>($"/api/admin/shares/{ShareId}", new { enabled = false });

        using HttpResponseMessage response = await device.SendRawAsync(
            new HttpRequestMessage(HttpMethod.Get, ticket.GetProperty("url").GetString()!));

        Assert.False(response.IsSuccessStatusCode, "共享已关闭时不得继续下载");
        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Conflict,
            "实际状态: " + response.StatusCode);

        Record("FIX01", $"票据后权限/共享被撤销 -> {response.StatusCode}，在控传输按策略停止");
    }

    [Fact]
    public async Task FIX01_directory_replaced_by_junction_after_ticket_is_rejected()
    {
        var device = await PairDeviceAsync();
        JsonElement ticket = await IssueTicket(device, "目录");

        // 把目录换成一个指向共享外的 junction：重新解析必须拒绝重解析点
        string outside = Path.Combine(Host.Root, "outside-fix01");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "泄漏.txt"), "OUTSIDE-SECRET");

        Directory.Delete(Host.SandboxPath("目录"), true);
        if (!TestFileSystem.TryCreateJunction(Host.SandboxPath("目录"), outside))
        {
            Record("FIX01", "junction 创建失败，跳过（NOT_RUN）");
            return;
        }

        using HttpResponseMessage response = await device.SendRawAsync(
            new HttpRequestMessage(HttpMethod.Get, ticket.GetProperty("url").GetString()!));

        Assert.False(response.IsSuccessStatusCode, "junction 替换后不得继续打包");

        if (response.StatusCode == HttpStatusCode.OK)
        {
            byte[] archive = await response.Content.ReadAsByteArrayAsync();
            using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
            Assert.DoesNotContain(zip.Entries, e => e.Name.Contains("泄漏"));
        }

        Record("FIX01", $"票据后目录被替换为 junction -> {response.StatusCode}，未泄漏共享外内容");
    }

    [Fact]
    public async Task FIX01_streaming_zip_does_not_buffer_the_whole_payload()
    {
        // 64 MiB 的合成文件：流式打包时托管堆增长必须远小于负载本身
        const int size = 64 * 1024 * 1024;
        string path = Host.SandboxPath("大文件.bin");
        using (FileStream stream = File.Create(path))
        {
            byte[] block = Sandbox.Pattern(1 << 20, 7);
            for (int i = 0; i < size / block.Length; i++) stream.Write(block, 0, block.Length);
        }

        var device = await PairDeviceAsync();
        JsonElement ticket = await IssueTicket(device, "大文件.bin");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetTotalMemory(true);

        // ResponseHeadersRead: 不在客户端缓冲整个响应，才能测到服务端的流式行为
        using HttpResponseMessage response = await device.SendStreamingAsync(
            new HttpRequestMessage(HttpMethod.Get, ticket.GetProperty("url").GetString()!));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        long received = 0;
        await using (Stream body = await response.Content.ReadAsStreamAsync())
        {
            byte[] buffer = new byte[1 << 20];
            int read;
            while ((read = await body.ReadAsync(buffer)) > 0) received += read;
        }

        long after = GC.GetTotalMemory(true);
        long heapDelta = after - before;

        Assert.True(received > size, "归档应包含压缩后的负载");
        Assert.True(heapDelta < 32L * 1024 * 1024,
            $"托管堆增长 {heapDelta / 1024 / 1024} MB，说明整个负载被缓冲了");

        Record("FIX01", $"64 MiB 流式打包：归档 {received / 1024 / 1024} MiB，托管堆变化 {heapDelta / 1024 / 1024} MB");
    }

    // ==================================================================== FIX-02

    private async Task<JsonElement> Recent(LanDiskClient device, int limit = 50, int offset = 0)
    {
        ApiResult<JsonElement> result = await device.GetAsync<JsonElement>($"/api/recent?limit={limit}&offset={offset}");
        Assert.True(result.IsSuccess, result.Raw);
        return result.Body;
    }

    [Fact]
    public async Task FIX02_single_share_with_more_than_1000_candidates_is_not_silently_cut()
    {
        const int count = 1500;
        string dir = Host.SandboxPath("最近大量");
        Directory.CreateDirectory(dir);
        // 明确比 seed 文件更新，才能断言“最新的一条排在最前”
        DateTime baseTime = DateTime.Now.AddMinutes(10);
        for (int i = 0; i < count; i++)
        {
            string file = Path.Combine(dir, $"r{i:D4}.txt");
            File.WriteAllText(file, "x");
            File.SetLastWriteTime(file, baseTime.AddSeconds(i));
        }

        var device = await PairDeviceAsync();
        JsonElement recent = await Recent(device, limit: 50);

        int total = recent.GetProperty("total").GetInt32();
        Assert.True(total >= count, $"最近候选被静默截断：total={total}，期望至少 {count}");

        // 最新的一条必须排在最前（r1499）
        JsonElement first = recent.GetProperty("items").EnumerateArray().First();
        Assert.Equal("r1499.txt", first.GetProperty("entry").GetProperty("name").GetString());

        Record("FIX02", $"单共享 {count} 个候选全部参与排序：total={total}，最新为 r1499.txt");
    }

    [Fact]
    public async Task FIX02_pagination_is_stable_and_monotonic()
    {
        const int count = 1200;
        string dir = Host.SandboxPath("最近分页");
        Directory.CreateDirectory(dir);
        DateTime baseTime = DateTime.Now.AddHours(-2);
        for (int i = 0; i < count; i++)
        {
            string file = Path.Combine(dir, $"p{i:D4}.txt");
            File.WriteAllText(file, "x");
            File.SetLastWriteTime(file, baseTime.AddSeconds(i));
        }

        var device = await PairDeviceAsync();

        var seen = new List<string>();
        for (int offset = 0; offset < 300; offset += 50)
        {
            JsonElement page = await Recent(device, limit: 50, offset: offset);
            foreach (JsonElement item in page.GetProperty("items").EnumerateArray())
                seen.Add(item.GetProperty("entry").GetProperty("path").GetString()!);
        }

        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(300, seen.Count);

        // 严格按修改时间倒序
        JsonElement all = await Recent(device, limit: 200);
        string[] firstPage = all.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("entry").GetProperty("path").GetString()!).ToArray();
        Assert.Equal(firstPage, seen.Take(firstPage.Length).ToArray());

        Record("FIX02", "最近结果跨分页稳定、无重复、与一次性取回一致");
    }

    [Fact]
    public async Task FIX02_equal_timestamps_use_a_stable_secondary_order()
    {
        string dir = Host.SandboxPath("最近同时间");
        Directory.CreateDirectory(dir);
        DateTime same = DateTime.Now.AddMinutes(-30);
        foreach (string name in new[] { "c.txt", "a.txt", "b.txt", "A2.txt", "a10.txt", "a2.txt" })
        {
            string file = Path.Combine(dir, name);
            File.WriteAllText(file, "x");
            File.SetLastWriteTime(file, same);
        }

        var device = await PairDeviceAsync();
        string[] FirstPage() => Recent(device, limit: 200).GetAwaiter().GetResult()
            .GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("entry").GetProperty("path").GetString()!).ToArray();

        string[] run1 = FirstPage();
        string[] run2 = FirstPage();
        Assert.Equal(run1, run2);

        // 同时间组内必须按路径序（唯一键）稳定排列，翻页不跳动
        var group = run1.Where(p => p.Contains("最近同时间", StringComparison.Ordinal)).ToArray();
        Assert.Equal(group.OrderBy(p => p, StringComparer.Ordinal), group);

        Record("FIX02", "相同修改时间按相对路径稳定次级排序，重复请求结果一致");
    }

    [Fact]
    public async Task FIX02_multi_share_merge_is_globally_ordered()
    {
        string second = Path.Combine(Host.Root, "second-share");
        Directory.CreateDirectory(second);
        DateTime now = DateTime.Now;
        for (int i = 0; i < 20; i++)
        {
            string file = Path.Combine(second, $"s{i:D2}.txt");
            File.WriteAllText(file, "x");
            File.SetLastWriteTime(file, now.AddMinutes(-i));   // 第二个共享更新
        }
        string secondId = (await Admin.CreateShareAsync(second, name: "第二共享")).GetProperty("id").GetString()!;

        var device = await PairDeviceAsync();
        JsonElement recent = await Recent(device, limit: 200);

        string[] names = recent.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("entry").GetProperty("name").GetString()!).ToArray();

        Assert.Contains("s00.txt", names);
        // 第二个共享的最新项应排在本沙箱较旧项之前
        int s00 = Array.IndexOf(names, "s00.txt");
        int older = Array.IndexOf(names, "普通文件.txt");
        if (older >= 0) Assert.True(s00 < older, "多共享合并未按全局修改时间排序");

        DateTimeOffset[] times = recent.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("entry").GetProperty("modifiedUtc").GetDateTimeOffset()).ToArray();
        Assert.Equal(times.OrderByDescending(t => t), times);

        Record("FIX02", $"多共享合并共 {recent.GetProperty("total").GetInt32()} 项，全局按修改时间倒序");
        _ = secondId;
    }

    [Fact]
    public async Task FIX02_truncated_is_accurate_and_carries_a_reason()
    {
        string dir = Host.SandboxPath("最近截断");
        Directory.CreateDirectory(dir);
        for (int i = 0; i < 40; i++) File.WriteAllText(Path.Combine(dir, $"t{i:D2}.txt"), "x");

        Host.Runtime.Settings.RecentMaxPerShare = 10;
        var device = await PairDeviceAsync();

        JsonElement recent = await Recent(device, limit: 200);

        Assert.True(recent.GetProperty("truncated").GetBoolean(), "达到硬上限时必须标记 truncated");
        JsonElement reasons = recent.GetProperty("truncatedReasons");
        Assert.True(reasons.GetArrayLength() > 0, "截断必须带原因");
        Assert.Contains(reasons.EnumerateArray().Select(r => r.GetString()!),
            r => r.Contains("per_share_limit", StringComparison.Ordinal));

        Record("FIX02", "硬上限触发时 truncated=true 且给出原因：" +
            string.Join("、", reasons.EnumerateArray().Select(r => r.GetString()!)));
    }

    [Fact]
    public async Task FIX02_truncated_is_false_when_everything_was_scanned()
    {
        string dir = Host.SandboxPath("最近完整");
        Directory.CreateDirectory(dir);
        for (int i = 0; i < 30; i++) File.WriteAllText(Path.Combine(dir, $"k{i:D2}.txt"), "x");

        var device = await PairDeviceAsync();
        JsonElement recent = await Recent(device, limit: 200);

        Assert.False(recent.GetProperty("truncated").GetBoolean(),
            "没有触达任何上限时不得谎报截断");
        Assert.Equal(0, recent.GetProperty("truncatedReasons").GetArrayLength());

        Record("FIX02", "未触达上限时 truncated=false 且无原因项（语义与真实扫描一致）");
    }

    [Fact]
    public async Task FIX02_offline_share_is_skipped_without_failing()
    {
        var device = await PairDeviceAsync();

        // 让共享根目录失联，模拟移动盘被拔出
        string moved = Host.SandboxRoot + "-recent-detached";
        Directory.Move(Host.SandboxRoot, moved);
        try
        {
            Host.Runtime.RefreshShareHealth();
            JsonElement recent = await Recent(device, limit: 50);
            Assert.Equal(0, recent.GetProperty("total").GetInt32());
            Assert.Empty(recent.GetProperty("items").EnumerateArray());
            Record("FIX02", "离线共享被跳过，最近列表为空且不报错");
        }
        finally
        {
            Directory.Move(moved, Host.SandboxRoot);
            Host.Runtime.RefreshShareHealth();
        }
    }

    [Fact]
    public async Task FIX02_unauthorized_share_is_not_leaked()
    {
        string secret = Path.Combine(Host.Root, "secret-share");
        Directory.CreateDirectory(secret);
        File.WriteAllText(Path.Combine(secret, "机密-最近.txt"), "secret");
        string secretId = (await Admin.CreateShareAsync(secret, name: "机密共享")).GetProperty("id").GetString()!;
        await Admin.PatchAsync<JsonElement>($"/api/admin/shares/{secretId}", new { enabled = false });

        var device = await PairDeviceAsync();
        JsonElement recent = await Recent(device, limit: 200);

        string[] names = recent.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("entry").GetProperty("name").GetString()!).ToArray();
        Assert.DoesNotContain("机密-最近.txt", names);

        Record("FIX02", "未授权/已关闭共享不出现在最近列表");
    }

    [Fact]
    public async Task FIX02_large_scan_reports_the_scan_limit_instead_of_dropping_silently()
    {
        string dir = Host.SandboxPath("最近扫描上限");
        Directory.CreateDirectory(dir);
        for (int i = 0; i < 60; i++) File.WriteAllText(Path.Combine(dir, $"z{i:D2}.txt"), "x");

        Host.Runtime.Settings.RecentMaxScanEntries = 10;
        var device = await PairDeviceAsync();

        JsonElement recent = await Recent(device, limit: 200);
        Assert.True(recent.GetProperty("truncated").GetBoolean());
        Assert.Contains(recent.GetProperty("truncatedReasons").EnumerateArray().Select(r => r.GetString()!),
            r => r.Contains("scan_limit", StringComparison.Ordinal));

        Record("FIX02", "扫描上限触发时给出 scan_limit 原因");
    }

    // ==================================================================== helpers

    private static string HashEntry(ZipArchive zip, string name)
    {
        ZipArchiveEntry entry = zip.Entries.First(e => e.FullName == name);
        using Stream stream = entry.Open();
        using var sha = SHA256.Create();
        return Convert.ToHexStringLower(sha.ComputeHash(stream));
    }
}
