using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using LanDisk.Core.Configuration;
using LanDisk.Core.State;
using LanDisk.Update;

namespace LanDisk.Updater;

/// <summary>
/// The process that replaces LanDisk while LanDisk is not running.
///
/// It is launched by the server, copied out of the installation directory first, and given the PID
/// it must outlive. Sequence:
///
///   wait for exit → verify staged package again → back up program + state → apply → restart
///   → health check → commit, or roll back and restart the previous version
///
/// Every stage is written to the shared state document before it happens, so a crash at any point
/// is reported to the user on the next start instead of being silently swallowed.
/// </summary>
internal static class Program
{
    private static readonly object LogGate = new();
    private static string? _logPath;

    private static int Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (Exception ex)
        {
            return Fail(null, "启动参数错误：" + ex.Message);
        }

        _logPath = Path.Combine(options.DataRoot, "updates", "updater.log");
        Log("──────────────────────────────────────────────");
        Log($"更新程序启动，参数：{string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}");

        UpdatePaths paths = UpdatePaths.ForDataRoot(options.DataRoot);
        paths.EnsureCreated();
        var store = new UpdateStateStore(paths.StateFile);
        var applier = new UpdateApplier(options.InstallRoot, message => Log(message));

        string fromVersion = options.FromVersion;
        UpdateBackup? backup = null;
        UpdateManifest? incomingManifest = null;

        // A transaction record left by an earlier run means that run was interrupted. It is reported
        // before this one starts, so the history explains the gap instead of silently continuing.
        ReportInterruptedTransaction(paths, store);

        try
        {
            // ---------------------------------------------------------------- 1. wait for the old process
            store.Transition(UpdateStage.Applying, "等待 LanDisk 主程序退出。");
            if (!WaitForExit(options.WaitProcessId, TimeSpan.FromSeconds(120)))
            {
                string message = $"主程序（PID {options.WaitProcessId}）在 120 秒内没有退出，已放弃本次更新。";
                Log(message);
                return Fail(store, message, UpdateStage.Failed);
            }

            WaitForPortRelease(options.HttpPort, TimeSpan.FromSeconds(30));

            // ---------------------------------------------------------------- 2. verify again
            StoreVerifyResult verify = VerifyStaged(options, store);
            if (!verify.Ok || verify.Package is null)
            {
                Log("暂存包校验失败：" + verify.Message);
                return Fail(store, verify.Message ?? "暂存包校验失败。", UpdateStage.Failed);
            }

            incomingManifest = verify.Package.Manifest;
            Log($"暂存包校验通过：版本 {incomingManifest.Version}，构建提交 {incomingManifest.BuildCommit}，"
                + $"签名者 {verify.Package.KeyId}，文件 {incomingManifest.Files.Count} 个。");

            // The health baseline the server captured before handing over. Without it the post-update
            // check could only say "it answers HTTP", not "it is still the same installation".
            PreUpdateSnapshot? snapshot = UpdateSnapshotStore.ReadHealth(paths);
            if (snapshot is null)
            {
                Log("警告：没有找到更新前的健康快照，自检只能核对包声明的版本与构建提交。");
            }
            else
            {
                Log($"更新前状态：版本 {snapshot.Version}，构建提交 {snapshot.BuildCommit}，"
                    + $"管理端口 {snapshot.AdminPort}，局域网端口 {snapshot.LanPort}，"
                    + $"证书指纹 {snapshot.CertificateThumbprint ?? "(无)"}。");
            }

            // From here on the installation is being modified, so the transaction is durable.
            UpdateSnapshotStore.WriteTransaction(paths, new UpdateTransaction
            {
                Stage = "Applying",
                FromVersion = fromVersion,
                ToVersion = incomingManifest.Version,
                FromBuildCommit = snapshot?.BuildCommit,
                ToBuildCommit = incomingManifest.BuildCommit,
                PackageSha256 = options.PackageSha256,
                PayloadDigest = incomingManifest.PayloadDigest,
                StagingRoot = options.StagingRoot,
                StartedAt = options.StartedAt
            });

            // ---------------------------------------------------------------- 3. back up
            store.Transition(UpdateStage.Applying, "正在备份当前程序与状态文件。");
            backup = applier.Backup(options.DataRoot, fromVersion);

            UpdateTransaction transaction = UpdateSnapshotStore.ReadTransaction(paths) ?? new UpdateTransaction();
            transaction.BackupRoot = backup.Root;
            UpdateSnapshotStore.WriteTransaction(paths, transaction);

            // ---------------------------------------------------------------- 4. apply
            UpdatePlan plan = UpdatePlan.Build(verify.Package.PayloadRoot, incomingManifest, options.InstallRoot);
            foreach (string warning in plan.Warnings) Log("警告：" + warning);
            Log($"更新计划：写入 {plan.FilesToWrite} 个，删除 {plan.FilesToDelete} 个，跳过 {plan.Unchanged.Count} 个（{plan.BytesToWrite} 字节待写入）。");

            UpdateApplyResult apply = applier.Apply(plan, incomingManifest);
            Log(apply.Message ?? string.Empty);
            foreach (string failure in apply.Failures.Take(20)) Log("  应用失败：" + failure);

            if (!apply.Ok)
            {
                return RollbackAndRestart(options, store, applier, backup, incomingManifest, snapshot, paths,
                    $"更新写入失败：{apply.Message}", fromVersion,
                    options.PackageSha256, incomingManifest.PayloadDigest);
            }

            // ---------------------------------------------------------------- 5. restart
            store.Transition(UpdateStage.Restarting, $"正在启动 LanDisk {incomingManifest.Version}。");
            int? restartedPid = StartEntryPoint(options, out string startError);
            if (restartedPid is null)
            {
                return RollbackAndRestart(options, store, applier, backup, incomingManifest, snapshot, paths,
                    "无法启动更新后的程序：" + startError, fromVersion,
                    options.PackageSha256, incomingManifest.PayloadDigest);
            }
            Log($"已启动更新后的 LanDisk（PID {restartedPid}）。");

            // ---------------------------------------------------------------- 6. health check
            store.Transition(UpdateStage.HealthChecking,
                $"正在对 LanDisk {incomingManifest.Version} 执行健康自检（最多 {options.HealthTimeoutSeconds} 秒）。");

            UpdateHealthExpectation expectation = UpdateHealthExpectation.FromSnapshot(snapshot, incomingManifest, isRollback: false);
            UpdateHealthResult health = UpdateHealth
                .WaitAsync(options.HttpPort, TimeSpan.FromSeconds(options.HealthTimeoutSeconds), expectation)
                .GetAwaiter().GetResult();

            Log($"健康检查：{(health.Ok ? "通过" : "未通过")} — {health.Message}（尝试 {health.Attempts} 次，用时 {health.Elapsed.TotalSeconds:F1} 秒）");
            foreach (string failure in health.Failures.Take(20)) Log("  自检不通过：" + failure);

            if (!health.Ok)
            {
                StopProcess(restartedPid.Value, TimeSpan.FromSeconds(20));
                return RollbackAndRestart(options, store, applier, backup, incomingManifest, snapshot, paths,
                    $"更新后自检未通过：{health.Message}", fromVersion,
                    options.PackageSha256, incomingManifest.PayloadDigest);
            }

            // ---------------------------------------------------------------- 7. commit
            UpdateStateDocument document = store.Read();
            document.InstalledVersion = incomingManifest.Version;
            document.InstalledCommit = incomingManifest.BuildCommit;
            document.PendingVersion = null;
            document.PendingStagingRoot = null;
            document.PendingPackageSha256 = null;
            document.PendingKeyId = null;
            document.PendingReleaseNotes = null;
            document.PendingBuildCommit = null;
            document.PendingPayloadDigest = null;
            document.StagedAt = null;
            document.Stage = UpdateStage.Committed;
            document.StageMessage = $"已更新到 {incomingManifest.Version}，健康自检通过。";
            document.StageUpdatedAt = DateTimeOffset.Now;
            document.LastOutcome = "Committed";
            document.LastMessage = document.StageMessage;
            document.LastBackupDir = backup.Root;
            document.NeedsReconcile = true;
            store.Write(document);

            store.AppendHistory(new UpdateHistoryEntry
            {
                FromVersion = fromVersion,
                ToVersion = incomingManifest.Version,
                Outcome = "Committed",
                Message = document.StageMessage + $"（包 SHA256 {options.PackageSha256}）",
                PackageSha256 = options.PackageSha256,
                PayloadDigest = incomingManifest.PayloadDigest,
                KeyId = verify.Package.KeyId,
                BackupDir = backup.Root,
                StartedAt = options.StartedAt,
                FinishedAt = DateTimeOffset.Now
            });

            MarkTransactionComplete(paths, "Committed");
            Cleanup(paths, options);
            Log($"更新完成：{fromVersion} → {incomingManifest.Version}（包 SHA256 {options.PackageSha256}）。");
            return 0;
        }
        catch (Exception ex)
        {
            Log("更新过程中出现异常：" + ex);
            if (backup is not null)
            {
                return RollbackAndRestart(options, store, applier, backup, incomingManifest, null, paths,
                    "更新过程中出现异常：" + ex.Message, fromVersion, options.PackageSha256, null);
            }

            return Fail(store, "更新过程中出现异常：" + ex.Message, UpdateStage.Failed);
        }
    }

    // ------------------------------------------------------------------ stages

    private sealed class StoreVerifyResult
    {
        public bool Ok { get; init; }
        public VerifiedUpdatePackage? Package { get; init; }
        public string? Message { get; init; }
    }

    private static StoreVerifyResult VerifyStaged(Options options, UpdateStateStore store)
    {
        try
        {
            using var key = ReleasePublicKey.Create();
            VerifiedUpdatePackage package = UpdatePackage.LoadStaged(
                options.StagingRoot, key, options.FromVersion, expectedKeyId: null);
            return new StoreVerifyResult { Ok = true, Package = package };
        }
        catch (UpdatePackageException ex)
        {
            return new StoreVerifyResult { Ok = false, Message = $"[{ex.Code}] {ex.Message}" };
        }
        catch (UpdateSignatureException ex)
        {
            return new StoreVerifyResult { Ok = false, Message = $"[{ex.Code}] {ex.Message}" };
        }
    }

    /// <summary>
    /// Reports a transaction left behind by an interrupted run.
    ///
    /// The record names the backup and the package, so the next start can say what state the machine
    /// was left in instead of the operator having to reconstruct it from a log. It is kept on disk:
    /// it is evidence, and deleting it would erase the only durable trace of the interruption.
    /// </summary>
    private static void ReportInterruptedTransaction(UpdatePaths paths, UpdateStateStore store)
    {
        UpdateTransaction? previous = UpdateSnapshotStore.ReadTransaction(paths);
        if (previous is null || previous.Completed) return;

        UpdateStateDocument document = store.Read();
        string message = $"上一次更新（{previous.FromVersion} → {previous.ToVersion}）在“{previous.Stage}”阶段被中断，"
                         + $"未走到终态。备份位置：{previous.BackupRoot ?? "(未开始备份)"}，"
                         + $"更新包 SHA256：{previous.PackageSha256 ?? "(未知)"}。";

        Log("检测到未完成的事务：" + message);

        document.LastOutcome = "Interrupted";
        document.LastMessage = message;
        document.NeedsReconcile = true;
        if (document.Stage is UpdateStage.Applying or UpdateStage.Restarting or UpdateStage.HealthChecking)
        {
            document.Stage = UpdateStage.Failed;
            document.StageMessage = message;
        }
        store.Write(document);

        store.AppendHistory(new UpdateHistoryEntry
        {
            FromVersion = previous.FromVersion,
            ToVersion = previous.ToVersion,
            Outcome = "Interrupted",
            Message = message,
            PackageSha256 = previous.PackageSha256,
            PayloadDigest = previous.PayloadDigest,
            BackupDir = previous.BackupRoot,
            StartedAt = previous.StartedAt,
            FinishedAt = DateTimeOffset.Now
        });
    }

    /// <summary>
    /// Removes the transaction record once the attempt reached a terminal state.
    ///
    /// The outcome itself lives in the update state document and the history; this file only exists
    /// to make an *unfinished* attempt recognisable, so it is deleted rather than kept.
    /// </summary>
    private static void MarkTransactionComplete(UpdatePaths paths, string outcome)
    {
        Log($"事务结束（{outcome}），清除未完成标记。");
        UpdateSnapshotStore.ClearTransaction(paths);
    }

    private static int RollbackAndRestart(
        Options options,
        UpdateStateStore store,
        UpdateApplier applier,
        UpdateBackup backup,
        UpdateManifest? failedManifest,
        PreUpdateSnapshot? snapshot,
        UpdatePaths paths,
        string reason,
        string fromVersion,
        string packageSha256,
        string? payloadDigest)
    {
        Log("开始回滚：" + reason);
        store.Transition(UpdateStage.RollingBack, reason);

        // Make sure nothing half-updated is still running before we swap files back.
        StopLanDiskProcesses(options.InstallRoot, TimeSpan.FromSeconds(20));

        UpdateRollbackResult rollback = applier.Rollback(options.DataRoot, backup, failedManifest);
        Log(rollback.Message ?? string.Empty);
        foreach (string failure in rollback.Failures.Take(20)) Log("  回滚失败：" + failure);

        // The restored settings may name a different port; re-read before health checking — but the live port
        // the running host handed over still wins.
        //
        // This is the rollback twin of the defect fixed in the forward path. It computed the port from the
        // restored settings and passed it as an explicit override, which outranks everything, so a rollback put
        // the service back on the stored/default port while the health check looked at the live one. The
        // asymmetry was visible in the log as "HTTP 端口 8891，HTTPS 端口 18892": the HTTPS argument already went
        // through the fixed precedence, the HTTP one was overridden. Putting the service back where it was is
        // the same requirement on the way back as on the way forward.
        int port = UpdatePortChoice.Resolve(
            0,
            options.HttpPort,
            ReadConfiguredPort(options.DataRoot, 0));

        int? restartedPid = StartEntryPoint(options, out string startError, port);
        UpdateHealthExpectation expectation = UpdateHealthExpectation.FromSnapshot(snapshot, failedManifest, isRollback: true);
        UpdateHealthResult health = restartedPid is null
            ? UpdateHealthResult.Fail("无法启动回滚后的程序：" + startError)
            : UpdateHealth.WaitAsync(port, TimeSpan.FromSeconds(options.HealthTimeoutSeconds), expectation)
                .GetAwaiter().GetResult();

        Log($"回滚后健康检查：{(health.Ok ? "通过" : "未通过")} — {health.Message}");
        foreach (string failure in health.Failures.Take(20)) Log("  回滚后自检不通过：" + failure);

        bool recovered = rollback.Ok && health.Ok;

        UpdateStateDocument document = store.Read();
        document.InstalledVersion = recovered ? fromVersion : "unknown";
        document.InstalledCommit = recovered ? (snapshot?.BuildCommit ?? document.InstalledCommit) : document.InstalledCommit;
        document.PendingVersion = null;
        document.PendingStagingRoot = null;
        document.PendingPackageSha256 = null;
        document.PendingKeyId = null;
        document.PendingReleaseNotes = null;
        document.PendingBuildCommit = null;
        document.PendingPayloadDigest = null;
        document.StagedAt = null;

        // "Rolled back" is a claim about the machine, not about our own error handling: it is only
        // recorded when the previous version is actually serving again. Otherwise the state says
        // RECOVERY_REQUIRED and the backup is kept.
        if (recovered)
        {
            document.Stage = UpdateStage.RolledBack;
            document.StageMessage = $"更新失败，已自动回滚到 {fromVersion} 并自检通过。原因：{reason}";
        }
        else
        {
            document.Stage = UpdateStage.Failed;
            document.StageMessage = "更新失败，自动回滚也没有恢复到可用状态（RECOVERY_REQUIRED）。"
                                    + $"原因：{reason}；回滚结果：{rollback.Message}；"
                                    + $"回滚后自检：{health.Message}";
        }

        document.StageUpdatedAt = DateTimeOffset.Now;
        document.LastOutcome = recovered ? "RolledBack" : "RecoveryRequired";
        document.LastMessage = document.StageMessage;
        document.LastBackupDir = backup.Root;
        document.NeedsReconcile = true;
        store.Write(document);

        store.AppendHistory(new UpdateHistoryEntry
        {
            FromVersion = fromVersion,
            ToVersion = failedManifest?.Version ?? "unknown",
            Outcome = recovered ? "RolledBack" : "RecoveryRequired",
            Message = document.StageMessage + $"（包 SHA256 {packageSha256}）",
            PackageSha256 = string.IsNullOrWhiteSpace(packageSha256) ? null : packageSha256,
            PayloadDigest = payloadDigest,
            KeyId = failedManifest?.KeyId,
            BackupDir = backup.Root,
            StartedAt = options.StartedAt,
            FinishedAt = DateTimeOffset.Now
        });

        if (recovered)
        {
            MarkTransactionComplete(paths, "RolledBack");
            Cleanup(paths, options);
        }
        else
        {
            // Keep everything: the backup, the staging directory and the transaction record are the
            // only way a human can put this machine back together.
            Log("回滚未能恢复可用状态，已保留备份与暂存目录以便人工恢复。");
            Log($"  备份：{backup.Root}");
            Log($"  暂存：{options.StagingRoot}");
        }

        // Exit code 10 tells anyone watching that the update did not stick.
        return recovered ? 10 : 11;
    }

    private static void Cleanup(UpdatePaths paths, Options options)
    {
        try
        {
            if (Directory.Exists(options.StagingRoot)) Directory.Delete(options.StagingRoot, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        UpdateApplier.PruneBackups(paths.BackupDir, keep: 3);
    }

    // ------------------------------------------------------------------ process helpers

    private static bool WaitForExit(int pid, TimeSpan timeout)
    {
        if (pid <= 0) return true;

        try
        {
            using Process process = Process.GetProcessById(pid);
            if (process.WaitForExit((int)timeout.TotalMilliseconds)) return true;

            Log($"主程序仍然存活，尝试强制结束 PID {pid}。");
            process.Kill(entireProcessTree: true);
            return process.WaitForExit(15_000);
        }
        catch (ArgumentException)
        {
            // Already gone.
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static void StopProcess(int pid, TimeSpan timeout)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(15_000);
            }
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>
    /// Stops any LanDisk process still started from this installation directory, so a rollback does
    /// not fight a half-started new version for the port.
    /// </summary>
    private static void StopLanDiskProcesses(string installRoot, TimeSpan timeout)
    {
        string normalized = UpdateLayout.Normalize(installRoot);
        string[] names = [UpdateLayout.ShellFileName, UpdateLayout.ServerFileName];

        foreach (string name in names)
        {
            foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)))
            {
                try
                {
                    if (process.Id == Environment.ProcessId) continue;

                    string? path = process.MainModule?.FileName;
                    if (path is null) continue;
                    if (!UpdateLayout.Normalize(Path.GetDirectoryName(path)!).Equals(normalized, StringComparison.OrdinalIgnoreCase))
                        continue;

                    Log($"结束仍在运行的 {name}（PID {process.Id}）。");
                    if (!process.WaitForExit((int)timeout.TotalMilliseconds))
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(15_000);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Access to another process's module list can be denied; keep going.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }

    private static void WaitForPortRelease(int port, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return;
            }
            catch (SocketException)
            {
                Thread.Sleep(300);
            }
        }
        Log($"端口 {port} 在 {timeout.TotalSeconds:F0} 秒内没有释放，仍继续尝试启动。");
    }

    private static int? StartEntryPoint(Options options, out string error, int? portOverride = null)
    {
        error = string.Empty;
        try
        {
            string entry = UpdateLayout.ResolveEntryPoint(options.InstallRoot);
            var info = new ProcessStartInfo(entry)
            {
                WorkingDirectory = options.InstallRoot,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // Tell the shell it is coming back from an update: start into the tray, do not steal focus.
            info.ArgumentList.Add("--from-update");

            // Hand back the same data directory and ports the previous instance was using. Without
            // this the restarted process would silently fall back to the default data directory
            // (losing the configuration the user is looking at) and to the default ports.
            info.ArgumentList.Add("--data-dir");
            info.ArgumentList.Add(options.DataRoot);

            // The live ports the running host handed over win over the ones stored in settings.json.
            //
            // The order used to be the other way round, and that is a real defect rather than a style choice:
            // a host started with `--port 18891` was restarted on the stored default 8891, so the updater's own
            // post-update health check — which looks at the port it was told about — failed, and the update
            // rolled itself back. An installation running on non-default ports could therefore never complete
            // an update. Stored settings say how the product was last configured; the live value says how it is
            // actually serving, and putting the service back where it was is the whole job here.
            int port = UpdatePortChoice.Resolve(
                portOverride ?? 0,
                options.HttpPort,
                ReadConfiguredPort(options.DataRoot, 0));
            info.ArgumentList.Add("--port");
            info.ArgumentList.Add(port.ToString());

            int httpsPort = UpdatePortChoice.Resolve(
                0,
                options.HttpsPort,
                ReadConfiguredHttpsPort(options.DataRoot, 0));
            if (httpsPort > 0 && httpsPort != port)
            {
                info.ArgumentList.Add("--https-port");
                info.ArgumentList.Add(httpsPort.ToString());
            }

            Process? process = Process.Start(info);
            if (process is null)
            {
                error = "Process.Start 返回空。";
                return null;
            }

            Log($"已启动 {entry}（PID {process.Id}，数据目录 {options.DataRoot}，HTTP 端口 {port}，HTTPS 端口 {httpsPort}）。");
            return process.Id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            error = ex.Message;
            return null;
        }
    }

    private static int ReadConfiguredPort(string dataRoot, int fallback)
    {
        try
        {
            AppPaths paths = AppPaths.ForDataRoot(dataRoot);
            AppSettings settings = JsonStore.Read(paths.SettingsFile, () => new AppSettings());
            return settings.HttpPort > 0 ? settings.HttpPort : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static int ReadConfiguredHttpsPort(string dataRoot, int fallback)
    {
        try
        {
            AppPaths paths = AppPaths.ForDataRoot(dataRoot);
            AppSettings settings = JsonStore.Read(paths.SettingsFile, () => new AppSettings());
            return settings.HttpsPort > 0 ? settings.HttpsPort : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    // ------------------------------------------------------------------ logging

    private static void Log(string message)
    {
        string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}";
        lock (LogGate)
        {
            try
            {
                if (_logPath is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                    File.AppendAllText(_logPath, line + Environment.NewLine);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static int Fail(UpdateStateStore? store, string message, UpdateStage stage = UpdateStage.Failed)
    {
        Log("失败：" + message);
        try
        {
            store?.Transition(stage, message, needsReconcile: true);
            if (store is not null)
            {
                UpdateStateDocument document = store.Read();
                document.LastOutcome = "Failed";
                document.LastMessage = message;
                store.Write(document);
            }
        }
        catch (IOException) { }

        return 1;
    }

    // ------------------------------------------------------------------ arguments

    private sealed class Options
    {
        public required string DataRoot { get; init; }
        public required string InstallRoot { get; init; }
        public required string StagingRoot { get; init; }
        public int WaitProcessId { get; init; }
        public int HttpPort { get; init; } = 8891;
        public int HttpsPort { get; init; } = 8892;
        public string FromVersion { get; init; } = "0.0.0";
        public string ExpectVersion { get; init; } = string.Empty;
        public string ExpectCommit { get; init; } = string.Empty;
        public int HealthTimeoutSeconds { get; init; } = 90;

        /// <summary>SHA-256 of the exact <c>.lupd</c> being applied; carried into the history.</summary>
        public string PackageSha256 { get; init; } = string.Empty;

        public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;

        public static Options Parse(string[] args)
        {
            string? Value(string name)
            {
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
                }
                return null;
            }

            string dataRoot = Value("--data-root")
                              ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanDisk");
            string installRoot = Value("--install-root") ?? AppContext.BaseDirectory;
            string? staging = Value("--staging-root");

            if (string.IsNullOrWhiteSpace(staging))
                throw new ArgumentException("缺少 --staging-root。");

            int healthSeconds = 90;
            if (int.TryParse(Value("--health-timeout-seconds"), out int parsedHealth) && parsedHealth > 0 && parsedHealth <= 900)
                healthSeconds = parsedHealth;

            return new Options
            {
                DataRoot = Path.GetFullPath(dataRoot),
                InstallRoot = UpdateLayout.Normalize(installRoot),
                StagingRoot = Path.GetFullPath(staging),
                WaitProcessId = int.TryParse(Value("--wait-pid"), out int pid) ? pid : 0,
                HttpPort = int.TryParse(Value("--http-port"), out int port) ? port : 8891,
                HttpsPort = int.TryParse(Value("--https-port"), out int httpsPort) ? httpsPort : 0,
                FromVersion = Value("--from-version") ?? "0.0.0",
                ExpectVersion = Value("--expect-version") ?? string.Empty,
                ExpectCommit = Value("--expect-commit") ?? string.Empty,
                HealthTimeoutSeconds = healthSeconds,
                PackageSha256 = Value("--package-sha256") ?? string.Empty
            };
        }
    }
}
