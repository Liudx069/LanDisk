using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>M01/M02/M04 (media serving and HTTP range semantics) and D01/D02 (downloads).</summary>
public sealed class MediaAndDownloadTests : TestBase
{
    private byte[] _payload = [];

    protected override void SeedSandbox(TestHost host)
    {
        // A deterministic 300 KB payload stands in for a media file for the range tests. Media
        // container/encoding support is asserted separately and never claimed from the extension.
        _payload = Sandbox.Pattern(300 * 1024, seed: 7);
        host.WriteSandboxBytes("媒体/样本.mp4", _payload);
        host.WriteSandboxBytes("媒体/样本.wav", _payload);
        host.WriteSandboxBytes("媒体/不支持的编码.mkv", _payload);
        host.WriteSandboxBytes("下载/中文 名称 文件.bin", _payload);
        host.WriteSandboxBytes("下载/empty.bin", []);
    }

    private string Mp4Query => LanDiskClient.FileQuery(ShareId, "媒体/样本.mp4");

    [Fact]
    public async Task M02_full_request_returns_200_with_accept_ranges()
    {
        var device = await PairDeviceAsync();
        using HttpResponseMessage response = await device.SendRawAsync(new HttpRequestMessage(HttpMethod.Get, "/api/file/content" + Mp4Query));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(_payload.Length, response.Content.Headers.ContentLength);
        Assert.Equal("bytes", response.Headers.AcceptRanges.Single());
        Assert.Equal("video/mp4", response.Content.Headers.ContentType?.MediaType);
        byte[] body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(Sandbox.Sha256Hex(_payload), Sandbox.Sha256Hex(body));
        Record("M02", "完整请求 200 + Accept-Ranges");
    }

    [Fact]
    public async Task M02_head_returns_headers_without_a_body()
    {
        var device = await PairDeviceAsync();
        using HttpResponseMessage response = await device.SendRawAsync(new HttpRequestMessage(HttpMethod.Head, "/api/file/content" + Mp4Query));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(_payload.Length, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task M02_single_range_returns_206_and_the_exact_bytes()
    {
        var device = await PairDeviceAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/file/content" + Mp4Query);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1023);
        using HttpResponseMessage response = await device.SendRawAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(1024, response.Content.Headers.ContentLength);
        Assert.Equal($"bytes 0-1023/{_payload.Length}", response.Content.Headers.ContentRange?.ToString());
        byte[] body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(_payload[..1024], body);
        Record("M02", "单区间 206 正确");
    }

    [Fact]
    public async Task M02_middle_and_open_ended_ranges_are_correct()
    {
        var device = await PairDeviceAsync();

        using var middle = new HttpRequestMessage(HttpMethod.Get, "/api/file/content" + Mp4Query);
        middle.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(100_000, 199_999);
        using HttpResponseMessage middleResponse = await device.SendRawAsync(middle);
        Assert.Equal(HttpStatusCode.PartialContent, middleResponse.StatusCode);
        byte[] middleBody = await middleResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(_payload[100_000..200_000], middleBody);

        using var openEnded = new HttpRequestMessage(HttpMethod.Get, "/api/file/content" + Mp4Query);
        openEnded.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(299_000, null);
        using HttpResponseMessage openResponse = await device.SendRawAsync(openEnded);
        Assert.Equal(HttpStatusCode.PartialContent, openResponse.StatusCode);
        Assert.Equal($"bytes 299000-{_payload.Length - 1}/{_payload.Length}", openResponse.Content.Headers.ContentRange?.ToString());
        Record("M02", "中间区间与开放区间正确");
    }

    [Fact]
    public async Task M02_suffix_range_returns_the_tail()
    {
        var device = await PairDeviceAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/file/content" + Mp4Query);
        request.Headers.TryAddWithoutValidation("Range", "bytes=-1024");
        using HttpResponseMessage response = await device.SendRawAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal($"bytes {_payload.Length - 1024}-{_payload.Length - 1}/{_payload.Length}",
            response.Content.Headers.ContentRange?.ToString());
        byte[] body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(_payload[^1024..], body);
        Record("M02", "末尾区间正确");
    }

    [Fact]
    public async Task M02_unsatisfiable_range_returns_416_with_content_range()
    {
        var device = await PairDeviceAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/file/content" + Mp4Query);
        request.Headers.TryAddWithoutValidation("Range", $"bytes={_payload.Length + 10}-");
        using HttpResponseMessage response = await device.SendRawAsync(request);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Equal($"bytes */{_payload.Length}", response.Content.Headers.ContentRange?.ToString());
        Record("M02", "越界区间返回 416");
    }

    [Fact]
    public async Task M02_conditional_requests_are_honoured()
    {
        var device = await PairDeviceAsync();

        using HttpResponseMessage initial = await device.SendRawAsync(new HttpRequestMessage(HttpMethod.Get, "/api/file/content" + Mp4Query));
        string etag = initial.Headers.ETag!.ToString();

        // If-None-Match with the current validator → 304 with no body.
        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/api/file/content" + Mp4Query);
        conditional.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using HttpResponseMessage notModified = await device.SendRawAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Empty(await notModified.Content.ReadAsByteArrayAsync());

        // If-Range with a matching validator → the range is applied.
        using var matching = new HttpRequestMessage(HttpMethod.Get, "/api/file/content" + Mp4Query);
        matching.Headers.TryAddWithoutValidation("If-Range", etag);
        matching.Headers.TryAddWithoutValidation("Range", "bytes=0-99");
        using HttpResponseMessage partial = await device.SendRawAsync(matching);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);

        // If-Range with a stale validator → the range is ignored and the full entity is sent.
        using var stale = new HttpRequestMessage(HttpMethod.Get, "/api/file/content" + Mp4Query);
        stale.Headers.TryAddWithoutValidation("If-Range", "\"stale-validator\"");
        stale.Headers.TryAddWithoutValidation("Range", "bytes=0-99");
        using HttpResponseMessage full = await device.SendRawAsync(stale);
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal(_payload.Length, full.Content.Headers.ContentLength);
        Record("M02", "条件请求 304 / If-Range 语义正确");
    }

    [Fact]
    public async Task M02_multi_range_falls_back_to_a_full_response()
    {
        var device = await PairDeviceAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/file/content" + Mp4Query);
        request.Headers.TryAddWithoutValidation("Range", "bytes=0-99,200-299");
        using HttpResponseMessage response = await device.SendRawAsync(request);

        // RFC 9110 allows ignoring Range; the documented behaviour is a plain 200.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(_payload.Length, response.Content.Headers.ContentLength);
        Record("M02", "多区间请求回退为 200（已文档化）");
    }

    [Fact]
    public async Task M04_unknown_media_container_is_served_with_its_declared_type_and_downloadable()
    {
        var device = await PairDeviceAsync();

        using HttpResponseMessage stream = await device.SendRawAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/file/content" + LanDiskClient.FileQuery(ShareId, "媒体/不支持的编码.mkv", "disposition=inline")));

        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        Assert.Equal("video/x-matroska", stream.Content.Headers.ContentType?.MediaType);

        using HttpResponseMessage download = await device.SendRawAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/file/download" + LanDiskClient.FileQuery(ShareId, "媒体/不支持的编码.mkv")));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(Sandbox.Sha256Hex(_payload), Sandbox.Sha256Hex(await download.Content.ReadAsByteArrayAsync()));
        Record("M04", "未支持容器仍可下载，未声称可播放");
    }

    [Fact]
    public async Task M01_audio_is_served_with_range_support_for_native_playback()
    {
        var device = await PairDeviceAsync();
        using HttpResponseMessage response = await device.SendRawAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/file/content" + LanDiskClient.FileQuery(ShareId, "媒体/样本.wav", "disposition=inline")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/wav", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("bytes", response.Headers.AcceptRanges.Single());
        Record("M01", "音频以内联方式提供并支持 Range（原生播放所需）");
    }

    [Fact]
    public async Task D01_download_bytes_and_chinese_filename_are_correct()
    {
        var device = await PairDeviceAsync();
        using HttpResponseMessage response = await device.SendRawAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/file/download" + LanDiskClient.FileQuery(ShareId, "下载/中文 名称 文件.bin")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        byte[] body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(_payload.Length, body.Length);
        Assert.Equal(Sandbox.Sha256Hex(_payload), Convert.ToHexStringLower(SHA256.HashData(body)));

        string disposition = response.Content.Headers.ContentDisposition!.ToString();
        Assert.Contains("attachment", disposition);
        Assert.Contains("filename*=UTF-8''", disposition);
        Assert.Contains(Uri.EscapeDataString("中文 名称 文件.bin"), disposition);
        Record("D01", "下载字节一致，中文文件名按 RFC 6266 编码");
    }

    [Fact]
    public async Task D01_zero_byte_file_downloads_cleanly()
    {
        var device = await PairDeviceAsync();
        using HttpResponseMessage response = await device.SendRawAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/file/download" + LanDiskClient.FileQuery(ShareId, "下载/empty.bin")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(0, response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task D02_download_is_recorded_as_handed_over_not_as_saved_on_the_phone()
    {
        var device = await PairDeviceAsync();
        using HttpResponseMessage response = await device.SendRawAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/file/download" + LanDiskClient.FileQuery(ShareId, "下载/中文 名称 文件.bin")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResult<JsonElement> transfers = await device.GetAsync<JsonElement>("/api/transfers?limit=50");
        JsonElement[] records = transfers.Body.GetProperty("transfers").EnumerateArray()
            .Where(t => t.GetProperty("direction").GetString() == "Download")
            .ToArray();

        Assert.NotEmpty(records);

        // The server records that it started serving bytes. There is deliberately no field that
        // could be read as "the iPhone saved this file".
        foreach (JsonElement record in records)
        {
            Assert.False(record.TryGetProperty("savedOnDevice", out _));

            // 1.7.7 起下载记录会走到终态（缺陷 E 的修复）：先前这里断言 transferredBytes 恒为 0，
            // 那实际上是在**固化缺陷** —— 记录被永久留在 InProgress，页面上表现为"0 B / 0 B 进行中"。
            // 仍然保留的意思只有一条：这些字段说明服务端把内容交给了浏览器，
            // 它**不等于**手机已保存成功（savedOnDevice 依旧不存在）。
            string state = record.GetProperty("state").GetString() ?? string.Empty;
            Assert.False(state.Equals("inProgress", StringComparison.OrdinalIgnoreCase),
                "下载结束后不得仍处于进行中");
            Assert.Equal(307200, record.GetProperty("transferredBytes").GetInt64());
        }
        Record("D02", "下载状态仅表示服务端已交给浏览器");
    }

    [Fact]
    public async Task D02_download_requires_authentication()
    {
        var anonymous = new LanDiskClient(Host.NewClient());
        ApiResult<JsonElement> result = await anonymous.GetAsync<JsonElement>(
            "/api/file/download" + LanDiskClient.FileQuery(ShareId, "下载/中文 名称 文件.bin"));

        Assert.Equal(HttpStatusCode.Unauthorized, result.Status);
        Record("D02", "未认证下载被拒绝");
    }
}
