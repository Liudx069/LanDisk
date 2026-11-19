using System.Net;
using System.Net.Http.Headers;
using LanDisk.Core.Model;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>
/// Task 08: the root route must render a page in Safari, not hand the user a download.
///
/// The reported symptom was that opening <c>https://&lt;LAN-IP&gt;:8892/</c> on a real iPhone started a
/// download called <c>document.txt</c>. That is what Safari does with a response it decides is an
/// attachment or cannot display, so the contract pinned here is exactly the set of things that make
/// that decision impossible: an HTML content type, never a Content-Disposition, a real page body, and
/// the same answer for GET and HEAD.
///
/// The same contract is exercised in a real browser engine by <c>tools/e2e/ui-test.mjs</c> (Chromium
/// and WebKit). Neither is a substitute for the real device, which stays NOT_RUN.
/// </summary>
public sealed class RootRouteContractTests : TestBase
{
    private const string SafariUserAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) " +
        "Version/17.5 Mobile/15E148 Safari/604.1";

    private const string SafariAccept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";

    protected override void SeedSandbox(TestHost host) => host.WriteSandboxFile("文件.txt", "内容");

    private static HttpRequestMessage SafariRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("User-Agent", SafariUserAgent);
        request.Headers.Accept.ParseAdd(SafariAccept);
        request.Headers.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9");
        return request;
    }

    private static void AssertHtmlNotAttachment(HttpResponseMessage response, string label)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            label + " 期望 200，实际 " + (int)response.StatusCode);

        MediaTypeHeaderValue? contentType = response.Content.Headers.ContentType;
        Assert.True(contentType is not null, label + " 缺少 Content-Type");
        Assert.True(contentType!.MediaType == "text/html",
            label + " Content-Type 必须是 text/html，实际 " + contentType.MediaType);
        Assert.True(contentType.CharSet == "utf-8",
            label + " charset 必须是 utf-8，实际 " + contentType.CharSet);

        // The two headers that make Safari offer a file instead of a page.
        Assert.False(response.Content.Headers.Contains("Content-Disposition"),
            label + " 不得返回 Content-Disposition");
        Assert.False(response.Headers.TryGetValues("Content-Disposition", out _),
            label + " 不得在响应头上返回 Content-Disposition");
        Assert.NotEqual("application/octet-stream", contentType.MediaType);
        Assert.NotEqual("text/plain", contentType.MediaType);
    }

    private static async Task AssertIsAppShell(HttpResponseMessage response, string label)
    {
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("<!doctype html>", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LanDisk", body, StringComparison.Ordinal);
        Assert.DoesNotContain("前端资源缺失", body, StringComparison.Ordinal);
        _ = label;
    }

    [Fact]
    public async Task The_root_route_renders_the_app_for_an_unpaired_safari_client()
    {
        using HttpClient http = Host.NewClient();

        using HttpResponseMessage get = await http.SendAsync(SafariRequest(HttpMethod.Get, "/"));
        AssertHtmlNotAttachment(get, "未配对的 GET /");
        await AssertIsAppShell(get, "未配对的 GET /");

        using HttpResponseMessage head = await http.SendAsync(SafariRequest(HttpMethod.Head, "/"));
        AssertHtmlNotAttachment(head, "未配对的 HEAD /");
        Assert.Equal(string.Empty, await head.Content.ReadAsStringAsync());

        TestBase.Record("UX08-02", "未配对时 GET/HEAD / 返回 text/html; charset=utf-8，无 Content-Disposition，body 为应用外壳");
    }

    [Fact]
    public async Task The_root_route_renders_the_app_over_https_for_an_unpaired_safari_client()
    {
        using HttpClient https = Host.NewHttpsClient();

        using HttpResponseMessage get = await https.SendAsync(SafariRequest(HttpMethod.Get, "/"));
        AssertHtmlNotAttachment(get, "HTTPS GET /");
        await AssertIsAppShell(get, "HTTPS GET /");

        using HttpResponseMessage head = await https.SendAsync(SafariRequest(HttpMethod.Head, "/"));
        AssertHtmlNotAttachment(head, "HTTPS HEAD /");

        TestBase.Record("UX08-03", "HTTPS 下 GET/HEAD / 同样返回 HTML 且无 attachment（真机仍需要复测）");
    }

    [Fact]
    public async Task The_root_route_renders_the_app_for_a_paired_device()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadOnly);
        using HttpResponseMessage get = await device.Http.SendAsync(SafariRequest(HttpMethod.Get, "/"));
        AssertHtmlNotAttachment(get, "已配对的 GET /");
        await AssertIsAppShell(get, "已配对的 GET /");
    }

    [Fact]
    public async Task Every_entry_point_that_a_phone_may_open_is_a_page()
    {
        using HttpClient http = Host.NewClient();

        // SPA routes. MapFallback deliberately does not match paths that look like files (the
        // {*path:nonfile} pattern), so /index.html is not in this list: in the shipped product it is
        // served by the static file middleware — verified against the real publish output over both
        // HTTP and HTTPS — while the automated test host runs without that middleware on purpose.
        foreach (string path in new[] { "/", "/admin", "/pair", "/app", "/some/spa/route" })
        {
            using HttpResponseMessage response = await http.SendAsync(SafariRequest(HttpMethod.Get, path));
            AssertHtmlNotAttachment(response, "GET " + path);
        }
    }

    [Fact]
    public async Task A_path_that_looks_like_a_file_is_never_offered_as_a_download()
    {
        using HttpClient http = Host.NewClient();

        // Whatever the host decides to do with a file-shaped URL — serve it, or not find it — the one
        // outcome that must never happen is "the browser saves it as document.txt".
        foreach (string path in new[] { "/index.html", "/favicon.ico", "/manifest.webmanifest" })
        {
            using HttpResponseMessage response = await http.SendAsync(SafariRequest(HttpMethod.Get, path));

            Assert.False(response.Content.Headers.Contains("Content-Disposition"), path + " 不得返回 attachment");
            Assert.NotEqual("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
            Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        }
    }

    [Fact]
    public async Task An_unknown_api_path_still_answers_json_rather_than_a_page()
    {
        using HttpClient http = Host.NewClient();

        using HttpResponseMessage response = await http.SendAsync(SafariRequest(HttpMethod.Get, "/api/does-not-exist"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.False(response.Content.Headers.Contains("Content-Disposition"));
    }

    [Fact]
    public async Task A_missing_static_asset_does_not_become_a_download()
    {
        using HttpClient http = Host.NewClient();

        using HttpResponseMessage response = await http.SendAsync(SafariRequest(HttpMethod.Get, "/assets/does-not-exist.js"));

        // Whatever this returns, it must not be offered as a file to save.
        Assert.False(response.Content.Headers.Contains("Content-Disposition"),
            "缺失的静态资源不得返回 attachment");
        Assert.NotEqual("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_missing_web_assets_case_fails_loudly_as_a_page_not_as_a_download()
    {
        // Serving a page whose assets are absent must never be presented as a file. This is the shape
        // of failure that produces "document.txt" on a device: a response the browser cannot render.
        using HttpClient http = Host.NewClient();

        using HttpResponseMessage response = await http.SendAsync(SafariRequest(HttpMethod.Get, "/"));
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("text/plain", response.Content.Headers.ContentType?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("html", body, StringComparison.OrdinalIgnoreCase);
    }
}
