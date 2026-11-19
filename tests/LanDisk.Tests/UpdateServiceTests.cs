using System.Text;
using LanDisk.Tests.Infrastructure;
using LanDisk.Update;

namespace LanDisk.Tests;

/// <summary>
/// Staging, state persistence and the startup reconciliation the management page reports.
///
/// The service under test is given an ephemeral public key so the tests never need the release
/// private key. The shipping default (the compiled-in release key) is covered separately by
/// <c>ReleasePublicKey_self_check_matches_the_compiled_in_identifier</c>.
/// </summary>
public sealed class UpdateServiceTests
{
    private static (UpdateService Service, UpdateTestKit Kit, string DataRoot, string InstallRoot) Create(string label)
    {
        var kit = new UpdateTestKit(label);
        string dataRoot = kit.NewDirectory("data");
        string installRoot = kit.CreateInstall("install", "1.2.0", new Dictionary<string, string>
        {
            ["LanDisk.exe"] = "old-exe",
            ["LanDisk.Server.dll"] = "old-server"
        });

        var service = new UpdateService(dataRoot, installRoot, "1.2.0", "commit-1.2.0")
        {
            PublicKeyProvider = () => kit.Key
        };

        return (service, kit, dataRoot, installRoot);
    }

    // ------------------------------------------------------------------ state store

    [Fact]
    public void The_state_store_round_trips_and_keeps_history_newest_first()
    {
        using var kit = new UpdateTestKit(nameof(The_state_store_round_trips_and_keeps_history_newest_first));
        string dataRoot = kit.NewDirectory("data");
        var store = new UpdateStateStore(Path.Combine(dataRoot, "updates", "state.json"));

        Assert.Equal(UpdateStage.None, store.Read().Stage);

        store.Transition(UpdateStage.Staged, "已暂存");
        Assert.Equal(UpdateStage.Staged, store.Read().Stage);
        Assert.Equal("已暂存", store.Read().StageMessage);

        for (int i = 0; i < 25; i++)
        {
            store.AppendHistory(new UpdateHistoryEntry
            {
                FromVersion = "1.0.0",
                ToVersion = "1.0." + i,
                Outcome = "Committed",
                StartedAt = DateTimeOffset.Now,
                FinishedAt = DateTimeOffset.Now
            }, keep: 20);
        }

        UpdateStateDocument document = store.Read();
        Assert.Equal(20, document.History.Count);
        Assert.Equal("1.0.24", document.History[0].ToVersion);
    }

    [Fact]
    public void A_corrupt_state_file_is_treated_as_an_empty_state_instead_of_crashing()
    {
        using var kit = new UpdateTestKit(nameof(A_corrupt_state_file_is_treated_as_an_empty_state_instead_of_crashing));
        string dataRoot = kit.NewDirectory("data");
        string path = Path.Combine(dataRoot, "updates", "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json at all");

        var store = new UpdateStateStore(path);
        UpdateStateDocument document = store.Read();

        Assert.Equal(UpdateStage.None, document.Stage);
        Assert.Empty(document.History);

        // It must still be writable afterwards.
        store.Transition(UpdateStage.Staged, "恢复");
        Assert.Equal(UpdateStage.Staged, store.Read().Stage);
    }

    // ------------------------------------------------------------------ startup reconciliation

    [Fact]
    public void Reconcile_adopts_the_version_recorded_in_the_install_manifest()
    {
        (UpdateService service, UpdateTestKit kit, _, _) = Create(nameof(Reconcile_adopts_the_version_recorded_in_the_install_manifest));
        using (kit)
        {
            UpdateStateDocument document = service.Reconcile();
            Assert.Equal("1.2.0", document.InstalledVersion);
            Assert.Equal("test", document.InstalledCommit);
        }
    }

    [Fact]
    public void Reconcile_reports_an_update_that_never_reached_a_terminal_stage()
    {
        (UpdateService service, UpdateTestKit kit, _, _) = Create(nameof(Reconcile_reports_an_update_that_never_reached_a_terminal_stage));
        using (kit)
        {
            service.Store.Transition(UpdateStage.Applying, "正在应用", needsReconcile: true);

            UpdateStateDocument document = service.Reconcile();

            Assert.Equal(UpdateStage.Failed, document.Stage);
            Assert.Equal("Interrupted", document.LastOutcome);
            Assert.Contains("未能完成", document.LastMessage);
            Assert.False(document.NeedsReconcile);
            Assert.Null(document.PendingStagingRoot);
        }
    }

    [Fact]
    public void Reconcile_keeps_a_committed_outcome_for_the_ui_to_show()
    {
        (UpdateService service, UpdateTestKit kit, _, _) = Create(nameof(Reconcile_keeps_a_committed_outcome_for_the_ui_to_show));
        using (kit)
        {
            UpdateStateDocument document = service.Store.Read();
            document.Stage = UpdateStage.Committed;
            document.LastOutcome = "Committed";
            document.LastMessage = "已更新到 1.3.0。";
            document.NeedsReconcile = true;
            service.Store.Write(document);

            UpdateStateDocument reconciled = service.Reconcile();

            Assert.Equal(UpdateStage.Committed, reconciled.Stage);
            Assert.Equal("Committed", reconciled.LastOutcome);
            Assert.False(reconciled.NeedsReconcile);
        }
    }

    // ------------------------------------------------------------------ staging

    [Fact]
    public async Task Staging_a_valid_package_records_everything_the_ui_needs_to_show_it()
    {
        (UpdateService service, UpdateTestKit kit, _, _) = Create(nameof(Staging_a_valid_package_records_everything_the_ui_needs_to_show_it));
        using (kit)
        {
            string package = kit.BuildPackage("good", new PackageSpec
            {
                Version = "1.3.0",
                BuildCommit = "abcdef1234567890",
                ReleaseNotes = "本次更新修复了若干问题。"
            });

            StagedUpdate staged;
            using (var stream = File.OpenRead(package))
            {
                staged = await service.StageAsync(stream, "LanDisk-update-1.3.0.lupd");
            }

            Assert.True(staged.Ok, staged.Message);
            Assert.Equal("1.3.0", staged.Version);
            Assert.Equal(kit.KeyId, staged.KeyId);
            Assert.Equal(2, staged.FileCount);
            Assert.True(staged.PackageSha256!.Length == 64);
            Assert.True(Directory.Exists(staged.StagingRoot));

            UpdateStateDocument document = service.Store.Read();
            Assert.Equal(UpdateStage.Staged, document.Stage);
            Assert.Equal("1.3.0", document.PendingVersion);
            Assert.Equal(kit.KeyId, document.PendingKeyId);
            Assert.Equal("abcdef1234567890", document.PendingBuildCommit);
            Assert.NotNull(document.StagedAt);

            // The uploaded package itself is not kept: the verified staging directory is.
            Assert.Empty(Directory.GetFiles(service.Paths.IncomingDir));

            TestBase.Record("UPDATE-06", $"上传校验：{staged.FileCount} 个文件 / {staged.TotalBytes} 字节，签名者 {staged.KeyId}");
        }
    }

    [Fact]
    public async Task A_package_signed_by_an_untrusted_key_is_refused_and_leaves_nothing_behind()
    {
        (UpdateService service, UpdateTestKit kit, _, _) = Create(nameof(A_package_signed_by_an_untrusted_key_is_refused_and_leaves_nothing_behind));
        using (kit)
        {
            using var stranger = new UpdateTestKit("stranger");
            string package = stranger.BuildPackage("foreign", new PackageSpec { Version = "1.3.0" });

            StagedUpdate staged;
            using (var stream = File.OpenRead(package))
            {
                staged = await service.StageAsync(stream, "foreign.lupd");
            }

            Assert.False(staged.Ok);
            // The signer identity is checked first: a package signed by an unknown key is refused as
            // "unknown signer", which is the actionable diagnosis.
            Assert.Equal("UNKNOWN_KEY_ID", staged.Code);

            UpdateStateDocument document = service.Store.Read();
            Assert.Null(document.PendingVersion);
            Assert.Equal(UpdateStage.None, document.Stage);
            Assert.Empty(Directory.GetFiles(service.Paths.IncomingDir));
            Assert.False(Directory.Exists(Path.Combine(service.Paths.StagingDir, "payload")));

            TestBase.Record("UPDATE-07", "陌生密钥签名的更新包被服务端拒绝，未留下暂存内容");
        }
    }

    [Fact]
    public async Task A_file_that_is_not_a_zip_is_refused_with_a_clear_code()
    {
        (UpdateService service, UpdateTestKit kit, _, _) = Create(nameof(A_file_that_is_not_a_zip_is_refused_with_a_clear_code));
        using (kit)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("这不是一个 zip 文件"));

            StagedUpdate staged = await service.StageAsync(stream, "not-a-package.lupd");

            Assert.False(staged.Ok);
            Assert.Equal("NOT_A_ZIP", staged.Code);
        }
    }

    [Fact]
    public async Task An_empty_upload_is_refused()
    {
        (UpdateService service, UpdateTestKit kit, _, _) = Create(nameof(An_empty_upload_is_refused));
        using (kit)
        {
            using var stream = new MemoryStream([]);

            StagedUpdate staged = await service.StageAsync(stream, "empty.lupd");

            Assert.False(staged.Ok);
            Assert.Equal("PACKAGE_EMPTY", staged.Code);
        }
    }

    [Fact]
    public void Inspecting_a_package_reports_the_signed_metadata_without_staging_it()
    {
        (UpdateService service, UpdateTestKit kit, _, _) = Create(nameof(Inspecting_a_package_reports_the_signed_metadata_without_staging_it));
        using (kit)
        {
            string package = kit.BuildPackage("inspect", new PackageSpec { Version = "1.5.0", BuildCommit = "deadbeef" });

            UpdateVerificationReport report = service.Inspect(package);

            Assert.True(report.Ok, report.Message);
            Assert.Equal("1.5.0", report.Version);
            Assert.Equal("deadbeef", report.BuildCommit);
            Assert.Equal(64, report.PackageSha256!.Length);
            Assert.Equal(UpdateStage.None, service.Store.Read().Stage);
        }
    }

    // ------------------------------------------------------------------ plan and discard

    [Fact]
    public async Task The_staged_plan_describes_exactly_what_will_change()
    {
        (UpdateService service, UpdateTestKit kit, _, _) = Create(nameof(The_staged_plan_describes_exactly_what_will_change));
        using (kit)
        {
            string package = kit.BuildPackage("plan", new PackageSpec { Version = "1.3.0" }
                .Only(("LanDisk.exe", "new-exe"), ("LanDisk.Server.dll", "new-server")));
            using (var stream = File.OpenRead(package)) await service.StageAsync(stream, "plan.lupd");

            (UpdatePlan? plan, string? error) = service.DescribeStagedPlan();

            Assert.Null(error);
            Assert.NotNull(plan);
            Assert.Equal(2, plan!.FilesToWrite);
            Assert.Equal(0, plan.FilesToDelete);
            Assert.Contains(plan.Writes, a => a.RelativePath == "LanDisk.exe" && a.TargetExists);
        }
    }

    [Fact]
    public async Task Discarding_a_staged_package_clears_the_state_and_the_disk()
    {
        (UpdateService service, UpdateTestKit kit, _, _) = Create(nameof(Discarding_a_staged_package_clears_the_state_and_the_disk));
        using (kit)
        {
            string package = kit.BuildPackage("discard", new PackageSpec { Version = "1.3.0" });
            StagedUpdate staged;
            using (var stream = File.OpenRead(package)) staged = await service.StageAsync(stream, "discard.lupd");
            Assert.True(staged.Ok);

            Assert.True(service.DiscardPending());

            UpdateStateDocument document = service.Store.Read();
            Assert.Null(document.PendingVersion);
            Assert.Null(document.PendingStagingRoot);
            Assert.Equal(UpdateStage.None, document.Stage);
            Assert.False(Directory.Exists(staged.StagingRoot));
            Assert.Null(service.PendingStagingRoot);
        }
    }

    // ------------------------------------------------------------------ apply preconditions

    [Fact]
    public void Apply_is_refused_when_nothing_is_staged()
    {
        (UpdateService service, UpdateTestKit kit, _, _) = Create(nameof(Apply_is_refused_when_nothing_is_staged));
        using (kit)
        {
            ApplyRequest request = service.RequestApply(8891);

            Assert.False(request.Accepted);
            Assert.Equal("NOTHING_STAGED", request.Code);
        }
    }

    [Fact]
    public async Task Apply_is_refused_when_the_installation_has_no_updater()
    {
        (UpdateService service, UpdateTestKit kit, _, _) = Create(nameof(Apply_is_refused_when_the_installation_has_no_updater));
        using (kit)
        {
            string package = kit.BuildPackage("no-updater", new PackageSpec { Version = "1.3.0" });
            using (var stream = File.OpenRead(package)) await service.StageAsync(stream, "no-updater.lupd");

            ApplyRequest request = service.RequestApply(8891);

            Assert.False(request.Accepted);
            Assert.Equal("UPDATER_MISSING", request.Code);
            Assert.Contains("LanDisk.Updater.exe", request.Message);

            // Refusing here is important: a half-installed update with no way to roll back is
            // worse than not updating at all.
            Assert.Equal(UpdateStage.Staged, service.Store.Read().Stage);
        }
    }

    [Fact]
    public async Task Apply_copies_the_updater_out_of_the_installation_directory_before_launching_it()
    {
        (UpdateService service, UpdateTestKit kit, _, string installRoot) = Create(nameof(Apply_copies_the_updater_out_of_the_installation_directory_before_launching_it));
        using (kit)
        {
            // A harmless stand-in for the real updater: this test asserts the launch contract, not
            // the updater's behaviour (which is exercised end to end against a real release
            // candidate). where.exe accepts any argument and exits immediately.
            string updaterDir = Directory.CreateDirectory(Path.Combine(installRoot, UpdateLayout.UpdaterDirectoryName)).FullName;
            File.Copy(Path.Combine(Environment.SystemDirectory, "where.exe"), Path.Combine(updaterDir, UpdateLayout.UpdaterFileName));

            string package = kit.BuildPackage("apply-contract", new PackageSpec { Version = "1.3.0" });
            using (var stream = File.OpenRead(package)) await service.StageAsync(stream, "apply-contract.lupd");

            ApplyRequest request = service.RequestApply(8891);

            Assert.True(request.Accepted, request.Message);
            Assert.Equal("1.3.0", request.TargetVersion);
            Assert.NotNull(request.UpdaterProcessId);

            // The runner copy must live outside the installation directory, because the update is
            // about to replace that directory's contents.
            string runnerDir = Path.Combine(service.Paths.Root, "runner");
            string[] runners = Directory.GetFiles(runnerDir, UpdateLayout.UpdaterFileName, SearchOption.AllDirectories);
            Assert.Single(runners);
            Assert.DoesNotContain(installRoot, runners[0], StringComparison.OrdinalIgnoreCase);

            UpdateStateDocument document = service.Store.Read();
            Assert.Equal(UpdateStage.Applying, document.Stage);
            Assert.True(document.NeedsReconcile);

            try
            {
                if (request.UpdaterProcessId is int pid) System.Diagnostics.Process.GetProcessById(pid).Kill();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }

            TestBase.Record("UPDATE-08", "应用更新前会把 updater 复制到安装目录之外再启动（避免替换自身）");
        }
    }
}
