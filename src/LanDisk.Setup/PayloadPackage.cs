using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace LanDisk.Setup;

public sealed record PayloadInfo(
    string Version,
    string Commit,
    string BuildId,
    int FileCount,
    long TotalBytes,
    string Sha256,
    bool DigestVerified);

public sealed record InstallResult(
    bool Ok,
    int FilesWritten,
    int FilesRemoved,
    int FilesUnchanged,
    long BytesWritten,
    string? Message,
    List<string> Failures);

/// <summary>
/// The verified release payload and the code that puts it on disk.
///
/// <para>The payload is the same archive that was tested as <c>LanDisk-win-x64.zip</c>, carried inside
/// the setup executable. Before a single byte is written the installer checks the archive against the
/// digest recorded at build time, then extracts into a staging directory and confirms that what came
/// out is exactly what <c>install-manifest.json</c> says should be there — including the
/// <c>buildInfo</c> record of <c>build-info.json</c>. Only after all of that does it touch the
/// installation directory, and it keeps a backup of anything it replaces so an interrupted run can
/// put things back.</para>
///
/// <para>This is deliberately not "unzip over the folder". A half-replaced installation is far worse
/// than a failed install, because it looks like it worked.</para>
/// </summary>
public sealed class PayloadPackage : IDisposable
{
    private readonly string _zipPath;
    private readonly bool _ownsFile;

    private PayloadPackage(string zipPath, bool ownsFile, PayloadInfo info, IReadOnlyList<string> files, string buildInfoSha256, long buildInfoBytes)
    {
        _zipPath = zipPath;
        _ownsFile = ownsFile;
        Info = info;
        Files = files;
        BuildInfoSha256 = buildInfoSha256;
        BuildInfoBytes = buildInfoBytes;
    }

    public PayloadInfo Info { get; }

    /// <summary>Program files the payload declares, relative paths with forward slashes.</summary>
    public IReadOnlyList<string> Files { get; }

    public string BuildInfoSha256 { get; }

    public long BuildInfoBytes { get; }

    /// <summary>Reads the payload embedded in this executable, or null when it was built without one.</summary>
    public static PayloadPackage? FromEmbedded()
    {
        Assembly assembly = typeof(PayloadPackage).Assembly;
        using Stream? resource = assembly.GetManifestResourceStream(SetupLayout.PayloadResourceName);
        if (resource is null) return null;

        string temp = Path.Combine(Path.GetTempPath(), "landisk-payload-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
        using (var file = File.Create(temp)) resource.CopyTo(file);

        string? expected = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "LanDiskPayloadSha256")?.Value;

        return Open(temp, ownsFile: true, expectedSha256: expected);
    }

    /// <summary>Reads a payload archive from disk. Used by tests and by the acceptance scripts.</summary>
    public static PayloadPackage Open(string zipPath, bool ownsFile = false, string? expectedSha256 = null)
    {
        if (!File.Exists(zipPath)) throw new FileNotFoundException("找不到安装载荷。", zipPath);

        string actual = Sha256OfFile(zipPath);
        bool digestVerified = false;
        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"安装载荷校验失败：期望 {expectedSha256}，实际 {actual}。安装已中止，磁盘未被改动。");
            }
            digestVerified = true;
        }

        using var archive = ZipFile.OpenRead(zipPath);
        string prefix = DetectBasePrefix(archive);
        using JsonDocument manifest = ReadJson(archive, prefix, SetupLayout.InstallManifestName);
        using JsonDocument buildInfo = ReadJson(archive, prefix, SetupLayout.BuildInfoName);

        var files = new List<string>();
        if (manifest.RootElement.TryGetProperty("files", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement entry in list.EnumerateArray())
            {
                string? name = entry.GetString();
                if (!string.IsNullOrWhiteSpace(name)) files.Add(name.Replace('\\', '/'));
            }
        }

        if (files.Count == 0) throw new InvalidDataException("安装载荷里的 install-manifest.json 没有文件清单。");

        string version = buildInfo.RootElement.TryGetProperty("version", out JsonElement v) ? v.GetString() ?? "" : "";
        string commit = buildInfo.RootElement.TryGetProperty("commit", out JsonElement c) ? c.GetString() ?? "" : "";
        string buildId = buildInfo.RootElement.TryGetProperty("buildId", out JsonElement b) ? b.GetString() ?? "" : "";

        string buildInfoSha = Sha256OfEntry(archive, prefix + SetupLayout.BuildInfoName)
                              ?? throw new InvalidDataException("安装载荷里缺少 build-info.json。");

        long buildInfoBytes = EntryLength(archive, prefix + SetupLayout.BuildInfoName);

        // Cross-check the manifest's own record of build-info.json (the invariant introduced in task 06)
        // before believing either file.
        if (manifest.RootElement.TryGetProperty("buildInfo", out JsonElement record))
        {
            string? recordedFile = record.TryGetProperty("path", out JsonElement p) ? p.GetString() : null;
            string? recordedSha = record.TryGetProperty("sha256", out JsonElement s) ? s.GetString() : null;
            long recordedBytes = record.TryGetProperty("bytes", out JsonElement n) ? n.GetInt64() : -1;

            if (!string.Equals(recordedFile, SetupLayout.BuildInfoName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("install-manifest.json 的 buildInfo.path 不是 build-info.json。");
            if (!string.Equals(recordedSha, buildInfoSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("install-manifest.json 记录的 build-info.json 哈希与载荷内容不一致。");
            if (recordedBytes != buildInfoBytes)
                throw new InvalidDataException("install-manifest.json 记录的 build-info.json 长度与载荷内容不一致。");
        }
        else
        {
            throw new InvalidDataException("install-manifest.json 没有 buildInfo 记录（发布链缺陷，拒绝安装）。");
        }

        long total = 0;
        foreach (string name in files)
        {
            total += EntryLength(archive, prefix + name);
        }

        var info = new PayloadInfo(version, commit, buildId, files.Count, total, actual, digestVerified);
        return new PayloadPackage(zipPath, ownsFile, info, files, buildInfoSha, buildInfoBytes);
    }

    // ------------------------------------------------------------------ install

    /// <summary>
    /// Installs (or repairs) the payload into <paramref name="targetDir"/>.
    ///
    /// <paramref name="installerCopySource"/> is the running setup executable; a copy is placed next
    /// to the program so the uninstall entry keeps working after the original download is deleted.
    /// </summary>
    public InstallResult InstallTo(string targetDir, string? installerCopySource, Action<string>? log = null)
    {
        targetDir = Path.GetFullPath(targetDir);
        var failures = new List<string>();
        string staging = targetDir + ".staging-" + Guid.NewGuid().ToString("N")[..6];
        string backup = targetDir + ".backup-" + Guid.NewGuid().ToString("N")[..6];

        int written = 0, removed = 0, unchanged = 0;
        long bytes = 0;

        try
        {
            log?.Invoke("解压到暂存目录并校验…");
            Directory.CreateDirectory(staging);
            ExtractTo(staging);

            // 1. What came out of the archive must be exactly what the manifest promised.
            List<string> problems = VerifyStaging(staging);
            if (problems.Count > 0)
            {
                return new InstallResult(false, 0, 0, 0, 0,
                    "安装载荷自检失败，安装目录未被改动。", problems);
            }

            // 2. Back up whatever is about to be replaced or removed.
            HashSet<string> previous = ReadPreviousManifest(targetDir);
            Directory.CreateDirectory(backup);
            foreach (string relative in previous)
            {
                if (SetupLayout.InstallerOwnedFiles.Contains(relative, StringComparer.OrdinalIgnoreCase)) continue;
                string existing = Path.Combine(targetDir, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(existing)) continue;
                string saved = Path.Combine(backup, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                File.Copy(existing, saved, overwrite: true);
            }

            // 3. Replace program files.
            Directory.CreateDirectory(targetDir);
            foreach (string relative in Files)
            {
                string source = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                string destination = Path.Combine(targetDir, relative.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(destination) && SameContent(source, destination))
                {
                    unchanged++;
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                string temp = destination + ".new";
                File.Copy(source, temp, overwrite: true);
                File.Move(temp, destination, overwrite: true);
                written++;
                bytes += new FileInfo(destination).Length;
            }

            // 4. Remove files the previous installation owned and this one does not. Files the user
            //    added to the program folder are not in that set and are therefore never touched.
            var current = new HashSet<string>(Files, StringComparer.OrdinalIgnoreCase);
            foreach (string stale in previous)
            {
                if (current.Contains(stale)) continue;
                if (SetupLayout.InstallerOwnedFiles.Contains(stale, StringComparer.OrdinalIgnoreCase)) continue;
                if (!IsSafeRelative(stale)) continue;

                string victim = Path.Combine(targetDir, stale.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(victim)) continue;
                File.Delete(victim);
                removed++;
            }

            // 5. The installer's own copy, so "uninstall" keeps working from the Start menu entry.
            if (!string.IsNullOrEmpty(installerCopySource) && File.Exists(installerCopySource))
            {
                string copy = SetupLayout.SetupCopyPathFor(targetDir);
                if (!SameContent(installerCopySource, copy))
                {
                    string temp = copy + ".new";
                    File.Copy(installerCopySource, temp, overwrite: true);
                    File.Move(temp, copy, overwrite: true);
                }
            }

            // 6. The install manifest is written last: until it lands, the previous manifest still
            //    describes the directory, so an interrupted install stays recoverable.
            WriteManifest(targetDir);

            TryDelete(staging);
            TryDelete(backup);

            log?.Invoke($"已安装 {written} 个文件（{unchanged} 个未变化，{removed} 个过期文件已清理）。");
            return new InstallResult(true, written, removed, unchanged, bytes,
                $"已安装 {Info.Version}（提交 {Short(Info.Commit)}）。", failures);
        }
        catch (Exception ex)
        {
            failures.Add(ex.Message);
            log?.Invoke("安装失败，正在回滚：" + ex.Message);
            Rollback(targetDir, backup, failures);
            TryDelete(staging);
            return new InstallResult(false, written, removed, unchanged, bytes,
                "安装未完成，已恢复到安装前的状态。", failures);
        }
    }

    private void ExtractTo(string staging)
    {
        using var archive = ZipFile.OpenRead(_zipPath);
        string prefix = DetectBasePrefix(archive);

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal)) continue;

            string relative = entry.FullName[prefix.Length..].Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(relative)) continue;
            if (!IsSafeRelative(relative)) throw new InvalidDataException("安装载荷包含非法路径：" + relative);

            string destination = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    private List<string> VerifyStaging(string staging)
    {
        var problems = new List<string>();

        foreach (string relative in Files)
        {
            string path = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) problems.Add("载荷缺少声明过的文件：" + relative);
        }

        string buildInfo = Path.Combine(staging, SetupLayout.BuildInfoName);
        if (!File.Exists(buildInfo))
        {
            problems.Add("载荷缺少 build-info.json。");
            return problems;
        }

        long length = new FileInfo(buildInfo).Length;
        if (length != BuildInfoBytes) problems.Add("载荷中的 build-info.json 长度与清单记录不一致。");

        string sha = Sha256OfFile(buildInfo);
        if (!sha.Equals(BuildInfoSha256, StringComparison.OrdinalIgnoreCase))
            problems.Add("载荷中的 build-info.json 哈希与清单记录不一致。");

        string manifest = Path.Combine(staging, SetupLayout.InstallManifestName);
        if (!File.Exists(manifest)) problems.Add("载荷缺少 install-manifest.json。");

        return problems;
    }

    private void WriteManifest(string targetDir)
    {
        var files = new List<string>(Files);
        files.Sort(StringComparer.Ordinal);

        string buildInfoPath = SetupLayout.BuildInfoPathFor(targetDir);
        var manifest = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["product"] = SetupLayout.ProductName,
            ["version"] = Info.Version,
            ["buildCommit"] = Info.Commit,
            ["installedAt"] = DateTimeOffset.Now.ToString("o"),
            ["files"] = files,
            ["buildInfo"] = new Dictionary<string, object?>
            {
                ["path"] = SetupLayout.BuildInfoName,
                ["bytes"] = new FileInfo(buildInfoPath).Length,
                ["sha256"] = Sha256OfFile(buildInfoPath)
            }
        };

        string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        string path = SetupLayout.ManifestPathFor(targetDir);
        string temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }

    private static HashSet<string> ReadPreviousManifest(string targetDir)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string path = SetupLayout.ManifestPathFor(targetDir);
        if (!File.Exists(path)) return result;

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("files", out JsonElement list)) return result;
            if (list.ValueKind != JsonValueKind.Array) return result;

            foreach (JsonElement entry in list.EnumerateArray())
            {
                string? name = entry.GetString();
                if (!string.IsNullOrWhiteSpace(name)) result.Add(name.Replace('\\', '/'));
            }
        }
        catch
        {
            // A damaged manifest means "we do not know what we own", which must mean "delete nothing".
        }

        return result;
    }

    private static void Rollback(string targetDir, string backup, List<string> failures)
    {
        if (!Directory.Exists(backup)) return;

        string backupRoot = Path.GetFullPath(backup);
        foreach (string file in Directory.EnumerateFiles(backupRoot, "*", SearchOption.AllDirectories))
        {
            try
            {
                string relative = Path.GetRelativePath(backupRoot, file);
                string destination = Path.Combine(targetDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
            }
            catch (Exception ex)
            {
                failures.Add("恢复失败 " + file + "：" + ex.Message);
            }
        }

        TryDelete(backup);
    }

    // ------------------------------------------------------------------ helpers

    public static bool IsSafeRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return false;
        if (relative.StartsWith('/') || relative.StartsWith('\\')) return false;
        if (relative.Length > 1 && relative[1] == ':') return false;
        if (relative.Split('/', '\\').Any(part => part == "..")) return false;
        return true;
    }

    private static bool SameContent(string a, string b)
    {
        try
        {
            var left = new FileInfo(a);
            var right = new FileInfo(b);
            if (!right.Exists || left.Length != right.Length) return false;
            return Sha256OfFile(a).Equals(Sha256OfFile(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static string Sha256OfFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string? Sha256OfEntry(ZipArchive archive, string name)
    {
        ZipArchiveEntry? entry = archive.GetEntry(name);
        if (entry is null) return null;
        using Stream stream = entry.Open();
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static long EntryLength(ZipArchive archive, string name)
    {
        ZipArchiveEntry? entry = archive.GetEntry(name);
        if (entry is null) throw new InvalidDataException("载荷缺少文件：" + name);
        return entry.Length;
    }

    private static JsonDocument ReadJson(ZipArchive archive, string prefix, string name)
    {
        ZipArchiveEntry? entry = archive.GetEntry(prefix + name);
        if (entry is null) throw new InvalidDataException("载荷缺少 " + name + "。");

        using Stream stream = entry.Open();
        return JsonDocument.Parse(stream);
    }

    /// <summary>
    /// The release archive wraps everything in a single top-level folder. Detect it rather than
    /// hard-coding the name, so renaming that folder cannot silently install nothing.
    /// </summary>
    private static string DetectBasePrefix(ZipArchive archive)
    {
        string? candidate = null;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName;
            int slash = name.IndexOf('/');
            if (slash <= 0) return string.Empty;

            string head = name[..(slash + 1)];
            if (candidate is null) candidate = head;
            else if (!candidate.Equals(head, StringComparison.Ordinal)) return string.Empty;
        }

        return candidate ?? string.Empty;
    }

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
    }

    public static string Short(string commit) => commit.Length >= 8 ? commit[..8] : commit;

    public void Dispose()
    {
        if (!_ownsFile) return;
        try { if (File.Exists(_zipPath)) File.Delete(_zipPath); } catch { }
    }
}
