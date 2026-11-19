using System.Text.Json;
using LanDisk.Tests.Infrastructure;
using LanDisk.Update;

namespace LanDisk.Tests;

/// <summary>
/// Task 06: <c>install-manifest.json</c> must record <c>build-info.json</c>.
///
/// The release ZIP already contained the file and the signed <c>.lupd</c> already carried it, but the
/// manifest's <c>files</c> list never mentioned it, because the publish script enumerated the
/// installation directory <em>before</em> writing the identity file. The fix is an ordering change in
/// the pipeline plus a recorded path/length/hash that can be checked independently.
///
/// These tests pin both halves: the record exists and is accurate, it survives JSON round-tripping
/// under the exact field name the PowerShell pipeline writes, and the applier keeps it true when it
/// writes a new manifest after an update.
/// </summary>
public sealed class InstallManifestIntegrityTests
{
    private static string WriteBuildInfo(string installRoot, string version, string commit)
    {
        InstallBuildInfoFile.Write(installRoot, new InstallBuildInfo
        {
            Version = version,
            Commit = commit,
            BuildId = "Release/win-x64",
            BuiltAt = "2026-09-27T00:00:00.0000000+08:00",
            Runtime = "win-x64",
            SelfContained = true
        });
        return Path.Combine(installRoot, InstallBuildInfoFile.FileName);
    }

    // ------------------------------------------------------------------ the recorded claim

    [Fact]
    public void The_install_manifest_records_the_build_identity_file()
    {
        using var kit = new UpdateTestKit(nameof(The_install_manifest_records_the_build_identity_file));
        string install = kit.NewDirectory("install");
        File.WriteAllText(Path.Combine(install, "LanDisk.exe"), "exe");
        string buildInfo = WriteBuildInfo(install, "1.4.0", "abcdef1234567890");

        // Order matters and is asserted: write the identity file, *then* enumerate, then record.
        var files = Directory.EnumerateFiles(install, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(install, p).Replace('\\', '/'))
            .Where(p => p != InstallManifest.FileName)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var manifest = new InstallManifest
        {
            Version = "1.4.0",
            BuildCommit = "abcdef1234567890",
            Files = files,
            BuildInfo = InstallManifestFileRecord.TryCapture(install, InstallBuildInfoFile.FileName)
        };
        manifest.Write(install);

        InstallManifest? read = InstallManifest.TryRead(install);

        Assert.NotNull(read);
        Assert.Contains(InstallBuildInfoFile.FileName, read!.Files);
        Assert.NotNull(read.BuildInfo);
        Assert.Equal(InstallBuildInfoFile.FileName, read.BuildInfo!.Path);
        Assert.Equal(new FileInfo(buildInfo).Length, read.BuildInfo.Bytes);
        Assert.Equal(UpdatePackage.HashFile(buildInfo), read.BuildInfo.Sha256);
        Assert.DoesNotContain(InstallManifest.FileName, read.Files);

        TestBase.Record("PKG06-01", "install-manifest.json 的 files 明确包含 build-info.json，并记录其 bytes 与 sha256");
    }

    [Fact]
    public void Enumerating_before_writing_the_identity_file_is_what_caused_the_defect()
    {
        // The exact root cause, kept as an executable statement: with the old order the file is simply
        // not there yet, so no enumeration can ever list it.
        using var kit = new UpdateTestKit(nameof(Enumerating_before_writing_the_identity_file_is_what_caused_the_defect));
        string install = kit.NewDirectory("install");
        File.WriteAllText(Path.Combine(install, "LanDisk.exe"), "exe");

        List<string> enumeratedEarly = Directory.EnumerateFiles(install, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(install, p).Replace('\\', '/'))
            .ToList();

        WriteBuildInfo(install, "1.4.0", "abcdef1234567890");

        List<string> enumeratedLate = Directory.EnumerateFiles(install, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(install, p).Replace('\\', '/'))
            .ToList();

        Assert.DoesNotContain(InstallBuildInfoFile.FileName, enumeratedEarly);
        Assert.Contains(InstallBuildInfoFile.FileName, enumeratedLate);

        TestBase.Record("PKG06-02", "先枚举、后写 build-info.json 时清单必然漏项；改为先写后枚举");
    }

    [Fact]
    public void The_recorded_field_names_match_what_the_publish_pipeline_writes()
    {
        // The pipeline is PowerShell, the reader is C#. They agree only if the JSON field names agree,
        // so the contract is pinned here rather than discovered in a release build.
        using var kit = new UpdateTestKit(nameof(The_recorded_field_names_match_what_the_publish_pipeline_writes));
        string install = kit.NewDirectory("install");
        WriteBuildInfo(install, "1.4.0", "abcdef1234567890");

        var manifest = new InstallManifest
        {
            Version = "1.4.0",
            BuildCommit = "abcdef1234567890",
            Files = [InstallBuildInfoFile.FileName],
            BuildInfo = InstallManifestFileRecord.TryCapture(install, InstallBuildInfoFile.FileName)
        };
        manifest.Write(install);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(InstallManifest.PathFor(install)));

        Assert.Equal("install-manifest.json", InstallManifest.FileName);
        Assert.True(document.RootElement.TryGetProperty("buildInfo", out JsonElement buildInfo));
        Assert.Equal("build-info.json", buildInfo.GetProperty("path").GetString());
        Assert.Equal(new FileInfo(Path.Combine(install, InstallBuildInfoFile.FileName)).Length,
            buildInfo.GetProperty("bytes").GetInt64());
        Assert.Equal(UpdatePackage.HashFile(Path.Combine(install, InstallBuildInfoFile.FileName)),
            buildInfo.GetProperty("sha256").GetString());
    }

    [Fact]
    public void An_installation_that_predates_the_record_still_loads()
    {
        // Installations made before this field existed must not be reported as corrupt: the record is
        // optional on read, and its absence is simply absence.
        using var kit = new UpdateTestKit(nameof(An_installation_that_predates_the_record_still_loads));
        string install = kit.NewDirectory("install");

        File.WriteAllText(InstallManifest.PathFor(install), """
            {
              "schemaVersion": 1,
              "product": "LanDisk",
              "version": "1.3.0",
              "buildCommit": "old",
              "installedAt": "2026-01-01T00:00:00+08:00",
              "files": [ "LanDisk.exe" ]
            }
            """);

        InstallManifest? read = InstallManifest.TryRead(install);

        Assert.NotNull(read);
        Assert.Equal("1.3.0", read!.Version);
        Assert.Contains("LanDisk.exe", read.Files);
        Assert.Null(read.BuildInfo);
    }

    [Fact]
    public void Capturing_an_absent_identity_file_records_nothing()
    {
        using var kit = new UpdateTestKit(nameof(Capturing_an_absent_identity_file_records_nothing));
        string install = kit.NewDirectory("install");

        Assert.Null(InstallManifestFileRecord.TryCapture(install, InstallBuildInfoFile.FileName));
    }

    // ------------------------------------------------------------------ after an update

    private static (string Install, string DataRoot, UpdateApplier Applier) PrepareInstallation(UpdateTestKit kit, string name)
    {
        string install = kit.CreateInstall(name, "1.3.0", new Dictionary<string, string>
        {
            ["LanDisk.exe"] = "old-exe"
        });

        // A real 1.3.0 installation carries the identity file; the fixture mirrors the publish order.
        WriteBuildInfo(install, "1.3.0", "1111111111111111");
        new InstallManifest
        {
            Version = "1.3.0",
            BuildCommit = "1111111111111111",
            Files = ["LanDisk.exe", InstallBuildInfoFile.FileName],
            BuildInfo = InstallManifestFileRecord.TryCapture(install, InstallBuildInfoFile.FileName)
        }.Write(install);

        string dataRoot = kit.NewDirectory("data-" + name);
        return (install, dataRoot, new UpdateApplier(install));
    }

    [Fact]
    public void Applying_an_update_refreshes_the_recorded_identity()
    {
        using var kit = new UpdateTestKit(nameof(Applying_an_update_refreshes_the_recorded_identity));
        (string install, _, UpdateApplier applier) = PrepareInstallation(kit, "install");

        string beforeSha = InstallManifest.TryRead(install)!.BuildInfo!.Sha256;

        string newBuildInfo = JsonSerializer.Serialize(
            new InstallBuildInfo { Version = "1.4.0", Commit = "2222222222222222", Runtime = "win-x64", SelfContained = true },
            UpdatePackage.SerializerOptions);

        string package = kit.BuildPackage("record-refresh", new PackageSpec
        {
            Version = "1.4.0",
            BuildCommit = "2222222222222222"
        }.Only(("LanDisk.exe", "new-exe"), (InstallBuildInfoFile.FileName, newBuildInfo)));

        VerifiedUpdatePackage verified = kit.Extract(package, "1.3.0");
        UpdatePlan plan = UpdatePlan.Build(verified.PayloadRoot, verified.Manifest, install);
        Assert.True(applier.Apply(plan, verified.Manifest).Ok);

        InstallManifest after = InstallManifest.TryRead(install)!;
        string buildInfoPath = Path.Combine(install, InstallBuildInfoFile.FileName);

        Assert.Contains(InstallBuildInfoFile.FileName, after.Files);
        Assert.NotNull(after.BuildInfo);
        Assert.Equal(InstallBuildInfoFile.FileName, after.BuildInfo!.Path);
        Assert.Equal(new FileInfo(buildInfoPath).Length, after.BuildInfo.Bytes);
        Assert.Equal(UpdatePackage.HashFile(buildInfoPath), after.BuildInfo.Sha256);
        Assert.NotEqual(beforeSha, after.BuildInfo.Sha256);

        TestBase.Record("PKG06-03", "升级后 install-manifest 记录的 build-info 摘要随磁盘文件一起刷新");
    }

    [Fact]
    public void Rollback_restores_the_identity_file_and_its_record_together()
    {
        using var kit = new UpdateTestKit(nameof(Rollback_restores_the_identity_file_and_its_record_together));
        (string install, string dataRoot, UpdateApplier applier) = PrepareInstallation(kit, "install");

        string beforeSha = InstallManifest.TryRead(install)!.BuildInfo!.Sha256;

        string newBuildInfo = JsonSerializer.Serialize(
            new InstallBuildInfo { Version = "1.4.0", Commit = "2222222222222222", Runtime = "win-x64", SelfContained = true },
            UpdatePackage.SerializerOptions);

        string package = kit.BuildPackage("record-rollback", new PackageSpec
        {
            Version = "1.4.0",
            BuildCommit = "2222222222222222"
        }.Only(("LanDisk.exe", "new-exe"), (InstallBuildInfoFile.FileName, newBuildInfo)));

        VerifiedUpdatePackage verified = kit.Extract(package, "1.3.0");
        UpdatePlan plan = UpdatePlan.Build(verified.PayloadRoot, verified.Manifest, install);
        UpdateBackup backup = applier.Backup(dataRoot, "1.3.0");
        Assert.True(applier.Apply(plan, verified.Manifest).Ok);

        UpdateRollbackResult rollback = applier.Rollback(dataRoot, backup, verified.Manifest);
        Assert.True(rollback.Ok, string.Join("; ", rollback.Failures));

        InstallManifest restored = InstallManifest.TryRead(install)!;
        string buildInfoPath = Path.Combine(install, InstallBuildInfoFile.FileName);

        Assert.Equal("1.3.0", InstallBuildInfoFile.TryRead(install)!.Version);
        Assert.NotNull(restored.BuildInfo);
        Assert.Equal(InstallBuildInfoFile.FileName, restored.BuildInfo!.Path);
        // The restored record must describe the restored bytes, not the ones the failed version wrote.
        Assert.Equal(new FileInfo(buildInfoPath).Length, restored.BuildInfo.Bytes);
        Assert.Equal(UpdatePackage.HashFile(buildInfoPath), restored.BuildInfo.Sha256);
        Assert.Equal(beforeSha, restored.BuildInfo.Sha256);

        TestBase.Record("PKG06-04", "回滚后 build-info.json 与其在 install-manifest 中的记录一起恢复为旧值");
    }
}
