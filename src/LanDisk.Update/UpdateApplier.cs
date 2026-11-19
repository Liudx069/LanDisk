using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace LanDisk.Update;

public sealed class UpdateBackup
{
    public required string Root { get; init; }
    public required string ProgramDir { get; init; }
    public required string StateDir { get; init; }
    public required string FromVersion { get; init; }
    public int ProgramFileCount { get; init; }
    public long ProgramBytes { get; init; }
    public int StateFileCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Byte-level record of the managed state set at backup time.</summary>
    public ManagedStateSnapshot? StateSnapshot { get; init; }
}

public sealed class UpdateApplyResult
{
    public bool Ok { get; init; }
    public int FilesWritten { get; init; }
    public int FilesDeleted { get; init; }
    public int FilesVerified { get; init; }
    public long BytesWritten { get; init; }
    public string? Message { get; init; }
    public List<string> Failures { get; init; } = [];
    public TimeSpan Duration { get; init; }
}

public sealed class UpdateRollbackResult
{
    public bool Ok { get; init; }
    public int FilesRestored { get; init; }
    public int FilesRemoved { get; init; }
    public int StateFilesRestored { get; init; }

    /// <summary>Managed state files created by the failed version and removed by this rollback.</summary>
    public int ExtraStateFilesRemoved { get; init; }

    /// <summary>Managed state files whose restored content did not match the pre-update snapshot.</summary>
    public List<string> StateMismatches { get; init; } = [];

    public string? Message { get; init; }
    public List<string> Failures { get; init; } = [];
}

/// <summary>
/// The files under the data root that describe what LanDisk knows: shares, paired devices,
/// settings, secrets and TLS material. They are captured before an update and restored on rollback.
///
/// Deliberately excluded: shared file content (never touched, it lives outside the data root), the
/// thumbnail cache, the fallback store, logs, the recycle bin payload and the update system's own
/// <c>updates/</c> directory — caches, user-visible history and the update record itself, none of
/// which should be silently rolled back.
/// </summary>
public static class UpdateBackupScope
{
    public static readonly string[] StateFiles =
    [
        "settings.json",
        "shares.json",
        "devices.json",
        "recycle-index.json",
        "transfers.json",
        "favorites.json",
        "preferences.json",
        "shell.json",
        "admin-url.txt",
        // The install identity (install id, stable <name>.local hostname, fixed ports) lives at the
        // data root, not in the program directory. Excluding it from the managed scope used to mean
        // "we happen not to touch it"; including it means an upgrade keeps it and a rollback restores
        // it byte for byte, and a failed version that drops a fresh one has it cleaned up again.
        "install.json"
    ];

    public static readonly string[] StateDirectories =
    [
        "secrets",
        "tls"
    ];

    /// <summary>
    /// Every managed state file that currently exists, as data-root-relative forward-slash paths.
    /// Used both to take the snapshot and to detect files a failed version added.
    /// </summary>
    public static List<string> Enumerate(string dataRoot)
    {
        var found = new List<string>();
        string root = Path.GetFullPath(dataRoot);

        foreach (string name in StateFiles)
        {
            if (File.Exists(Path.Combine(root, name))) found.Add(name);
        }

        foreach (string directory in StateDirectories)
        {
            string full = Path.Combine(root, directory);
            if (!Directory.Exists(full)) continue;

            foreach (string file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            {
                found.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
            }
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    /// <summary>True when a data-root-relative path is managed state and may be rolled back.</summary>
    public static bool IsManaged(string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/');
        if (StateFiles.Contains(normalized, StringComparer.OrdinalIgnoreCase)) return true;

        foreach (string directory in StateDirectories)
        {
            if (normalized.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}

/// <summary>
/// Performs the file level work of an update: snapshot, stage-in-place, verify, and roll back.
///
/// Every operation is deliberately restartable and idempotent: the updater may be interrupted at any
/// point, and running the same step again must converge rather than compound damage.
/// </summary>
public sealed class UpdateApplier
{
    private readonly string _installRoot;
    private readonly Action<string>? _log;

    public UpdateApplier(string installRoot, Action<string>? log = null)
    {
        _installRoot = Path.GetFullPath(installRoot);
        _log = log;
    }

    public string InstallRoot => _installRoot;

    // ---------------------------------------------------------------- backup

    /// <summary>
    /// Copies the whole program directory plus the LanDisk state metadata into a timestamped backup.
    /// The backup is what makes "自动回滚" a promise instead of a slogan.
    /// </summary>
    public UpdateBackup Backup(string dataRoot, string fromVersion)
    {
        string root = Path.Combine(Path.GetFullPath(dataRoot), "updates", "backup",
            $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Sanitize(fromVersion)}");

        string programDir = Path.Combine(root, "program");
        string stateDir = Path.Combine(root, "state");
        Directory.CreateDirectory(programDir);
        Directory.CreateDirectory(stateDir);

        int programFiles = 0;
        long programBytes = 0;

        foreach (string source in Directory.EnumerateFiles(_installRoot, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(_installRoot, source);
            string target = Path.Combine(programDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            programFiles++;
            programBytes += new FileInfo(source).Length;
        }

        int stateFiles = 0;
        string dataFull = Path.GetFullPath(dataRoot);
        var snapshot = new ManagedStateSnapshot { DataRoot = dataFull, TakenAt = DateTimeOffset.Now };

        // The snapshot records the exact set and hashes, not just copies of the files. Rollback uses
        // it to prove the old state came back and to remove state the failed version added.
        foreach (string relative in UpdateBackupScope.Enumerate(dataFull))
        {
            string source = Path.Combine(dataFull, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(source)) continue;

            string target = Path.Combine(stateDir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            stateFiles++;

            snapshot.Files.Add(new ManagedStateEntry
            {
                Path = relative,
                Bytes = new FileInfo(source).Length,
                Sha256 = UpdatePackage.HashFile(source)
            });
        }

        WriteSnapshotFile(Path.Combine(root, "state-snapshot.json"), snapshot);
        WriteSnapshotFile(Path.Combine(dataFull, "updates", "pre-update-state.json"), snapshot);

        var backup = new UpdateBackup
        {
            Root = root,
            ProgramDir = programDir,
            StateDir = stateDir,
            FromVersion = fromVersion,
            ProgramFileCount = programFiles,
            ProgramBytes = programBytes,
            StateFileCount = stateFiles,
            CreatedAt = DateTimeOffset.Now,
            StateSnapshot = snapshot
        };

        _log?.Invoke($"已备份程序文件 {programFiles} 个（{programBytes} 字节）与受管状态文件 {stateFiles} 个到 {root}");
        return backup;
    }

    public static void WriteSnapshotFile(string path, ManagedStateSnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, UpdatePackage.SerializerOptions));
        File.Move(temp, path, overwrite: true);
    }

    public static ManagedStateSnapshot? ReadSnapshotFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ManagedStateSnapshot>(File.ReadAllText(path), UpdatePackage.SerializerOptions);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    // ---------------------------------------------------------------- apply

    public UpdateApplyResult Apply(UpdatePlan plan, UpdateManifest manifest)
    {
        var stopwatch = Stopwatch.StartNew();
        var failures = new List<string>();
        int written = 0;
        int deleted = 0;
        long bytes = 0;

        foreach (UpdatePlanAction action in plan.Actions)
        {
            try
            {
                if (action.Kind == UpdateActionKind.Write)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(action.TargetPath)!);
                    CopyWithRetry(action.SourcePath, action.TargetPath);
                    written++;
                    bytes += action.Bytes;
                    _log?.Invoke($"写入 {action.RelativePath}");
                }
                else
                {
                    if (File.Exists(action.TargetPath))
                    {
                        File.Delete(action.TargetPath);
                        deleted++;
                        _log?.Invoke($"删除过期文件 {action.RelativePath}");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{action.RelativePath}: {ex.Message}");
                _log?.Invoke($"失败 {action.RelativePath}: {ex.Message}");
            }
        }

        // The install manifest is written last: until it lands, the previous manifest still describes
        // the directory, so an interrupted apply stays recoverable.
        if (failures.Count == 0)
        {
            string commit = manifest.BuildCommit;
            new InstallManifest
            {
                Version = manifest.Version,
                BuildCommit = commit,
                InstalledAt = DateTimeOffset.Now,
                Files = [.. manifest.Files.Select(f => f.Path)],

                // Refresh the declaration from the bytes that are now on disk, so an installation
                // never keeps claiming the previous release's build-info.json.
                BuildInfo = InstallManifestFileRecord.TryCapture(_installRoot, InstallBuildInfoFile.FileName)
            }.Write(_installRoot);
        }

        int verified = VerifyApplied(manifest, failures);
        stopwatch.Stop();

        return new UpdateApplyResult
        {
            Ok = failures.Count == 0,
            FilesWritten = written,
            FilesDeleted = deleted,
            FilesVerified = verified,
            BytesWritten = bytes,
            Duration = stopwatch.Elapsed,
            Failures = failures,
            Message = failures.Count == 0
                ? $"已写入 {written} 个文件、删除 {deleted} 个过期文件，校验 {verified} 个文件。"
                : $"有 {failures.Count} 个文件未能写入或删除。"
        };
    }

    /// <summary>Re-hashes what is now on disk against the signed manifest.</summary>
    private int VerifyApplied(UpdateManifest manifest, List<string> failures)
    {
        int verified = 0;
        foreach (UpdateFileEntry file in manifest.Files)
        {
            string target = UpdatePath.ResolveWithin(_installRoot, file.Path);
            try
            {
                if (!File.Exists(target))
                {
                    failures.Add($"{file.Path}: 更新后文件缺失");
                    continue;
                }

                if (new FileInfo(target).Length != file.Bytes)
                {
                    failures.Add($"{file.Path}: 更新后大小不符");
                    continue;
                }

                if (!UpdatePackage.HashFile(target).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"{file.Path}: 更新后哈希不符");
                    continue;
                }

                verified++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{file.Path}: 校验失败 {ex.Message}");
            }
        }
        return verified;
    }

    // ---------------------------------------------------------------- rollback

    /// <summary>
    /// Restores the program directory from a backup and removes files the failed version introduced.
    /// State metadata is restored too, because a half-migrated settings file is worse than an old one.
    /// </summary>
    public UpdateRollbackResult Rollback(string dataRoot, UpdateBackup backup, UpdateManifest? failedManifest)
    {
        var failures = new List<string>();
        int restored = 0;
        int removed = 0;
        int stateRestored = 0;

        // 1. Remove files the new version added, so the old binary cannot load a newer assembly.
        if (failedManifest is not null)
        {
            var backupFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(backup.ProgramDir))
            {
                foreach (string file in Directory.EnumerateFiles(backup.ProgramDir, "*", SearchOption.AllDirectories))
                    backupFiles.Add(Path.GetRelativePath(backup.ProgramDir, file));
            }

            foreach (UpdateFileEntry file in failedManifest.Files)
            {
                if (backupFiles.Contains(file.Path)) continue;
                if (!UpdatePath.IsSafeRelativePath(file.Path)) continue;

                try
                {
                    string target = UpdatePath.ResolveWithin(_installRoot, file.Path);
                    if (File.Exists(target))
                    {
                        File.Delete(target);
                        removed++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failures.Add($"删除新增文件失败 {file.Path}: {ex.Message}");
                }
            }
        }

        // 2. Restore the program directory exactly as it was.
        if (Directory.Exists(backup.ProgramDir))
        {
            foreach (string source in Directory.EnumerateFiles(backup.ProgramDir, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(backup.ProgramDir, source);
                string target = Path.Combine(_installRoot, relative);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    CopyWithRetry(source, target);
                    restored++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failures.Add($"恢复文件失败 {relative}: {ex.Message}");
                }
            }
        }
        else
        {
            failures.Add("备份目录不存在，无法恢复程序文件。");
        }

        // 3. Restore the managed state exactly: first drop what the failed version added, then put
        //    back the recorded set and prove the bytes match. Restoring without the first step would
        //    leave a newer settings/index file behind that the older binary would then misread.
        int extraStateRemoved = 0;
        var stateMismatches = new List<string>();
        string dataRootFull = Path.GetFullPath(dataRoot);

        ManagedStateSnapshot? snapshot = backup.StateSnapshot
            ?? ReadSnapshotFile(Path.Combine(backup.Root, "state-snapshot.json"));

        var baseline = new HashSet<string>(
            snapshot?.Files.Select(f => f.Path) ?? [],
            StringComparer.OrdinalIgnoreCase);

        if (snapshot is null)
        {
            failures.Add("缺少更新前的受管状态快照，无法保证状态被精确恢复。");
        }
        else
        {
            foreach (string relative in UpdateBackupScope.Enumerate(dataRootFull))
            {
                if (baseline.Contains(relative)) continue;

                try
                {
                    string target = Path.Combine(dataRootFull, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(target))
                    {
                        File.Delete(target);
                        extraStateRemoved++;
                        _log?.Invoke($"回滚：移除新版本新增的受管状态文件 {relative}");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failures.Add($"移除新增状态文件失败 {relative}: {ex.Message}");
                }
            }
        }

        if (Directory.Exists(backup.StateDir))
        {
            foreach (string source in Directory.EnumerateFiles(backup.StateDir, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(backup.StateDir, source);
                string target = Path.Combine(dataRootFull, relative);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    CopyWithRetry(source, target);
                    stateRestored++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failures.Add($"恢复状态文件失败 {relative}: {ex.Message}");
                }
            }
        }

        // 4. Prove the restored state is byte-identical to the pre-update snapshot. A rollback that
        //    only claims to have worked is not good enough to report as success.
        if (snapshot is not null)
        {
            foreach (ManagedStateEntry entry in snapshot.Files)
            {
                string target = Path.Combine(dataRootFull, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    if (!File.Exists(target))
                    {
                        stateMismatches.Add($"{entry.Path}: 回滚后缺失");
                        continue;
                    }

                    if (new FileInfo(target).Length != entry.Bytes
                        || !UpdatePackage.HashFile(target).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        stateMismatches.Add($"{entry.Path}: 内容与更新前不一致");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    stateMismatches.Add($"{entry.Path}: 校验失败 {ex.Message}");
                }
            }

            // Anything still present that was not in the baseline is also a mismatch.
            foreach (string relative in UpdateBackupScope.Enumerate(dataRootFull))
            {
                if (!baseline.Contains(relative)) stateMismatches.Add($"{relative}: 回滚后仍然存在");
            }
        }

        if (stateMismatches.Count > 0)
            failures.AddRange(stateMismatches.Select(m => "状态恢复不一致 " + m));

        return new UpdateRollbackResult
        {
            Ok = failures.Count == 0,
            FilesRestored = restored,
            FilesRemoved = removed,
            StateFilesRestored = stateRestored,
            ExtraStateFilesRemoved = extraStateRemoved,
            StateMismatches = stateMismatches,
            Failures = failures,
            Message = failures.Count == 0
                ? $"已恢复 {restored} 个程序文件、移除 {removed} 个新增程序文件、恢复 {stateRestored} 个受管状态文件"
                  + $"（其中移除新版本新增的状态文件 {extraStateRemoved} 个），并与更新前快照逐字节一致。"
                : $"回滚过程中有 {failures.Count} 个操作失败。"
        };
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Replaces a file in place, retrying briefly so that a still-exiting process or an antivirus
    /// scanner holding a transient handle does not turn into a failed update.
    /// </summary>
    private static void CopyWithRetry(string source, string target, int attempts = 12)
    {
        IOException? last = null;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                string staging = target + ".landisk-new";
                File.Copy(source, staging, overwrite: true);
                File.Move(staging, target, overwrite: true);
                return;
            }
            catch (IOException ex)
            {
                last = ex;
                Thread.Sleep(250);
            }
            catch (UnauthorizedAccessException ex)
            {
                last = new IOException(ex.Message, ex);
                Thread.Sleep(250);
            }
        }
        throw last ?? new IOException($"无法写入 {target}");
    }

    private static string Sanitize(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray();
        return new string(chars);
    }

    /// <summary>Drops all but the newest <paramref name="keep"/> backups so disk use stays bounded.</summary>
    public static int PruneBackups(string backupRoot, int keep = 3)
    {
        try
        {
            if (!Directory.Exists(backupRoot)) return 0;
            var directories = new DirectoryInfo(backupRoot).GetDirectories()
                .OrderByDescending(d => d.Name, StringComparer.Ordinal)
                .ToList();

            int removed = 0;
            foreach (DirectoryInfo stale in directories.Skip(keep))
            {
                try { stale.Delete(recursive: true); removed++; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return removed;
        }
        catch (IOException) { return 0; }
    }

    /// <summary>Reads back the signed manifest of a staged package for reporting, without extracting.</summary>
    public static UpdateManifest? TryReadStagedManifest(string stagingRoot)
    {
        try
        {
            string path = Path.Combine(stagingRoot, UpdatePackage.StagedManifestName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(path), UpdatePackage.SerializerOptions);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    public static string Sha256Of(string value)
        => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}
