using System.Net;
using LanDisk.Core.Media;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>
/// Content-Disposition formatting. Downloads must keep the real (often non-ASCII) file name where
/// the client supports RFC 5987, and must still produce a usable name where it does not.
/// </summary>
public sealed class HeaderFormatTests
{
    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("video-h264-aac.mp4", "video-h264-aac.mp4")]
    [InlineData("说明.txt", "file.txt")]
    [InlineData("报告-2026.txt", "2026.txt")]
    [InlineData("中文 名称 文件.bin", "file.bin")]
    public void ASCII_FALLBACK_IS_READABLE(string fileName, string expected)
    {
        Assert.Equal(expected, MimeTypes.BuildAsciiFallback(fileName));
    }

    [Fact]
    public void ASCII_FALLBACK_NEVER_IS_A_ROW_OF_UNDERSCORES()
    {
        foreach (string name in new[] { "界面测试共享-20260927-004502.zip", "说明.txt", "📷 相册 🎬.jpg", "日本語のファイル.doc" })
        {
            string fallback = MimeTypes.BuildAsciiFallback(name);
            Assert.DoesNotMatch("^_+", fallback);
            Assert.True(fallback.Count(char.IsLetterOrDigit) >= 3, $"回退名不可用: {name} -> {fallback}");
        }
    }

    [Fact]
    public void CONTENT_DISPOSITION_PUTS_THE_UTF8_NAME_FIRST_AND_KEEPS_A_FALLBACK()
    {
        string header = MimeTypes.ContentDisposition("报告 2026.pdf", attachment: true);

        Assert.StartsWith("attachment;", header, StringComparison.Ordinal);
        int extended = header.IndexOf("filename*=UTF-8''", StringComparison.Ordinal);
        int plain = header.IndexOf("filename=\"", StringComparison.Ordinal);

        Assert.True(extended >= 0, "缺少 RFC 5987 扩展参数");
        Assert.True(plain >= 0, "缺少普通 filename 回退");
        Assert.True(extended < plain, "扩展参数应排在普通参数之前");

        Assert.Contains(Uri.EscapeDataString("报告 2026.pdf"), header, StringComparison.Ordinal);
        Assert.Contains("filename=\"2026.pdf\"", header, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DOWNLOAD_HEADER_KEEPS_THE_CHINESE_NAME_IN_THE_EXTENDED_PARAMETER()
    {
        await using TestHost host = await TestHost.CreateAsync("HeaderFormat");
        var admin = new LanDiskClient(host.NewClient());
        await admin.AdminLoginAsync(host.AdminToken);

        host.WriteSandboxFile("中文 名称 文件.txt", "内容");
        string shareId = (await admin.CreateShareAsync(host.SandboxRoot))
            .GetProperty("id").GetString()!;

        string token = await admin.CreatePairingTokenAsync();
        var device = new LanDiskClient(host.NewClient());
        await device.RedeemAsync(token, "头部检查");

        using HttpResponseMessage response = await device.SendRawAsync(new HttpRequestMessage(
            HttpMethod.Get, "/api/file/download" + LanDiskClient.FileQuery(shareId, "中文 名称 文件.txt")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string disposition = response.Content.Headers.ContentDisposition!.ToString();

        Assert.Contains("filename*=UTF-8''", disposition, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString("中文 名称 文件.txt"), disposition, StringComparison.Ordinal);
        Assert.Contains("filename=\"file.txt\"", disposition, StringComparison.Ordinal);
    }
}
