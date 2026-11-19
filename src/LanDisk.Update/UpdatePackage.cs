using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanDisk.Update;

public sealed class UpdatePackageException : Exception
{
    public UpdatePackageException(string code, string message) : base(message) => Code = code;

    /// <summary>Stable machine-readable reason, surfaced to the admin UI untranslated.</summary>
    public string Code { get; }
}

/// <summary>
/// Path rules for everything that comes out of a <c>.lupd</c> package.
///
/// A package is hostile input until proven otherwise: it arrives over the network from a browser
/// upload. Every name inside it is validated with <see cref="IsSafeRelativePath"/> first and only
/// then joined to a staging root, and the join is re-checked with <see cref="ResolveWithin"/> so
/// that a name which passes the text rules can still never escape the directory it was written to.
/// </summary>
public static class UpdatePath
{
    public const string PayloadPrefix = "payload/";

    private static readonly char[] InvalidNameChars = ['<', '>', ':', '"', '|', '?', '*', '\0'];

    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.Length > 260) return false;
        if (path.Contains('\\')) return false;
        if (path.StartsWith('/')) return false;
        if (Path.IsPathRooted(path)) return false;
        if (path.Contains(':')) return false;
        if (path.IndexOfAny(InvalidNameChars) >= 0) return false;

        string[] segments = path.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0) return false;
            if (segment == "." || segment == "..") return false;
            if (segment.EndsWith(' ') || segment.EndsWith('.')) return false;
            if (segment.Contains(' ')) return false;

            // Windows reserves these names with *any* extension: NUL.txt is just as unopenable as NUL.
            string stem = segment.Split('.')[0];
            if (IsReservedDeviceName(stem)) return false;
        }

        return true;
    }

    private static bool IsReservedDeviceName(string stem)
        => stem.ToUpperInvariant() switch
        {
            "CON" or "PRN" or "AUX" or "NUL" => true,
            "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9" => true,
            "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9" => true,
            _ => false
        };

    /// <summary>
    /// Joins a validated relative path to <paramref name="root"/> and proves the result is inside it.
    /// Uses segment-wise containment on the fully resolved path rather than a string prefix test,
    /// so a sibling directory sharing a name prefix cannot be reached.
    /// </summary>
    public static string ResolveWithin(string root, string relativePath)
    {
        if (!IsSafeRelativePath(relativePath))
            throw new UpdatePackageException("UNSAFE_PATH", $"更新包内的路径不合法：{relativePath}");

        string rootFull = Path.GetFullPath(root);
        string combined = Path.GetFullPath(Path.Combine(rootFull, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        string[] rootSegments = rootFull.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string[] combinedSegments = combined.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (combinedSegments.Length <= rootSegments.Length)
            throw new UpdatePackageException("UNSAFE_PATH", $"更新包内的路径越出目标目录：{relativePath}");

        for (int i = 0; i < rootSegments.Length; i++)
        {
            if (!string.Equals(rootSegments[i], combinedSegments[i], StringComparison.OrdinalIgnoreCase))
                throw new UpdatePackageException("UNSAFE_PATH", $"更新包内的路径越出目标目录：{relativePath}");
        }

        return combined;
    }
}

/// <summary>Where a package is allowed to place files once applied.</summary>
public enum UpdateFileDisposition
{
    /// <summary>Program files next to the executable; replaced in place.</summary>
    Program
}

/// <summary>
/// A verified, extracted update package.
///
/// "Verified" is a specific claim: the manifest's own <c>keyId</c> matched the built-in release key,
/// the signature over the canonical manifest verified, the release notes matched the hash the signed
/// manifest promised, and every extracted file hashed to the digest it promised. Only then is
/// <see cref="IsVerified"/> true and only then will the applier look at it.
/// </summary>
public sealed class VerifiedUpdatePackage
{
    public required UpdateManifest Manifest { get; init; }
    public required UpdateSignature Signature { get; init; }
    public required string StagingRoot { get; init; }
    public required string PayloadRoot { get; init; }
    public required long TotalBytes { get; init; }
    public required string PackageSha256 { get; init; }
    public bool IsVerified { get; init; }
    public string KeyId { get; init; } = string.Empty;

    /// <summary>The release notes text, verified against the signed hash.</summary>
    public string ReleaseNotes { get; init; } = string.Empty;
}

public sealed class UpdateVerificationReport
{
    public bool Ok { get; set; }
    public string? Code { get; set; }
    public string? Message { get; set; }
    public string? Version { get; set; }
    public string? CurrentVersion { get; set; }
    public string? BuildCommit { get; set; }
    public string? ReleaseNotes { get; set; }
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public string? PackageSha256 { get; set; }
    public string? SignerKeyId { get; set; }
    public string? PayloadDigest { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public List<string> Warnings { get; set; } = [];
}

/// <summary>
/// Reads <c>.lupd</c> packages. The format is a ZIP holding a signed manifest plus a payload tree:
///
/// <code>
/// update-manifest.json
/// update-manifest.sig.json
/// release-notes.md
/// payload/LanDisk.exe
/// payload/LanDisk.Server.dll
/// ...
/// </code>
///
/// Verification never writes outside the staging root and never trusts a name, a length or a hash
/// found anywhere except the signed manifest.
/// </summary>
public static class UpdatePackage
{
    public const string ManifestEntryName = "update-manifest.json";
    public const string SignatureEntryName = "update-manifest.sig.json";
    public const string ReleaseNotesEntryName = "release-notes.md";
    public const string FileExtension = ".lupd";

    /// <summary>Copies of the signed manifest kept inside the staging root, for the updater process.</summary>
    public const string StagedManifestName = "manifest.json";
    public const string StagedSignatureName = "signature.json";
    public const string StagedReleaseNotesName = "release-notes.md";
    public const string PayloadDirectoryName = "payload";

    /// <summary>Refuse absurd packages before spending disk or time on them.</summary>
    public const long MaxTotalUncompressedBytes = 3L * 1024 * 1024 * 1024;
    public const long MaxSingleFileBytes = 1L * 1024 * 1024 * 1024;
    public const int MaxFileCount = 20_000;
    public const long MaxReleaseNotesBytes = 256 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static JsonSerializerOptions SerializerOptions => JsonOptions;

    /// <summary>
    /// Reads the manifest and signature out of a package without extracting the payload. Used to
    /// show the admin what they are about to install before anything is written to disk.
    /// </summary>
    public static (UpdateManifest Manifest, UpdateSignature Signature) ReadManifest(Stream package)
    {
        using var archive = new System.IO.Compression.ZipArchive(package, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);

        UpdateManifest? manifest = ReadJsonEntry<UpdateManifest>(archive, ManifestEntryName)
            ?? throw new UpdatePackageException("MANIFEST_MISSING", "更新包内缺少 update-manifest.json。");
        UpdateSignature? signature = ReadJsonEntry<UpdateSignature>(archive, SignatureEntryName)
            ?? throw new UpdatePackageException("SIGNATURE_MISSING", "更新包内缺少 update-manifest.sig.json（未签名的包一律拒绝）。");

        return (manifest, signature);
    }

    public static (UpdateManifest Manifest, UpdateSignature Signature) ReadManifest(string packagePath)
    {
        using FileStream stream = File.OpenRead(packagePath);
        return ReadManifest(stream);
    }

    /// <summary>
    /// Checks only the format version.
    ///
    /// This runs before signature verification because the canonical payload layout depends on the
    /// schema, so an unknown version cannot be verified at all. It is a format decision, not a trust
    /// decision: no other field of the manifest is read before the signature has been checked.
    /// </summary>
    public static void EnsureSchemaSupported(UpdateManifest manifest)
    {
        if (manifest.SchemaVersion == UpdateManifest.LegacySchemaVersion)
        {
            throw new UpdatePackageException("LEGACY_SCHEMA_UNSUPPORTED",
                "这个更新包使用旧的格式版本 1：它的签名没有覆盖签名者标识与更新说明，"
                + "因此无法证明这两项没有被替换。请改用为该版本重新构建并签名的更新包，"
                + "或使用完整的安装包升级。");
        }

        if (manifest.SchemaVersion != UpdateManifest.CurrentSchemaVersion)
        {
            throw new UpdatePackageException("SCHEMA_UNSUPPORTED",
                $"不支持的更新包格式版本：{manifest.SchemaVersion}。");
        }
    }

    /// <summary>
    /// Structural and policy checks that need no payload bytes.
    ///
    /// Always called <em>after</em> <see cref="UpdateSigning.VerifyOrThrow"/>: until the signature has
    /// been checked, the manifest is attacker-supplied text, and using it to produce any diagnosis
    /// would both mislead the operator and turn this method into an oracle.
    /// </summary>
    public static void ValidateManifestShape(UpdateManifest manifest, string? currentVersion = null)
    {
        EnsureSchemaSupported(manifest);

        if (!string.Equals(manifest.Product, UpdateManifest.ProductName, StringComparison.Ordinal))
            throw new UpdatePackageException("PRODUCT_MISMATCH", $"更新包不是 LanDisk 的：{manifest.Product}。");

        if (!Version.TryParse(manifest.Version, out Version? packageVersion))
            throw new UpdatePackageException("VERSION_INVALID", $"更新包版本号无法解析：{manifest.Version}。");

        if (!string.IsNullOrWhiteSpace(currentVersion) && Version.TryParse(currentVersion, out Version? installed))
        {
            if (packageVersion <= installed)
                throw new UpdatePackageException("VERSION_NOT_NEWER",
                    $"更新包版本 {manifest.Version} 不高于当前已安装的 {currentVersion}，已拒绝安装。");
        }

        if (string.IsNullOrWhiteSpace(manifest.MinimumUpgradeVersion)
            || !Version.TryParse(manifest.MinimumUpgradeVersion, out Version? minimum))
        {
            throw new UpdatePackageException("VERSION_INVALID",
                $"更新包的最低可升级版本无法解析：{manifest.MinimumUpgradeVersion}。");
        }

        if (Version.TryParse(currentVersion, out Version? current) && current < minimum)
        {
            throw new UpdatePackageException("VERSION_TOO_OLD",
                $"更新包要求至少从 {manifest.MinimumUpgradeVersion} 升级，当前为 {currentVersion}。");
        }

        if (!string.Equals(manifest.Architecture, "win-x64", StringComparison.OrdinalIgnoreCase))
            throw new UpdatePackageException("ARCHITECTURE_MISMATCH", $"更新包架构不匹配：{manifest.Architecture}。");

        if (!UpdatePath.IsSafeRelativePath(manifest.EntryPoint))
            throw new UpdatePackageException("UNSAFE_PATH", $"更新包入口程序名不合法：{manifest.EntryPoint}。");

        if (string.IsNullOrWhiteSpace(manifest.KeyId) || manifest.KeyId.Length < 8)
            throw new UpdatePackageException("KEY_ID_MISSING", "更新包清单没有声明合法的签名者标识（keyId）。");

        if (string.IsNullOrWhiteSpace(manifest.BuildCommit) || manifest.BuildCommit.Length < 7)
            throw new UpdatePackageException("BUILD_COMMIT_MISSING",
                "更新包清单没有声明构建提交号（buildCommit），无法核对候选身份。");

        if (!UpdatePath.IsSafeRelativePath(manifest.ReleaseNotesEntry))
            throw new UpdatePackageException("UNSAFE_PATH",
                $"更新说明的路径不合法：{manifest.ReleaseNotesEntry}。");

        if (!IsSha256(manifest.ReleaseNotesSha256))
            throw new UpdatePackageException("RELEASE_NOTES_HASH_MISSING",
                "更新包清单没有声明更新说明的哈希，无法证明用户看到的就是发布者签名过的内容。");

        if (manifest.ReleaseNotesBytes < 0 || manifest.ReleaseNotesBytes > MaxReleaseNotesBytes)
            throw new UpdatePackageException("RELEASE_NOTES_TOO_LONG",
                $"更新说明长度不合法：{manifest.ReleaseNotesBytes} 字节。");

        if (manifest.Files.Count == 0)
            throw new UpdatePackageException("EMPTY_PACKAGE", "更新包内没有任何文件。");

        if (manifest.Files.Count > MaxFileCount)
            throw new UpdatePackageException("TOO_MANY_FILES", $"更新包文件数 {manifest.Files.Count} 超过上限 {MaxFileCount}。");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (UpdateFileEntry file in manifest.Files)
        {
            if (!UpdatePath.IsSafeRelativePath(file.Path))
                throw new UpdatePackageException("UNSAFE_PATH", $"更新包内的路径不合法：{file.Path}。");

            // The install manifest is written by the applier, never shipped: it describes what the
            // installed version owns, so a package cannot carry the answer to its own question.
            if (string.Equals(file.Path, InstallManifest.FileName, StringComparison.OrdinalIgnoreCase))
                throw new UpdatePackageException("RESERVED_PATH",
                    $"更新包不能包含 {InstallManifest.FileName}：该文件由安装程序自身生成。");

            if (!seen.Add(file.Path))
                throw new UpdatePackageException("DUPLICATE_PATH", $"更新包内路径重复：{file.Path}。");

            if (file.Bytes < 0 || file.Bytes > MaxSingleFileBytes)
                throw new UpdatePackageException("FILE_TOO_LARGE", $"更新包内文件过大：{file.Path}。");

            if (!IsSha256(file.Sha256))
                throw new UpdatePackageException("HASH_INVALID", $"更新包内文件的哈希不合法：{file.Path}。");

            total += file.Bytes;
            if (total > MaxTotalUncompressedBytes)
                throw new UpdatePackageException("PACKAGE_TOO_LARGE", "更新包解压后总大小超过上限。");
        }

        bool hasEntryPoint = manifest.Files.Any(f => string.Equals(f.Path, manifest.EntryPoint, StringComparison.OrdinalIgnoreCase));
        if (!hasEntryPoint)
            throw new UpdatePackageException("ENTRY_POINT_MISSING", $"更新包内缺少入口程序 {manifest.EntryPoint}。");

        string expectedDigest = UpdateSigning.ComputePayloadDigest(manifest.Files);
        if (!string.Equals(manifest.PayloadDigest, expectedDigest, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdatePackageException("PAYLOAD_DIGEST_MISMATCH",
                "更新包清单里的 payloadDigest 与文件列表不一致，清单已被改动。");
        }
    }

    /// <summary>
    /// Full verification: signer identity, signature, release notes, then extract and hash every
    /// extracted byte against the signed manifest. Extraction happens into
    /// <paramref name="stagingRoot"/> only.
    /// </summary>
    public static VerifiedUpdatePackage Extract(
        string packagePath,
        string stagingRoot,
        string? currentVersion,
        ECDsa publicKey,
        Action<string, int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(packagePath))
            throw new UpdatePackageException("PACKAGE_MISSING", "更新包文件不存在。");

        string packageSha256;
        using (FileStream hashStream = File.OpenRead(packagePath))
        {
            packageSha256 = Convert.ToHexStringLower(SHA256.HashData(hashStream));
        }

        UpdateManifest manifest;
        UpdateSignature signature;
        string releaseNotes;
        using (FileStream stream = File.OpenRead(packagePath))
        {
            (manifest, signature) = ReadManifest(stream);
        }

        // Format version, then signature, then policy. Nothing in the manifest is acted on — not even
        // to produce an error message — until it is proven to be the publisher's manifest.
        EnsureSchemaSupported(manifest);
        UpdateSigning.VerifyOrThrow(manifest, signature, publicKey);
        ValidateManifestShape(manifest, currentVersion);

        using (FileStream stream = File.OpenRead(packagePath))
        {
            releaseNotes = ReadReleaseNotes(stream, manifest);
        }

        string payloadRoot = Path.Combine(stagingRoot, PayloadDirectoryName);
        if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true);
        Directory.CreateDirectory(payloadRoot);

        // The updater runs as a separate process after the server has exited, so the signed manifest
        // and the verified release notes have to be readable from the staging directory alone.
        File.WriteAllText(Path.Combine(stagingRoot, StagedManifestName), SerializeManifest(manifest));
        File.WriteAllText(Path.Combine(stagingRoot, StagedSignatureName), SerializeSignature(signature));
        File.WriteAllText(Path.Combine(stagingRoot, StagedReleaseNotesName), releaseNotes);

        var expected = manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);

        using (FileStream stream = File.OpenRead(packagePath))
        using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read))
        {
            var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalBytes = 0;
            int done = 0;

            foreach (System.IO.Compression.ZipArchiveEntry entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (entry.FullName.EndsWith('/')) continue;
                if (entry.FullName is ManifestEntryName or SignatureEntryName or ReleaseNotesEntryName) continue;

                if (!entry.FullName.StartsWith(UpdatePath.PayloadPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                string relative = entry.FullName[UpdatePath.PayloadPrefix.Length..];
                if (!UpdatePath.IsSafeRelativePath(relative))
                    throw new UpdatePackageException("UNSAFE_PATH", $"更新包内的路径不合法：{entry.FullName}");

                if (!expected.TryGetValue(relative, out UpdateFileEntry? declared))
                    throw new UpdatePackageException("UNDECLARED_FILE",
                        $"更新包内存在签名清单未声明的文件：{relative}（包已被篡改）。");

                if (entry.Length != declared.Bytes)
                    throw new UpdatePackageException("SIZE_MISMATCH",
                        $"文件大小与签名清单不符：{relative}（清单 {declared.Bytes}，实际 {entry.Length}）。");

                string destination = UpdatePath.ResolveWithin(payloadRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                using (Stream source = entry.Open())
                using (FileStream target = File.Create(destination))
                {
                    source.CopyTo(target);
                }

                string actualHash = HashFile(destination);
                if (!string.Equals(actualHash, declared.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new UpdatePackageException("HASH_MISMATCH",
                        $"文件内容与签名清单不符：{relative}（包已被篡改或损坏）。");

                totalBytes += declared.Bytes;
                extracted.Add(relative);
                progress?.Invoke(relative, ++done, manifest.Files.Count);
            }

            List<string> missing = [.. expected.Keys.Where(k => !extracted.Contains(k))];
            if (missing.Count > 0)
                throw new UpdatePackageException("FILE_MISSING",
                    $"更新包缺少清单中声明的文件：{string.Join("、", missing.Take(5))}"
                    + (missing.Count > 5 ? $" 等 {missing.Count} 个" : string.Empty));
        }

        return new VerifiedUpdatePackage
        {
            Manifest = manifest,
            Signature = signature,
            StagingRoot = Path.GetFullPath(stagingRoot),
            PayloadRoot = payloadRoot,
            TotalBytes = manifest.Files.Sum(f => f.Bytes),
            PackageSha256 = packageSha256,
            IsVerified = true,
            KeyId = manifest.KeyId,
            ReleaseNotes = releaseNotes
        };
    }

    public static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// Reads the release notes and proves they are the bytes the signed manifest promised.
    ///
    /// Without this the notes are just attacker-controlled text shown next to a valid signature:
    /// the user would read "安全性修复" while the package contained something else.
    /// </summary>
    public static string ReadReleaseNotes(Stream package, UpdateManifest manifest)
    {
        using var archive = new System.IO.Compression.ZipArchive(package, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);

        System.IO.Compression.ZipArchiveEntry? entry = archive.Entries
            .FirstOrDefault(e => string.Equals(e.FullName, manifest.ReleaseNotesEntry, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
            throw new UpdatePackageException("RELEASE_NOTES_MISSING",
                $"更新包内缺少签名清单声明的更新说明：{manifest.ReleaseNotesEntry}。");

        if (entry.Length != manifest.ReleaseNotesBytes)
            throw new UpdatePackageException("RELEASE_NOTES_MISMATCH",
                $"更新说明长度与签名清单不符（清单 {manifest.ReleaseNotesBytes}，实际 {entry.Length}）。");

        string text;
        using (Stream stream = entry.Open())
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            text = reader.ReadToEnd();
        }

        string actual = UpdateSigning.ComputeReleaseNotesHash(text);
        if (!string.Equals(actual, manifest.ReleaseNotesSha256, StringComparison.OrdinalIgnoreCase))
            throw new UpdatePackageException("RELEASE_NOTES_MISMATCH",
                "更新说明内容与签名清单不符（包已被篡改）。");

        return text;
    }

    public static UpdateManifest ParseManifestJson(string json)
        => JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions)
           ?? throw new UpdatePackageException("MANIFEST_INVALID", "update-manifest.json 无法解析。");

    public static UpdateSignature ParseSignatureJson(string json)
        => JsonSerializer.Deserialize<UpdateSignature>(json, JsonOptions)
           ?? throw new UpdatePackageException("SIGNATURE_INVALID", "update-manifest.sig.json 无法解析。");

    public static string SerializeManifest(UpdateManifest manifest) => JsonSerializer.Serialize(manifest, JsonOptions);

    public static string SerializeSignature(UpdateSignature signature) => JsonSerializer.Serialize(signature, JsonOptions);

    /// <summary>
    /// Re-verifies a staging directory that was produced by <see cref="Extract"/>, but which this
    /// process did not create.
    ///
    /// This is the trust boundary for the updater: it starts after the server has already exited, so
    /// it re-checks the signer identity, the signature, the release notes and every payload hash
    /// from scratch rather than believing a file that merely says the check was done.
    /// </summary>
    public static VerifiedUpdatePackage LoadStaged(
        string stagingRoot,
        ECDsa publicKey,
        string? currentVersion = null,
        string? expectedKeyId = null)
    {
        stagingRoot = Path.GetFullPath(stagingRoot);
        string manifestPath = Path.Combine(stagingRoot, StagedManifestName);
        string signaturePath = Path.Combine(stagingRoot, StagedSignatureName);
        string releaseNotesPath = Path.Combine(stagingRoot, StagedReleaseNotesName);
        string payloadRoot = Path.Combine(stagingRoot, PayloadDirectoryName);

        if (!File.Exists(manifestPath))
            throw new UpdatePackageException("STAGING_INCOMPLETE", "暂存目录缺少 manifest.json。");
        if (!File.Exists(signaturePath))
            throw new UpdatePackageException("STAGING_INCOMPLETE", "暂存目录缺少 signature.json。");
        if (!Directory.Exists(payloadRoot))
            throw new UpdatePackageException("STAGING_INCOMPLETE", "暂存目录缺少 payload 子目录。");

        UpdateManifest manifest = ParseManifestJson(File.ReadAllText(manifestPath));
        UpdateSignature signature = ParseSignatureJson(File.ReadAllText(signaturePath));

        EnsureSchemaSupported(manifest);

        if (expectedKeyId is not null && !string.Equals(manifest.KeyId, expectedKeyId, StringComparison.OrdinalIgnoreCase))
        {
            // The key id is signed, so a mismatch here means the staging directory was swapped for a
            // package signed by someone else; say that plainly instead of reporting a crypto failure.
            throw new UpdateSignatureException("SIGNER_CHANGED",
                "暂存目录中的签名者与请求安装时不一致。");
        }

        UpdateSigning.VerifyOrThrow(manifest, signature, publicKey);
        ValidateManifestShape(manifest, currentVersion);

        string releaseNotes = string.Empty;
        if (File.Exists(releaseNotesPath))
        {
            releaseNotes = File.ReadAllText(releaseNotesPath);
            string notesHash = UpdateSigning.ComputeReleaseNotesHash(releaseNotes);
            if (!string.Equals(notesHash, manifest.ReleaseNotesSha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdatePackageException("RELEASE_NOTES_MISMATCH", "暂存目录中的更新说明与签名清单不符。");
        }
        else
        {
            throw new UpdatePackageException("RELEASE_NOTES_MISSING", "暂存目录缺少 release-notes.md。");
        }

        long total = 0;
        foreach (UpdateFileEntry file in manifest.Files)
        {
            string path = UpdatePath.ResolveWithin(payloadRoot, file.Path);
            if (!File.Exists(path))
                throw new UpdatePackageException("FILE_MISSING", $"暂存目录缺少文件：{file.Path}。");

            var info = new FileInfo(path);
            if (info.Length != file.Bytes)
                throw new UpdatePackageException("SIZE_MISMATCH",
                    $"暂存文件大小与签名清单不符：{file.Path}（清单 {file.Bytes}，实际 {info.Length}）。");

            if (!HashFile(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdatePackageException("HASH_MISMATCH", $"暂存文件内容与签名清单不符：{file.Path}。");

            total += file.Bytes;
        }

        return new VerifiedUpdatePackage
        {
            Manifest = manifest,
            Signature = signature,
            StagingRoot = stagingRoot,
            PayloadRoot = payloadRoot,
            TotalBytes = total,
            PackageSha256 = string.Empty,
            IsVerified = true,
            KeyId = manifest.KeyId,
            ReleaseNotes = releaseNotes
        };
    }

    private static bool IsSha256(string? value)
        => !string.IsNullOrEmpty(value) && value.Length == 64 && value.All(Uri.IsHexDigit);

    private static T? ReadJsonEntry<T>(System.IO.Compression.ZipArchive archive, string name) where T : class
    {
        System.IO.Compression.ZipArchiveEntry? entry =
            archive.GetEntry(name) ?? archive.Entries.FirstOrDefault(e => string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;

        if (entry.Length > 4 * 1024 * 1024)
            throw new UpdatePackageException("MANIFEST_TOO_LARGE", $"{name} 过大，拒绝解析。");

        using Stream stream = entry.Open();
        using var reader = new StreamReader(stream);
        string json = reader.ReadToEnd();
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new UpdatePackageException("MANIFEST_INVALID", $"{name} 不是合法 JSON：{ex.Message}");
        }
    }
}
