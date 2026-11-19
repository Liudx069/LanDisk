namespace LanDisk.Core.Model;

/// <summary>Fine grained capabilities a share may grant. Devices can only ever narrow these.</summary>
[Flags]
public enum ShareCapabilities
{
    None = 0,
    Read = 1 << 0,
    Upload = 1 << 1,
    Rename = 1 << 2,
    Delete = 1 << 3,

    ReadWrite = Read | Upload | Rename | Delete,
    All = ReadWrite
}

/// <summary>How much a paired device is allowed to do, regardless of the share's own flags.</summary>
public enum DeviceAccessLevel
{
    ReadOnly = 0,
    ReadWrite = 1
}

public sealed class ShareDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Absolute Windows directory the user explicitly authorised.</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>Stable volume identity (\\?\Volume{GUID}\) captured when the share was created.</summary>
    public string VolumeGuidPath { get; set; } = string.Empty;

    /// <summary>Mount point (drive letter) observed at creation, purely informational.</summary>
    public string MountPointAtCreation { get; set; } = string.Empty;

    /// <summary>File object id of the share root, used to detect the root being swapped.</summary>
    public string RootObjectId { get; set; } = string.Empty;

    public ShareCapabilities Capabilities { get; set; } = ShareCapabilities.Read;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public string? Note { get; set; }

    /// <summary>Last known state, refreshed by the share health probe.</summary>
    public ShareState State { get; set; } = ShareState.Unknown;
    public string? StateDetail { get; set; }
}

public enum ShareState
{
    Unknown,
    Online,
    Offline,
    IdentityMismatch,
    PermissionDenied
}

public sealed class DeviceDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "未命名设备";
    public DeviceAccessLevel Level { get; set; } = DeviceAccessLevel.ReadOnly;
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.Now;
    public string? LastSeenIp { get; set; }
    public string? UserAgent { get; set; }
    public bool Revoked { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string SessionEpoch { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Administrative reach granted to this phone, independently of <see cref="Level"/>.
    ///
    /// <para>Absent in files written before 1.7.0 reads as <see cref="DeviceCapability.None"/>: an upgrade
    /// must never widen what an existing phone may do.</para>
    /// </summary>
    public DeviceCapability Capabilities { get; set; } = DeviceCapability.None;

    /// <summary>Per-device CSRF token, readable by the paired client and required on writes.</summary>
    public string CsrfToken { get; set; } = Security.SecretTokens.NewToken();

    /// <summary>
    /// Monotonic revision of this device's authority, bumped whenever <see cref="Level"/> or
    /// <see cref="Capabilities"/> change.
    ///
    /// <para><b>Why a revision and not the fields.</b> The phone used to decide whether to re-read its
    /// permissions by comparing the single <c>writeFiles</c> boolean from the heartbeat. That misses every
    /// other change the operator can make — network management, extension install and execute, system view —
    /// and it cannot tell "nothing changed" from "I already applied this". A revision answers both: it moves
    /// when anything about the phone's authority moves, and only then.</para>
    ///
    /// <para>Absent in files written before 1.7.2 reads as 0, which is a legitimate starting point rather
    /// than a special case.</para>
    /// </summary>
    public int PermissionRevision { get; set; }
}

public sealed class RecycleEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ShareId { get; set; } = string.Empty;
    public string OriginalRelativePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long Length { get; set; }
    public string VolumeGuidPath { get; set; } = string.Empty;
    public string SourceObjectId { get; set; } = string.Empty;

    /// <summary>Path of the stored payload relative to the per-volume store root.</summary>
    public string StoredRelativePath { get; set; } = string.Empty;

    /// <summary>True when the payload had to be copied because the store sits on another volume.</summary>
    public bool WasCopied { get; set; }

    public string? ContentHash { get; set; }
    public DateTimeOffset DeletedAt { get; set; } = DateTimeOffset.Now;
    public string DeletedByDeviceId { get; set; } = string.Empty;
    public string DeletedByDeviceName { get; set; } = string.Empty;
    public RecycleState State { get; set; } = RecycleState.Staged;
}

public enum RecycleState
{
    Staged,
    Committed,
    Restored,
    Purged,
    Failed
}

public enum TransferDirection { Upload, Download }

public sealed class TransferRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public TransferDirection Direction { get; set; }
    public string ShareId { get; set; } = string.Empty;
    public string ShareName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public long TransferredBytes { get; set; }
    public TransferState State { get; set; } = TransferState.InProgress;
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public string? Error { get; set; }
}

public enum TransferState
{
    InProgress,
    Processing,
    Committed,
    Failed,
    Cancelled
}

public sealed class AuditEntry
{
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    public string Category { get; set; } = "general";
    public string Action { get; set; } = string.Empty;
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }

    /// <summary>
    /// Which kind of actor this was: "device", "local-admin" or "system". Null on entries written
    /// before task 09, which is why readers must treat a missing value as "device".
    /// </summary>
    public string? Source { get; set; }

    public string? ShareId { get; set; }
    public string? RelativePath { get; set; }
    public string? Detail { get; set; }
    public bool Success { get; set; } = true;
}
