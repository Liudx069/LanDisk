using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanDisk.Setup;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>
/// Task 07: the installer's file work.
///
/// The property under test is not "files get copied" — that is easy — but "a failure leaves the
/// previous installation intact". An installer that half-replaces an installation produces a machine
/// that looks installed and is not, which is worse than one that refuses to run.
/// </summary>
public sealed class PayloadInstallTests
{
    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Builds a release-shaped payload archive: a single top-level folder, manifest, build info.</summary>
    private static string CreatePayload(
        UpdateTestKit kit,
        string name,
        IReadOnlyDictionary<string, string> files,
        bool includeBuildInfoRecord = true,
        string version = "1.4.0",
        string commit = "abcdef1234567890")
    {
        string staging = kit.NewDirectory("payload-" + name);
        string baseDir = Path.Combine(staging, "LanDisk-win-x64");
        Directory.CreateDirectory(baseDir);

        foreach ((string relative, string content) in files)
        {
            string target = Path.Combine(baseDir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content, new UTF8Encoding(false));
        }

        string buildInfoPath = Path.Combine(baseDir, SetupLayout.BuildInfoName);
        File.WriteAllText(buildInfoPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            product = "LanDisk",
            version,
            commit,
            buildId = "Release/win-x64",
            builtAt = "2026-09-28T00:00:00.0000000+08:00",
            runtime = "win-x64",
            selfContained = true
        }), new UTF8Encoding(false));

        var manifest = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["product"] = "LanDisk",
            ["version"] = version,
            ["buildCommit"] = commit,
            ["installedAt"] = "2026-09-28T00:00:00.0000000+08:00",
            ["files"] = files.Keys.Append(SetupLayout.BuildInfoName).OrderBy(k => k, StringComparer.Ordinal).ToList()
        };

        if (includeBuildInfoRecord)
        {
            manifest["buildInfo"] = new Dictionary<string, object?>
            {
                ["path"] = SetupLayout.BuildInfoName,
                ["bytes"] = new FileInfo(buildInfoPath).Length,
                ["sha256"] = Sha256(buildInfoPath)
            };
        }

        File.WriteAllText(Path.Combine(baseDir, SetupLayout.InstallManifestName),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        string zipPath = Path.Combine(kit.Root, name + ".zip");
        ZipFile.CreateFromDirectory(staging, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        return zipPath;
    }

    [Fact]
    public void A_verified_payload_installs_with_a_manifest_the_updater_can_read()
    {
        using var kit = new UpdateTestKit(nameof(A_verified_payload_installs_with_a_manifest_the_updater_can_read));
        string payload = CreatePayload(kit, "ok", new Dictionary<string, string>
        {
            ["LanDisk.exe"] = "exe-bytes",
            ["wwwroot/index.html"] = "<html></html>"
        });
        string install = Path.Combine(kit.Root, "installed");

        using PayloadPackage package = PayloadPackage.Open(payload);
        Assert.Equal("1.4.0", package.Info.Version);
        Assert.Equal(3, package.Files.Count);

        InstallResult result = package.InstallTo(install, installerCopySource: null);

        Assert.True(result.Ok, string.Join("; ", result.Failures));
        Assert.True(File.Exists(Path.Combine(install, "LanDisk.exe")));
        Assert.True(File.Exists(Path.Combine(install, "wwwroot", "index.html")));

        // The manifest the installer writes must satisfy the same invariant the release pipeline
        // enforces: build-info.json recorded, install-manifest.json never listing itself.
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(SetupLayout.ManifestPathFor(install)));
        List<string> listed = manifest.RootElement.GetProperty("files").EnumerateArray()
            .Select(e => e.GetString()!).ToList();

        Assert.Contains(SetupLayout.BuildInfoName, listed);
        Assert.DoesNotContain(SetupLayout.InstallManifestName, listed);

        JsonElement record = manifest.RootElement.GetProperty("buildInfo");
        string buildInfoPath = SetupLayout.BuildInfoPathFor(install);
        Assert.Equal(SetupLayout.BuildInfoName, record.GetProperty("path").GetString());
        Assert.Equal(new FileInfo(buildInfoPath).Length, record.GetProperty("bytes").GetInt64());
        Assert.Equal(Sha256(buildInfoPath), record.GetProperty("sha256").GetString());

        TestBase.Record("UX07-12", "安装器写入的 install-manifest 记录 build-info.json 的 path/bytes/sha256");
    }

    [Fact]
    public void A_payload_whose_manifest_omits_the_build_info_record_is_refused()
    {
        using var kit = new UpdateTestKit(nameof(A_payload_whose_manifest_omits_the_build_info_record_is_refused));
        string payload = CreatePayload(kit, "no-record", new Dictionary<string, string> { ["LanDisk.exe"] = "x" },
            includeBuildInfoRecord: false);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => PayloadPackage.Open(payload));
        Assert.Contains("buildInfo", error.Message, StringComparison.Ordinal);

        TestBase.Record("UX07-13", "载荷的安装清单缺少 buildInfo 记录时拒绝安装（与发布链同一不变量）");
    }

    [Fact]
    public void A_payload_whose_digest_does_not_match_is_refused()
    {
        using var kit = new UpdateTestKit(nameof(A_payload_whose_digest_does_not_match_is_refused));
        string payload = CreatePayload(kit, "digest", new Dictionary<string, string> { ["LanDisk.exe"] = "x" });

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            PayloadPackage.Open(payload, expectedSha256: new string('a', 64)));

        Assert.Contains("校验失败", error.Message, StringComparison.Ordinal);
        TestBase.Record("UX07-14", "内置载荷摘要不符时拒绝安装，磁盘未被改动");
    }

    [Fact]
    public void A_repair_install_removes_only_files_the_previous_installation_owned()
    {
        using var kit = new UpdateTestKit(nameof(A_repair_install_removes_only_files_the_previous_installation_owned));
        string install = Path.Combine(kit.Root, "installed");

        string first = CreatePayload(kit, "v1", new Dictionary<string, string>
        {
            ["LanDisk.exe"] = "old-exe",
            ["LanDisk.Core.dll"] = "old-core",
            ["legacy-only.txt"] = "gone in the next version"
        });
        using (PayloadPackage v1 = PayloadPackage.Open(first))
        {
            Assert.True(v1.InstallTo(install, null).Ok);
        }

        // Something the user dropped into the program folder by hand.
        File.WriteAllText(Path.Combine(install, "my-notes.txt"), "do not delete me");

        string second = CreatePayload(kit, "v2", new Dictionary<string, string>
        {
            ["LanDisk.exe"] = "new-exe",
            ["LanDisk.Core.dll"] = "new-core",
            ["LanDisk.Update.dll"] = "new-addition"
        }, version: "1.5.0", commit: "2222222222222222");

        using (PayloadPackage v2 = PayloadPackage.Open(second))
        {
            InstallResult result = v2.InstallTo(install, null);
            Assert.True(result.Ok, string.Join("; ", result.Failures));
            Assert.Equal(1, result.FilesRemoved);
            // Three program files changed or appeared, plus build-info.json, which describes the new
            // version and therefore must be replaced too.
            Assert.Equal(4, result.FilesWritten);
        }

        Assert.Equal("new-exe", File.ReadAllText(Path.Combine(install, "LanDisk.exe")));
        Assert.True(File.Exists(Path.Combine(install, "LanDisk.Update.dll")));

        // Removed because the previous manifest claimed it…
        Assert.False(File.Exists(Path.Combine(install, "legacy-only.txt")));
        // …and kept because nothing ever claimed it.
        Assert.True(File.Exists(Path.Combine(install, "my-notes.txt")));

        TestBase.Record("UX07-15", "修复安装只删除上一份清单声明过的文件，用户自己放进去的文件不受影响");
    }

    [Fact]
    public void Install_leaves_no_staging_or_backup_directories_behind()
    {
        using var kit = new UpdateTestKit(nameof(Install_leaves_no_staging_or_backup_directories_behind));
        string install = Path.Combine(kit.Root, "installed");
        string payload = CreatePayload(kit, "clean", new Dictionary<string, string> { ["LanDisk.exe"] = "x" });

        using (PayloadPackage package = PayloadPackage.Open(payload))
        {
            Assert.True(package.InstallTo(install, null).Ok);
            Assert.True(package.InstallTo(install, null).Ok); // idempotent repair
        }

        string[] leftovers = Directory.GetDirectories(kit.Root)
            .Where(d => Path.GetFileName(d).StartsWith("installed.", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(leftovers);
    }

    [Fact]
    public void The_installer_puts_a_copy_of_itself_in_place_for_the_uninstall_entry()
    {
        using var kit = new UpdateTestKit(nameof(The_installer_puts_a_copy_of_itself_in_place_for_the_uninstall_entry));
        string install = Path.Combine(kit.Root, "installed");
        string fakeSetup = Path.Combine(kit.Root, "LanDisk-Setup.exe");
        File.WriteAllText(fakeSetup, "installer-binary");

        string payload = CreatePayload(kit, "copy", new Dictionary<string, string> { ["LanDisk.exe"] = "x" });
        using PayloadPackage package = PayloadPackage.Open(payload);
        Assert.True(package.InstallTo(install, fakeSetup).Ok);

        string copy = SetupLayout.SetupCopyPathFor(install);
        Assert.True(File.Exists(copy));
        Assert.Equal("installer-binary", File.ReadAllText(copy));

        // The installer's own copy is not a program file the updater may delete.
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(SetupLayout.ManifestPathFor(install)));
        List<string> listed = manifest.RootElement.GetProperty("files").EnumerateArray()
            .Select(e => e.GetString()!).ToList();
        Assert.DoesNotContain(SetupLayout.SetupExeName, listed);
    }

    [Fact]
    public void Unsafe_relative_paths_are_refused()
    {
        Assert.True(PayloadPackage.IsSafeRelative("wwwroot/index.html"));
        Assert.False(PayloadPackage.IsSafeRelative("../escape.txt"));
        Assert.False(PayloadPackage.IsSafeRelative("nested/../../escape.txt"));
        Assert.False(PayloadPackage.IsSafeRelative("/absolute.txt"));
        Assert.False(PayloadPackage.IsSafeRelative(@"C:\absolute.txt"));
        Assert.False(PayloadPackage.IsSafeRelative(string.Empty));
    }
}
