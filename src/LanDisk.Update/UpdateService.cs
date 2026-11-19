using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using LanDisk.Core.Configuration;
using LanDisk.Core.Networking;

namespace LanDisk.Update;

public sealed class StagedUpdate
{
    public bool Ok { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
    public string? Version { get; init; }
    public string? StagingRoot { get; init; }
    public string? PackageSha256 { get; init; }
    public string? PayloadDigest { get; init; }
    public string? KeyId { get; init; }
    public string? ReleaseNotes { get; init; }
    public string? BuildCommit { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public int FileCount { get; init; }
    public long TotalBytes { get; init; }
    public double VerifySeconds { get; init; }
}

public sealed class ApplyRequest
{
    public bool Accepted { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
    public int? UpdaterProcessId { get; init; }
    public string? StagingRoot { get; init; }
    public string? TargetVersion { get; init; }
    public string? PackageSha256 { get; init; }
    public string? ExpectedBuildCommit { get; init; }
}

/// <summary>
/// Server side orchestration of the update lifecycle.
///
/// The split of responsibility is deliberate: this class stages and verifies (work that belongs to
/// the process the user is talking to), while the actual file swap, restart, health check and
/// rollback are done by a separate <c>LanDisk.Updater.exe</c> that outlives this process. A program
/// cannot reliably replace itself, so the code that replaces it must not be the code being replaced.
/// </summary>
public sealed class UpdateService : IDisposable
{
    /// <summary>Refuse uploads beyond this. A signed LanDisk package is around 100 MB.</summary>
    public const long MaxPackageBytes = 1L * 1024 * 1024 * 1024;

    private readonly UpdatePaths _paths;
    private readonly Action<string>? _log;

    public UpdateService(
        string dataRoot,
        string installRoot,
        string currentVersion,
        string? currentCommit = null,
        Action<string>? log = null)
    {
        _paths = UpdatePaths.ForDataRoot(dataRoot);
        _paths.EnsureCreated();
        Store = new UpdateStateStore(_paths.StateFile);
        _log = log;

        DataRoot = Path.GetFullPath(dataRoot);
        InstallRoot = UpdateLayout.Normalize(installRoot);
        CurrentVersion = currentVersion;
        CurrentCommit = currentCommit ?? string.Empty;
    }

    public string DataRoot { get; }
    public string InstallRoot { get; }
    public string CurrentVersion { get; }
    public string CurrentCommit { get; }
    public UpdatePaths Paths => _paths;
    public UpdateStateStore Store { get; }

    /// <summary>
    /// The key a package must be signed with.
    ///
    /// The shipping default is the key compiled into the product (<see cref="ReleasePublicKey"/>).
    /// It is replaceable so the automated tests can sign with a throw-away key they generate
    /// themselves, which keeps the real release private key out of the test environment entirely.
    /// </summary>
    public Func<ECDsa> PublicKeyProvider { get; set; } = ReleasePublicKey.Create;

    private ECDsa? _verificationKey;
    private readonly object _keyGate = new();

    /// <summary>
    /// The verification key, created once and owned by this service.
    ///
    /// It is deliberately cached and <em>not</em> disposed per call: the provider may hand back a key
    /// the caller still owns, and disposing someone else's key is a bug that only surfaces later as
    /// an inexplicable ObjectDisposedException.
    /// </summary>
    private ECDsa VerificationKey
    {
        get
        {
            lock (_keyGate)
            {
                return _verificationKey ??= PublicKeyProvider();
            }
        }
    }

    public void Dispose()
    {
        lock (_keyGate)
        {
            _verificationKey?.Dispose();
            _verificationKey = null;
        }
    }

    public string? UpdaterPath => UpdateLayout.FindUpdater(InstallRoot);

    public string? PendingStagingRoot
    {
        get
        {
            UpdateStateDocument document = Store.Read();
            if (string.IsNullOrWhiteSpace(document.PendingStagingRoot)) return null;
            return Directory.Exists(document.PendingStagingRoot) ? document.PendingStagingRoot : null;
        }
    }

    /// <summary>
    /// Called at startup: adopts the version now on disk and clears the flags the updater left for us.
    /// This is how the admin page can say "上次更新已完成" or "上次更新失败，已自动回滚".
    /// </summary>
    public UpdateStateDocument Reconcile()
    {
        UpdateStateDocument document = Store.Read();

        InstallManifest? manifest = InstallManifest.TryRead(InstallRoot);
        string installed = manifest?.Version ?? CurrentVersion;
        document.InstalledVersion = installed;
        document.InstalledCommit = manifest?.BuildCommit ?? CurrentCommit;

        bool inFlight = document.Stage is UpdateStage.Applying or UpdateStage.Restarting
            or UpdateStage.HealthChecking or UpdateStage.RollingBack;

        // The restarted instance comes up *while* the updater is still health checking and about to
        // write the final outcome. Reconciling now would race with it and could announce a failure
        // that is actually a success in progress, so the updater keeps ownership until it is gone.
        if (inFlight && IsUpdaterRunning())
        {
            _log?.Invoke("检测到更新程序仍在运行，暂不修改更新状态。");
            return document;
        }

        if (inFlight)
        {
            // The updater is gone and we are running: either it died or the machine restarted
            // mid-update. Report it honestly instead of pretending success.
            document.LastOutcome = "Interrupted";
            document.LastMessage = $"上一次更新停在“{StageText(document.Stage)}”阶段，未能完成。当前运行版本 {installed}。";
            document.Stage = UpdateStage.Failed;
            document.StageMessage = document.LastMessage;
            document.NeedsReconcile = false;
            document.PendingStagingRoot = null;
            document.PendingVersion = null;
        }
        else if (document.Stage is UpdateStage.Committed or UpdateStage.RolledBack or UpdateStage.Failed)
        {
            document.NeedsReconcile = false;
        }

        // A pending package from a previous session that no longer exists on disk is stale.
        if (!string.IsNullOrWhiteSpace(document.PendingStagingRoot) && !Directory.Exists(document.PendingStagingRoot))
        {
            document.PendingStagingRoot = null;
            document.PendingVersion = null;
            if (document.Stage == UpdateStage.Staged)
            {
                document.Stage = UpdateStage.None;
                document.StageMessage = "暂存的更新包已不存在，已清除待安装状态。";
            }
        }

        Store.Write(document);
        return document;
    }

    /// <summary>
    /// True when the update helper belonging to this data root is alive.
    ///
    /// The match is on the runner directory the server itself created, so an unrelated process that
    /// merely shares the executable name cannot hold the update state hostage.
    /// </summary>
    public bool IsUpdaterRunning()
    {
        string runnerRoot = Path.Combine(_paths.Root, "runner");
        string processName = Path.GetFileNameWithoutExtension(UpdateLayout.UpdaterFileName);

        try
        {
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                try
                {
                    string? path = process.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path)
                        && path.StartsWith(runnerRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Another user's process, or one that exited between the two calls.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Process enumeration is best effort; treat an error as "not running".
        }

        return false;
    }

    /// <summary>Read-only look at a package: shows the user what they are about to install.</summary>
    public UpdateVerificationReport Inspect(string packagePath)
    {
        var report = new UpdateVerificationReport { CurrentVersion = CurrentVersion, Ok = false };

        try
        {
            (UpdateManifest manifest, UpdateSignature signature) = UpdatePackage.ReadManifest(packagePath);
            report.Version = manifest.Version;
            report.BuildCommit = manifest.BuildCommit;
            report.FileCount = manifest.Files.Count;
            report.TotalBytes = manifest.Files.Sum(f => f.Bytes);
            report.PublishedAt = manifest.PublishedAt;
            report.SignerKeyId = manifest.KeyId;
            report.PayloadDigest = manifest.PayloadDigest;

            ECDsa key = VerificationKey;
            UpdatePackage.EnsureSchemaSupported(manifest);
            UpdateSigning.VerifyOrThrow(manifest, signature, key);
            UpdatePackage.ValidateManifestShape(manifest, CurrentVersion);

            // The notes are only "the release notes" if they are the bytes the signature covers.
            using (FileStream notesStream = File.OpenRead(packagePath))
            {
                report.ReleaseNotes = UpdatePackage.ReadReleaseNotes(notesStream, manifest);
            }

            try
            {
                using FileStream stream = File.OpenRead(packagePath);
                report.PackageSha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
            }
            catch (IOException) { }

            report.Ok = true;
            report.Message = "更新包签名有效，更新说明与文件清单均与签名一致。";
        }
        catch (UpdatePackageException ex)
        {
            report.Code = ex.Code;
            report.Message = ex.Message;
        }
        catch (UpdateSignatureException ex)
        {
            report.Code = ex.Code;
            report.Message = ex.Message;
        }
        catch (FileNotFoundException)
        {
            report.Code = "PACKAGE_MISSING";
            report.Message = "更新包文件不存在。";
        }

        return report;
    }

    /// <summary>
    /// Verifies an uploaded package end to end and stages the verified payload.
    ///
    /// The read from <paramref name="package"/> is asynchronous on purpose: this stream is normally
    /// the HTTP request body, and Kestrel refuses synchronous reads on it. Reading asynchronously
    /// keeps the upload path non-blocking without disabling that protection server-wide.
    /// </summary>
    public async Task<StagedUpdate> StageAsync(
        Stream package,
        string? originalFileName = null,
        CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();

        string token = $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("n")[..8]}";
        string incomingPath = Path.Combine(_paths.IncomingDir, token + UpdatePackage.FileExtension);
        string? stagingRoot = null;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            long copied = 0;
            bool tooLarge = false;

            using (var target = new FileStream(incomingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[128 * 1024];
                int read;
                while ((read = await package.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    copied += read;
                    if (copied > MaxPackageBytes)
                    {
                        tooLarge = true;
                        break;
                    }
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            if (tooLarge)
            {
                return Fail("PACKAGE_TOO_LARGE", $"更新包超过上限 {MaxPackageBytes / (1024 * 1024)} MB。", null, "超过大小上限，未完整接收");
            }

            if (copied == 0)
            {
                return Fail("PACKAGE_EMPTY", "上传的更新包为空。", null, "上传内容为空");
            }

            stagingRoot = _paths.NewStagingRoot(token);
            ECDsa key = VerificationKey;
            VerifiedUpdatePackage verified = UpdatePackage.Extract(incomingPath, stagingRoot, CurrentVersion, key);
            stopwatch.Stop();

            var document = Store.Read();
            document.PendingVersion = verified.Manifest.Version;
            document.PendingStagingRoot = verified.StagingRoot;
            document.PendingPackageSha256 = verified.PackageSha256;
            document.PendingKeyId = verified.KeyId;
            document.PendingReleaseNotes = verified.ReleaseNotes;
            document.PendingBuildCommit = verified.Manifest.BuildCommit;
            document.PendingPayloadDigest = verified.Manifest.PayloadDigest;
            document.StagedAt = DateTimeOffset.Now;
            document.Stage = UpdateStage.Staged;
            document.StageMessage = $"更新包 {verified.Manifest.Version} 已校验并暂存。";
            document.StageUpdatedAt = DateTimeOffset.Now;
            document.NeedsReconcile = false;
            Store.Write(document);

            RecordAttempt(new UpdateHistoryEntry
            {
                FromVersion = CurrentVersion,
                ToVersion = verified.Manifest.Version,
                Outcome = "Staged",
                Message = $"更新包已通过校验并暂存（{verified.Manifest.Files.Count} 个文件）。",
                PackageSha256 = verified.PackageSha256,
                PayloadDigest = verified.Manifest.PayloadDigest,
                KeyId = verified.KeyId,
                StartedAt = DateTimeOffset.Now,
                FinishedAt = DateTimeOffset.Now
            });

            _log?.Invoke($"更新包 {verified.Manifest.Version} 校验通过（签名者 {verified.KeyId}，包 SHA256 {verified.PackageSha256}），已暂存到 {verified.StagingRoot}");

            return new StagedUpdate
            {
                Ok = true,
                Version = verified.Manifest.Version,
                StagingRoot = verified.StagingRoot,
                PackageSha256 = verified.PackageSha256,
                PayloadDigest = verified.Manifest.PayloadDigest,
                KeyId = verified.KeyId,
                ReleaseNotes = verified.ReleaseNotes,
                BuildCommit = verified.Manifest.BuildCommit,
                PublishedAt = verified.Manifest.PublishedAt,
                FileCount = verified.Manifest.Files.Count,
                TotalBytes = verified.TotalBytes,
                VerifySeconds = stopwatch.Elapsed.TotalSeconds,
                Message = "更新包签名、更新说明与全部文件哈希校验通过，已暂存待安装。"
            };
        }
        catch (UpdatePackageException ex)
        {
            if (stagingRoot is not null) SafeDeleteDirectory(stagingRoot);
            return Fail(ex.Code, ex.Message, incomingPath);
        }
        catch (UpdateSignatureException ex)
        {
            if (stagingRoot is not null) SafeDeleteDirectory(stagingRoot);
            return Fail(ex.Code, ex.Message, incomingPath);
        }
        catch (InvalidDataException)
        {
            if (stagingRoot is not null) SafeDeleteDirectory(stagingRoot);
            return Fail("NOT_A_ZIP", "上传的文件不是合法的 .lupd 更新包（ZIP 结构损坏）。", incomingPath);
        }
        catch (IOException ex)
        {
            if (stagingRoot is not null) SafeDeleteDirectory(stagingRoot);
            return Fail("IO_ERROR", "写入暂存目录失败：" + ex.Message, incomingPath);
        }
        finally
        {
            SafeDelete(incomingPath);
        }
    }

    /// <summary>
    /// Records what happened to a package, including the ones that were refused.
    ///
    /// A rejected package is exactly the case an operator later needs to explain, so the history
    /// keeps its hash whenever the bytes were readable. When they were not, the record says why
    /// rather than leaving an unexplained null.
    /// </summary>
    private StagedUpdate Fail(string code, string message, string? packagePath = null, string? noBytesReason = null)
    {
        string? hash = null;
        string notes = string.Empty;
        noBytesReason ??= "内容为空或未完整接收";

        if (packagePath is not null && File.Exists(packagePath))
        {
            try
            {
                using FileStream stream = File.OpenRead(packagePath);
                hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                notes = $"（无法读取更新包以计算 SHA256：{ex.GetType().Name}）";
            }
        }
        else
        {
            notes = "（没有可计算 SHA256 的完整更新包：" + noBytesReason + "）";
        }

        RecordAttempt(new UpdateHistoryEntry
        {
            FromVersion = CurrentVersion,
            ToVersion = "—",
            Outcome = "Rejected",
            Message = $"[{code}] {message}{notes}",
            PackageSha256 = hash,
            StartedAt = DateTimeOffset.Now,
            FinishedAt = DateTimeOffset.Now
        });

        return new StagedUpdate { Ok = false, Code = code, Message = message };
    }

    /// <summary>Computes exactly which files an install would write and delete.</summary>
    public (UpdatePlan? Plan, string? Error) DescribeStagedPlan()
    {
        string? stagingRoot = PendingStagingRoot;
        if (stagingRoot is null) return (null, "没有待安装的更新包。");

        try
        {
            ECDsa key = VerificationKey;
            VerifiedUpdatePackage verified = UpdatePackage.LoadStaged(stagingRoot, key);
            return (UpdatePlan.Build(verified.PayloadRoot, verified.Manifest, InstallRoot), null);
        }
        catch (UpdatePackageException ex)
        {
            return (null, ex.Message);
        }
        catch (UpdateSignatureException ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Verifies the staged package once more, copies the updater out of the installation directory so
    /// it can survive being replaced, and starts it detached. The caller must then stop the server.
    /// </summary>
    public ApplyRequest RequestApply(int httpPort, TimeSpan? healthTimeout = null, int? httpsPort = null)
    {
        string? stagingRoot = PendingStagingRoot;
        if (stagingRoot is null)
            return new ApplyRequest { Accepted = false, Code = "NOTHING_STAGED", Message = "没有待安装的更新包。" };

        string? updater = UpdaterPath;
        if (updater is null)
        {
            return RecordApplyRefusal("REFUSED", "UPDATER_MISSING",
                $"安装目录缺少 {UpdateLayout.UpdaterDirectoryName}\\{UpdateLayout.UpdaterFileName}，"
                + "无法安全地自动更新。请使用完整安装包手工升级。",
                Store.Read().PendingPackageSha256, Store.Read().PendingPayloadDigest);
        }

        UpdateStateDocument document = Store.Read();
        if (document.Stage is UpdateStage.Applying or UpdateStage.Restarting or UpdateStage.HealthChecking)
        {
            return RecordApplyRefusal("REFUSED", "ALREADY_APPLYING", "更新已经在进行中。",
                document.PendingPackageSha256, document.PendingPayloadDigest);
        }

        string targetVersion = document.PendingVersion ?? "unknown";
        string packageSha256 = document.PendingPackageSha256 ?? string.Empty;
        string payloadDigest = document.PendingPayloadDigest ?? string.Empty;

        // Re-verify immediately before handing over: the staging directory is on disk and could have
        // been modified between staging and apply.
        try
        {
            ECDsa key = VerificationKey;
            VerifiedUpdatePackage verified = UpdatePackage.LoadStaged(stagingRoot, key, CurrentVersion, document.PendingKeyId);
            targetVersion = verified.Manifest.Version;
            payloadDigest = verified.Manifest.PayloadDigest;
        }
        catch (UpdatePackageException ex)
        {
            return RecordApplyRefusal("REFUSED", ex.Code, ex.Message, packageSha256, payloadDigest);
        }
        catch (UpdateSignatureException ex)
        {
            return RecordApplyRefusal("REFUSED", ex.Code, ex.Message, packageSha256, payloadDigest);
        }

        // Capture the pre-update health baseline the updater must match afterwards. It is written
        // next to the staging area so the updater can read it without trusting our in-memory state.
        PreUpdateSnapshot snapshot;
        try
        {
            snapshot = CaptureSnapshot(httpPort, httpsPort ?? 0);
            WriteSnapshot(snapshot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RecordApplyRefusal("REFUSED", "SNAPSHOT_FAILED",
                "无法写入更新前的状态快照，已取消安装：" + ex.Message, packageSha256, payloadDigest);
        }

        string runnerDir = Path.Combine(_paths.Root, "runner", Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(runnerDir);
        string runnerExe = Path.Combine(runnerDir, UpdateLayout.UpdaterFileName);
        File.Copy(updater, runnerExe, overwrite: true);

        var info = new ProcessStartInfo(runnerExe)
        {
            WorkingDirectory = runnerDir,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("--data-root");
        info.ArgumentList.Add(DataRoot);
        info.ArgumentList.Add("--install-root");
        info.ArgumentList.Add(InstallRoot);
        info.ArgumentList.Add("--staging-root");
        info.ArgumentList.Add(stagingRoot);
        info.ArgumentList.Add("--wait-pid");
        info.ArgumentList.Add(Environment.ProcessId.ToString());
        info.ArgumentList.Add("--http-port");
        info.ArgumentList.Add(httpPort.ToString());
        info.ArgumentList.Add("--https-port");
        info.ArgumentList.Add((httpsPort ?? 0).ToString());
        info.ArgumentList.Add("--from-version");
        info.ArgumentList.Add(CurrentVersion);
        info.ArgumentList.Add("--expect-version");
        info.ArgumentList.Add(targetVersion);
        info.ArgumentList.Add("--expect-commit");
        info.ArgumentList.Add(document.PendingBuildCommit ?? string.Empty);
        info.ArgumentList.Add("--package-sha256");
        info.ArgumentList.Add(packageSha256);
        info.ArgumentList.Add("--health-timeout-seconds");
        info.ArgumentList.Add(((int)(healthTimeout ?? TimeSpan.FromSeconds(90)).TotalSeconds).ToString());

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return RecordApplyRefusal("FAILED", "UPDATER_LAUNCH_FAILED", "无法启动更新程序：" + ex.Message, packageSha256, payloadDigest);
        }

        if (process is null)
            return RecordApplyRefusal("FAILED", "UPDATER_LAUNCH_FAILED", "无法启动更新程序。", packageSha256, payloadDigest);

        document.Stage = UpdateStage.Applying;
        document.StageMessage = $"更新程序已启动（PID {process.Id}），等待主程序退出后应用 {targetVersion}。";
        document.StageUpdatedAt = DateTimeOffset.Now;
        document.NeedsReconcile = true;
        document.LastAttemptAt = DateTimeOffset.Now;
        Store.Write(document);

        RecordAttempt(new UpdateHistoryEntry
        {
            FromVersion = CurrentVersion,
            ToVersion = targetVersion,
            Outcome = "Applying",
            Message = $"更新程序已启动（PID {process.Id}），即将停止服务并应用。",
            PackageSha256 = packageSha256,
            PayloadDigest = payloadDigest,
            KeyId = document.PendingKeyId,
            BackupDir = null,
            StartedAt = DateTimeOffset.Now,
            FinishedAt = DateTimeOffset.Now
        });

        _log?.Invoke($"已启动更新程序 {runnerExe}（PID {process.Id}），目标版本 {targetVersion}，包 SHA256 {packageSha256}");

        return new ApplyRequest
        {
            Accepted = true,
            UpdaterProcessId = process.Id,
            StagingRoot = stagingRoot,
            TargetVersion = targetVersion,
            PackageSha256 = packageSha256,
            Message = "更新程序已启动，LanDisk 即将重启并自检。"
        };
    }

    /// <summary>Records why an install request was refused, keeping the package hash in the history.</summary>
    private ApplyRequest RecordApplyRefusal(
        string outcome,
        string code,
        string message,
        string? packageSha256,
        string? payloadDigest)
    {
        RecordAttempt(new UpdateHistoryEntry
        {
            FromVersion = CurrentVersion,
            ToVersion = "—",
            Outcome = outcome,
            Message = $"[{code}] {message}",
            PackageSha256 = string.IsNullOrWhiteSpace(packageSha256) ? null : packageSha256,
            PayloadDigest = string.IsNullOrWhiteSpace(payloadDigest) ? null : payloadDigest,
            StartedAt = DateTimeOffset.Now,
            FinishedAt = DateTimeOffset.Now
        });

        return new ApplyRequest { Accepted = false, Code = code, Message = message };
    }

    /// <summary>Removes a staged package the user decided not to install.</summary>
    public bool DiscardPending()
    {
        UpdateStateDocument document = Store.Read();
        string? stagingRoot = document.PendingStagingRoot;
        string? packageSha256 = document.PendingPackageSha256;
        string? payloadDigest = document.PendingPayloadDigest;
        string? pendingVersion = document.PendingVersion;

        document.PendingVersion = null;
        document.PendingStagingRoot = null;
        document.PendingPackageSha256 = null;
        document.PendingKeyId = null;
        document.PendingReleaseNotes = null;
        document.PendingBuildCommit = null;
        document.PendingPayloadDigest = null;
        document.StagedAt = null;
        if (document.Stage == UpdateStage.Staged)
        {
            document.Stage = UpdateStage.None;
            document.StageMessage = "已放弃待安装的更新包。";
            document.StageUpdatedAt = DateTimeOffset.Now;
        }
        Store.Write(document);

        if (!string.IsNullOrWhiteSpace(stagingRoot)) SafeDeleteDirectory(stagingRoot);

        if (pendingVersion is not null)
        {
            RecordAttempt(new UpdateHistoryEntry
            {
                FromVersion = CurrentVersion,
                ToVersion = pendingVersion,
                Outcome = "Discarded",
                Message = "用户放弃了待安装的更新包，未做任何改动。",
                PackageSha256 = packageSha256,
                PayloadDigest = payloadDigest,
                StartedAt = DateTimeOffset.Now,
                FinishedAt = DateTimeOffset.Now
            });
        }

        return true;
    }

    public static string StageText(UpdateStage stage) => stage switch
    {
        UpdateStage.None => "空闲",
        UpdateStage.Staged => "已暂存",
        UpdateStage.Applying => "应用中",
        UpdateStage.Restarting => "重启中",
        UpdateStage.HealthChecking => "自检中",
        UpdateStage.Committed => "完成",
        UpdateStage.RollingBack => "回滚中",
        UpdateStage.RolledBack => "已回滚",
        UpdateStage.Failed => "失败",
        _ => stage.ToString()
    };

    /// <summary>Appends one attempt to the shared history, keeping a bounded number of entries.</summary>
    public void RecordAttempt(UpdateHistoryEntry entry, int keep = 40)
        => Store.AppendHistory(entry, keep);

    // ------------------------------------------------------------------ pre-update snapshots

    /// <summary>
    /// Captures the health facts the restarted instance must reproduce.
    ///
    /// This is deliberately the server's own view of itself rather than anything the updater infers:
    /// the updater's job is to rebuild exactly this, and "the update worked" then means "the running
    /// product reports the same product, version, build, ports, certificate and stores as before".
    /// </summary>
    public PreUpdateSnapshot CaptureSnapshot(int adminPort, int lanPort)
    {
        AppPaths paths = AppPaths.ForDataRoot(DataRoot);
        AppSettings settings = JsonStore.Read(paths.SettingsFile, () => new AppSettings());

        string? thumbprint = null;
        try
        {
            using System.Security.Cryptography.X509Certificates.X509Certificate2? certificate =
                new CertificateService(paths).TryLoadCaCertificate();
            thumbprint = certificate?.Thumbprint;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException)
        {
            thumbprint = null;
        }

        InstallBuildInfo? installInfo = InstallBuildInfoFile.TryRead(InstallRoot);

        return new PreUpdateSnapshot
        {
            Product = UpdateManifest.ProductName,
            BuildInfoVersion = installInfo?.Version,
            BuildInfoCommit = installInfo?.Commit,
            Version = CurrentVersion,
            BuildCommit = CurrentCommit,
            DataRoot = DataRoot,
            AdminPort = adminPort,
            LanPort = lanPort,
            CertificateThumbprint = thumbprint,
            SharesStoreSha256 = HashFileOrNull(paths.SharesFile),
            DevicesStoreSha256 = HashFileOrNull(paths.DevicesFile),
            SettingsSha256 = HashFileOrNull(paths.SettingsFile),
            ShareCount = CountEntries(paths.SharesFile),
            DeviceCount = CountEntries(paths.DevicesFile),
            TakenAt = DateTimeOffset.Now
        };
    }

    public void WriteSnapshot(PreUpdateSnapshot snapshot)
    {
        _paths.EnsureCreated();
        string path = _paths.PreUpdateSnapshotFile;
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, SnapshotJson));
        File.Move(temp, path, overwrite: true);
    }

    public PreUpdateSnapshot? ReadSnapshot()
    {
        try
        {
            string path = _paths.PreUpdateSnapshotFile;
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<PreUpdateSnapshot>(File.ReadAllText(path), SnapshotJson);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    /// <summary>Number of records in a JSON array store; used only for reporting, never for policy.</summary>
    private static int CountEntries(string path)
    {
        try
        {
            if (!File.Exists(path)) return 0;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.GetArrayLength() : 0;
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return 0; }
    }

    private static string? HashFileOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? UpdatePackage.HashFile(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions SnapshotJson = new(UpdatePackage.SerializerOptions);

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void SafeDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
