namespace LanDisk.Update;

/// <summary>
/// One file inside an update package. Paths are always share-relative, forward slash separated and
/// validated before use; the hash is what actually authorises the bytes.
/// </summary>
public sealed class UpdateFileEntry
{
    public string Path { get; set; } = string.Empty;
    public long Bytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>
/// The manifest at the root of a <c>.lupd</c> package.
///
/// <para><b>Everything a user or the updater acts on is a field of this manifest</b>, and the whole
/// manifest is covered by the signature. That includes the signer identity (<see cref="KeyId"/>),
/// the release notes (through <see cref="ReleaseNotesSha256"/>) and the build identity
/// (<see cref="BuildCommit"/>), so none of them can be edited without invalidating the signature.
/// The previous schema signed a hand-rolled rendering that omitted <c>keyId</c> and the release
/// notes text, which meant those two could be replaced losslessly while the signature still
/// verified; schema 2 exists to close exactly that hole.</para>
///
/// <para>The signature input is <b>canonical JSON</b> produced from this object by
/// <see cref="UpdateSigning.ComputeSignedPayload"/>: fixed field order, no insignificant whitespace,
/// ASCII-only escaping, files sorted by path. Reformatting <c>update-manifest.json</c> on disk
/// therefore cannot change what was signed, while changing any value does.</para>
/// </summary>
public sealed class UpdateManifest
{
    public const string ProductName = "LanDisk";
    public const int CurrentSchemaVersion = 2;

    /// <summary>The only schema this build can verify. Older packages are refused with a specific reason.</summary>
    public static readonly int[] KnownSchemaVersions = [CurrentSchemaVersion];

    /// <summary>Schema 1 signed a payload that did not cover keyId or the release notes.</summary>
    public const int LegacySchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Product { get; set; } = ProductName;
    public string Version { get; set; } = string.Empty;

    /// <summary>Oldest installed version this package is allowed to upgrade from.</summary>
    public string MinimumUpgradeVersion { get; set; } = "1.0.0";

    public string Architecture { get; set; } = "win-x64";

    /// <summary>Exact git commit the packaged binaries were built from.</summary>
    public string BuildCommit { get; set; } = string.Empty;

    /// <summary>Human readable build identifier (configuration, runtime identifier, …).</summary>
    public string BuildId { get; set; } = string.Empty;

    public DateTimeOffset PublishedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>Entry point inside the payload; defaults to the Windows shell.</summary>
    public string EntryPoint { get; set; } = "LanDisk.exe";

    /// <summary>Identity of the signing key. Signed, and required to match the built-in key.</summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>Path of the release notes inside the package. Signed.</summary>
    public string ReleaseNotesEntry { get; set; } = UpdatePackage.ReleaseNotesEntryName;

    /// <summary>SHA-256 of the UTF-8 release notes text. Signed.</summary>
    public string ReleaseNotesSha256 { get; set; } = string.Empty;

    /// <summary>Length of the UTF-8 release notes text. Signed.</summary>
    public long ReleaseNotesBytes { get; set; }

    /// <summary>SHA-256 over every payload entry, so the file list itself is summarised by one value.</summary>
    public string PayloadDigest { get; set; } = string.Empty;

    public List<UpdateFileEntry> Files { get; set; } = [];
}

/// <summary>Detached signature block stored next to the manifest.</summary>
public sealed class UpdateSignature
{
    public string Algorithm { get; set; } = UpdateSigning.AlgorithmName;

    /// <summary>Base64 ECDSA P-256 signature over the canonical manifest payload.</summary>
    public string Value { get; set; } = string.Empty;
}
