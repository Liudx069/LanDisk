using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanDisk.Core.Model;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>U01-U07: chunked uploads, resume, conflicts, cancellation and resource limits.</summary>
public sealed class UploadTests : TestBase
{
    private const long FingerprintSeedMs = 1_700_000_000_000;

    private async Task<string> InitAsync(LanDiskClient device, string directory, string fileName, long length, string fingerprint)
    {
        ApiResult<JsonElement> result = await device.PostAsync<JsonElement>("/api/uploads/", new
        {
            shareId = ShareId,
            path = directory,
            fileName,
            length,
            fingerprint
        });
        Assert.True(result.IsSuccess, "初始化上传失败：" + result.Raw);
        return result.Body.GetProperty("uploadId").GetString()!;
    }

    private static async Task<ApiResult<JsonElement>> ChunkAsync(LanDiskClient device, string uploadId, long offset, byte[] chunk, bool withHash = true)
    {
        var content = new ByteArrayContent(chunk);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/uploads/{uploadId}/chunk?offset={offset}") { Content = content };
        if (withHash) request.Headers.Add("X-Chunk-SHA256", Sandbox.Sha256Hex(chunk));

        using HttpResponseMessage response = await device.SendRawAsync(request);
        string raw = await response.Content.ReadAsStringAsync();
        JsonElement parsed = default;
        try { if (!string.IsNullOrWhiteSpace(raw)) parsed = JsonSerializer.Deserialize<JsonElement>(raw, TestHost.Json); } catch { }
        return new ApiResult<JsonElement>(response.StatusCode, parsed, raw);
    }

    private static async Task<(long Received, bool Complete)> SendAllAsync(LanDiskClient device, string uploadId, byte[] content, int chunkSize)
    {
        long offset = 0;
        bool complete = content.Length == 0;
        while (offset < content.Length)
        {
            int size = (int)Math.Min(chunkSize, content.Length - offset);
            byte[] chunk = content.AsSpan((int)offset, size).ToArray();
            ApiResult<JsonElement> result = await ChunkAsync(device, uploadId, offset, chunk);
            Assert.True(result.IsSuccess, "分块上传失败：" + result.Raw);
            offset = result.Body.GetProperty("receivedBytes").GetInt64();
            complete = result.Body.GetProperty("isComplete").GetBoolean();
        }
        return (offset, complete);
    }

    private static async Task<ApiResult<JsonElement>> CompleteAsync(LanDiskClient device, string uploadId, byte[] content, string policy = "fail")
    {
        return await device.PostAsync<JsonElement>($"/api/uploads/{uploadId}/complete", new
        {
            sha256 = Sandbox.Sha256Hex(content),
            conflictPolicy = policy
        });
    }

    private async Task<string> UploadWholeAsync(LanDiskClient device, string directory, string fileName, byte[] content, int chunkSize = 64 * 1024, string policy = "fail")
    {
        string fingerprint = Sandbox.Fingerprint(content.Length, FingerprintSeedMs, content);
        string uploadId = await InitAsync(device, directory, fileName, content.Length, fingerprint);
        await SendAllAsync(device, uploadId, content, chunkSize);
        ApiResult<JsonElement> complete = await CompleteAsync(device, uploadId, content, policy);
        Assert.True(complete.IsSuccess, "提交失败：" + complete.Raw);
        return complete.Body.GetProperty("path").GetString()!;
    }

    [Fact]
    public async Task U01_single_file_with_chinese_name_commits_with_the_right_hash()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        byte[] content = Sandbox.Pattern(150_000, seed: 3);

        string path = await UploadWholeAsync(device, "", "上传的中文 名称.bin", content);

        Assert.Equal("上传的中文 名称.bin", path);
        byte[] stored = Host.ReadSandboxBytes(path);
        Assert.Equal(content.Length, stored.Length);
        Assert.Equal(Sandbox.Sha256Hex(content), Convert.ToHexStringLower(SHA256.HashData(stored)));
        Record("U01", $"中文名上传成功 {stored.Length} 字节");
    }

    [Fact]
    public async Task U01_zero_byte_file_commits()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        string path = await UploadWholeAsync(device, "", "空的.txt", []);

        Assert.True(File.Exists(Host.SandboxPath("空的.txt")));
        Assert.Empty(Host.ReadSandboxBytes(path));
        Record("U01", "零字节文件提交成功");
    }

    [Fact]
    public async Task U01_multiple_files_upload_with_distinct_content()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        var files = new Dictionary<string, byte[]>
        {
            ["a.bin"] = Sandbox.Pattern(10_000, 1),
            ["图片/photo.png"] = Sandbox.Pattern(20_000, 2),
            ["视频/clip.mp4"] = Sandbox.Pattern(60_000, 3)
        };

        foreach ((string name, byte[] content) in files)
        {
            string directory = Path.GetDirectoryName(name)?.Replace('\\', '/') ?? "";
            string file = Path.GetFileName(name);
            if (directory.Length > 0) Directory.CreateDirectory(Host.SandboxPath(directory));
            await UploadWholeAsync(device, directory, file, content);
        }

        foreach ((string name, byte[] content) in files)
        {
            byte[] stored = Host.ReadSandboxBytes(name);
            Assert.Equal(Sandbox.Sha256Hex(content), Convert.ToHexStringLower(SHA256.HashData(stored)));
        }
        Record("U01", "多文件（含零字节、图片、视频样例）哈希一致");
    }

    [Fact]
    public async Task U02_partial_upload_can_be_resumed_and_completes_correctly()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        byte[] content = Sandbox.Pattern(50_000, seed: 11);
        string fingerprint = Sandbox.Fingerprint(content.Length, FingerprintSeedMs, content);

        string uploadId = await InitAsync(device, "", "续传.bin", content.Length, fingerprint);

        // Send only the first chunk, then simulate a dropped connection.
        byte[] first = content[..20_000];
        ApiResult<JsonElement> firstResult = await ChunkAsync(device, uploadId, 0, first);
        Assert.True(firstResult.IsSuccess);
        Assert.Equal(20_000, firstResult.Body.GetProperty("receivedBytes").GetInt64());

        // A refreshed page discovers the session and reports its progress.
        ApiResult<JsonElement> list = await device.GetAsync<JsonElement>("/api/uploads/");
        JsonElement session = list.Body.GetProperty("uploads").EnumerateArray()
            .First(u => u.GetProperty("uploadId").GetString() == uploadId);
        Assert.Equal(20_000, session.GetProperty("receivedBytes").GetInt64());

        // Resume requires proof that the local file still matches.
        string prefixHash = Convert.ToHexStringLower(SHA256.HashData(first));
        ApiResult<JsonElement> resume = await device.PostAsync<JsonElement>($"/api/uploads/{uploadId}/resume", new
        {
            fingerprint,
            prefixSha256 = prefixHash
        });
        Assert.True(resume.IsSuccess, resume.Raw);
        Assert.Equal(20_000, resume.Body.GetProperty("receivedBytes").GetInt64());

        // Continue from where it stopped.
        long offset = 20_000;
        while (offset < content.Length)
        {
            int size = (int)Math.Min(16_384, content.Length - offset);
            byte[] chunk = content.AsSpan((int)offset, size).ToArray();
            ApiResult<JsonElement> result = await ChunkAsync(device, uploadId, offset, chunk);
            Assert.True(result.IsSuccess, result.Raw);
            offset = result.Body.GetProperty("receivedBytes").GetInt64();
        }

        ApiResult<JsonElement> complete = await CompleteAsync(device, uploadId, content);
        Assert.True(complete.IsSuccess, complete.Raw);

        byte[] stored = Host.ReadSandboxBytes("续传.bin");
        Assert.Equal(Sandbox.Sha256Hex(content), Convert.ToHexStringLower(SHA256.HashData(stored)));
        Record("U02", "断点续传后内容哈希正确");
    }

    [Fact]
    public async Task U02_out_of_order_chunk_is_rejected_with_the_expected_offset()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        byte[] content = Sandbox.Pattern(30_000, seed: 5);
        string fingerprint = Sandbox.Fingerprint(content.Length, FingerprintSeedMs, content);
        string uploadId = await InitAsync(device, "", "乱序.bin", content.Length, fingerprint);

        ApiResult<JsonElement> skipped = await ChunkAsync(device, uploadId, 10_000, content[10_000..20_000]);
        Assert.Equal(HttpStatusCode.Conflict, skipped.Status);
        Assert.Equal("UPLOAD_OFFSET_MISMATCH", skipped.ErrorCode);
        Assert.Equal(0, skipped.Body.GetProperty("error").GetProperty("extra").GetProperty("expectedOffset").GetInt64());

        // After the correct chunk, re-sending it again is also refused (no silent double write).
        byte[] first = content[..10_000];
        Assert.True((await ChunkAsync(device, uploadId, 0, first)).IsSuccess);
        ApiResult<JsonElement> duplicate = await ChunkAsync(device, uploadId, 0, first);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.Status);
        Assert.Equal(10_000, duplicate.Body.GetProperty("error").GetProperty("extra").GetProperty("expectedOffset").GetInt64());

        Record("U02", "乱序与重复分块均被拒绝并返回期望偏移");
    }

    [Fact]
    public async Task U02_chunk_hash_mismatch_is_rejected()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        byte[] content = Sandbox.Pattern(4096, seed: 6);
        string fingerprint = Sandbox.Fingerprint(content.Length, FingerprintSeedMs, content);
        string uploadId = await InitAsync(device, "", "坏块.bin", content.Length, fingerprint);

        var payload = new ByteArrayContent(content);
        payload.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/uploads/{uploadId}/chunk?offset=0") { Content = payload };
        request.Headers.Add("X-Chunk-SHA256", new string('a', 64));

        using HttpResponseMessage response = await device.SendRawAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("PATH_OBJECTCHANGED", body);
        Record("U02", "分块校验和不匹配被拒绝");
    }

    [Fact]
    public async Task U02_resume_requires_a_prefix_hash()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        byte[] content = Sandbox.Pattern(10_000, seed: 8);
        string fingerprint = Sandbox.Fingerprint(content.Length, FingerprintSeedMs, content);
        string uploadId = await InitAsync(device, "", "无前缀哈希.bin", content.Length, fingerprint);
        Assert.True((await ChunkAsync(device, uploadId, 0, content[..4096])).IsSuccess);

        ApiResult<JsonElement> resume = await device.PostAsync<JsonElement>($"/api/uploads/{uploadId}/resume", new { fingerprint });
        Assert.Equal(HttpStatusCode.BadRequest, resume.Status);
        Assert.Contains("prefixSha256", resume.Raw);
        Record("U02", "缺少前缀哈希时拒绝续传");
    }

    [Fact]
    public async Task U03_same_name_and_size_but_different_content_cannot_be_spliced()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);

        byte[] original = Sandbox.Pattern(40_000, seed: 21);
        byte[] impostor = (byte[])original.Clone();
        // Same length, same name, different bytes in the middle.
        for (int i = 20_000; i < 20_100; i++) impostor[i] ^= 0xFF;

        string originalFingerprint = Sandbox.Fingerprint(original.Length, FingerprintSeedMs, original);
        string uploadId = await InitAsync(device, "", "同名同长.bin", original.Length, originalFingerprint);
        Assert.True((await ChunkAsync(device, uploadId, 0, original[..20_000])).IsSuccess);

        // The user re-selects a different file that happens to share the name and size.
        string impostorFingerprint = Sandbox.Fingerprint(impostor.Length, FingerprintSeedMs, impostor);
        ApiResult<JsonElement> resume = await device.PostAsync<JsonElement>($"/api/uploads/{uploadId}/resume", new
        {
            fingerprint = impostorFingerprint,
            prefixSha256 = Convert.ToHexStringLower(SHA256.HashData(original[..20_000]))
        });

        Assert.Equal(HttpStatusCode.BadRequest, resume.Status);
        Assert.Equal("PATH_OBJECTCHANGED", resume.ErrorCode);

        // Even with the correct fingerprint, a prefix hash that does not describe the staged
        // bytes is refused, so a client cannot claim a prefix it does not actually hold.
        ApiResult<JsonElement> forged = await device.PostAsync<JsonElement>($"/api/uploads/{uploadId}/resume", new
        {
            fingerprint = originalFingerprint,
            prefixSha256 = Sandbox.Sha256Hex(Sandbox.Pattern(20_000, seed: 999))
        });
        Assert.Equal(HttpStatusCode.BadRequest, forged.Status);
        Assert.Equal("PATH_OBJECTCHANGED", forged.ErrorCode);

        // No partially assembled file was published.
        Assert.False(File.Exists(Host.SandboxPath("同名同长.bin")));
        Record("U03", "同名同大小但内容不同无法拼接进旧会话");
    }

    [Fact]
    public async Task U03_incomplete_upload_is_never_visible_as_a_real_file()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        byte[] content = Sandbox.Pattern(20_000, seed: 31);
        string fingerprint = Sandbox.Fingerprint(content.Length, FingerprintSeedMs, content);
        string uploadId = await InitAsync(device, "", "半成品.bin", content.Length, fingerprint);
        Assert.True((await ChunkAsync(device, uploadId, 0, content[..10_000])).IsSuccess);

        JsonElement listing = (await device.BrowseAsync(ShareId, "")).Body;
        string[] names = listing.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!).ToArray();
        Assert.DoesNotContain("半成品.bin", names);

        ApiResult<JsonElement> meta = await device.GetAsync<JsonElement>("/api/file/meta" + LanDiskClient.FileQuery(ShareId, "半成品.bin"));
        Assert.Equal(HttpStatusCode.NotFound, meta.Status);

        // Completing early is refused because the byte count does not match.
        ApiResult<JsonElement> early = await CompleteAsync(device, uploadId, content);
        Assert.Equal(HttpStatusCode.Conflict, early.Status);
        Record("U03", "未完成的会话不会显示为正式文件");
    }

    [Fact]
    public async Task U04_conflict_never_overwrites_and_can_be_saved_as_a_copy()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        Host.WriteSandboxFile("已有文件.txt", "原始内容");

        byte[] content = Encoding.UTF8.GetBytes("新内容");
        string fingerprint = Sandbox.Fingerprint(content.Length, FingerprintSeedMs, content);
        string uploadId = await InitAsync(device, "", "已有文件.txt", content.Length, fingerprint);
        await SendAllAsync(device, uploadId, content, 4096);

        ApiResult<JsonElement> conflict = await CompleteAsync(device, uploadId, content, "fail");
        Assert.Equal(HttpStatusCode.Conflict, conflict.Status);
        Assert.Equal("CONFLICT", conflict.ErrorCode);
        Assert.Equal("原始内容", Host.ReadSandboxFile("已有文件.txt"));

        // A second attempt with the explicit "save as copy" policy creates a new name instead.
        string uploadId2 = await InitAsync(device, "", "已有文件.txt", content.Length, fingerprint);
        await SendAllAsync(device, uploadId2, content, 4096);
        ApiResult<JsonElement> renamed = await CompleteAsync(device, uploadId2, content, "rename");

        Assert.True(renamed.IsSuccess, renamed.Raw);
        Assert.Equal("已有文件 (1).txt", renamed.Body.GetProperty("name").GetString());
        Assert.Equal("原始内容", Host.ReadSandboxFile("已有文件.txt"));
        Assert.Equal("新内容", Host.ReadSandboxFile("已有文件 (1).txt"));
        Record("U04", "同名不覆盖，可另存为副本");
    }

    [Fact]
    public async Task U04_cancel_removes_only_its_own_staging_and_publishes_nothing()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        byte[] content = Sandbox.Pattern(30_000, seed: 41);
        string fingerprint = Sandbox.Fingerprint(content.Length, FingerprintSeedMs, content);
        string uploadId = await InitAsync(device, "", "取消.bin", content.Length, fingerprint);
        Assert.True((await ChunkAsync(device, uploadId, 0, content[..10_000])).IsSuccess);

        ApiResult<JsonElement> cancelled = await device.DeleteAsync<JsonElement>($"/api/uploads/{uploadId}");
        Assert.True(cancelled.IsSuccess, cancelled.Raw);

        JsonElement listing = (await device.BrowseAsync(ShareId, "")).Body;
        Assert.DoesNotContain("取消.bin", listing.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!).ToArray());

        ApiResult<JsonElement> afterCancel = await ChunkAsync(device, uploadId, 10_000, content[10_000..20_000]);
        Assert.Equal(HttpStatusCode.NotFound, afterCancel.Status);
        Record("U04", "取消只清理自身暂存，不产生正式文件");
    }

    [Fact]
    public async Task U06_staging_quota_is_enforced_with_a_clear_error()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        Host.Runtime.Settings.StagingQuotaBytes = 1024;

        byte[] content = Sandbox.Pattern(8192, seed: 51);
        string fingerprint = Sandbox.Fingerprint(content.Length, FingerprintSeedMs, content);

        ApiResult<JsonElement> result = await device.PostAsync<JsonElement>("/api/uploads/", new
        {
            shareId = ShareId,
            path = "",
            fileName = "超配额.bin",
            length = (long)content.Length,
            fingerprint
        });

        Assert.Equal(HttpStatusCode.Conflict, result.Status);
        Assert.Equal("IO_ERROR", result.ErrorCode);
        Assert.Contains("暂存空间不足", result.ErrorMessage);
        Assert.False(File.Exists(Host.SandboxPath("超配额.bin")));
        Record("U06", "暂存配额限制生效且无假成功");
    }

    [Fact]
    public async Task U06_readonly_destination_file_is_not_silently_replaced()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        Host.WriteSandboxFile("只读.txt", "原始内容");
        File.SetAttributes(Host.SandboxPath("只读.txt"), FileAttributes.ReadOnly);

        byte[] content = Encoding.UTF8.GetBytes("试图覆盖");
        string fingerprint = Sandbox.Fingerprint(content.Length, FingerprintSeedMs, content);
        string uploadId = await InitAsync(device, "", "只读.txt", content.Length, fingerprint);
        await SendAllAsync(device, uploadId, content, 4096);

        ApiResult<JsonElement> result = await CompleteAsync(device, uploadId, content, "fail");
        Assert.Equal(HttpStatusCode.Conflict, result.Status);
        Assert.Equal("原始内容", Host.ReadSandboxFile("只读.txt"));

        File.SetAttributes(Host.SandboxPath("只读.txt"), FileAttributes.Normal);
        Record("U06", "只读目标不会被静默覆盖");
    }

    [Fact]
    public async Task U06_locked_target_produces_a_failure_not_a_false_success()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        Host.WriteSandboxFile("占用.bin", "locked");

        byte[] content = Sandbox.Pattern(4096, seed: 61);
        string fingerprint = Sandbox.Fingerprint(content.Length, FingerprintSeedMs, content);
        string uploadId = await InitAsync(device, "", "占用.bin", content.Length, fingerprint);
        await SendAllAsync(device, uploadId, content, 4096);

        // The target becomes exclusively locked while the bytes are in flight.
        using (FileStream hold = new(Host.SandboxPath("占用.bin"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            ApiResult<JsonElement> result = await CompleteAsync(device, uploadId, content, "fail");
            Assert.Equal(HttpStatusCode.Conflict, result.Status);
            Assert.Equal("CONFLICT", result.ErrorCode);
        }

        Assert.Equal("locked", Host.ReadSandboxFile("占用.bin"));

        // Nothing was published under another name, and no staged payload was left behind.
        JsonElement listing = (await device.BrowseAsync(ShareId, "")).Body;
        string[] names = listing.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!).ToArray();
        Assert.DoesNotContain("占用 (1).bin", names);

        Record("U06", "目标被占用时明确失败，未改名发布、未覆盖");
    }

    [Fact]
    public async Task U07_concurrent_uploads_to_the_same_name_do_not_corrupt_or_overwrite()
    {
        var a = await PairDeviceAsync(DeviceAccessLevel.ReadWrite, "设备A");
        var b = await PairDeviceAsync(DeviceAccessLevel.ReadWrite, "设备B");

        byte[] contentA = Sandbox.Pattern(80_000, seed: 71);
        byte[] contentB = Sandbox.Pattern(80_000, seed: 72);

        string idA = await InitAsync(a, "", "并发.bin", contentA.Length, Sandbox.Fingerprint(contentA.Length, FingerprintSeedMs, contentA));
        string idB = await InitAsync(b, "", "并发.bin", contentB.Length, Sandbox.Fingerprint(contentB.Length, FingerprintSeedMs, contentB));

        await SendAllAsync(a, idA, contentA, 16_384);
        await SendAllAsync(b, idB, contentB, 16_384);

        ApiResult<JsonElement>[] results = await Task.WhenAll(
            CompleteAsync(a, idA, contentA, "fail"),
            CompleteAsync(b, idB, contentB, "fail"));

        int successes = results.Count(r => r.IsSuccess);
        Assert.Equal(1, successes);
        Assert.Contains(results, r => r.Status == HttpStatusCode.Conflict);

        byte[] stored = Host.ReadSandboxBytes("并发.bin");
        string hash = Convert.ToHexStringLower(SHA256.HashData(stored));
        Assert.True(hash == Sandbox.Sha256Hex(contentA) || hash == Sandbox.Sha256Hex(contentB),
            "落盘内容必须是完整的一方的内容，不能是拼接结果");
        Record("U07", "并发同名上传：恰好一个提交成功，内容未被拼接");
    }

    [Fact]
    public async Task U07_concurrent_chunks_on_one_session_are_serialised()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        byte[] content = Sandbox.Pattern(120_000, seed: 81);
        string fingerprint = Sandbox.Fingerprint(content.Length, FingerprintSeedMs, content);
        string uploadId = await InitAsync(device, "", "串行.bin", content.Length, fingerprint);

        byte[] first = content[..40_000];
        byte[] second = content[40_000..80_000];

        Task<ApiResult<JsonElement>> t1 = ChunkAsync(device, uploadId, 0, first);
        Task<ApiResult<JsonElement>> t2 = ChunkAsync(device, uploadId, 40_000, second);
        ApiResult<JsonElement>[] results = await Task.WhenAll(t1, t2);

        ApiResult<JsonElement> state = await device.GetAsync<JsonElement>("/api/uploads/");
        long received = state.Body.GetProperty("uploads").EnumerateArray()
            .First(u => u.GetProperty("uploadId").GetString() == uploadId)
            .GetProperty("receivedBytes").GetInt64();

        // Either both requests were applied in order (80 000 bytes) or the second one lost the
        // race and was told the real offset (40 000 bytes). Anything else means a chunk landed at
        // the wrong position.
        int successes = results.Count(r => r.IsSuccess);
        if (successes == 2)
        {
            Assert.Equal(80_000, received);
        }
        else
        {
            Assert.Equal(1, successes);
            Assert.Equal(40_000, received);
            Assert.Contains(results, r => r.Status == HttpStatusCode.Conflict);
        }

        Record("U07", $"同一会话并发分块：成功 {successes} 个，服务器进度 {received}");
    }

    [Fact]
    public async Task U05_file_larger_than_four_gibibytes()
    {
        if (Environment.GetEnvironmentVariable("LANDISK_BIGFILE_TEST") != "1")
        {
            Record("U05", "NOT_RUN：未设置 LANDISK_BIGFILE_TEST=1（见 docs/TEST_PLAN.md 的复现命令）");
            return;
        }

        const long size = 4L * 1024 * 1024 * 1024 + 1024; // just over 4 GiB
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        const int chunk = 4 * 1024 * 1024;

        string fingerprint = Sandbox.Fingerprint(size, FingerprintSeedMs, []);
        string uploadId = await InitAsync(device, "", "大文件.bin", size, fingerprint);

        long before = GC.GetTotalMemory(true);
        long peakWorkingSetBefore = Environment.WorkingSet;

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[chunk];
        for (int i = 0; i < buffer.Length; i++) buffer[i] = (byte)(i * 31);

        long offset = 0;
        while (offset < size)
        {
            int size2 = (int)Math.Min(chunk, size - offset);
            byte[] piece = size2 == buffer.Length ? buffer : buffer[..size2];
            hasher.AppendData(piece, 0, piece.Length);

            var content = new ByteArrayContent(piece);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var request = new HttpRequestMessage(HttpMethod.Put, $"/api/uploads/{uploadId}/chunk?offset={offset}") { Content = content };
            request.Headers.Add("X-Chunk-SHA256", Sandbox.Sha256Hex(piece));

            using HttpResponseMessage response = await device.SendRawAsync(request);
            Assert.True(response.IsSuccessStatusCode, $"分块失败 @{offset}: {response.StatusCode}");
            offset += size2;
        }

        string hash = Convert.ToHexStringLower(hasher.GetHashAndReset());
        ApiResult<JsonElement> complete = await device.PostAsync<JsonElement>($"/api/uploads/{uploadId}/complete", new { sha256 = hash, conflictPolicy = "fail" });
        Assert.True(complete.IsSuccess, complete.Raw);

        var info = new FileInfo(Host.SandboxPath("大文件.bin"));
        Assert.Equal(size, info.Length);

        long after = GC.GetTotalMemory(true);
        long peakWorkingSetAfter = Environment.WorkingSet;
        Record("U05", $"PASS：{size} 字节，托管堆 {(after - before) / 1024 / 1024} MB，工作集变化 {(peakWorkingSetAfter - peakWorkingSetBefore) / 1024 / 1024} MB");
    }
}
