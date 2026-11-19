using System.Net;
using System.Text.Json;
using LanDisk.Core.Model;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>Response hardening applied to every request, including error responses.</summary>
public sealed class SecurityHeaderTests : TestBase
{
    protected override void SeedSandbox(TestHost host) => host.WriteSandboxFile("文件.txt", "内容");

    private static void AssertHardened(HttpResponseMessage response)
    {
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("same-origin", response.Headers.GetValues("Cross-Origin-Resource-Policy").Single());

        string csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("object-src 'none'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("'wasm-unsafe-eval'", csp);
        Assert.DoesNotContain("'unsafe-eval'", csp.Replace("'wasm-unsafe-eval'", "", StringComparison.Ordinal));
        Assert.DoesNotContain("unsafe-inline' 'unsafe-eval", csp);
    }

    [Fact]
    public async Task Security_headers_are_present_on_api_responses()
    {
        var device = await PairDeviceAsync();
        using HttpResponseMessage response = await device.SendRawAsync(new HttpRequestMessage(HttpMethod.Get, "/api/health"));

        AssertHardened(response);
        // The framework may reorder the directives, so assert on their presence.
        string cacheControl = response.Headers.GetValues("Cache-Control").Single();
        Assert.Contains("no-store", cacheControl);
        Assert.Contains("no-cache", cacheControl);
        Assert.Contains("must-revalidate", cacheControl);
        Assert.False(response.Headers.Contains("Server"), "不应暴露服务器标识");
        Record("SEC", "API 响应安全头与私有缓存策略正确");
    }

    [Fact]
    public async Task Security_headers_are_present_on_error_responses_too()
    {
        var device = await PairDeviceAsync();
        using HttpResponseMessage response = await device.SendRawAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/browse?shareId=missing&path=.."));

        Assert.True((int)response.StatusCode >= 400);
        AssertHardened(response);
        Record("SEC", "错误响应同样带有安全头");
    }

    [Fact]
    public async Task Error_bodies_do_not_leak_absolute_paths_or_stack_traces()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);

        ApiResult<JsonElement> result = await device.PostAsync<JsonElement>("/api/file/rename", new
        {
            shareId = ShareId,
            path = "文件.txt",
            newName = "a/b",
            version = "1-1"
        });

        Assert.Equal(HttpStatusCode.BadRequest, result.Status);
        Assert.DoesNotContain(Host.SandboxRoot, result.Raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("   at ", result.Raw, StringComparison.Ordinal);
        Assert.DoesNotContain("LanDisk.Core", result.Raw, StringComparison.Ordinal);

        // An unexpected internal failure is mapped to a generic code with no detail.
        ApiResult<JsonElement> missing = await device.BrowseAsync(ShareId, "不存在的目录");
        Assert.Equal(HttpStatusCode.NotFound, missing.Status);
        Assert.DoesNotContain(Host.SandboxRoot, missing.Raw, StringComparison.OrdinalIgnoreCase);
        Record("SEC", "错误信息不泄露绝对路径与堆栈");
    }

    [Fact]
    public async Task Media_responses_are_private_and_not_shared_cached()
    {
        var device = await PairDeviceAsync();
        using HttpResponseMessage response = await device.SendRawAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/file/content" + LanDiskClient.FileQuery(ShareId, "文件.txt", "disposition=inline")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", string.Join(",", response.Headers.GetValues("Cache-Control")));
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Record("SEC", "私有内容禁用共享缓存");
    }

    [Fact]
    public async Task Share_listing_never_exposes_absolute_paths_to_a_device()
    {
        var device = await PairDeviceAsync();
        ApiResult<JsonElement> me = await device.GetAsync<JsonElement>("/api/me");

        Assert.DoesNotContain(Host.SandboxRoot, me.Raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("volumeGuidPath", me.Raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rootPath", me.Raw, StringComparison.OrdinalIgnoreCase);
        Record("SEC", "手机端看不到本机绝对路径与卷标识");
    }
}
