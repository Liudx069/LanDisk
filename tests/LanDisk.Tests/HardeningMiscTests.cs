using System.Text.Json;
using LanDisk.Core.Configuration;
using LanDisk.Tests.Infrastructure;
using LanDisk.Update;

namespace LanDisk.Tests;

/// <summary>
/// H04-02 (build identity is visible), H04-09 (the tray notice is remembered) and the small
/// contracts around them.
/// </summary>
public sealed class HardeningMiscTests
{
    // ------------------------------------------------------------------ build identity

    [Fact]
    public void BuildIdentity_reports_an_empty_commit_for_a_development_build()
    {
        // The test assemblies are built without -p:LanDiskBuildCommit, so this is the "dev build"
        // case: it must be able to say "I have no commit" rather than inventing one.
        string commit = BuildIdentity.Commit;

        Assert.NotNull(commit);
        if (commit.Length > 0)
        {
            // A CI build of this same suite may stamp a commit; then it must look like one.
            Assert.True(commit.Length >= 7, "提交号看起来不合法：" + commit);
        }

        Assert.False(string.IsNullOrWhiteSpace(BuildIdentity.AssemblyVersion));
    }

    [Fact]
    public async Task The_status_endpoint_exposes_the_build_commit_field_the_verifier_reads()
    {
        await using TestHost host = await TestHost.CreateAsync(nameof(The_status_endpoint_exposes_the_build_commit_field_the_verifier_reads));
        var admin = new LanDiskClient(host.NewClient());
        await admin.AdminLoginAsync(host.AdminToken);

        ApiResult<JsonElement> status = await admin.GetAsync<JsonElement>("/api/admin/status");
        Assert.True(status.IsSuccess, status.Raw);

        // The field must exist even when empty: a missing property would make the candidate identity
        // check silently pass on a build that carries no commit at all.
        Assert.True(status.Body.TryGetProperty("buildCommit", out JsonElement commit));
        Assert.Equal(JsonValueKind.String, commit.ValueKind);
        Assert.True(status.Body.TryGetProperty("buildId", out JsonElement buildId));
        Assert.Equal(JsonValueKind.String, buildId.ValueKind);
    }

    [Fact]
    public async Task The_health_endpoint_reports_the_certificate_that_exists()
    {
        // Its own (empty) installation directory: the health endpoint treats a conflicting
        // build-info.json as an error, and the shared test output directory may hold one from another
        // run, which has nothing to do with the certificate this test is about.
        string installRoot = Path.Combine(TestPaths.TempRoot, "health-cert-install-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(installRoot);

        await using TestHost host = await TestHost.CreateAsync(
            nameof(The_health_endpoint_reports_the_certificate_that_exists),
            options => options.InstallRoot = installRoot);
        var admin = new LanDiskClient(host.NewClient());
        await admin.AdminLoginAsync(host.AdminToken);

        // The host creates the local CA and its server leaf on first use; the CA thumbprint must be
        // reported so the updater can prove the installation's HTTPS identity did not change,
        // independently of whether HTTPS happens to be on. The CA — not the leaf — is that identity:
        // a leaf may legitimately be re-issued when the LAN address changes.
        _ = host.Runtime.EnsureCertificates();

        ApiResult<JsonElement> health = await admin.GetAsync<JsonElement>("/api/admin/health");
        Assert.True(health.IsSuccess, health.Raw);

        string? thumbprint = health.Body.GetProperty("certificateThumbprint").GetString();
        Assert.False(string.IsNullOrWhiteSpace(thumbprint), "已有证书时必须报告指纹");

        PreUpdateSnapshot snapshot = host.Runtime.Updates.CaptureSnapshot(
            host.Runtime.Settings.HttpPort, host.Runtime.Settings.HttpsPort);
        Assert.Equal(snapshot.CertificateThumbprint, thumbprint, ignoreCase: true);
    }

    // ------------------------------------------------------------------ tray notice preference

    [Fact]
    public void The_tray_notice_preference_round_trips()
    {
        using var kit = new UpdateTestKit(nameof(The_tray_notice_preference_round_trips));
        string dataRoot = kit.NewDirectory("data");

        ShellPreferences fresh = ShellPreferences.Load(dataRoot);
        Assert.False(fresh.TrayNoticeAcknowledged);
        Assert.False(fresh.TrayNoticeSuppressed);

        fresh.TrayNoticeAcknowledged = true;
        fresh.TrayNoticeSuppressed = true;
        fresh.Save(dataRoot);

        ShellPreferences reloaded = ShellPreferences.Load(dataRoot);
        Assert.True(reloaded.TrayNoticeAcknowledged);
        Assert.True(reloaded.TrayNoticeSuppressed);

        Assert.True(File.Exists(Path.Combine(dataRoot, ShellPreferences.FileName)));

        TestBase.Record("H04-09", "托盘提示偏好写入 shell.json 并可再次读回");
    }

    [Fact]
    public void A_damaged_preference_file_falls_back_to_defaults_instead_of_crashing()
    {
        using var kit = new UpdateTestKit(nameof(A_damaged_preference_file_falls_back_to_defaults_instead_of_crashing));
        string dataRoot = kit.NewDirectory("data");
        File.WriteAllText(Path.Combine(dataRoot, ShellPreferences.FileName), "{ not json");

        ShellPreferences preferences = ShellPreferences.Load(dataRoot);

        Assert.False(preferences.TrayNoticeSuppressed);
    }

    [Fact]
    public void The_shell_preference_file_is_part_of_the_managed_state_scope()
    {
        // The tray-notice choice must survive an upgrade and a rollback like any other setting.
        Assert.Contains(ShellPreferences.FileName, UpdateBackupScope.StateFiles);
        Assert.True(UpdateBackupScope.IsManaged(ShellPreferences.FileName));
    }

    [Fact]
    public void The_install_identity_is_part_of_the_managed_state_scope()
    {
        // install.json holds the install id, the stable <name>.local hostname and the fixed ports.
        // It sits at the data root, so it must be inside the same managed scope as every other
        // data-root state file: an upgrade leaves it alone and a rollback restores it byte for byte.
        // It used to be outside the scope, which made the real-device acceptance run report the
        // legitimate file as "leftover state a failed version added".
        Assert.Contains(InstallIdentity.FileName, UpdateBackupScope.StateFiles);
        Assert.True(UpdateBackupScope.IsManaged(InstallIdentity.FileName));
    }

    // ------------------------------------------------------------------ minimum upgrade semantics

    [Fact]
    public void A_package_below_the_minimum_upgrade_version_is_refused_with_the_new_field_name()
    {
        using var kit = new UpdateTestKit(nameof(A_package_below_the_minimum_upgrade_version_is_refused_with_the_new_field_name));
        string package = kit.BuildPackage("min-upgrade", new PackageSpec
        {
            Version = "2.0.0",
            MinimumUpgradeVersion = "1.9.0"
        });

        UpdatePackageException error = Assert.Throws<UpdatePackageException>(
            () => kit.Extract(package, currentVersion: "1.3.0"));

        Assert.Equal("VERSION_TOO_OLD", error.Code);
        Assert.Contains("1.9.0", error.Message);
    }

    [Fact]
    public void An_unparseable_minimum_upgrade_version_is_refused()
    {
        using var kit = new UpdateTestKit(nameof(An_unparseable_minimum_upgrade_version_is_refused));
        string package = kit.BuildPackage("bad-min", new PackageSpec
        {
            Version = "1.4.0",
            MinimumUpgradeVersion = "not-a-version"
        });

        UpdatePackageException error = Assert.Throws<UpdatePackageException>(() => kit.Extract(package, "1.3.0"));
        Assert.Equal("VERSION_INVALID", error.Code);
    }
}
