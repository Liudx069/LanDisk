using System.Net;
using System.Text.Json;
using LanDisk.Core.Model;
using LanDisk.Core.Paths;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>F04 (path traversal), F05 (links and races), F06 (internal directory isolation).</summary>
public sealed class PathSafetyTests : TestBase
{
    protected override void SeedSandbox(TestHost host)
    {
        host.WriteSandboxFile("keep/note.txt", "hello");
        Directory.CreateDirectory(host.SandboxPath("empty"));
        Directory.CreateDirectory(Path.Combine(host.Root, "outside"));
        host.WriteSandboxBytes("blob.bin", Sandbox.Pattern(128));
        File.WriteAllText(Path.Combine(host.Root, "outside", "secret.txt"), "outside-secret");
    }

    [Theory]
    [InlineData("../outside/secret.txt", "PATH_PARENTTRAVERSAL")]
    [InlineData("..\\outside\\secret.txt", "PATH_PARENTTRAVERSAL")]
    [InlineData("keep/../../outside/secret.txt", "PATH_PARENTTRAVERSAL")]
    [InlineData("%2e%2e/outside/secret.txt", "PATH_INVALIDENCODING")]
    [InlineData("C:\\Windows\\win.ini", "PATH_DRIVELETTER")]
    [InlineData("\\\\server\\share\\x", "PATH_UNCPATH")]
    [InlineData("//server/share/x", "PATH_UNCPATH")]
    [InlineData("\\\\.\\C:", "PATH_DEVICEPATH")]
    [InlineData("keep/note.txt:stream", "PATH_ALTERNATEDATASTREAM")]
    [InlineData("keep/CON", "PATH_RESERVEDDEVICENAME")]
    [InlineData("keep/aux.txt", "PATH_RESERVEDDEVICENAME")]
    [InlineData("keep/trailing.", "PATH_TRAILINGDOTORSPACE")]
    [InlineData("keep/trailing ", "PATH_TRAILINGDOTORSPACE")]
    public async Task F04_traversal_and_illegal_forms_are_rejected(string path, string expectedCode)
    {
        // A write capable device so that the write endpoints reach path validation rather than
        // stopping at the permission check (that behaviour is covered by A03).
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);

        ApiResult<JsonElement> browse = await device.BrowseAsync(ShareId, path);
        Assert.Equal(HttpStatusCode.BadRequest, browse.Status);
        Assert.Equal(expectedCode, browse.ErrorCode);

        ApiResult<JsonElement> download = await device.GetAsync<JsonElement>("/api/file/download" + LanDiskClient.FileQuery(ShareId, path));
        Assert.Equal(HttpStatusCode.BadRequest, download.Status);

        ApiResult<JsonElement> rename = await device.PostAsync<JsonElement>("/api/file/rename", new { shareId = ShareId, path, newName = "x", version = "1-1" });
        Assert.Equal(HttpStatusCode.BadRequest, rename.Status);

        Record("F04", $"{path} -> {browse.ErrorCode}");
    }

    [Fact]
    public async Task F04_single_encoded_traversal_in_the_query_string_is_rejected()
    {
        var device = await PairDeviceAsync();

        // Sent without re-escaping, so the server receives a single-encoded "%2e%2e" and the
        // framework decodes it to ".." before the handler runs.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/browse?shareId={ShareId}&path=%2e%2e%2foutside");
        using HttpResponseMessage response = await device.SendRawAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("PATH_PARENTTRAVERSAL", body);
        Record("F04", "单次编码的 .. 被拒绝");
    }

    [Fact]
    public async Task F04_double_encoded_traversal_is_rejected()
    {
        var device = await PairDeviceAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/browse?shareId={ShareId}&path=%252e%252e%252foutside");
        using HttpResponseMessage response = await device.SendRawAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("PATH_INVALIDENCODING", body);
        Record("F04", "重复编码的 .. 被拒绝");
    }

    [Fact]
    public async Task F04_absolute_path_and_device_path_on_write_endpoints()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);

        ApiResult<JsonElement> delete = await device.PostAsync<JsonElement>("/api/file/delete", new
        {
            shareId = ShareId,
            path = @"\\.\PhysicalDrive0",
            version = "1-1"
        });
        Assert.Equal(HttpStatusCode.BadRequest, delete.Status);
        Assert.Equal("PATH_DEVICEPATH", delete.ErrorCode);
    }

    [Fact]
    public void F04_root_prefix_containment_is_segment_wise()
    {
        // A sibling directory that merely shares a string prefix must never be treated as
        // living inside the share.
        Assert.False(PathRules.IsWithinRoot(@"D:\data\share", @"D:\data\share2\secret.txt"));
        Assert.False(PathRules.IsWithinRoot(@"D:\data\share", @"D:\data\share-evil\x"));
        Assert.True(PathRules.IsWithinRoot(@"D:\data\share", @"D:\data\share\sub\file.txt"));
        Assert.True(PathRules.IsWithinRoot(@"D:\data\share", @"D:\DATA\SHARE\sub"));
        Assert.False(PathRules.IsWithinRoot(@"D:\data\share", @"D:\data"));
    }

    [Fact]
    public async Task F05_junction_is_refused_as_a_share_root()
    {
        string junction = Path.Combine(Host.Root, "junction-root");
        string target = Path.Combine(Host.Root, "outside");

        if (!TestFileSystem.TryCreateJunction(junction, target))
        {
            Record("F05", "junction 创建失败，跳过（NOT_RUN）");
            return;
        }

        ApiResult<JsonElement> result = await Admin.PostAsync<JsonElement>("/api/admin/shares", new
        {
            rootPath = junction,
            displayName = "junction",
            allowUpload = true
        });

        Assert.Equal(HttpStatusCode.BadRequest, result.Status);
        Assert.Equal("REPARSE_ROOT", result.ErrorCode);
        Record("F05", "junction 根目录被拒绝: " + result.ErrorCode);
    }

    [Fact]
    public async Task F05_junction_inside_a_share_is_refused()
    {
        string escaping = Host.SandboxPath("escape");
        string target = Path.Combine(Host.Root, "outside");

        if (!TestFileSystem.TryCreateJunction(escaping, target))
        {
            Record("F05", "junction 创建失败，跳过（NOT_RUN）");
            return;
        }

        var device = await PairDeviceAsync();

        // The junction itself must not be traversed...
        ApiResult<JsonElement> throughLink = await device.BrowseAsync(ShareId, "escape");
        Assert.Equal(HttpStatusCode.BadRequest, throughLink.Status);
        Assert.Contains(throughLink.ErrorCode, new[] { "PATH_REPARSEPOINT", "PATH_OUTSIDESHARE" });

        // ...and a file reached through it must not be readable either.
        ApiResult<JsonElement> file = await device.GetAsync<JsonElement>(
            "/api/file/download" + LanDiskClient.FileQuery(ShareId, "escape/secret.txt"));
        Assert.Equal(HttpStatusCode.BadRequest, file.Status);

        // Deep traversal through the junction must fail as well.
        ApiResult<JsonElement> deep = await device.BrowseAsync(ShareId, "escape/..");
        Assert.True(deep.Status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound);

        Record("F05", "junction 内部路径被拒绝: " + throughLink.ErrorCode);
    }

    [Fact]
    public async Task F05_hard_linked_file_cannot_be_modified()
    {
        string original = Host.SandboxPath("keep/note.txt");
        string link = Host.SandboxPath("hardlink.txt");

        if (!TestFileSystem.TryCreateHardLink(link, original))
        {
            Record("F05", "硬链接创建失败，跳过（NOT_RUN）");
            return;
        }

        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        JsonElement listing = (await device.BrowseAsync(ShareId, "")).Body;
        JsonElement entry = listing.GetProperty("entries").EnumerateArray()
            .First(e => e.GetProperty("name").GetString() == "hardlink.txt");
        string version = entry.GetProperty("version").GetString()!;

        ApiResult<JsonElement> delete = await device.PostAsync<JsonElement>("/api/file/delete", new
        {
            shareId = ShareId,
            path = "hardlink.txt",
            version
        });
        Assert.Equal(HttpStatusCode.BadRequest, delete.Status);
        Assert.Equal("PATH_HARDLINK", delete.ErrorCode);

        // Reading is still allowed: only mutation is refused.
        ApiResult<JsonElement> meta = await device.GetAsync<JsonElement>("/api/file/meta" + LanDiskClient.FileQuery(ShareId, "hardlink.txt"));
        Assert.Equal(HttpStatusCode.OK, meta.Status);
        Record("F05", "硬链接写操作被拒绝");
    }

    [Fact]
    public async Task F05_stale_version_token_is_refused()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        Host.WriteSandboxFile("stale.txt", "first version");

        JsonElement listing = (await device.BrowseAsync(ShareId, "")).Body;
        JsonElement entry = listing.GetProperty("entries").EnumerateArray()
            .First(e => e.GetProperty("name").GetString() == "stale.txt");
        string version = entry.GetProperty("version").GetString()!;

        // Someone else replaces the file with different content and a different length.
        await Task.Delay(20);
        Host.WriteSandboxFile("stale.txt", "a completely different and longer payload");

        ApiResult<JsonElement> delete = await device.PostAsync<JsonElement>("/api/file/delete", new
        {
            shareId = ShareId,
            path = "stale.txt",
            version
        });

        Assert.Equal(HttpStatusCode.BadRequest, delete.Status);
        Assert.Equal("PATH_OBJECTCHANGED", delete.ErrorCode);
        Assert.True(File.Exists(Host.SandboxPath("stale.txt")), "对象变化时不得执行删除");
        Record("F05", "版本令牌不匹配时拒绝写入");
    }

    [Fact]
    public async Task F06_internal_store_directory_is_invisible_and_unreachable()
    {
        // Force the per-volume store to materialise inside the sandbox volume.
        Host.WriteSandboxFile("keep/note.txt", "hello");
        string storeRoot = Path.Combine(
            Path.GetPathRoot(Host.SandboxRoot)!,
            LanDisk.Core.Configuration.AppPaths.VolumeStoreName);
        Directory.CreateDirectory(Path.Combine(storeRoot, "recycle", "decoy"));
        File.WriteAllText(Path.Combine(storeRoot, "recycle", "decoy", "secret.txt"), "internal-only");

        var device = await PairDeviceAsync();

        ApiResult<JsonElement> browseStore = await device.BrowseAsync(ShareId, LanDisk.Core.Configuration.AppPaths.VolumeStoreName);
        Assert.Equal(HttpStatusCode.BadRequest, browseStore.Status);
        Assert.Equal("PATH_OUTSIDESHARE", browseStore.ErrorCode);

        ApiResult<JsonElement> downloadStore = await device.GetAsync<JsonElement>(
            "/api/file/download" + LanDiskClient.FileQuery(ShareId, LanDisk.Core.Configuration.AppPaths.VolumeStoreName + "/recycle/decoy/secret.txt"));
        Assert.Equal(HttpStatusCode.BadRequest, downloadStore.Status);

        Record("F06", "内部存储目录被拒绝: " + browseStore.ErrorCode);
    }

    [Fact]
    public async Task F06_internal_store_is_filtered_from_listings()
    {
        Directory.CreateDirectory(Host.SandboxPath(LanDisk.Core.Configuration.AppPaths.VolumeStoreName));
        File.WriteAllText(Host.SandboxPath(LanDisk.Core.Configuration.AppPaths.VolumeStoreName + "/marker.txt"), "x");
        Host.WriteSandboxFile("visible.txt", "x");

        var device = await PairDeviceAsync();
        JsonElement listing = (await device.BrowseAsync(ShareId, "")).Body;
        string[] names = listing.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!)
            .ToArray();

        Assert.Contains("visible.txt", names);
        Assert.DoesNotContain(LanDisk.Core.Configuration.AppPaths.VolumeStoreName, names);
        Record("F06", "列表已过滤内部目录");
    }

    [Fact]
    public async Task F06_application_data_root_is_unreachable_even_from_a_wide_share()
    {
        // A share that contains the application's data root must still not expose it.
        JsonElement wide = await Admin.CreateShareAsync(Host.Root, name: "宽共享");
        string wideId = wide.GetProperty("id").GetString()!;
        var device = await PairDeviceAsync();

        string relativeToWide = Path.GetRelativePath(Host.Root, Host.DataRoot).Replace('\\', '/');
        ApiResult<JsonElement> browse = await device.BrowseAsync(wideId, relativeToWide);
        Assert.Equal(HttpStatusCode.BadRequest, browse.Status);
        Assert.Equal("PATH_OUTSIDESHARE", browse.ErrorCode);

        ApiResult<JsonElement> token = await device.GetAsync<JsonElement>(
            "/api/file/download" + LanDiskClient.FileQuery(wideId, relativeToWide + "/secrets/admin-token.txt"));
        Assert.Equal(HttpStatusCode.BadRequest, token.Status);

        Record("F06", "应用数据目录不可达: " + browse.ErrorCode);
    }

    [Fact]
    public async Task F06_volume_store_cannot_be_shared_through_the_admin_api()
    {
        string storeRoot = Path.Combine(Path.GetPathRoot(Host.SandboxRoot)!, LanDisk.Core.Configuration.AppPaths.VolumeStoreName);
        Directory.CreateDirectory(storeRoot);

        ApiResult<JsonElement> result = await Admin.PostAsync<JsonElement>("/api/admin/shares", new
        {
            rootPath = storeRoot,
            displayName = "内部目录"
        });

        Assert.Equal(HttpStatusCode.BadRequest, result.Status);
        Assert.Equal("INTERNAL_PATH", result.ErrorCode);
        Record("F06", "内部目录不能被共享");
    }

    [Fact]
    public async Task F06_application_install_directory_cannot_be_shared()
    {
        ApiResult<JsonElement> result = await Admin.PostAsync<JsonElement>("/api/admin/shares", new
        {
            rootPath = AppContext.BaseDirectory,
            displayName = "安装目录"
        });

        Assert.Equal(HttpStatusCode.BadRequest, result.Status);
        Assert.Equal("INTERNAL_PATH", result.ErrorCode);
        Record("F06", "安装目录不能被共享");
    }

    [Fact]
    public async Task F04_unc_share_root_is_refused()
    {
        ApiResult<JsonElement> result = await Admin.PostAsync<JsonElement>("/api/admin/shares", new
        {
            rootPath = @"\\localhost\c$\Windows",
            displayName = "unc"
        });

        Assert.Equal(HttpStatusCode.BadRequest, result.Status);
        Assert.Equal("UNC_NOT_SUPPORTED", result.ErrorCode);
    }
}
