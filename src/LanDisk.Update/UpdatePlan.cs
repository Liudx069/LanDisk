using System.Text.Json;

namespace LanDisk.Update;

/// <summary>
/// A file whose bytes an installation also *declares* in the install manifest, so the claim can be
/// checked independently instead of being taken on trust.
///
/// <para>The only such file today is <c>build-info.json</c> (see <see cref="InstallBuildInfo"/>):
/// it states which release the directory holds, and the manifest now repeats its path, length and
/// hash. The release pipeline refuses to produce a candidate unless the three recorded values match
/// the bytes actually shipped inside the runtime ZIP and the signed <c>.lupd</c> payload; the
/// applier refreshes the record whenever it writes a new manifest, so the claim stays true after an
/// update as well.</para>
/// </summary>
public sealed class InstallManifestFileRecord
{
    public string Path { get; set; } = string.Empty;
    public long Bytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>
    /// Records the file as it currently exists under <paramref name="installRoot"/>, or returns
    /// <c>null</c> when it is not there. Reading the bytes back is the point: a record that merely
    /// repeats what a package claimed would prove nothing about the directory.
    /// </summary>
    public static InstallManifestFileRecord? TryCapture(string installRoot, string relativePath)
    {
        try
        {
            string path = System.IO.Path.Combine(UpdateLayout.Normalize(installRoot), relativePath);
            if (!File.Exists(path)) return null;

            return new InstallManifestFileRecord
            {
                Path = relativePath,
                Bytes = new FileInfo(path).Length,
                Sha256 = UpdatePackage.HashFile(path)
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// Record of which files in the installation directory belong to LanDisk itself.
///
/// This is what makes an update safe: the applier is allowed to delete a file only if the previous
/// install manifest claims it. Anything a user dropped into the program folder by hand is invisible
/// here and is therefore never removed by an update or a rollback.
/// </summary>
public sealed class InstallManifest
{
    public const string FileName = "install-manifest.json";

    public int SchemaVersion { get; set; } = 1;
    public string Product { get; set; } = UpdateManifest.ProductName;
    public string Version { get; set; } = string.Empty;
    public string BuildCommit { get; set; } = string.Empty;
    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.Now;
    public List<string> Files { get; set; } = [];

    /// <summary>
    /// Path/length/hash of <c>build-info.json</c> as installed. Optional on read: installations made
    /// before this field existed simply have no record, which is reported rather than treated as
    /// corruption.
    /// </summary>
    public InstallManifestFileRecord? BuildInfo { get; set; }

    public static string PathFor(string installRoot) => Path.Combine(installRoot, FileName);

    public static InstallManifest? TryRead(string installRoot)
    {
        try
        {
            string path = PathFor(installRoot);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(path), UpdatePackage.SerializerOptions);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public void Write(string installRoot)
    {
        string path = PathFor(installRoot);
        Directory.CreateDirectory(installRoot);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, UpdatePackage.SerializerOptions));
        File.Move(temp, path, overwrite: true);
    }
}

public enum UpdateActionKind
{
    Write,
    Delete
}

public sealed class UpdatePlanAction
{
    public UpdateActionKind Kind { get; init; }
    public required string RelativePath { get; init; }

    /// <summary>Where the new bytes come from; empty for a delete.</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>Absolute destination under the installation root.</summary>
    public required string TargetPath { get; init; }

    public long Bytes { get; init; }
    public bool TargetExists { get; init; }

    /// <summary>
    /// True when the target is not listed in the previous install manifest and is not identical to
    /// the incoming file. The file is still replaced (an update has to be able to ship a file the
    /// old manifest never knew about), but it is reported so the user is told what changed.
    /// </summary>
    public bool OverwritingUntracked { get; init; }
}

/// <summary>
/// The concrete set of file operations an update performs, computed before anything is touched.
/// Showing this to the admin, and logging it, is what makes the update reviewable rather than magic.
/// </summary>
public sealed class UpdatePlan
{
    public required IReadOnlyList<UpdatePlanAction> Actions { get; init; }
    public required IReadOnlyList<string> Unchanged { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Bytes that are already byte-identical on disk, so the update does not rewrite them.</summary>
    public long UnchangedBytes { get; init; }

    public IEnumerable<UpdatePlanAction> Writes => Actions.Where(a => a.Kind == UpdateActionKind.Write);
    public IEnumerable<UpdatePlanAction> Deletes => Actions.Where(a => a.Kind == UpdateActionKind.Delete);

    public int FilesToWrite => Writes.Count();
    public int FilesToDelete => Deletes.Count();
    public long BytesToWrite => Writes.Sum(a => a.Bytes);

    public bool HasWork => Actions.Count > 0;

    public static UpdatePlan Build(string payloadRoot, UpdateManifest manifest, string installRoot)
    {
        installRoot = Path.GetFullPath(installRoot);
        var actions = new List<UpdatePlanAction>();
        var unchanged = new List<string>();
        var warnings = new List<string>();

        InstallManifest? previous = InstallManifest.TryRead(installRoot);
        var previousFiles = previous is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(previous.Files, StringComparer.OrdinalIgnoreCase);

        if (previous is null)
            warnings.Add("安装目录中没有 install-manifest.json（可能是旧版本安装）。本次更新只覆盖同名文件，不会删除任何文件。");

        var newFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        long unchangedBytes = 0;

        foreach (UpdateFileEntry file in manifest.Files)
        {
            newFiles.Add(file.Path);
            string source = UpdatePath.ResolveWithin(payloadRoot, file.Path);
            string target = UpdatePath.ResolveWithin(installRoot, file.Path);

            bool targetExists = File.Exists(target);
            if (targetExists && FileHasHash(target, file.Sha256))
            {
                unchangedBytes += file.Bytes;
                unchanged.Add(file.Path);
                continue;
            }

            bool untracked = targetExists && !previousFiles.Contains(file.Path);
            if (untracked) warnings.Add($"将覆盖安装目录中未由 LanDisk 记录的文件：{file.Path}");

            actions.Add(new UpdatePlanAction
            {
                Kind = UpdateActionKind.Write,
                RelativePath = file.Path,
                SourcePath = source,
                TargetPath = target,
                Bytes = file.Bytes,
                TargetExists = targetExists,
                OverwritingUntracked = untracked
            });
        }

        // Only files the previous install manifest claims may be removed. A file the user added to
        // the program folder is not ours to delete.
        foreach (string stale in previousFiles.Where(p => !newFiles.Contains(p)).OrderBy(p => p, StringComparer.Ordinal))
        {
            if (string.Equals(stale, InstallManifest.FileName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!UpdatePath.IsSafeRelativePath(stale)) continue;

            string target = UpdatePath.ResolveWithin(installRoot, stale);
            if (!File.Exists(target)) continue;

            actions.Add(new UpdatePlanAction
            {
                Kind = UpdateActionKind.Delete,
                RelativePath = stale,
                TargetPath = target,
                TargetExists = true
            });
        }

        return new UpdatePlan
        {
            Actions = actions,
            Unchanged = unchanged,
            Warnings = warnings,
            UnchangedBytes = unchangedBytes
        };
    }

    private static bool FileHasHash(string path, string expectedSha256)
    {
        try
        {
            return UpdatePackage.HashFile(path).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
