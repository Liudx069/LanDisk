using System.Text;
using System.Text.Json;
using LanDisk.Tests.Infrastructure;
using LanDisk.Update;

namespace LanDisk.Tests;

/// <summary>
/// The part of the update system that touches the disk: planning, backing up, applying, verifying
/// and rolling back.
///
/// Every test here runs entirely inside its own temporary "installation" and data directory. The
/// user's real installation under %LOCALAPPDATA%\LanDisk is never read or written.
/// </summary>
public sealed class UpdateApplyTests
{
    private static string StateFile(string dataRoot) => Path.Combine(dataRoot, "settings.json");

    // ------------------------------------------------------------------ planning

    [Fact]
    public void Plan_writes_only_what_changed_and_skips_identical_files()
    {
        using var kit = new UpdateTestKit(nameof(Plan_writes_only_what_changed_and_skips_identical_files));
        string install = kit.CreateInstall("install", "1.2.0", new Dictionary<string, string>
        {
            ["LanDisk.exe"] = "old-exe",
            ["LanDisk.Server.dll"] = "same-dll"
        });

        string package = kit.BuildPackage("plan", new PackageSpec()
            .Only(
                ("LanDisk.exe", "new-exe"),
                ("LanDisk.Server.dll", "same-dll"),
                ("LanDisk.Core.dll", "brand-new")));
        VerifiedUpdatePackage verified = kit.Extract(package, "1.1.0");

        UpdatePlan plan = UpdatePlan.Build(verified.PayloadRoot, verified.Manifest, install);

        Assert.Equal(2, plan.FilesToWrite);
        Assert.Equal(0, plan.FilesToDelete);
        Assert.Contains(plan.Unchanged, p => p == "LanDisk.Server.dll");
        Assert.Contains(plan.Writes, a => a.RelativePath == "LanDisk.exe" && a.TargetExists);
        Assert.Contains(plan.Writes, a => a.RelativePath == "LanDisk.Core.dll" && !a.TargetExists);
        Assert.Equal(Encoding.UTF8.GetByteCount("same-dll"), plan.UnchangedBytes);
    }

    [Fact]
    public void Plan_deletes_only_files_the_previous_install_manifest_claims()
    {
        using var kit = new UpdateTestKit(nameof(Plan_deletes_only_files_the_previous_install_manifest_claims));
        string install = kit.CreateInstall("install", "1.2.0", new Dictionary<string, string>
        {
            ["LanDisk.exe"] = "old-exe",
            ["LanDisk.Legacy.dll"] = "dropped-in-1.3"
        });

        // A file the user put there by hand: not in the install manifest, so not ours to delete.
        File.WriteAllText(Path.Combine(install, "我的笔记.txt"), "do not delete me");

        string package = kit.BuildPackage("plan-delete", new PackageSpec().Only(("LanDisk.exe", "new-exe")));
        VerifiedUpdatePackage verified = kit.Extract(package, "1.1.0");

        UpdatePlan plan = UpdatePlan.Build(verified.PayloadRoot, verified.Manifest, install);

        Assert.Equal(1, plan.FilesToDelete);
        Assert.Contains(plan.Deletes, a => a.RelativePath == "LanDisk.Legacy.dll");
        Assert.DoesNotContain(plan.Actions, a => a.RelativePath.Contains("我的笔记"));
    }

    [Fact]
    public void Plan_without_a_previous_install_manifest_deletes_nothing_and_says_so()
    {
        using var kit = new UpdateTestKit(nameof(Plan_without_a_previous_install_manifest_deletes_nothing_and_says_so));
        string install = kit.NewDirectory("legacy-install");
        File.WriteAllText(Path.Combine(install, "LanDisk.exe"), "old-exe");
        File.WriteAllText(Path.Combine(install, "LanDisk.Old.dll"), "from-an-older-release");

        string package = kit.BuildPackage("plan-legacy", new PackageSpec().Only(("LanDisk.exe", "new-exe")));
        VerifiedUpdatePackage verified = kit.Extract(package, "1.1.0");

        UpdatePlan plan = UpdatePlan.Build(verified.PayloadRoot, verified.Manifest, install);

        Assert.Equal(0, plan.FilesToDelete);
        Assert.NotEmpty(plan.Warnings);
        Assert.Contains(plan.Warnings, w => w.Contains("install-manifest.json"));
        Assert.True(File.Exists(Path.Combine(install, "LanDisk.Old.dll")));
    }

    [Fact]
    public void Plan_flags_overwriting_a_file_the_install_manifest_never_tracked()
    {
        using var kit = new UpdateTestKit(nameof(Plan_flags_overwriting_a_file_the_install_manifest_never_tracked));
        string install = kit.CreateInstall("install", "1.2.0", new Dictionary<string, string>
        {
            ["LanDisk.exe"] = "old-exe"
        });
        File.WriteAllText(Path.Combine(install, "LanDisk.Server.dll"), "user-copy");

        string package = kit.BuildPackage("plan-untracked", new PackageSpec()
            .Only(("LanDisk.exe", "new-exe"), ("LanDisk.Server.dll", "official")));
        VerifiedUpdatePackage verified = kit.Extract(package, "1.1.0");

        UpdatePlan plan = UpdatePlan.Build(verified.PayloadRoot, verified.Manifest, install);

        UpdatePlanAction write = Assert.Single(plan.Writes, a => a.RelativePath == "LanDisk.Server.dll");
        Assert.True(write.OverwritingUntracked);
        Assert.Contains(plan.Warnings, w => w.Contains("LanDisk.Server.dll"));
    }

    // ------------------------------------------------------------------ apply

    [Fact]
    public void Apply_replaces_program_files_deletes_stale_ones_and_rewrites_the_install_manifest()
    {
        using var kit = new UpdateTestKit(nameof(Apply_replaces_program_files_deletes_stale_ones_and_rewrites_the_install_manifest));
        string dataRoot = kit.NewDirectory("data");
        string install = kit.CreateInstall("install", "1.2.0", new Dictionary<string, string>
        {
            ["LanDisk.exe"] = "old-exe",
            ["LanDisk.Legacy.dll"] = "to be removed"
        });

        string package = kit.BuildPackage("apply", new PackageSpec()
            .Only(("LanDisk.exe", "new-exe"), ("LanDisk.Core.dll", "added")));
        VerifiedUpdatePackage verified = kit.Extract(package, "1.1.0");

        var applier = new UpdateApplier(install);
        UpdatePlan plan = UpdatePlan.Build(verified.PayloadRoot, verified.Manifest, install);
        UpdateApplyResult result = applier.Apply(plan, verified.Manifest);

        Assert.True(result.Ok, string.Join("; ", result.Failures));
        Assert.Equal(2, result.FilesWritten);
        Assert.Equal(1, result.FilesDeleted);
        Assert.Equal(2, result.FilesVerified);
        Assert.Equal("new-exe", File.ReadAllText(Path.Combine(install, "LanDisk.exe")));
        Assert.Equal("added", File.ReadAllText(Path.Combine(install, "LanDisk.Core.dll")));
        Assert.False(File.Exists(Path.Combine(install, "LanDisk.Legacy.dll")));

        InstallManifest? manifest = InstallManifest.TryRead(install);
        Assert.NotNull(manifest);
        Assert.Equal("1.2.0", manifest!.Version);
        Assert.Contains("LanDisk.Core.dll", manifest.Files);

        TestBase.Record("UPDATE-03", $"应用更新：写入 {result.FilesWritten}，删除 {result.FilesDeleted}，校验 {result.FilesVerified}");
    }

    [Fact]
    public void Apply_never_touches_files_the_user_added_to_the_installation_directory()
    {
        using var kit = new UpdateTestKit(nameof(Apply_never_touches_files_the_user_added_to_the_installation_directory));
        string install = kit.CreateInstall("install", "1.2.0", new Dictionary<string, string>
        {
            ["LanDisk.exe"] = "old-exe",
            ["LanDisk.Legacy.dll"] = "to be removed"
        });

        string userFile = Path.Combine(install, "备份说明.txt");
        File.WriteAllText(userFile, "这是用户自己放的文件");
        string userDir = Path.Combine(install, "我的扩展");
        Directory.CreateDirectory(userDir);
        File.WriteAllText(Path.Combine(userDir, "plugin.dll"), "user plugin");

        string package = kit.BuildPackage("apply-user-files", new PackageSpec().Only(("LanDisk.exe", "new-exe")));
        VerifiedUpdatePackage verified = kit.Extract(package, "1.1.0");

        var applier = new UpdateApplier(install);
        UpdatePlan plan = UpdatePlan.Build(verified.PayloadRoot, verified.Manifest, install);
        UpdateApplyResult result = applier.Apply(plan, verified.Manifest);

        Assert.True(result.Ok);
        Assert.Equal("这是用户自己放的文件", File.ReadAllText(userFile));
        Assert.Equal("user plugin", File.ReadAllText(Path.Combine(userDir, "plugin.dll")));
        Assert.False(File.Exists(Path.Combine(install, "LanDisk.Legacy.dll")));
    }

    [Fact]
    public void Apply_reports_which_file_failed_instead_of_stopping_half_way_silently()
    {
        using var kit = new UpdateTestKit(nameof(Apply_reports_which_file_failed_instead_of_stopping_half_way_silently));
        string install = kit.CreateInstall("install", "1.2.0", new Dictionary<string, string>
        {
            ["LanDisk.exe"] = "old-exe"
        });

        // A directory where a file must go: the write cannot succeed, and the failure must be named.
        Directory.CreateDirectory(Path.Combine(install, "LanDisk.Core.dll"));

        string package = kit.BuildPackage("apply-fail", new PackageSpec()
            .Only(("LanDisk.exe", "new-exe"), ("LanDisk.Core.dll", "added")));
        VerifiedUpdatePackage verified = kit.Extract(package, "1.1.0");

        var applier = new UpdateApplier(install, _ => { });
        UpdatePlan plan = UpdatePlan.Build(verified.PayloadRoot, verified.Manifest, install);
        UpdateApplyResult result = applier.Apply(plan, verified.Manifest);

        Assert.False(result.Ok);
        Assert.Contains(result.Failures, f => f.Contains("LanDisk.Core.dll"));
        Assert.Equal("new-exe", File.ReadAllText(Path.Combine(install, "LanDisk.exe")));
        // A failed apply must not claim a version it did not achieve.
        Assert.Equal("1.2.0", InstallManifest.TryRead(install)?.Version);
    }

    // ------------------------------------------------------------------ backup and rollback

    [Fact]
    public void Backup_captures_program_files_and_state_metadata()
    {
        using var kit = new UpdateTestKit(nameof(Backup_captures_program_files_and_state_metadata));
        string dataRoot = kit.NewDirectory("data");
        string install = kit.CreateInstall("install", "1.2.0", new Dictionary<string, string>
        {
            ["LanDisk.exe"] = "old-exe",
            ["wwwroot/index.html"] = "<html></html>"
        });

        File.WriteAllText(Path.Combine(dataRoot, "settings.json"), """{"httpPort":8891,"httpsPort":8892}""");
        File.WriteAllText(Path.Combine(dataRoot, "shares.json"), """{"shares":[]}""");
        Directory.CreateDirectory(Path.Combine(dataRoot, "secrets"));
        File.WriteAllText(Path.Combine(dataRoot, "secrets", "admin-token.txt"), "token-value");

        var applier = new UpdateApplier(install);
        UpdateBackup backup = applier.Backup(dataRoot, "1.2.0");

        Assert.True(File.Exists(Path.Combine(backup.ProgramDir, "LanDisk.exe")));
        Assert.True(File.Exists(Path.Combine(backup.ProgramDir, "wwwroot", "index.html")));
        Assert.True(File.Exists(Path.Combine(backup.StateDir, "settings.json")));
        Assert.True(File.Exists(Path.Combine(backup.StateDir, "secrets", "admin-token.txt")));
        // Two program files plus install-manifest.json: the manifest is part of the installation
        // directory and must be restorable, otherwise a rollback could not tell which files the
        // previous version owned.
        Assert.Equal(3, backup.ProgramFileCount);
        Assert.Equal(3, backup.StateFileCount);
    }

    [Fact]
    public void Rollback_restores_program_files_removes_added_files_and_restores_state()
    {
        using var kit = new UpdateTestKit(nameof(Rollback_restores_program_files_removes_added_files_and_restores_state));
        string dataRoot = kit.NewDirectory("data");
        string install = kit.CreateInstall("install", "1.2.0", new Dictionary<string, string>
        {
            ["LanDisk.exe"] = "old-exe",
            ["LanDisk.Legacy.dll"] = "will be rolled back"
        });

        File.WriteAllText(Path.Combine(dataRoot, "settings.json"), """{"httpPort":8891,"httpsPort":8892}""");
        File.WriteAllText(Path.Combine(dataRoot, "shares.json"), """{"shares":["BF"]}""");

        var applier = new UpdateApplier(install);
        UpdateBackup backup = applier.Backup(dataRoot, "1.2.0");

        string package = kit.BuildPackage("rollback", new PackageSpec()
            .Only(("LanDisk.exe", "new-exe"), ("LanDisk.Core.dll", "added by the new version")));
        VerifiedUpdatePackage verified = kit.Extract(package, "1.1.0");

        UpdatePlan plan = UpdatePlan.Build(verified.PayloadRoot, verified.Manifest, install);
        Assert.True(applier.Apply(plan, verified.Manifest).Ok);

        // The new version also "migrates" the settings file; rollback must put the old one back.
        File.WriteAllText(Path.Combine(dataRoot, "settings.json"), """{"httpPort":18991,"httpsPort":18992}""");
        Assert.Equal("new-exe", File.ReadAllText(Path.Combine(install, "LanDisk.exe")));

        UpdateRollbackResult rollback = applier.Rollback(dataRoot, backup, verified.Manifest);

        Assert.True(rollback.Ok, string.Join("; ", rollback.Failures));
        Assert.Equal("old-exe", File.ReadAllText(Path.Combine(install, "LanDisk.exe")));
        Assert.Equal("will be rolled back", File.ReadAllText(Path.Combine(install, "LanDisk.Legacy.dll")));
        Assert.False(File.Exists(Path.Combine(install, "LanDisk.Core.dll")));
        Assert.Equal("""{"httpPort":8891,"httpsPort":8892}""", File.ReadAllText(Path.Combine(dataRoot, "settings.json")));
        Assert.Equal("1.2.0", InstallManifest.TryRead(install)?.Version);

        TestBase.Record("UPDATE-04", $"回滚：恢复 {rollback.FilesRestored}，移除新增 {rollback.FilesRemoved}，恢复状态 {rollback.StateFilesRestored}");
    }

    [Fact]
    public void An_update_does_not_change_the_configured_ports_or_the_paired_devices()
    {
        using var kit = new UpdateTestKit(nameof(An_update_does_not_change_the_configured_ports_or_the_paired_devices));
        string dataRoot = kit.NewDirectory("data");
        string install = kit.CreateInstall("install", "1.2.0", new Dictionary<string, string>
        {
            ["LanDisk.exe"] = "old-exe"
        });

        const string settings = """{"httpPort":8891,"httpsPort":8892,"lanEnabled":true,"lanBindAddress":"192.168.2.6"}""";
        const string devices = """{"devices":[{"name":"iPhone","level":"readOnly"}]}""";
        File.WriteAllText(StateFile(dataRoot), settings);
        File.WriteAllText(Path.Combine(dataRoot, "devices.json"), devices);

        string package = kit.BuildPackage("ports", new PackageSpec().Only(("LanDisk.exe", "new-exe")));
        VerifiedUpdatePackage verified = kit.Extract(package, "1.1.0");

        var applier = new UpdateApplier(install);
        UpdatePlan plan = UpdatePlan.Build(verified.PayloadRoot, verified.Manifest, install);
        Assert.True(applier.Apply(plan, verified.Manifest).Ok);

        // This is the promise that matters to the user: an upgrade keeps 8891/8892 and every
        // already paired phone working, because the update only ever touches program files.
        Assert.Equal(settings, File.ReadAllText(StateFile(dataRoot)));
        Assert.Equal(devices, File.ReadAllText(Path.Combine(dataRoot, "devices.json")));

        TestBase.Record("UPDATE-05", "升级前后 settings.json / devices.json 字节完全一致（端口与配对设备保持不变）");
    }

    [Fact]
    public void Backup_pruning_keeps_the_newest_and_never_throws()
    {
        using var kit = new UpdateTestKit(nameof(Backup_pruning_keeps_the_newest_and_never_throws));
        string backupRoot = kit.NewDirectory("backups");
        foreach (string name in new[] { "20260101-000000-1.0.0", "20260102-000000-1.1.0", "20260103-000000-1.2.0", "20260104-000000-1.3.0" })
        {
            Directory.CreateDirectory(Path.Combine(backupRoot, name));
        }

        int removed = UpdateApplier.PruneBackups(backupRoot, keep: 2);

        Assert.Equal(2, removed);
        Assert.True(Directory.Exists(Path.Combine(backupRoot, "20260104-000000-1.3.0")));
        Assert.True(Directory.Exists(Path.Combine(backupRoot, "20260103-000000-1.2.0")));
        Assert.False(Directory.Exists(Path.Combine(backupRoot, "20260101-000000-1.0.0")));
    }

    // ------------------------------------------------------------------ install manifest

    [Fact]
    public void InstallManifest_round_trips_and_tolerates_damage()
    {
        using var kit = new UpdateTestKit(nameof(InstallManifest_round_trips_and_tolerates_damage));
        string install = kit.NewDirectory("install");

        new InstallManifest { Version = "1.2.0", BuildCommit = "abc", Files = ["a", "b"] }.Write(install);

        InstallManifest? read = InstallManifest.TryRead(install);
        Assert.NotNull(read);
        Assert.Equal("1.2.0", read!.Version);
        Assert.Equal(["a", "b"], read.Files);

        File.WriteAllText(InstallManifest.PathFor(install), "{ this is not json");
        Assert.Null(InstallManifest.TryRead(install));

        File.Delete(InstallManifest.PathFor(install));
        Assert.Null(InstallManifest.TryRead(install));
    }

    // ------------------------------------------------------------------ layout

    [Fact]
    public void The_entry_point_prefers_the_shell_and_falls_back_to_the_server()
    {
        using var kit = new UpdateTestKit(nameof(The_entry_point_prefers_the_shell_and_falls_back_to_the_server));
        string install = kit.NewDirectory("install");

        Assert.Throws<FileNotFoundException>(() => UpdateLayout.ResolveEntryPoint(install));

        File.WriteAllText(Path.Combine(install, UpdateLayout.ServerFileName), "server");
        Assert.EndsWith(UpdateLayout.ServerFileName, UpdateLayout.ResolveEntryPoint(install));

        File.WriteAllText(Path.Combine(install, UpdateLayout.ShellFileName), "shell");
        Assert.EndsWith(UpdateLayout.ShellFileName, UpdateLayout.ResolveEntryPoint(install));

        Assert.Null(UpdateLayout.FindUpdater(install));
        string updaterDir = Directory.CreateDirectory(Path.Combine(install, UpdateLayout.UpdaterDirectoryName)).FullName;
        File.WriteAllText(Path.Combine(updaterDir, UpdateLayout.UpdaterFileName), "updater");
        Assert.NotNull(UpdateLayout.FindUpdater(install));
    }
}
