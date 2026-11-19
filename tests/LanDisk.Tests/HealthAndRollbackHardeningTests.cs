using System.Net;
using System.Text;
using System.Text.Json;
using LanDisk.Core.Model;
using LanDisk.Core.Networking;
using LanDisk.Tests.Infrastructure;
using LanDisk.Update;

namespace LanDisk.Tests;

/// <summary>
/// H04-05, H04-06 and H04-07: the health contract, the package hash trail, and rollback that
/// restores the managed state set exactly.
/// </summary>
public sealed class HealthAndRollbackHardeningTests
{
    /// <summary>
    /// A private, empty installation directory for one test.
    ///
    /// <para>The health endpoint reports a <c>build-info.json</c> that disagrees with the running build as
    /// an <b>error</b>, which is right for a real installation and wrong for a test that never installed
    /// anything: with the default install root the test would be reading whatever the previous test run
    /// left in the shared test output directory, so a version bump alone could fail it. Modelling a real
    /// installation means giving the test one of its own.</para>
    /// </summary>
    private static string NewInstallRoot(string label)
    {
        string dir = Path.Combine(TestPaths.TempRoot, label + "-install-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string HealthJson(
        bool ok = true,
        string product = "LanDisk",
        string version = "1.3.0",
        string commit = "abcdef1234567890",
        int adminPort = 8891,
        int lanPort = 8892,
        string? cert = "THUMBPRINT",
        bool sharesOk = true,
        bool devicesOk = true,
        bool browseOk = true)
    {
        return JsonSerializer.Serialize(new
        {
            ok,
            degraded = false,
            product,
            version,
            buildCommit = commit,
            adminPort,
            lanPort,
            httpsEnabled = true,
            certificateThumbprint = cert,
            sharesStore = new { ok = sharesOk, count = 1, sha256 = "x", message = sharesOk ? null : "共享库损坏" },
            devicesStore = new { ok = devicesOk, count = 1, sha256 = "y", message = devicesOk ? null : "设备库损坏" },
            browseSmoke = new { ok = browseOk, message = browseOk ? "ok" : "无法列出目录" },
            installBuildInfo = new { product, version, buildCommit = commit },
            checks = Array.Empty<object>()
        });
    }

    private static UpdateHealthExpectation Expectation(bool forRollback = false) => new()
    {
        Product = "LanDisk",
        Version = forRollback ? "1.2.0" : "1.3.0",
        BuildCommit = forRollback ? "1111111111111111" : "abcdef1234567890",
        AdminPort = 8891,
        LanPort = 8892,
        CertificateThumbprint = "THUMBPRINT",
        CertificateWasPresent = true,
        DataRoot = @"C:\Users\x\AppData\Local\LanDisk"
    };

    private static string WithDataRoot(string json, string dataRoot)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        var node = System.Text.Json.Nodes.JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
        node["dataRoot"] = dataRoot;
        return node.ToJsonString();
    }

    // ------------------------------------------------------------------ health evaluation

    [Fact]
    public void A_fully_matching_health_report_passes()
    {
        string json = WithDataRoot(HealthJson(), @"C:\Users\x\AppData\Local\LanDisk");

        UpdateHealthResult result = UpdateHealth.Evaluate(json, Expectation());

        Assert.True(result.Ok, result.Message);
        Assert.Equal("1.3.0", result.Version);
        Assert.Equal("abcdef1234567890", result.BuildCommit);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void The_wrong_target_version_fails_the_gate()
    {
        string json = WithDataRoot(HealthJson(version: "1.2.0"), @"C:\Users\x\AppData\Local\LanDisk");

        UpdateHealthResult result = UpdateHealth.Evaluate(json, Expectation());

        Assert.False(result.Ok);
        Assert.Contains(result.Failures, f => f.Contains("version") && f.Contains("1.3.0"));
    }

    [Fact]
    public void The_wrong_build_commit_fails_the_gate()
    {
        string json = WithDataRoot(HealthJson(commit: "9999999999999999"), @"C:\Users\x\AppData\Local\LanDisk");

        UpdateHealthResult result = UpdateHealth.Evaluate(json, Expectation());

        Assert.False(result.Ok);
        Assert.Contains(result.Failures, f => f.Contains("buildCommit"));
    }

    [Fact]
    public void A_missing_build_commit_fails_the_gate()
    {
        string json = WithDataRoot(HealthJson(commit: string.Empty), @"C:\Users\x\AppData\Local\LanDisk");

        UpdateHealthResult result = UpdateHealth.Evaluate(json, Expectation());

        Assert.False(result.Ok);
        Assert.Contains(result.Failures, f => f.Contains("buildCommit") && f.Contains("缺失"));
    }

    [Fact]
    public void Changed_ports_fail_the_gate()
    {
        string json = WithDataRoot(HealthJson(adminPort: 18991, lanPort: 18992), @"C:\Users\x\AppData\Local\LanDisk");

        UpdateHealthResult result = UpdateHealth.Evaluate(json, Expectation());

        Assert.False(result.Ok);
        Assert.Contains(result.Failures, f => f.Contains("管理端口"));
        Assert.Contains(result.Failures, f => f.Contains("局域网端口"));
    }

    [Fact]
    public void A_changed_certificate_thumbprint_fails_the_gate()
    {
        string json = WithDataRoot(HealthJson(cert: "OTHER-THUMBPRINT"), @"C:\Users\x\AppData\Local\LanDisk");

        UpdateHealthResult result = UpdateHealth.Evaluate(json, Expectation());

        Assert.False(result.Ok);
        Assert.Contains(result.Failures, f => f.Contains("证书指纹"));
    }

    [Fact]
    public void A_disappeared_certificate_fails_the_gate_when_one_existed_before()
    {
        string json = WithDataRoot(HealthJson(cert: null), @"C:\Users\x\AppData\Local\LanDisk");

        UpdateHealthResult result = UpdateHealth.Evaluate(json, Expectation());

        Assert.False(result.Ok);
        Assert.Contains(result.Failures, f => f.Contains("证书指纹缺失"));
    }

    [Fact]
    public void A_share_store_that_cannot_load_fails_the_gate()
    {
        string json = WithDataRoot(HealthJson(sharesOk: false), @"C:\Users\x\AppData\Local\LanDisk");

        UpdateHealthResult result = UpdateHealth.Evaluate(json, Expectation());

        Assert.False(result.Ok);
        Assert.Contains(result.Failures, f => f.Contains("共享库"));
    }

    [Fact]
    public void A_paired_device_store_that_cannot_load_fails_the_gate()
    {
        string json = WithDataRoot(HealthJson(devicesOk: false), @"C:\Users\x\AppData\Local\LanDisk");

        UpdateHealthResult result = UpdateHealth.Evaluate(json, Expectation());

        Assert.False(result.Ok);
        Assert.Contains(result.Failures, f => f.Contains("配对设备库"));
    }

    [Fact]
    public void A_failed_browse_smoke_fails_the_gate()
    {
        string json = WithDataRoot(HealthJson(browseOk: false), @"C:\Users\x\AppData\Local\LanDisk");

        UpdateHealthResult result = UpdateHealth.Evaluate(json, Expectation());

        Assert.False(result.Ok);
        Assert.Contains(result.Failures, f => f.Contains("隔离目录浏览"));
    }

    [Fact]
    public void A_different_data_root_fails_the_gate()
    {
        string json = WithDataRoot(HealthJson(), @"D:\elsewhere\LanDisk");

        UpdateHealthResult result = UpdateHealth.Evaluate(json, Expectation());

        Assert.False(result.Ok);
        Assert.Contains(result.Failures, f => f.Contains("dataRoot"));
    }

    [Fact]
    public void Ok_false_fails_the_gate_and_names_the_failing_check()
    {
        string json = WithDataRoot(
            HealthJson(ok: false).Replace("\"checks\":[]", "\"checks\":[{\"id\":\"shares-store\",\"status\":\"error\",\"message\":\"坏了\"}]"),
            @"C:\Users\x\AppData\Local\LanDisk");

        UpdateHealthResult result = UpdateHealth.Evaluate(json, Expectation());

        Assert.False(result.Ok);
        Assert.Contains(result.Failures, f => f.Contains("ok=false") && f.Contains("shares-store"));
    }

    [Fact]
    public void A_non_json_health_response_is_reported_as_such()
    {
        UpdateHealthResult result = UpdateHealth.Evaluate("<html>502</html>", Expectation());

        Assert.False(result.Ok);
        Assert.Contains("不是合法 JSON", result.Message);
    }

    [Fact]
    public void A_rollback_expectation_checks_the_previous_version_and_commit()
    {
        string json = WithDataRoot(HealthJson(version: "1.2.0", commit: "1111111111111111"), @"C:\Users\x\AppData\Local\LanDisk");

        Assert.True(UpdateHealth.Evaluate(json, Expectation(forRollback: true)).Ok);

        // The failed version still running is not an acceptable rollback result.
        string stillNew = WithDataRoot(HealthJson(version: "1.3.0", commit: "abcdef1234567890"), @"C:\Users\x\AppData\Local\LanDisk");
        UpdateHealthResult result = UpdateHealth.Evaluate(stillNew, Expectation(forRollback: true));
        Assert.False(result.Ok);
        Assert.Contains(result.Failures, f => f.Contains("1.2.0"));
    }

    [Fact]
    public void An_expectation_built_from_a_snapshot_carries_every_compared_field()
    {
        var snapshot = new PreUpdateSnapshot
        {
            Version = "1.2.0",
            BuildCommit = "1111111111111111",
            DataRoot = @"C:\d",
            AdminPort = 8891,
            LanPort = 8892,
            CertificateThumbprint = "TP"
        };

        var manifest = new UpdateManifest { Version = "1.3.0", BuildCommit = "2222222222222222" };

        UpdateHealthExpectation forward = UpdateHealthExpectation.FromSnapshot(snapshot, manifest, isRollback: false);
        Assert.Equal("1.3.0", forward.Version);
        Assert.Equal("2222222222222222", forward.BuildCommit);
        Assert.Equal(8891, forward.AdminPort);
        Assert.Equal("TP", forward.CertificateThumbprint);
        Assert.True(forward.CertificateWasPresent);

        UpdateHealthExpectation backward = UpdateHealthExpectation.FromSnapshot(snapshot, manifest, isRollback: true);
        Assert.Equal("1.2.0", backward.Version);
        Assert.Equal("1111111111111111", backward.BuildCommit);
    }

    // ------------------------------------------------------------------ live health endpoint

    [Fact]
    public async Task The_running_health_endpoint_reports_every_field_the_updater_compares()
    {
        await using TestHost host = await TestHost.CreateAsync(
            nameof(The_running_health_endpoint_reports_every_field_the_updater_compares),
            options => options.InstallRoot = NewInstallRoot("health-fields"));
        var admin = new LanDiskClient(host.NewClient());
        await admin.AdminLoginAsync(host.AdminToken);

        ApiResult<JsonElement> result = await admin.GetAsync<JsonElement>("/api/admin/health");
        Assert.True(result.IsSuccess, result.Raw);

        JsonElement body = result.Body;
        Assert.Equal("LanDisk", body.GetProperty("product").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("version").GetString()));

        int adminPort = body.GetProperty("adminPort").GetInt32();
        int lanPort = body.GetProperty("lanPort").GetInt32();
        Assert.True(adminPort > 0 && lanPort > 0 && adminPort != lanPort);

        Assert.True(body.GetProperty("sharesStore").GetProperty("ok").GetBoolean());
        Assert.True(body.GetProperty("devicesStore").GetProperty("ok").GetBoolean());
        Assert.True(body.GetProperty("browseSmoke").GetProperty("ok").GetBoolean(), body.GetProperty("browseSmoke").GetProperty("message").GetString());

        List<string> ids = [.. body.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("id").GetString()!)];
        Assert.Contains("ports", ids);
        Assert.Contains("shares-store", ids);
        Assert.Contains("devices-store", ids);
        Assert.Contains("browse-smoke", ids);
        Assert.Contains("certificate", ids);

        TestBase.Record("H04-05", $"运行中的自检接口包含端口/状态库/证书/隔离浏览，共 {ids.Count} 项检查");
    }

    [Fact]
    public async Task The_health_snapshot_of_a_live_instance_matches_itself()
    {
        await using TestHost host = await TestHost.CreateAsync(
            nameof(The_health_snapshot_of_a_live_instance_matches_itself),
            options => options.InstallRoot = NewInstallRoot("health-snapshot"));
        var admin = new LanDiskClient(host.NewClient());
        await admin.AdminLoginAsync(host.AdminToken);

        // A published installation always carries build-info.json; give this test installation one so
        // this models a real install rather than a bare source tree. It is written into the test's own
        // installation directory — writing it into the shared test output directory is what made the
        // other test in this file depend on the previous run's leftovers.
        LanDisk.Update.InstallBuildInfoFile.Write(host.Runtime.Updates.InstallRoot, new LanDisk.Update.InstallBuildInfo
        {
            Version = LanDisk.Server.Endpoints.BuildInfo.Version,
            Commit = LanDisk.Server.Endpoints.BuildInfo.Commit
        });

        // Build the expectation the updater would build, from the service's own snapshot, then feed
        // the live health payload back in: a healthy instance must satisfy its own baseline.
        PreUpdateSnapshot snapshot = host.Runtime.Updates.CaptureSnapshot(host.Runtime.Settings.HttpPort, host.Runtime.Settings.HttpsPort);
        UpdateHealthExpectation expectation = UpdateHealthExpectation.FromSnapshot(snapshot, null, isRollback: false);

        ApiResult<JsonElement> result = await admin.GetAsync<JsonElement>("/api/admin/health");
        UpdateHealthResult evaluated = UpdateHealth.Evaluate(result.Raw, expectation);

        Assert.True(evaluated.Ok, evaluated.Message);
    }

    [Fact]
    public async Task The_snapshot_records_the_ports_certificate_and_store_hashes()
    {
        await using TestHost host = await TestHost.CreateAsync(nameof(The_snapshot_records_the_ports_certificate_and_store_hashes));
        var admin = new LanDiskClient(host.NewClient());
        await admin.AdminLoginAsync(host.AdminToken);
        await admin.CreateShareAsync(host.SandboxRoot);

        PreUpdateSnapshot snapshot = host.Runtime.Updates.CaptureSnapshot(8891, 8892);

        Assert.Equal("LanDisk", snapshot.Product);
        Assert.Equal(8891, snapshot.AdminPort);
        Assert.Equal(8892, snapshot.LanPort);
        Assert.Equal(host.DataRoot, snapshot.DataRoot, ignoreCase: true);
        Assert.NotNull(snapshot.SharesStoreSha256);
        Assert.Equal(1, snapshot.ShareCount);

        // The snapshot is stored where the updater can read it after this process is gone.
        host.Runtime.Updates.WriteSnapshot(snapshot);
        PreUpdateSnapshot? reloaded = host.Runtime.Updates.ReadSnapshot();
        Assert.NotNull(reloaded);
        Assert.Equal(snapshot.SharesStoreSha256, reloaded!.SharesStoreSha256);
        Assert.True(File.Exists(Path.Combine(host.DataRoot, "updates", "pre-update-health.json")));
    }

    // ------------------------------------------------------------------ managed state snapshot

    [Fact]
    public void The_managed_state_scope_covers_the_files_a_version_may_add()
    {
        using var kit = new UpdateTestKit(nameof(The_managed_state_scope_covers_the_files_a_version_may_add));
        string dataRoot = kit.NewDirectory("data");

        File.WriteAllText(Path.Combine(dataRoot, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(dataRoot, "shares.json"), "[]");
        Directory.CreateDirectory(Path.Combine(dataRoot, "secrets"));
        File.WriteAllText(Path.Combine(dataRoot, "secrets", "admin-token.txt"), "x");
        Directory.CreateDirectory(Path.Combine(dataRoot, "tls"));
        File.WriteAllText(Path.Combine(dataRoot, "tls", "landisk.pfx"), "y");

        // Not managed: caches, logs, the update system's own directory and, crucially, anything the
        // user keeps elsewhere.
        File.WriteAllText(Path.Combine(dataRoot, "landisk.log"), "log");
        Directory.CreateDirectory(Path.Combine(dataRoot, "thumbs"));
        File.WriteAllText(Path.Combine(dataRoot, "thumbs", "a.jpg"), "cache");
        Directory.CreateDirectory(Path.Combine(dataRoot, "updates"));
        File.WriteAllText(Path.Combine(dataRoot, "updates", "state.json"), "{}");

        List<string> managed = UpdateBackupScope.Enumerate(dataRoot);

        Assert.Contains("settings.json", managed);
        Assert.Contains("shares.json", managed);
        Assert.Contains("secrets/admin-token.txt", managed);
        Assert.Contains("tls/landisk.pfx", managed);
        Assert.DoesNotContain("landisk.log", managed);
        Assert.DoesNotContain("thumbs/a.jpg", managed);
        Assert.DoesNotContain(managed, p => p.StartsWith("updates/", StringComparison.OrdinalIgnoreCase));

        Assert.True(UpdateBackupScope.IsManaged("preferences.json"));
        Assert.True(UpdateBackupScope.IsManaged("shell.json"));
        Assert.True(UpdateBackupScope.IsManaged("secrets/anything.txt"));
        Assert.False(UpdateBackupScope.IsManaged("updates/state.json"));
        Assert.False(UpdateBackupScope.IsManaged("thumbs/a.jpg"));
    }

    [Fact]
    public void Rollback_removes_managed_state_files_the_failed_version_added()
    {
        using var kit = new UpdateTestKit(nameof(Rollback_removes_managed_state_files_the_failed_version_added));
        string dataRoot = kit.NewDirectory("data");
        string install = kit.CreateInstall("install", "1.2.0", new Dictionary<string, string>
        {
            ["LanDisk.exe"] = "old-exe"
        });

        File.WriteAllText(Path.Combine(dataRoot, "settings.json"), """{"httpPort":8891}""");
        File.WriteAllText(Path.Combine(dataRoot, "shares.json"), """[{"id":"BF"}]""");
        Directory.CreateDirectory(Path.Combine(dataRoot, "secrets"));
        File.WriteAllText(Path.Combine(dataRoot, "secrets", "admin-token.txt"), "token");

        var applier = new UpdateApplier(install, _ => { }) { };
        UpdateBackup backup = applier.Backup(dataRoot, "1.2.0");

        string package = kit.BuildPackage("rollback-extra-state", new PackageSpec()
            .Only(("LanDisk.exe", "new-exe")));
        VerifiedUpdatePackage verified = kit.Extract(package, "1.1.0");
        UpdatePlan plan = UpdatePlan.Build(verified.PayloadRoot, verified.Manifest, install);
        Assert.True(applier.Apply(plan, verified.Manifest).Ok);

        // The "new version" migrates state and adds files the old version never had.
        File.WriteAllText(Path.Combine(dataRoot, "settings.json"), """{"httpPort":18991,"httpsPort":18992}""");
        File.WriteAllText(Path.Combine(dataRoot, "preferences.json"), """{"sort":"size"}""");
        File.WriteAllText(Path.Combine(dataRoot, "shell.json"), """{"trayNoticeSuppressed":true}""");
        Directory.CreateDirectory(Path.Combine(dataRoot, "tls"));
        File.WriteAllText(Path.Combine(dataRoot, "tls", "new-cert.pfx"), "new");

        UpdateRollbackResult rollback = applier.Rollback(dataRoot, backup, verified.Manifest);

        Assert.True(rollback.Ok, string.Join("; ", rollback.Failures));
        Assert.True(rollback.ExtraStateFilesRemoved >= 3);
        Assert.Empty(rollback.StateMismatches);

        Assert.Equal("""{"httpPort":8891}""", File.ReadAllText(Path.Combine(dataRoot, "settings.json")));
        Assert.False(File.Exists(Path.Combine(dataRoot, "preferences.json")));
        Assert.False(File.Exists(Path.Combine(dataRoot, "shell.json")));
        Assert.False(File.Exists(Path.Combine(dataRoot, "tls", "new-cert.pfx")));
        Assert.Equal("token", File.ReadAllText(Path.Combine(dataRoot, "secrets", "admin-token.txt")));

        TestBase.Record("H04-07", $"回滚移除了失败版本新增的 {rollback.ExtraStateFilesRemoved} 个受管状态文件，并逐字节恢复了原有状态");
    }

    [Fact]
    public void Rollback_never_touches_files_outside_the_managed_scope()
    {
        using var kit = new UpdateTestKit(nameof(Rollback_never_touches_files_outside_the_managed_scope));
        string dataRoot = kit.NewDirectory("data");
        string install = kit.CreateInstall("install", "1.2.0", new Dictionary<string, string>
        {
            ["LanDisk.exe"] = "old-exe"
        });

        File.WriteAllText(Path.Combine(dataRoot, "settings.json"), "{}");

        // A user's own file in the data root, plus caches and logs: none of it is ours to remove.
        File.WriteAllText(Path.Combine(dataRoot, "我的说明.txt"), "用户自己放的文件");
        Directory.CreateDirectory(Path.Combine(dataRoot, "thumbs"));
        File.WriteAllText(Path.Combine(dataRoot, "thumbs", "cached.jpg"), "cache");
        Directory.CreateDirectory(Path.Combine(dataRoot, "updates"));
        File.WriteAllText(Path.Combine(dataRoot, "updates", "state.json"), "{}");

        var applier = new UpdateApplier(install);
        UpdateBackup backup = applier.Backup(dataRoot, "1.2.0");

        string package = kit.BuildPackage("rollback-scope", new PackageSpec().Only(("LanDisk.exe", "new-exe")));
        VerifiedUpdatePackage verified = kit.Extract(package, "1.1.0");
        UpdatePlan plan = UpdatePlan.Build(verified.PayloadRoot, verified.Manifest, install);
        Assert.True(applier.Apply(plan, verified.Manifest).Ok);

        UpdateRollbackResult rollback = applier.Rollback(dataRoot, backup, verified.Manifest);

        Assert.True(rollback.Ok, string.Join("; ", rollback.Failures));
        Assert.True(File.Exists(Path.Combine(dataRoot, "我的说明.txt")));
        Assert.True(File.Exists(Path.Combine(dataRoot, "thumbs", "cached.jpg")));
        Assert.True(File.Exists(Path.Combine(dataRoot, "updates", "state.json")));
    }

    [Fact]
    public void Rollback_reports_a_mismatch_instead_of_claiming_success()
    {
        using var kit = new UpdateTestKit(nameof(Rollback_reports_a_mismatch_instead_of_claiming_success));
        string dataRoot = kit.NewDirectory("data");
        string install = kit.CreateInstall("install", "1.2.0", new Dictionary<string, string>
        {
            ["LanDisk.exe"] = "old-exe"
        });

        File.WriteAllText(Path.Combine(dataRoot, "settings.json"), """{"httpPort":8891}""");
        var applier = new UpdateApplier(install);
        UpdateBackup backup = applier.Backup(dataRoot, "1.2.0");

        string package = kit.BuildPackage("rollback-mismatch", new PackageSpec().Only(("LanDisk.exe", "new-exe")));
        VerifiedUpdatePackage verified = kit.Extract(package, "1.1.0");
        UpdatePlan plan = UpdatePlan.Build(verified.PayloadRoot, verified.Manifest, install);
        Assert.True(applier.Apply(plan, verified.Manifest).Ok);

        // Corrupt the recorded baseline so the post-restore verification cannot pass. This models a
        // backup that silently lost data: the rollback must say so rather than report success.
        UpdateBackup damaged = new()
        {
            Root = backup.Root,
            ProgramDir = backup.ProgramDir,
            StateDir = backup.StateDir,
            FromVersion = backup.FromVersion,
            ProgramFileCount = backup.ProgramFileCount,
            ProgramBytes = backup.ProgramBytes,
            StateFileCount = backup.StateFileCount,
            CreatedAt = backup.CreatedAt,
            StateSnapshot = new ManagedStateSnapshot
            {
                DataRoot = dataRoot,
                Files = [.. backup.StateSnapshot!.Files, new ManagedStateEntry { Path = "settings.json", Bytes = 1, Sha256 = new string('f', 64) }]
            }
        };

        UpdateRollbackResult rollback = applier.Rollback(dataRoot, damaged, verified.Manifest);

        Assert.False(rollback.Ok);
        Assert.NotEmpty(rollback.StateMismatches);
    }

    [Fact]
    public void A_rollback_without_a_snapshot_is_reported_as_unverifiable()
    {
        using var kit = new UpdateTestKit(nameof(A_rollback_without_a_snapshot_is_reported_as_unverifiable));
        string dataRoot = kit.NewDirectory("data");
        string install = kit.CreateInstall("install", "1.2.0", new Dictionary<string, string>
        {
            ["LanDisk.exe"] = "old-exe"
        });

        File.WriteAllText(Path.Combine(dataRoot, "settings.json"), "{}");
        var applier = new UpdateApplier(install);
        UpdateBackup backup = applier.Backup(dataRoot, "1.2.0");

        UpdateBackup withoutSnapshot = new()
        {
            Root = backup.Root,
            ProgramDir = backup.ProgramDir,
            StateDir = backup.StateDir,
            FromVersion = backup.FromVersion,
            ProgramFileCount = backup.ProgramFileCount,
            ProgramBytes = backup.ProgramBytes,
            StateFileCount = backup.StateFileCount,
            CreatedAt = backup.CreatedAt,
            StateSnapshot = null
        };

        // Also remove the on-disk copy so there is genuinely nothing to restore against.
        File.Delete(Path.Combine(backup.Root, "state-snapshot.json"));

        UpdateRollbackResult rollback = applier.Rollback(dataRoot, withoutSnapshot, null);

        Assert.False(rollback.Ok);
        Assert.Contains(rollback.Failures, f => f.Contains("快照"));
    }

    // ------------------------------------------------------------------ transaction / interruption

    [Fact]
    public void An_unfinished_transaction_is_readable_and_clearable()
    {
        using var kit = new UpdateTestKit(nameof(An_unfinished_transaction_is_readable_and_clearable));
        string dataRoot = kit.NewDirectory("data");
        UpdatePaths paths = UpdatePaths.ForDataRoot(dataRoot);
        paths.EnsureCreated();

        Assert.Null(UpdateSnapshotStore.ReadTransaction(paths));

        UpdateSnapshotStore.WriteTransaction(paths, new UpdateTransaction
        {
            Stage = "Applying",
            FromVersion = "1.2.0",
            ToVersion = "1.3.0",
            PackageSha256 = new string('a', 64),
            BackupRoot = @"C:\backup"
        });

        UpdateTransaction? read = UpdateSnapshotStore.ReadTransaction(paths);
        Assert.NotNull(read);
        Assert.Equal("Applying", read!.Stage);
        Assert.Equal(new string('a', 64), read.PackageSha256);

        UpdateSnapshotStore.ClearTransaction(paths);
        Assert.Null(UpdateSnapshotStore.ReadTransaction(paths));
    }

    [Fact]
    public void Snapshots_round_trip_through_the_updates_directory()
    {
        using var kit = new UpdateTestKit(nameof(Snapshots_round_trip_through_the_updates_directory));
        string dataRoot = kit.NewDirectory("data");
        UpdatePaths paths = UpdatePaths.ForDataRoot(dataRoot);
        paths.EnsureCreated();

        UpdateSnapshotStore.WriteHealth(paths, new PreUpdateSnapshot
        {
            Version = "1.2.0",
            BuildCommit = "abc",
            AdminPort = 8891,
            LanPort = 8892,
            CertificateThumbprint = "TP"
        });

        UpdateSnapshotStore.WriteManagedState(paths, new ManagedStateSnapshot
        {
            DataRoot = dataRoot,
            Files = [new ManagedStateEntry { Path = "settings.json", Bytes = 3, Sha256 = "abc" }]
        });

        Assert.Equal("1.2.0", UpdateSnapshotStore.ReadHealth(paths)!.Version);
        Assert.Equal("TP", UpdateSnapshotStore.ReadHealth(paths)!.CertificateThumbprint);
        Assert.Single(UpdateSnapshotStore.ReadManagedState(paths)!.Files);

        // A damaged file is treated as absent rather than crashing the updater.
        File.WriteAllText(paths.PreUpdateSnapshotFile, "{ not json");
        Assert.Null(UpdateSnapshotStore.ReadHealth(paths));
    }

    // ------------------------------------------------------------------ package hash trail

    [Fact]
    public async Task A_successful_update_records_the_package_hash_in_the_history()
    {
        using var kit = new UpdateTestKit(nameof(A_successful_update_records_the_package_hash_in_the_history));
        string dataRoot = kit.NewDirectory("data");
        string install = kit.CreateInstall("install", "1.2.0", new Dictionary<string, string>
        {
            ["LanDisk.exe"] = "old-exe"
        });

        var service = new UpdateService(dataRoot, install, "1.2.0", "commit-1.2.0")
        {
            PublicKeyProvider = () => kit.Key
        };

        string package = kit.BuildPackage("hash-trail", new PackageSpec { Version = "1.3.0" });
        using (FileStream stream = File.OpenRead(package))
        {
            StagedUpdate staged = await service.StageAsync(stream, "hash-trail.lupd");
            Assert.True(staged.Ok, staged.Message);
            Assert.Equal(UpdatePackage.HashFile(package), staged.PackageSha256);

            UpdateStateDocument document = service.Store.Read();
            Assert.Equal(staged.PackageSha256, document.PendingPackageSha256);
            Assert.NotNull(document.PendingPayloadDigest);
        }

        UpdateHistoryEntry stagedEntry = service.Store.Read().History.First(h => h.Outcome == "Staged");
        Assert.Equal(UpdatePackage.HashFile(package), stagedEntry.PackageSha256);
        Assert.NotNull(stagedEntry.PayloadDigest);

        service.DiscardPending();
        UpdateHistoryEntry discarded = service.Store.Read().History.First(h => h.Outcome == "Discarded");
        Assert.Equal(UpdatePackage.HashFile(package), discarded.PackageSha256);

        TestBase.Record("H04-06", "暂存/放弃都会把更新包 SHA256 写入更新历史");
    }

    [Fact]
    public async Task A_rejected_package_records_its_hash_when_the_bytes_were_readable()
    {
        using var kit = new UpdateTestKit(nameof(A_rejected_package_records_its_hash_when_the_bytes_were_readable));
        using var stranger = new UpdateTestKit(nameof(A_rejected_package_records_its_hash_when_the_bytes_were_readable) + "-stranger");
        string dataRoot = kit.NewDirectory("data");
        string install = kit.CreateInstall("install", "1.2.0", new Dictionary<string, string>
        {
            ["LanDisk.exe"] = "old-exe"
        });

        var service = new UpdateService(dataRoot, install, "1.2.0", "commit-1.2.0")
        {
            PublicKeyProvider = () => kit.Key
        };

        string package = stranger.BuildPackage("rejected", new PackageSpec { Version = "1.3.0" });
        using (FileStream stream = File.OpenRead(package))
        {
            StagedUpdate staged = await service.StageAsync(stream, "rejected.lupd");
            Assert.False(staged.Ok);
        }

        UpdateHistoryEntry rejected = service.Store.Read().History.First(h => h.Outcome == "Rejected");
        Assert.Equal(UpdatePackage.HashFile(package), rejected.PackageSha256);
        Assert.Contains("UNKNOWN_KEY_ID", rejected.Message);

        TestBase.Record("H04-06", $"被拒绝的更新包也记录了 SHA256（{rejected.Outcome}）");
    }

    [Fact]
    public async Task A_rejection_that_never_read_the_bytes_says_why_there_is_no_hash()
    {
        using var kit = new UpdateTestKit(nameof(A_rejection_that_never_read_the_bytes_says_why_there_is_no_hash));
        string dataRoot = kit.NewDirectory("data");
        string install = kit.CreateInstall("install", "1.2.0", new Dictionary<string, string>
        {
            ["LanDisk.exe"] = "old-exe"
        });

        var service = new UpdateService(dataRoot, install, "1.2.0", "commit-1.2.0")
        {
            PublicKeyProvider = () => kit.Key
        };

        using var empty = new MemoryStream([]);
        StagedUpdate staged = await service.StageAsync(empty, "empty.lupd");
        Assert.False(staged.Ok);

        UpdateHistoryEntry rejected = service.Store.Read().History.First(h => h.Outcome == "Rejected");

        // No hash is possible here, and the record says why instead of leaving an unexplained null.
        Assert.Null(rejected.PackageSha256);
        Assert.Contains("没有可计算 SHA256 的完整更新包", rejected.Message);
        Assert.Contains("上传内容为空", rejected.Message);
    }

    [Fact]
    public async Task An_apply_refusal_also_keeps_the_hash_and_digest()
    {
        using var kit = new UpdateTestKit(nameof(An_apply_refusal_also_keeps_the_hash_and_digest));
        string dataRoot = kit.NewDirectory("data");
        string install = kit.CreateInstall("install", "1.2.0", new Dictionary<string, string>
        {
            ["LanDisk.exe"] = "old-exe"
        });

        var service = new UpdateService(dataRoot, install, "1.2.0", "commit-1.2.0")
        {
            PublicKeyProvider = () => kit.Key
        };

        string package = kit.BuildPackage("apply-refusal", new PackageSpec { Version = "1.3.0" });
        using (FileStream stream = File.OpenRead(package))
        {
            Assert.True((await service.StageAsync(stream, "apply-refusal.lupd")).Ok);
        }

        // No updater in the install directory, so the apply is refused — but the trail keeps the hash.
        ApplyRequest refused = service.RequestApply(8891);
        Assert.False(refused.Accepted);
        Assert.Equal("UPDATER_MISSING", refused.Code);

        UpdateHistoryEntry entry = service.Store.Read().History.First(h => h.Outcome == "REFUSED");
        Assert.Equal(UpdatePackage.HashFile(package), entry.PackageSha256);
        Assert.NotNull(entry.PayloadDigest);
        Assert.Contains("UPDATER_MISSING", entry.Message);
    }

    [Fact]
    public async Task Applying_writes_the_pre_update_health_snapshot_before_launching_the_updater()
    {
        using var kit = new UpdateTestKit(nameof(Applying_writes_the_pre_update_health_snapshot_before_launching_the_updater));
        string dataRoot = kit.NewDirectory("data");
        string install = kit.CreateInstall("install", "1.2.0", new Dictionary<string, string>
        {
            ["LanDisk.exe"] = "old-exe"
        });

        // A real executable is needed because RequestApply starts whatever is in updater\.
        string updaterDir = Directory.CreateDirectory(Path.Combine(install, UpdateLayout.UpdaterDirectoryName)).FullName;
        File.Copy(Path.Combine(Environment.SystemDirectory, "where.exe"), Path.Combine(updaterDir, UpdateLayout.UpdaterFileName));

        var service = new UpdateService(dataRoot, install, "1.2.0", "commit-1.2.0")
        {
            PublicKeyProvider = () => kit.Key
        };

        string package = kit.BuildPackage("snapshot-before-apply", new PackageSpec { Version = "1.3.0" });
        using (FileStream stream = File.OpenRead(package))
        {
            Assert.True((await service.StageAsync(stream, "snapshot.lupd")).Ok);
        }

        ApplyRequest request = service.RequestApply(8891, null, 8892);
        Assert.True(request.Accepted, request.Message);
        Assert.Equal(UpdatePackage.HashFile(package), request.PackageSha256);

        PreUpdateSnapshot? snapshot = service.ReadSnapshot();
        Assert.NotNull(snapshot);
        Assert.Equal("1.2.0", snapshot!.Version);
        Assert.Equal(8891, snapshot.AdminPort);
        Assert.Equal(8892, snapshot.LanPort);

        UpdateHistoryEntry applying = service.Store.Read().History.First(h => h.Outcome == "Applying");
        Assert.Equal(UpdatePackage.HashFile(package), applying.PackageSha256);

        try
        {
            if (request.UpdaterProcessId is int pid) System.Diagnostics.Process.GetProcessById(pid).Kill();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
    }

    // ------------------------------------------------------------------ LAN address ranking

    [Fact]
    public void Loopback_is_never_offered_as_a_phone_address()
    {
        IReadOnlyList<LanAddressCandidate> candidates = LanInfo.RankForDevices();

        Assert.DoesNotContain(candidates, c => c.Address.StartsWith("127.", StringComparison.Ordinal));
        Assert.DoesNotContain(candidates, c => c.Address == "0.0.0.0");
        Assert.DoesNotContain(candidates, c => c.Address.Contains("::1", StringComparison.Ordinal));
    }

    [Fact]
    public void Virtual_adapters_are_ranked_after_physical_private_addresses()
    {
        // Build a synthetic list through the ranking rule itself, using the classifier directly so
        // the assertion does not depend on which adapters this machine happens to have.
        Assert.True(LanInfo.IsVirtualAdapter("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter"));
        Assert.True(LanInfo.IsVirtualAdapter("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter"));
        Assert.True(LanInfo.IsVirtualAdapter("Docker Desktop", "Docker Network Adapter"));
        Assert.True(LanInfo.IsVirtualAdapter("Tailscale", "Tailscale Tunnel"));
        Assert.True(LanInfo.IsVirtualAdapter("Bluetooth Network Connection", "Bluetooth Device (Personal Area Network)"));

        Assert.False(LanInfo.IsVirtualAdapter("Ethernet", "Realtek PCIe GbE Family Controller"));
        Assert.False(LanInfo.IsVirtualAdapter("WLAN", "Intel(R) Wi-Fi 6 AX201 160MHz"));

        // And on this machine, whatever the real ranking is, no virtual candidate may come before a
        // physical one.
        IReadOnlyList<LanAddressCandidate> ranked = LanInfo.RankForDevices();
        int lastPhysical = ranked.ToList().FindLastIndex(c => !c.IsVirtual);
        int firstVirtual = ranked.ToList().FindIndex(c => c.IsVirtual);
        if (lastPhysical >= 0 && firstVirtual >= 0)
            Assert.True(lastPhysical < firstVirtual, "物理网卡地址必须排在虚拟网卡之前");
    }

    [Fact]
    public void Ranking_never_changes_the_configured_bind_address()
    {
        // The ranking is a display concern only; it must not mutate any address used for binding.
        IReadOnlyList<NetworkInterfaceInfo> before = LanInfo.ListIPv4();
        _ = LanInfo.RankForDevices();
        _ = LanInfo.BestForDevices();
        IReadOnlyList<NetworkInterfaceInfo> after = LanInfo.ListIPv4();

        Assert.Equal(
            before.Select(a => a.Address).OrderBy(a => a, StringComparer.Ordinal),
            after.Select(a => a.Address).OrderBy(a => a, StringComparer.Ordinal));
    }

    [Fact]
    public void Every_candidate_carries_a_reason_and_a_usable_url()
    {
        foreach (LanAddressCandidate candidate in LanInfo.RankForDevices())
        {
            Assert.False(string.IsNullOrWhiteSpace(candidate.Reason));
            Assert.Contains(candidate.Address, candidate.Url(8892));
            Assert.StartsWith("http://", candidate.Url(8892));
        }
    }

    [Fact]
    public void An_address_filter_restricts_the_list_to_the_bound_addresses()
    {
        IReadOnlyList<LanAddressCandidate> all = LanInfo.RankForDevices();
        if (all.Count == 0) return;   // A machine with no LAN adapter has nothing to filter.

        string only = all[0].Address;
        IReadOnlyList<LanAddressCandidate> filtered = LanInfo.RankForDevices([only]);

        Assert.All(filtered, c => Assert.Equal(only, c.Address));
    }
}
