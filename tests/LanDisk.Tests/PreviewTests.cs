using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>F03: safe preview, explicit downgrade, and no script execution from file content.</summary>
public sealed class PreviewTests : TestBase
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    protected override void SeedSandbox(TestHost host)
    {
        host.WriteSandboxBytes("图片/单像素.png", OnePixelPng);
        host.WriteSandboxFile("文本/说明.txt", "第一行\n第二行 <script>alert(1)</script>\n第三行", new UTF8Encoding(false));
        host.WriteSandboxFile("文本/utf16.txt", "带 BOM 的文本", new UnicodeEncoding(false, true));
        host.WriteSandboxFile("文本/evil.html", "<html><body><script>document.title='pwned'</script></body></html>");
        host.WriteSandboxFile("文本/evil.svg", "<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>");
        host.WriteSandboxFile("文本/script.js", "alert('x')");
        host.WriteSandboxFile("文档/说明.pdf", "%PDF-1.4\n%fake\n");
        host.WriteSandboxBytes("二进制/随机.bin", Sandbox.Pattern(4096));
        // < > : " / \ | ? * are illegal in NTFS names, so the hostile name uses the remaining
        // markup- and URL-significant characters that are actually allowed on disk.
        host.WriteSandboxFile("文件&%#'()+,-;=@[]!.txt", "名字里有特殊字符");
    }

    [Fact]
    public async Task F03_png_is_served_inline_with_image_content_type()
    {
        var device = await PairDeviceAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/api/file/content" + LanDiskClient.FileQuery(ShareId, "图片/单像素.png", "disposition=inline"));
        using HttpResponseMessage response = await device.SendRawAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        byte[] bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(OnePixelPng, bytes);
        Record("F03", "PNG 内联预览成功");
    }

    [Fact]
    public async Task F03_text_preview_returns_json_and_never_markup()
    {
        var device = await PairDeviceAsync();
        ApiResult<JsonElement> result = await device.GetAsync<JsonElement>("/api/file/text" + LanDiskClient.FileQuery(ShareId, "文本/说明.txt"));

        Assert.Equal(HttpStatusCode.OK, result.Status);
        string text = result.Body.GetProperty("text").GetString()!;
        Assert.Contains("<script>alert(1)</script>", text, StringComparison.Ordinal);
        Assert.False(result.Body.GetProperty("truncated").GetBoolean());

        // The response itself is JSON, so the browser never parses the file as HTML.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/file/text" + LanDiskClient.FileQuery(ShareId, "文本/说明.txt"));
        using HttpResponseMessage response = await device.SendRawAsync(request);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Record("F03", "文本预览以 JSON 返回，未渲染为页面");
    }

    [Fact]
    public async Task F03_utf16_text_is_decoded()
    {
        var device = await PairDeviceAsync();
        ApiResult<JsonElement> result = await device.GetAsync<JsonElement>("/api/file/text" + LanDiskClient.FileQuery(ShareId, "文本/utf16.txt"));
        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.Contains("带 BOM 的文本", result.Body.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("文本/evil.html", "text/html")]
    [InlineData("文本/evil.svg", "image/svg+xml")]
    [InlineData("文本/script.js", "text/javascript")]
    public async Task F03_active_content_is_forced_to_download(string path, string expectedInlineType)
    {
        var device = await PairDeviceAsync();

        // Even when the client explicitly asks for inline rendering, active content is attached.
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/api/file/content" + LanDiskClient.FileQuery(ShareId, path, "disposition=inline"));
        using HttpResponseMessage response = await device.SendRawAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ContentDispositionHeaderValue disposition = response.Content.Headers.ContentDisposition!;
        Assert.Equal("attachment", disposition.DispositionType);
        Assert.NotEqual(expectedInlineType, response.Content.Headers.ContentType?.MediaType);

        // The default (no disposition) path behaves the same way and is also an attachment.
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Record("F03", path + " 被强制为附件下载");
    }

    [Fact]
    public async Task F03_binary_content_is_refused_by_the_text_endpoint()
    {
        var device = await PairDeviceAsync();
        ApiResult<JsonElement> result = await device.GetAsync<JsonElement>("/api/file/text" + LanDiskClient.FileQuery(ShareId, "二进制/随机.bin"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, result.Status);
        Assert.Equal("NOT_TEXT", result.ErrorCode);
        Record("F03", "二进制文件文本预览被拒绝");
    }

    [Fact]
    public async Task F03_pdf_is_served_inline()
    {
        var device = await PairDeviceAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/api/file/content" + LanDiskClient.FileQuery(ShareId, "文档/说明.pdf", "disposition=inline"));
        using HttpResponseMessage response = await device.SendRawAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", response.Content.Headers.ContentDisposition?.DispositionType);
    }

    [Fact]
    public async Task F03_hostile_file_name_is_returned_as_data()
    {
        var device = await PairDeviceAsync();
        JsonElement listing = (await device.BrowseAsync(ShareId, "")).Body;
        JsonElement entry = listing.GetProperty("entries").EnumerateArray()
            .First(e => e.GetProperty("name").GetString()!.Contains("文件&%"));
        string name = entry.GetProperty("name").GetString()!;
        Assert.Contains("&", name);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/file/download" + LanDiskClient.FileQuery(ShareId, name));
        using HttpResponseMessage response = await device.SendRawAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Record("F03", "特殊字符文件名按数据返回，未触发标记解析");
    }

    [Fact]
    public async Task F03_text_preview_is_truncated_when_larger_than_the_limit()
    {
        var device = await PairDeviceAsync();
        string big = new string('A', 200_000);
        Host.WriteSandboxFile("文本/big.txt", big);

        Host.Runtime.Settings.TextPreviewMaxBytes = 4096;

        ApiResult<JsonElement> result = await device.GetAsync<JsonElement>("/api/file/text" + LanDiskClient.FileQuery(ShareId, "文本/big.txt"));
        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.True(result.Body.GetProperty("truncated").GetBoolean());
        Assert.True(result.Body.GetProperty("text").GetString()!.Length <= 4096);
        Record("F03", "超长文本预览被截断并明确提示");
    }

    [Fact]
    public async Task F03_oversized_image_is_downgraded_with_a_clear_error()
    {
        var device = await PairDeviceAsync();
        Host.WriteSandboxBytes("图片/big.png", Sandbox.Pattern(200_000));
        Host.Runtime.Settings.InlinePreviewMaxBytes = 1024;

        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/api/file/content" + LanDiskClient.FileQuery(ShareId, "图片/big.png", "disposition=inline"));
        using HttpResponseMessage response = await device.SendRawAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("PREVIEW_TOO_LARGE", body);

        // Downloading the same file still works.
        using var download = new HttpRequestMessage(HttpMethod.Get, "/api/file/download" + LanDiskClient.FileQuery(ShareId, "图片/big.png"));
        using HttpResponseMessage downloadResponse = await device.SendRawAsync(download);
        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);
        Record("F03", "过大图片明确降级，下载仍可用");
    }
}
