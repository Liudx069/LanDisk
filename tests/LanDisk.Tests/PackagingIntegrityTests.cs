using System.Text.Json;
using LanDisk.Tests.Infrastructure;
using LanDisk.Update;

namespace LanDisk.Tests;

/// <summary>
/// PKG05-03: the build identity file is an ordinary, signed payload file.
///
/// It used to be treated as release metadata and skipped by the packer, so an update replaced the
/// binaries while the installation directory kept claiming the previous version. These tests pin the
/// new rule: it is in the payload, it is hashed, it is replaced, and the health check refuses to
/// commit unless the running assembly, the manifest and the file on disk all agree.
/// </summary>
public sealed class PackagingIntegrityTests
{
    // ------------------------------------------------------------------ the file itself

    [Fact]
    public void The_build_identity_file_round_trips()
    {
        using var kit = new UpdateTestKit(nameof(The_build_identity_file_round_trips));
        string install = kit.NewDirectory("install");

        InstallBuildInfoFile.Write(install, new InstallBuildInfo
        {
            Version = "1.4.0",
            Commit = "abcdef1234567890",
            BuildId = "Release/win-x64",
            Runtime = "win-x64",
            SelfContained = true
        });

        InstallBuildInfo? read = InstallBuildInfoFile.TryRead(install);

        Assert.NotNull(read);
        Assert.Equal("1.4.0", read!.Version);
        Assert.Equal("abcdef1234567890", read.Commit);
        Assert.True(read.SelfContained);
        Assert.True(File.Exists(Path.Combine(install, InstallBuildInfoFile.FileName)));
    }

    [Fact]
    public void A_missing_or_damaged_build_identity_file_reads_as_absent()
    {
        using var kit = new UpdateTestKit(nameof(A_missing_or_damaged_build_identity_file_reads_as_absent));
        string install = kit.NewDirectory("install");

        Assert.Null(InstallBuildInfoFile.TryRead(install));

        File.WriteAllText(Path.Combine(install, InstallBuildInfoFile.FileName), "{ not json");
        Assert.Null(InstallBuildInfoFile.TryRead(install));
    }

    [Fact]
    public void The_build_identity_file_is_not_a_reserved_path()
    {
        // Only the install manifest is installer-generated metadata. Anything else the release ships
        // must be allowed in the payload, otherwise the installation can never update it.
        using var kit = new UpdateTestKit(nameof(The_build_identity_file_is_not_a_reserved_path));

        string package = kit.BuildPackage("build-info-payload", new PackageSpec { Version = "1.4.0" }
            .Only(("LanDisk.exe", "exe"), (InstallBuildInfoFile.FileName, """{"version":"1.4.0"}""")));

        VerifiedUpdatePackage verified = kit.Extract(package, "1.3.0");

        Assert.Contains(verified.Manifest.Files, f => f.Path == InstallBuildInfoFile.FileName);
        Assert.True(File.Exists(Path.Combine(verified.PayloadRoot, InstallBuildInfoFile.FileName)));
    }

    [Fact]
    public void A_tampered_build_identity_file_fails_verification()
    {
        using var kit = new UpdateTestKit(nameof(A_tampered_build_identity_file_fails_verification));

        // Same length, different content: only the signed hash can catch this.
        const string original = """{"version":"1.4.0"}""";
        string package = kit.BuildPackage("build-info-tampered", new PackageSpec { Version = "1.4.0" }
            .Only(("LanDisk.exe", "exe"), (InstallBuildInfoFile.FileName, original))
            .WithRaw(InstallBuildInfoFile.FileName, PackageSpec.SameLengthBytes(original, """{"version":"9.9.9"}""")));

        UpdatePackageException error = Assert.Throws<UpdatePackageException>(() => kit.Extract(package, "1.3.0"));
        Assert.Equal("HASH_MISMATCH", error.Code);

        TestBase.Record("PKG05-03", "篡改 build-info.json 的 payload 内容 → 哈希校验失败");
    }

    [Fact]
    public void A_manifest_that_omits_the_build_identity_file_simply_does_not_install_one()
    {
        // A package is not required to ship build-info.json; what it must not do is ship one that the
        // signed manifest does not declare, or declare one that is not in the archive.
        using var kit = new UpdateTestKit(nameof(A_manifest_that_omits_the_build_identity_file_simply_does_not_install_one));

        string package = kit.BuildPackage("no-build-info", new PackageSpec { Version = "1.4.0" }.Only(("LanDisk.exe", "exe")));
        VerifiedUpdatePackage verified = kit.Extract(package, "1.3.0");

        Assert.DoesNotContain(verified.Manifest.Files, f => f.Path == InstallBuildInfoFile.FileName);
    }

    [Fact]
    public void A_declared_but_absent_build_identity_file_is_refused()
    {
        using var kit = new UpdateTestKit(nameof(A_declared_but_absent_build_identity_file_is_refused));

        var spec = new PackageSpec { Version = "1.4.0" }.Only(("LanDisk.exe", "exe"), (InstallBuildInfoFile.FileName, "{}"));
        spec.Files.Remove(InstallBuildInfoFile.FileName);
        spec.DeclaredFiles =
        [
            new UpdateFileEntry { Path = "LanDisk.exe", Bytes = 3, Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("exe"u8.ToArray())) },
            new UpdateFileEntry { Path = InstallBuildInfoFile.FileName, Bytes = 2, Sha256 = new string('a', 64) }
        ];

        string package = kit.BuildPackage("declared-not-present", spec);
        UpdatePackageException error = Assert.Throws<UpdatePackageException>(() => kit.Extract(package, "1.3.0"));
        Assert.Equal("FILE_MISSING", error.Code);
    }

    // ------------------------------------------------------------------ apply / rollback

    [Fact]
    public void Applying_an_update_replaces_the_build_identity_file_and_rollback_restores_it()
    {
        using var kit = new UpdateTestKit(nameof(Applying_an_update_replaces_the_build_identity_file_and_rollback_restores_it));
        string dataRoot = kit.NewDirectory("data");
        string install = kit.CreateInstall("install", "1.3.0", new Dictionary<string, string>
        {
            ["LanDisk.exe"] = "old-exe"
        });

        // The state an installation is in before the update: it claims 1.3.0.
        InstallBuildInfoFile.Write(install, new InstallBuildInfo { Version = "1.3.0", Commit = "1111111111111111" });

        var applier = new UpdateApplier(install);
        UpdateBackup backup = applier.Backup(dataRoot, "1.3.0");

        string newBuildInfo = JsonSerializer.Serialize(
            new InstallBuildInfo { Version = "1.4.0", Commit = "2222222222222222" },
            UpdatePackage.SerializerOptions);

        string package = kit.BuildPackage("build-info-update", new PackageSpec
        {
            Version = "1.4.0",
            BuildCommit = "2222222222222222"
        }.Only(("LanDisk.exe", "new-exe"), (InstallBuildInfoFile.FileName, newBuildInfo)));

        VerifiedUpdatePackage verified = kit.Extract(package, "1.3.0");
        UpdatePlan plan = UpdatePlan.Build(verified.PayloadRoot, verified.Manifest, install);

        Assert.Contains(plan.Writes, a => a.RelativePath == InstallBuildInfoFile.FileName);

        Assert.True(applier.Apply(plan, verified.Manifest).Ok);
        Assert.Equal("1.4.0", InstallBuildInfoFile.TryRead(install)!.Version);

        UpdateRollbackResult rollback = applier.Rollback(dataRoot, backup, verified.Manifest);

        Assert.True(rollback.Ok, string.Join("; ", rollback.Failures));
        Assert.Equal("1.3.0", InstallBuildInfoFile.TryRead(install)!.Version);
        Assert.Equal("1111111111111111", InstallBuildInfoFile.TryRead(install)!.Commit);

        TestBase.Record("PKG05-03", "升级把 build-info.json 换成 1.4.0，回滚又按字节恢复为 1.3.0");
    }

    // ------------------------------------------------------------------ health agreement

    private static UpdateHealthExpectation Expectation() => new()
    {
        Product = "LanDisk",
        Version = "1.4.0",
        BuildCommit = "2222222222222222",
        RequireBuildCommit = true,
        // A package always ships the identity file, so the forward path demands it.
        RequireInstallBuildInfo = true
    };

    private static string HealthJson(string installVersion = "1.4.0", string installCommit = "2222222222222222", bool include = true)
    {
        var payload = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["product"] = "LanDisk",
            ["version"] = "1.4.0",
            ["buildCommit"] = "2222222222222222",
            ["sharesStore"] = new { ok = true, count = 0 },
            ["devicesStore"] = new { ok = true, count = 0 },
            ["browseSmoke"] = new { ok = true, message = "ok" }
        };
        if (include) payload["installBuildInfo"] = new { product = "LanDisk", version = installVersion, buildCommit = installCommit };
        return JsonSerializer.Serialize(payload);
    }

    [Fact]
    public void Three_way_agreement_passes()
    {
        UpdateHealthResult result = UpdateHealth.Evaluate(HealthJson(), Expectation());
        Assert.True(result.Ok, result.Message);
    }

    [Fact]
    public void A_stale_on_disk_build_identity_fails_the_gate()
    {
        // This is the exact 04 defect: binaries replaced, build-info.json left behind.
        UpdateHealthResult result = UpdateHealth.Evaluate(HealthJson(installVersion: "1.3.0", installCommit: "1111111111111111"), Expectation());

        Assert.False(result.Ok);
        Assert.Contains(result.Failures, f => f.Contains("build-info.json") && f.Contains("1.3.0"));
        Assert.Contains(result.Failures, f => f.Contains("build-info.json") && f.Contains("1111111111111111"));
    }

    [Fact]
    public void A_missing_on_disk_build_identity_fails_the_gate()
    {
        UpdateHealthResult result = UpdateHealth.Evaluate(HealthJson(include: false), Expectation());

        Assert.False(result.Ok);
        Assert.Contains(result.Failures, f => f.Contains("build-info.json"));
    }
}
