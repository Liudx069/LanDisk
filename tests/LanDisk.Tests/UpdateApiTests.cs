using System.Net;
using System.Text;
using System.Text.Json;
using LanDisk.Core.Model;
using LanDisk.Tests.Infrastructure;
using LanDisk.Update;

namespace LanDisk.Tests;

/// <summary>
/// The HTTP surface of the update system.
///
/// These tests use the real host with the real compiled-in release public key, so the rejection
/// cases here are the same rejections a real installation performs. The acceptance path needs the
/// private release key, so it is verified against a real release candidate by the release
/// verification step rather than by signing anything from inside the test process.
/// </summary>
public sealed class UpdateApiTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private LanDiskClient _admin = null!;
    private UpdateTestKit _kit = null!;
    private string _installRoot = string.Empty;

    public async Task InitializeAsync()
    {
        _kit = new UpdateTestKit(nameof(UpdateApiTests));
        _installRoot = _kit.CreateInstall("install", "1.2.0", new Dictionary<string, string>
        {
            ["LanDisk.exe"] = "old-exe",
            ["LanDisk.Server.dll"] = "old-server"
        });

        _host = await TestHost.CreateAsync(nameof(UpdateApiTests), options => options.InstallRoot = _installRoot);
        _admin = new LanDiskClient(_host.NewClient());
        await _admin.AdminLoginAsync(_host.AdminToken);
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        _kit.Dispose();
    }

    // ------------------------------------------------------------------ health

    [Fact]
    public async Task The_self_check_reports_ok_over_loopback_and_names_the_built_in_key()
    {
        ApiResult<JsonElement> result = await _admin.GetAsync<JsonElement>("/api/admin/health");

        Assert.True(result.IsSuccess, result.Raw);
        Assert.True(result.Body.GetProperty("ok").GetBoolean(), result.Raw);
        Assert.False(string.IsNullOrWhiteSpace(result.Body.GetProperty("version").GetString()));

        List<string> ids = [.. result.Body.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("id").GetString()!)];

        // These are the checks the updater relies on to decide "commit" versus "roll back".
        Assert.Contains("data-root", ids);
        Assert.Contains("settings", ids);
        Assert.Contains("ports", ids);
        Assert.Contains("shares-store", ids);
        Assert.Contains("devices-store", ids);
        Assert.Contains("listeners", ids);
        Assert.Contains("browse-smoke", ids);
        Assert.Contains("build-info", ids);
        Assert.Contains("release-key", ids);
        Assert.Contains("update-state", ids);

        JsonElement releaseKey = result.Body.GetProperty("checks").EnumerateArray().First(c => c.GetProperty("id").GetString() == "release-key");
        Assert.Equal("ok", releaseKey.GetProperty("status").GetString());

        // A test installation has no updater, and that must be a warning rather than a failure:
        // otherwise every post-update health check on a plain LanDisk.Server.exe install would
        // report the machine as broken and trigger a pointless rollback.
        JsonElement updater = result.Body.GetProperty("checks").EnumerateArray().First(c => c.GetProperty("id").GetString() == "updater");
        Assert.Equal("warn", updater.GetProperty("status").GetString());
        Assert.True(result.Body.GetProperty("degraded").GetBoolean());

        TestBase.Record("UPDATE-09", $"更新自检接口通过并包含 {ids.Count} 项检查（缺少 updater 时降级但不失败）");
    }

    [Fact]
    public async Task The_self_check_is_clean_when_the_installation_is_complete()
    {
        using var kit = new UpdateTestKit(nameof(The_self_check_is_clean_when_the_installation_is_complete));
        string install = kit.CreateInstall("full-install", "1.2.0", new Dictionary<string, string>
        {
            ["LanDisk.exe"] = "shell",
            ["LanDisk.Server.exe"] = "server"
        });
        string updaterDir = Directory.CreateDirectory(Path.Combine(install, UpdateLayout.UpdaterDirectoryName)).FullName;
        await File.WriteAllTextAsync(Path.Combine(updaterDir, UpdateLayout.UpdaterFileName), "updater");

        await using TestHost host = await TestHost.CreateAsync(nameof(The_self_check_is_clean_when_the_installation_is_complete),
            options => options.InstallRoot = install);
        var admin = new LanDiskClient(host.NewClient());
        await admin.AdminLoginAsync(host.AdminToken);

        ApiResult<JsonElement> result = await admin.GetAsync<JsonElement>("/api/admin/health");

        Assert.True(result.IsSuccess, result.Raw);
        Assert.True(result.Body.GetProperty("ok").GetBoolean(), result.Raw);
        Assert.False(result.Body.GetProperty("degraded").GetBoolean(), result.Raw);
        Assert.All(result.Body.GetProperty("checks").EnumerateArray(), c =>
            Assert.Equal("ok", c.GetProperty("status").GetString()));
    }

    // ------------------------------------------------------------------ authorisation

    [Fact]
    public async Task A_paired_device_can_never_reach_the_update_surface()
    {
        string token = await _admin.CreatePairingTokenAsync();

        var device = new LanDiskClient(_host.NewClient());
        await device.RedeemAsync(token, "手机");

        foreach (string url in new[] { "/api/admin/update/status", "/api/admin/update/history", "/api/admin/update/plan" })
        {
            ApiResult<JsonElement> result = await device.GetAsync<JsonElement>(url);
            Assert.False(result.IsSuccess, $"{url} 不应对已配对设备开放。");
            Assert.True(result.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                $"{url} 返回了 {result.Status}。");
        }

        // Installing software is a machine-owner action, not a share capability.
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("not a package"));
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/admin/update/upload") { Content = content };
        using HttpResponseMessage response = await device.SendRawAsync(upload);
        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);

        TestBase.Record("UPDATE-10", "已配对手机访问更新接口一律被拒绝（401/403）");
    }

    [Fact]
    public async Task Applying_an_update_requires_an_explicit_confirmation()
    {
        ApiResult<JsonElement> result = await _admin.PostAsync<JsonElement>("/api/admin/update/apply", new { });

        Assert.False(result.IsSuccess);
        Assert.Equal("CONFIRM_REQUIRED", result.ErrorCode);
    }

    [Fact]
    public async Task Applying_without_a_staged_package_is_refused()
    {
        ApiResult<JsonElement> result = await _admin.PostAsync<JsonElement>("/api/admin/update/apply", new { confirm = true });

        Assert.False(result.IsSuccess);
        Assert.Equal("NOTHING_STAGED", result.ErrorCode);
    }

    // ------------------------------------------------------------------ status

    [Fact]
    public async Task Status_reports_the_installation_the_key_and_the_absence_of_an_updater()
    {
        ApiResult<JsonElement> result = await _admin.GetAsync<JsonElement>("/api/admin/update/status");

        Assert.True(result.IsSuccess, result.Raw);
        Assert.Equal(LanDisk.Server.Endpoints.BuildInfo.Version, result.Body.GetProperty("currentVersion").GetString());
        Assert.Equal(_installRoot, result.Body.GetProperty("installRoot").GetString(), ignoreCase: true);
        Assert.True(result.Body.GetProperty("releaseKeyValid").GetBoolean());
        Assert.Equal(ReleasePublicKey.KeyId, result.Body.GetProperty("releaseKeyId").GetString());
        Assert.Equal(UpdatePackage.FileExtension, result.Body.GetProperty("packageExtension").GetString());
        Assert.False(result.Body.GetProperty("updaterAvailable").GetBoolean());
        Assert.Equal("None", result.Body.GetProperty("stage").GetString());
        // The status document omits nulls, so "no pending package" is the absence of the property.
        Assert.False(result.Body.TryGetProperty("pending", out _));
        Assert.Equal(JsonValueKind.Array, result.Body.GetProperty("history").ValueKind);
    }

    [Fact]
    public async Task Status_becomes_unavailable_as_soon_as_an_updater_is_present_only_for_real_installs()
    {
        // A published installation always carries updater\LanDisk.Updater.exe. The test
        // installation does not, which is exactly the "plain LanDisk.Server.exe publish from
        // task 01" shape the UI must explain instead of failing mysteriously at apply time.
        string updaterDir = Directory.CreateDirectory(Path.Combine(_installRoot, UpdateLayout.UpdaterDirectoryName)).FullName;
        await File.WriteAllTextAsync(Path.Combine(updaterDir, UpdateLayout.UpdaterFileName), "placeholder");

        try
        {
            ApiResult<JsonElement> result = await _admin.GetAsync<JsonElement>("/api/admin/update/status");
            Assert.True(result.Body.GetProperty("updaterAvailable").GetBoolean());
            Assert.NotNull(result.Body.GetProperty("updaterPath").GetString());
        }
        finally
        {
            File.Delete(Path.Combine(updaterDir, UpdateLayout.UpdaterFileName));
        }
    }

    // ------------------------------------------------------------------ upload rejections

    [Fact]
    public async Task Uploading_a_package_signed_by_a_stranger_is_rejected_with_the_release_key_intact()
    {
        string package = _kit.BuildPackage("stranger", new PackageSpec { Version = "1.3.0" });

        ApiResult<JsonElement> result = await UploadAsync(package, "stranger.lupd");

        Assert.True(result.ErrorCode == "UNKNOWN_KEY_ID", $"code={result.ErrorCode} status={result.Status} raw={result.Raw}");

        // Nothing may be left staged, and the status must still report an idle installation.
        ApiResult<JsonElement> status = await _admin.GetAsync<JsonElement>("/api/admin/update/status");
        Assert.Equal("None", status.Body.GetProperty("stage").GetString());
        Assert.False(status.Body.TryGetProperty("pending", out _));

        TestBase.Record("UPDATE-11", $"服务端使用内置发布公钥拒绝陌生签名：{result.ErrorCode}");
    }

    [Fact]
    public async Task Uploading_something_that_is_not_a_package_is_rejected()
    {
        string path = Path.Combine(_kit.Root, "not-a-package.lupd");
        await File.WriteAllTextAsync(path, "这不是更新包");

        ApiResult<JsonElement> result = await UploadAsync(path, "not-a-package.lupd");

        Assert.True(result.ErrorCode == "NOT_A_ZIP", $"code={result.ErrorCode} status={result.Status} raw={result.Raw}");
    }

    [Fact]
    public async Task Uploading_an_empty_body_is_rejected()
    {
        string path = Path.Combine(_kit.Root, "empty.lupd");
        await File.WriteAllBytesAsync(path, []);

        ApiResult<JsonElement> result = await UploadAsync(path, "empty.lupd");

        Assert.True(result.ErrorCode == "PACKAGE_EMPTY", $"code={result.ErrorCode} status={result.Status} raw={result.Raw}");
    }

    [Fact]
    public async Task Discarding_with_nothing_staged_is_harmless()
    {
        ApiResult<JsonElement> result = await _admin.PostAsync<JsonElement>("/api/admin/update/discard", new { });

        Assert.True(result.IsSuccess, result.Raw);
        Assert.True(result.Body.GetProperty("ok").GetBoolean());
    }

    // ------------------------------------------------------------------ helpers

    private async Task<ApiResult<JsonElement>> UploadAsync(string packagePath, string fileName)
    {
        byte[] bytes = await File.ReadAllBytesAsync(packagePath);
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/update/upload") { Content = content };
        request.Headers.Add("X-LanDisk-Package-Name", Uri.EscapeDataString(fileName));

        using HttpResponseMessage response = await _admin.SendRawAsync(request);
        string raw = await response.Content.ReadAsStringAsync();

        JsonElement body = default;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try { body = JsonSerializer.Deserialize<JsonElement>(raw, TestHost.Json); } catch { }
        }

        return new ApiResult<JsonElement>(response.StatusCode, body, raw);
    }
}
