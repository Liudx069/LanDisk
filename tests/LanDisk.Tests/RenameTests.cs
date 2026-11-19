using System.Net;
using System.Text.Json;
using LanDisk.Core.Model;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>R01: rename semantics, including case-only changes and hostile input.</summary>
public sealed class RenameTests : TestBase
{
    protected override void SeedSandbox(TestHost host)
    {
        host.WriteSandboxFile("文件.txt", "content");
        host.WriteSandboxFile("中文 名称.txt", "content");
        host.WriteSandboxFile("目录/里面的.txt", "content");
        host.WriteSandboxFile("Case.txt", "content");
        host.WriteSandboxFile("已有目标.txt", "target");
    }

    private async Task<string> VersionOf(LanDiskClient device, string name)
    {
        JsonElement listing = (await device.BrowseAsync(ShareId, "")).Body;
        return listing.GetProperty("entries").EnumerateArray()
            .First(e => e.GetProperty("name").GetString() == name)
            .GetProperty("version").GetString()!;
    }

    [Fact]
    public async Task R01_plain_rename_succeeds()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        string version = await VersionOf(device, "文件.txt");

        ApiResult<JsonElement> result = await device.PostAsync<JsonElement>("/api/file/rename", new
        {
            shareId = ShareId,
            path = "文件.txt",
            newName = "改名后.txt",
            version
        });

        Assert.True(result.IsSuccess, result.Raw);
        Assert.False(File.Exists(Host.SandboxPath("文件.txt")));
        Assert.True(File.Exists(Host.SandboxPath("改名后.txt")));
        Record("R01", "普通重命名成功");
    }

    [Fact]
    public async Task R01_chinese_and_space_rename_succeeds()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        string version = await VersionOf(device, "中文 名称.txt");

        ApiResult<JsonElement> result = await device.PostAsync<JsonElement>("/api/file/rename", new
        {
            shareId = ShareId,
            path = "中文 名称.txt",
            newName = "新的 中文 名字 🎬.txt",
            version
        });

        Assert.True(result.IsSuccess, result.Raw);
        Assert.True(File.Exists(Host.SandboxPath("新的 中文 名字 🎬.txt")));
        Record("R01", "中文、空格与 emoji 名称重命名成功");
    }

    [Fact]
    public async Task R01_directory_rename_succeeds()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        string version = await VersionOf(device, "目录");

        ApiResult<JsonElement> result = await device.PostAsync<JsonElement>("/api/file/rename", new
        {
            shareId = ShareId,
            path = "目录",
            newName = "新目录名",
            version
        });

        Assert.True(result.IsSuccess, result.Raw);
        Assert.True(File.Exists(Host.SandboxPath("新目录名/里面的.txt")));
        Record("R01", "文件夹重命名成功，内容保留");
    }

    [Fact]
    public async Task R01_case_only_rename_succeeds_and_is_not_treated_as_a_conflict()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        string version = await VersionOf(device, "Case.txt");

        ApiResult<JsonElement> result = await device.PostAsync<JsonElement>("/api/file/rename", new
        {
            shareId = ShareId,
            path = "Case.txt",
            newName = "CASE.txt",
            version
        });

        Assert.True(result.IsSuccess, result.Raw);

        string[] names = Directory.GetFiles(Host.SandboxRoot).Select(Path.GetFileName).ToArray()!;
        Assert.Contains("CASE.txt", names);
        Assert.DoesNotContain("Case.txt", names);
        Assert.DoesNotContain(names, n => n!.StartsWith(".landisk-casetmp", StringComparison.Ordinal));
        Record("R01", "仅大小写变化的重命名成功且未遗留临时名");
    }

    [Fact]
    public async Task R01_existing_target_is_refused()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        string version = await VersionOf(device, "文件.txt");

        ApiResult<JsonElement> result = await device.PostAsync<JsonElement>("/api/file/rename", new
        {
            shareId = ShareId,
            path = "文件.txt",
            newName = "已有目标.txt",
            version
        });

        Assert.Equal(HttpStatusCode.Conflict, result.Status);
        Assert.Equal("CONFLICT", result.ErrorCode);
        Assert.Equal("content", Host.ReadSandboxFile("文件.txt"));
        Assert.Equal("target", Host.ReadSandboxFile("已有目标.txt"));
        Record("R01", "重命名不覆盖已有目标");
    }

    [Theory]
    [InlineData("a/b.txt")]
    [InlineData("a\\b.txt")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("bad:name")]
    [InlineData("C:\\absolute.txt")]
    public async Task R01_illegal_new_names_are_refused(string newName)
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        string version = await VersionOf(device, "文件.txt");

        ApiResult<JsonElement> result = await device.PostAsync<JsonElement>("/api/file/rename", new
        {
            shareId = ShareId,
            path = "文件.txt",
            newName,
            version
        });

        Assert.Equal(HttpStatusCode.BadRequest, result.Status);
        Assert.True(File.Exists(Host.SandboxPath("文件.txt")), "拒绝重命名后原文件必须仍然存在");
        Record("R01", $"非法名称被拒绝: {newName}");
    }

    [Fact]
    public async Task R01_empty_new_name_is_refused()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        string version = await VersionOf(device, "文件.txt");

        ApiResult<JsonElement> result = await device.PostAsync<JsonElement>("/api/file/rename", new
        {
            shareId = ShareId,
            path = "文件.txt",
            newName = "",
            version
        });

        Assert.Equal(HttpStatusCode.BadRequest, result.Status);
    }

    [Fact]
    public async Task R01_stale_version_is_refused()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        string version = await VersionOf(device, "文件.txt");

        await Task.Delay(20);
        Host.WriteSandboxFile("文件.txt", "replaced content with a different length");

        ApiResult<JsonElement> result = await device.PostAsync<JsonElement>("/api/file/rename", new
        {
            shareId = ShareId,
            path = "文件.txt",
            newName = "不应发生.txt",
            version
        });

        Assert.Equal(HttpStatusCode.BadRequest, result.Status);
        Assert.Equal("PATH_OBJECTCHANGED", result.ErrorCode);
        Assert.False(File.Exists(Host.SandboxPath("不应发生.txt")));
    }

    [Fact]
    public async Task R01_share_root_cannot_be_renamed()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        ApiResult<JsonElement> result = await device.PostAsync<JsonElement>("/api/file/rename", new
        {
            shareId = ShareId,
            path = "",
            newName = "根目录改名",
            version = "0-0"
        });

        Assert.Equal(HttpStatusCode.Conflict, result.Status);
        Record("R01", "共享根目录不能被改名");
    }

    [Fact]
    public async Task R01_create_folder_round_trip()
    {
        var device = await PairDeviceAsync(DeviceAccessLevel.ReadWrite);
        ApiResult<JsonElement> created = await device.PostAsync<JsonElement>("/api/file/folder", new
        {
            shareId = ShareId,
            path = "",
            name = "新建 文件夹"
        });

        Assert.True(created.IsSuccess, created.Raw);
        Assert.True(Directory.Exists(Host.SandboxPath("新建 文件夹")));

        ApiResult<JsonElement> duplicate = await device.PostAsync<JsonElement>("/api/file/folder", new
        {
            shareId = ShareId,
            path = "",
            name = "新建 文件夹"
        });

        Assert.Equal(HttpStatusCode.Conflict, duplicate.Status);
        Record("R01", "新建文件夹及重名拒绝");
    }
}
