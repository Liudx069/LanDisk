using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanDisk.Update;

/// <summary>
/// Directory layout the update system owns. Everything lives under the application data root so
/// that it is never inside a share, never inside the installation directory, and survives a program
/// file rollout (which is exactly what it must do: the state has to outlive the process it updates).
/// </summary>
public sealed class UpdatePaths
{
    public required string DataRoot { get; init; }

    public string Root => Path.Combine(DataRoot, "updates");
    public string IncomingDir => Path.Combine(Root, "incoming");
    public string StagingDir => Path.Combine(Root, "staging");
    public string BackupDir => Path.Combine(Root, "backup");
    public string StateFile => Path.Combine(Root, "state.json");
    public string ApplyLockFile => Path.Combine(Root, "apply.lock");

    /// <summary>Health baseline captured before an update; the updater compares against it afterwards.</summary>
    public string PreUpdateSnapshotFile => Path.Combine(Root, "pre-update-health.json");

    /// <summary>Byte-level record of every managed state file, used to make rollback exact.</summary>
    public string ManagedStateSnapshotFile => Path.Combine(Root, "pre-update-state.json");

    /// <summary>
    /// Written when an update begins and removed when it reaches a terminal state.
    ///
    /// Its presence at startup means a previous attempt was interrupted; because it records the
    /// backup location and the package hash, the next run can say precisely what was left behind
    /// instead of guessing from a partially written file.
    /// </summary>
    public string TransactionFile => Path.Combine(Root, "transaction.json");

    public static UpdatePaths ForDataRoot(string dataRoot)
        => new() { DataRoot = Path.GetFullPath(dataRoot) };

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(IncomingDir);
        Directory.CreateDirectory(StagingDir);
        Directory.CreateDirectory(BackupDir);
    }

    public string NewStagingRoot(string token) => Path.Combine(StagingDir, token);

    public string NewBackupRoot(DateTimeOffset now, string version)
        => Path.Combine(BackupDir, $"{now:yyyyMMdd-HHmmss}-{Sanitize(version)}");

    private static string Sanitize(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray();
        return new string(chars);
    }
}

/// <summary>Lifecycle of one update attempt. Persisted so the UI can explain what happened even across a crash.</summary>
public enum UpdateStage
{
    None,
    Staged,
    Applying,
    Restarting,
    HealthChecking,
    Committed,
    RollingBack,
    RolledBack,
    Failed
}

public sealed class UpdateHistoryEntry
{
    public string FromVersion { get; set; } = string.Empty;
    public string ToVersion { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public string? Message { get; set; }

    /// <summary>SHA-256 of the exact <c>.lupd</c> file this attempt used, whenever it was readable.</summary>
    public string? PackageSha256 { get; set; }

    /// <summary>SHA-256 over the signed payload file list.</summary>
    public string? PayloadDigest { get; set; }

    public string? KeyId { get; set; }
    public string? BackupDir { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
}

/// <summary>
/// The state LanDisk observed about itself immediately before an update was applied.
///
/// The updater compares the restarted instance against this, so "the update worked" is a claim about
/// the running product rather than about the updater's own bookkeeping. Only LanDisk-managed state
/// is recorded: user share contents are never read.
/// </summary>
public sealed class PreUpdateSnapshot
{
    public int SchemaVersion { get; set; } = 1;
    public string Product { get; set; } = UpdateManifest.ProductName;
    public string Version { get; set; } = string.Empty;
    public string BuildCommit { get; set; } = string.Empty;
    public string DataRoot { get; set; } = string.Empty;
    public int AdminPort { get; set; }
    public int LanPort { get; set; }
    public string? CertificateThumbprint { get; set; }
    public string? SharesStoreSha256 { get; set; }
    public string? DevicesStoreSha256 { get; set; }
    public string? SettingsSha256 { get; set; }

    /// <summary>Identity recorded on disk before the update, when the installation had one.</summary>
    public string? BuildInfoVersion { get; set; }
    public string? BuildInfoCommit { get; set; }
    public int ShareCount { get; set; }
    public int DeviceCount { get; set; }
    public DateTimeOffset TakenAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>
/// A byte-level record of every LanDisk-managed state file, taken before an update.
///
/// The applier uses it to restore the exact previous set on rollback and — just as importantly — to
/// <em>remove</em> state files the failed version created. Without that second half a rollback could
/// leave a newer settings or index file behind that the older binary then misreads.
/// </summary>
public sealed class ManagedStateSnapshot
{
    public int SchemaVersion { get; set; } = 1;
    public string DataRoot { get; set; } = string.Empty;
    public DateTimeOffset TakenAt { get; set; } = DateTimeOffset.Now;
    public List<ManagedStateEntry> Files { get; set; } = [];
}

public sealed class ManagedStateEntry
{
    /// <summary>Path relative to the data root, using forward slashes.</summary>
    public string Path { get; set; } = string.Empty;
    public long Bytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>
/// Durable record of one update attempt, written before anything is touched.
///
/// Unlike the state document — which the server also writes and which describes "what is staged" —
/// this file exists purely so an interrupted run is recognisable on the next start.
/// </summary>
public sealed class UpdateTransaction
{
    public int SchemaVersion { get; set; } = 1;
    public string Stage { get; set; } = "Applying";
    public string FromVersion { get; set; } = string.Empty;
    public string ToVersion { get; set; } = string.Empty;
    public string? FromBuildCommit { get; set; }
    public string? ToBuildCommit { get; set; }
    public string? PackageSha256 { get; set; }
    public string? PayloadDigest { get; set; }
    public string? StagingRoot { get; set; }
    public string? BackupRoot { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public bool Completed { get; set; }
    public string? Outcome { get; set; }
}

/// <summary>Reads and writes the snapshots and the transaction record in the updates directory.</summary>
public static class UpdateSnapshotStore
{
    private static readonly JsonSerializerOptions Json = new(UpdatePackage.SerializerOptions);

    public static PreUpdateSnapshot? ReadHealth(UpdatePaths paths) => Read<PreUpdateSnapshot>(paths.PreUpdateSnapshotFile);

    public static void WriteHealth(UpdatePaths paths, PreUpdateSnapshot snapshot) => Write(paths.PreUpdateSnapshotFile, snapshot);

    public static ManagedStateSnapshot? ReadManagedState(UpdatePaths paths) => Read<ManagedStateSnapshot>(paths.ManagedStateSnapshotFile);

    public static void WriteManagedState(UpdatePaths paths, ManagedStateSnapshot snapshot) => Write(paths.ManagedStateSnapshotFile, snapshot);

    public static UpdateTransaction? ReadTransaction(UpdatePaths paths) => Read<UpdateTransaction>(paths.TransactionFile);

    public static void WriteTransaction(UpdatePaths paths, UpdateTransaction transaction) => Write(paths.TransactionFile, transaction);

    public static void ClearTransaction(UpdatePaths paths)
    {
        try { if (File.Exists(paths.TransactionFile)) File.Delete(paths.TransactionFile); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>
/// The persistent update state document.
///
/// It is written by the server (when a package is staged or an apply is requested) and by the
/// updater process (as it moves through the stages), so both sides must treat it as the single
/// source of truth and write it atomically.
/// </summary>
public sealed class UpdateStateDocument
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Version currently on disk in the installation directory.</summary>
    public string InstalledVersion { get; set; } = string.Empty;
    public string InstalledCommit { get; set; } = string.Empty;

    /// <summary>Version of the package waiting to be applied, if any.</summary>
    public string? PendingVersion { get; set; }
    public string? PendingStagingRoot { get; set; }
    public string? PendingPackageSha256 { get; set; }
    public string? PendingKeyId { get; set; }
    public string? PendingReleaseNotes { get; set; }
    public string? PendingBuildCommit { get; set; }

    /// <summary>SHA-256 over the signed payload file list of the pending package.</summary>
    public string? PendingPayloadDigest { get; set; }

    public DateTimeOffset? StagedAt { get; set; }

    public UpdateStage Stage { get; set; } = UpdateStage.None;
    public string? StageMessage { get; set; }
    public DateTimeOffset? StageUpdatedAt { get; set; }

    /// <summary>Outcome of the most recent completed attempt: <c>Committed</c>, <c>RolledBack</c> or <c>Failed</c>.</summary>
    public string? LastOutcome { get; set; }
    public string? LastMessage { get; set; }
    public string? LastBackupDir { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>True when the updater wrote a state the running server has not yet reconciled.</summary>
    public bool NeedsReconcile { get; set; }

    public List<UpdateHistoryEntry> History { get; set; } = [];
}

/// <summary>
/// Atomic reader/writer for <see cref="UpdateStateDocument"/>.
///
/// All writes go through a temp file and a replace, because both the server and the updater may be
/// running (briefly) at the same time and a half-written state file would be worse than no file.
/// </summary>
public sealed class UpdateStateStore
{
    private static readonly JsonSerializerOptions Json = new(UpdatePackage.SerializerOptions)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly string _path;
    private readonly object _gate = new();

    public UpdateStateStore(string stateFilePath) => _path = Path.GetFullPath(stateFilePath);

    public string StateFilePath => _path;

    public UpdateStateDocument Read()
    {
        lock (_gate)
        {
            return ReadUnlocked();
        }
    }

    private UpdateStateDocument ReadUnlocked()
    {
        try
        {
            if (!File.Exists(_path)) return new UpdateStateDocument();
            string json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json)) return new UpdateStateDocument();
            return JsonSerializer.Deserialize<UpdateStateDocument>(json, Json) ?? new UpdateStateDocument();
        }
        catch (JsonException)
        {
            // A corrupt state file must not brick the updater; the history is nice to have, not load bearing.
            return new UpdateStateDocument();
        }
        catch (IOException)
        {
            return new UpdateStateDocument();
        }
    }

    public void Write(UpdateStateDocument document)
    {
        lock (_gate)
        {
            WriteUnlocked(document);
        }
    }

    private void WriteUnlocked(UpdateStateDocument document)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        string json = JsonSerializer.Serialize(document, Json);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, json);

        if (File.Exists(_path))
        {
            try
            {
                File.Replace(temp, _path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                return;
            }
            catch (IOException) { }
            catch (PlatformNotSupportedException) { }
        }

        File.Move(temp, _path, overwrite: true);
    }

    /// <summary>Moves the document to a new stage, records the timestamp, and optionally queues it for display.</summary>
    public UpdateStateDocument Transition(UpdateStage stage, string? message = null, bool needsReconcile = false)
    {
        lock (_gate)
        {
            UpdateStateDocument document = ReadUnlocked();
            document.Stage = stage;
            document.StageMessage = message;
            document.StageUpdatedAt = DateTimeOffset.Now;
            document.NeedsReconcile = needsReconcile;
            WriteUnlocked(document);
            return document;
        }
    }

    public void AppendHistory(UpdateHistoryEntry entry, int keep = 20)
    {
        lock (_gate)
        {
            UpdateStateDocument document = ReadUnlocked();
            document.History.Insert(0, entry);
            if (document.History.Count > keep) document.History.RemoveRange(keep, document.History.Count - keep);
            WriteUnlocked(document);
        }
    }
}
