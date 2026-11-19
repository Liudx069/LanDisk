using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanDisk.Update;

namespace LanDisk.Tests.Infrastructure;

/// <summary>
/// Builds hostile and well formed <c>.lupd</c> packages for the update tests.
///
/// It signs with a throw-away ECDSA key generated per test rather than the real release key, so the
/// tests never need the private key that signs shipping packages, and a compromised test machine
/// cannot produce anything a real installation would accept.
/// </summary>
public sealed class UpdateTestKit : IDisposable
{
    public UpdateTestKit(string label)
    {
        Root = Path.Combine(TestPaths.TempRoot, "update-" + label + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
        Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    }

    public string Root { get; }

    public ECDsa Key { get; }

    public string KeyId => UpdateSigning.KeyId(Key);

    public string Combine(params string[] segments)
    {
        string combined = Root;
        foreach (string segment in segments) combined = System.IO.Path.Combine(combined, segment);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(combined)!);
        return combined;
    }

    public string NewDirectory(string name)
    {
        string directory = System.IO.Path.Combine(Root, name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Writes a package to disk and returns its path.</summary>
    public string BuildPackage(string name, PackageSpec spec)
    {
        string packagePath = System.IO.Path.Combine(Root, name.EndsWith(UpdatePackage.FileExtension) ? name : name + UpdatePackage.FileExtension);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(packagePath)!);

        var declared = spec.DeclaredFiles ?? spec.Files
            .Select(pair => new UpdateFileEntry
            {
                Path = Normalize(pair.Key),
                Bytes = Encoding.UTF8.GetByteCount(pair.Value),
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(pair.Value)))
            })
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToList();

        ECDsa signingKey = spec.SigningKey ?? Key;
        string releaseNotes = spec.ReleaseNotes;
        string signedKeyId = spec.KeyIdOverride ?? UpdateSigning.KeyId(signingKey);

        var manifest = new UpdateManifest
        {
            SchemaVersion = spec.SchemaVersion,
            Product = spec.Product,
            Version = spec.Version,
            MinimumUpgradeVersion = spec.MinimumUpgradeVersion,
            Architecture = spec.Architecture,
            BuildCommit = spec.BuildCommit,
            BuildId = spec.BuildId,
            PublishedAt = spec.PublishedAt,
            EntryPoint = spec.EntryPoint,
            KeyId = signedKeyId,
            ReleaseNotesEntry = spec.ReleaseNotesEntry,
            ReleaseNotesSha256 = spec.ReleaseNotesShaOverride
                ?? UpdateSigning.ComputeReleaseNotesHash(spec.ReleaseNotesForHash ?? releaseNotes),
            ReleaseNotesBytes = spec.ReleaseNotesBytesOverride
                ?? Encoding.UTF8.GetByteCount(spec.ReleaseNotesForHash ?? releaseNotes),
            PayloadDigest = spec.PayloadDigestOverride ?? UpdateSigning.ComputePayloadDigest(declared),
            Files = declared
        };

        UpdateSignature signature = spec.Sign
            ? new UpdateSignature
            {
                Algorithm = spec.SignatureAlgorithm,
                Value = Convert.ToBase64String(UpdateSigning.Sign(manifest, signingKey))
            }
            : new UpdateSignature { Algorithm = spec.SignatureAlgorithm, Value = spec.SignatureOverride ?? string.Empty };

        if (spec.SignatureOverride is not null)
            signature = new UpdateSignature { Algorithm = spec.SignatureAlgorithm, Value = spec.SignatureOverride };

        // A package whose manifest was edited *after* signing: the signature still belongs to the
        // unedited manifest, which is exactly the attack the signed semantics must catch.
        string manifestJson = spec.ManifestJsonOverride ?? UpdatePackage.SerializeManifest(manifest);

        using (var stream = new FileStream(packagePath, FileMode.Create, FileAccess.Write))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteText(archive, spec.ManifestEntryName, manifestJson);
            WriteText(archive, spec.SignatureEntryName, UpdatePackage.SerializeSignature(signature));

            if (!spec.OmittedReleaseNotesEntries.Contains(spec.ReleaseNotesEntry, StringComparer.OrdinalIgnoreCase))
                WriteText(archive, spec.ReleaseNotesEntry, spec.ReleaseNotesOverride ?? releaseNotes);

            foreach ((string relative, string content) in spec.Files)
            {
                byte[] bytes = spec.RawOverrides.TryGetValue(relative, out byte[]? raw)
                    ? raw
                    : Encoding.UTF8.GetBytes(content);
                WriteBytes(archive, UpdatePath.PayloadPrefix + Normalize(relative), bytes);
            }

            foreach ((string relative, byte[] bytes) in spec.ExtraPayloadEntries)
                WriteBytes(archive, UpdatePath.PayloadPrefix + Normalize(relative), bytes);

            // OmittedFiles are simply never written while the manifest still promises them, which is
            // the "package is missing a declared file" shape.
        }

        return packagePath;
    }

    /// <summary>Creates a directory that looks like an installed LanDisk, plus an install manifest.</summary>
    public string CreateInstall(string name, string version, IDictionary<string, string> files, string entryPoint = "LanDisk.exe")
    {
        string installRoot = NewDirectory(name);
        var written = new List<string>();

        foreach ((string relative, string content) in files)
        {
            string target = System.IO.Path.Combine(installRoot, Normalize(relative).Replace('/', '\\'));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content, new UTF8Encoding(false));
            written.Add(Normalize(relative));
        }

        // Same order the release pipeline now uses: the files are on disk first, then the manifest is
        // written from what is actually there — including the digest of build-info.json when present.
        new InstallManifest
        {
            Version = version,
            BuildCommit = "test",
            InstalledAt = DateTimeOffset.Now,
            Files = written,
            BuildInfo = InstallManifestFileRecord.TryCapture(installRoot, InstallBuildInfoFile.FileName)
        }.Write(installRoot);

        if (!files.Keys.Any(k => Normalize(k).Equals(Normalize(entryPoint), StringComparison.OrdinalIgnoreCase)))
        {
            // ValidateManifestShape requires the entry point to exist in the package, not on disk,
            // so this is only a convenience for tests that resolve the entry point by hand.
        }

        return installRoot;
    }

    /// <summary>The payload directory of a verified package, extracted under this kit's root.</summary>
    public VerifiedUpdatePackage Extract(string packagePath, string? currentVersion = null, string? stagingName = null)
    {
        string staging = System.IO.Path.Combine(Root, "staging", stagingName ?? Guid.NewGuid().ToString("N")[..8]);
        return UpdatePackage.Extract(packagePath, staging, currentVersion, Key);
    }

    public static string Normalize(string relative) => relative.Replace('\\', '/');

    private static void WriteText(ZipArchive archive, string name, string content)
        => WriteBytes(archive, name, new UTF8Encoding(false).GetBytes(content));

    private static void WriteBytes(ZipArchive archive, string name, byte[] bytes)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }

    public void Dispose()
    {
        Key.Dispose();
        try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { }
    }
}

/// <summary>Everything a test may want to distort in a package.</summary>
public sealed class PackageSpec
{
    public string Version { get; set; } = "1.2.0";
    public string MinimumUpgradeVersion { get; set; } = "1.0.0";
    public string Product { get; set; } = UpdateManifest.ProductName;
    public string Architecture { get; set; } = "win-x64";
    public string EntryPoint { get; set; } = "LanDisk.exe";
    public int SchemaVersion { get; set; } = UpdateManifest.CurrentSchemaVersion;
    public string BuildCommit { get; set; } = "test-commit";
    public string BuildId { get; set; } = "test-build";
    public DateTimeOffset PublishedAt { get; set; } = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    /// <summary>The release notes text shipped in the package (and hashed into the signed manifest).</summary>
    public string ReleaseNotes { get; set; } = "测试更新说明。";

    public string ReleaseNotesEntry { get; set; } = UpdatePackage.ReleaseNotesEntryName;

    /// <summary>Text written to the release notes entry, if it must differ from what was hashed.</summary>
    public string? ReleaseNotesOverride { get; set; }

    /// <summary>Text hashed into the manifest, if it must differ from what is written.</summary>
    public string? ReleaseNotesForHash { get; set; }

    public string? ReleaseNotesShaOverride { get; set; }
    public long? ReleaseNotesBytesOverride { get; set; }

    /// <summary>Replaces the signer identity in the manifest after signing (a tamper attempt).</summary>
    public string? KeyIdOverride { get; set; }

    public string? PayloadDigestOverride { get; set; }

    /// <summary>Replaces the whole manifest JSON, e.g. to reformat it without changing its meaning.</summary>
    public string? ManifestJsonOverride { get; set; }

    /// <summary>Payload files, keyed by relative path, with their text content.</summary>
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LanDisk.exe"] = "fake-lan-disk-exe-1.2.0",
        ["LanDisk.Server.dll"] = "fake-server-dll-1.2.0"
    };

    /// <summary>Overrides what the manifest claims, so the manifest can disagree with the payload.</summary>
    public List<UpdateFileEntry>? DeclaredFiles { get; set; }

    /// <summary>Payload entries that the signed manifest never mentions.</summary>
    public Dictionary<string, byte[]> ExtraPayloadEntries { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Files removed from the archive while the manifest still declares them.</summary>
    public List<string> OmittedFiles { get; } = [];

    /// <summary>Set to skip writing the release notes entry entirely.</summary>
    public List<string> OmittedReleaseNotesEntries { get; } = [];

    /// <summary>Raw bytes that replace the UTF-8 encoding of <see cref="Files"/> for a given path.</summary>
    public Dictionary<string, byte[]> RawOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Sign { get; set; } = true;
    public ECDsa? SigningKey { get; set; }
    public string SignatureAlgorithm { get; set; } = UpdateSigning.AlgorithmName;
    public string? SignatureOverride { get; set; }
    public string ManifestEntryName { get; set; } = UpdatePackage.ManifestEntryName;
    public string SignatureEntryName { get; set; } = UpdatePackage.SignatureEntryName;

    public static PackageSpec WithFile(string path, string content)
    {
        var spec = new PackageSpec();
        spec.Files[path] = content;
        return spec;
    }

    /// <summary>Replaces the whole payload with exactly these files.</summary>
    public PackageSpec Only(params (string Path, string Content)[] files)
    {
        Files.Clear();
        foreach ((string path, string content) in files) Files[path] = content;
        return this;
    }

    public PackageSpec With(string path, string content)
    {
        Files[path] = content;
        return this;
    }

    /// <summary>Escape hatch for the one-off knobs that do not deserve their own fluent method.</summary>
    public PackageSpec Also(Action<PackageSpec> configure)
    {
        configure(this);
        return this;
    }

    /// <summary>Replaces the bytes written for a declared path without changing what the manifest promises.</summary>
    public PackageSpec WithRaw(string path, byte[] bytes)
    {
        RawOverrides[path] = bytes;
        return this;
    }

    /// <summary>Adds a payload entry the signed manifest never declares.</summary>
    public PackageSpec WithUndeclared(string path, byte[] bytes)
    {
        ExtraPayloadEntries[path] = bytes;
        return this;
    }

    /// <summary>
    /// Returns a replacement with exactly the same byte length as <paramref name="original"/>, so a
    /// test can tamper with content while leaving the size in the manifest correct.
    /// </summary>
    public static byte[] SameLengthBytes(string original, string replacement)
    {
        string text = replacement.Length >= original.Length
            ? replacement[..original.Length]
            : replacement.PadRight(original.Length, 'x');
        return Encoding.UTF8.GetBytes(text);
    }
}
