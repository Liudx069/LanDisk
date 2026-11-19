using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanDisk.Core.Configuration;

public sealed class AppSettings
{
    /// <summary>Loopback management + device port for plain HTTP.</summary>
    public int HttpPort { get; set; } = 8891;

    /// <summary>HTTPS port used when TLS is enabled.</summary>
    public int HttpsPort { get; set; } = 8892;

    /// <summary>When false (default) the process only listens on 127.0.0.1 / ::1.</summary>
    public bool LanEnabled { get; set; }

    /// <summary>Specific local IPv4 address to bind for LAN access; empty means "all private addresses".</summary>
    public string LanBindAddress { get; set; } = string.Empty;

    public bool HttpsEnabled { get; set; }

    /// <summary>Unencrypted LAN HTTP compatibility mode. Off by default and deliberately noisy.</summary>
    public bool HttpCompatModeEnabled { get; set; }

    /// <summary>Optional extra CIDR restriction for LAN clients.</summary>
    public string AllowedCidr { get; set; } = string.Empty;

    /// <summary>Maximum size of a single uploaded chunk accepted by the server.</summary>
    public int MaxChunkBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Maximum number of concurrently open upload sessions per device.</summary>
    public int MaxConcurrentUploadsPerDevice { get; set; } = 4;

    /// <summary>Idle timeout after which an upload session is discarded.</summary>
    public int UploadIdleTimeoutMinutes { get; set; } = 30;

    /// <summary>Cap for the per-volume staging area in bytes; 0 means "derive from free space".</summary>
    public long StagingQuotaBytes { get; set; }

    /// <summary>Directory listing page size.</summary>
    public int ListingPageSize { get; set; } = 200;

    /// <summary>Maximum number of bytes returned by the text preview endpoint.</summary>
    public int TextPreviewMaxBytes { get; set; } = 512 * 1024;

    /// <summary>Maximum size of an image that the server will stream for inline preview.</summary>
    public long InlinePreviewMaxBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Diagnostic log retention in entries.</summary>
    public int AuditRetentionEntries { get; set; } = 5000;

    /// <summary>Longest edge of a generated thumbnail, in pixels.</summary>
    public int ThumbnailMaxEdge { get; set; } = 320;

    /// <summary>On-disk thumbnail cache budget in bytes; the oldest files are evicted first.</summary>
    public long ThumbnailCacheBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>Largest image the server will decode to build a thumbnail.</summary>
    public long ThumbnailSourceMaxBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Upper bound on the total size of one batch ZIP download.</summary>
    public long BatchZipMaxBytes { get; set; } = 32L * 1024 * 1024 * 1024;

    /// <summary>Upper bound on the number of entries inside one batch ZIP download.</summary>
    public int BatchZipMaxEntries { get; set; } = 20_000;

    /// <summary>Number of items a single batch delete request may carry.</summary>
    public int BatchDeleteMaxItems { get; set; } = 500;

    /// <summary>Upper bound on directory entries inspected by one recursive search request.</summary>
    public int SearchMaxScanEntries { get; set; } = 200_000;

    /// <summary>Upper bound on matches kept in memory for one recursive search request.</summary>
    public int SearchMaxResults { get; set; } = 20_000;

    /// <summary>Window, in days, used by the "recent" quick view.</summary>
    public int RecentWindowDays { get; set; } = 30;

    /// <summary>
    /// Hard cap on how many candidate files a single share may contribute to "recent".
    /// Reaching it must be reported through <c>truncated</c> with an explicit reason; it is never
    /// allowed to silently drop candidates.
    /// </summary>
    public int RecentMaxPerShare { get; set; } = 5000;

    /// <summary>
    /// Hard cap on directory entries a single share may be scanned for "recent". Reaching it is
    /// reported as <c>scan_limit</c> so the client never presents a partial result as complete.
    /// </summary>
    public int RecentMaxScanEntries { get; set; } = 200_000;

    public bool VerboseLogging { get; set; }
}

/// <summary>Filesystem layout owned by LanDisk. Nothing here is ever exposed through a share.</summary>
public sealed class AppPaths
{
    public required string DataRoot { get; init; }

    public string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public string SharesFile => Path.Combine(DataRoot, "shares.json");
    public string DevicesFile => Path.Combine(DataRoot, "devices.json");
    public string RecycleIndexFile => Path.Combine(DataRoot, "recycle-index.json");
    public string TransferFile => Path.Combine(DataRoot, "transfers.json");
    public string FavoritesFile => Path.Combine(DataRoot, "favorites.json");
    public string PreferencesFile => Path.Combine(DataRoot, "preferences.json");
    public string SecretsDir => Path.Combine(DataRoot, "secrets");
    public string AdminTokenFile => Path.Combine(SecretsDir, "admin-token.txt");
    public string TlsDir => Path.Combine(DataRoot, "tls");

    /// <summary>Legacy single self-signed server certificate (1.3.0 and earlier). Read for migration only.</summary>
    public string TlsPfxFile => Path.Combine(TlsDir, "landisk.pfx");

    public string TlsPasswordFile => Path.Combine(SecretsDir, "tls-password.txt");

    /// <summary>Legacy public certificate file. In 1.4.0 this holds the local CA certificate.</summary>
    public string TlsCerFile => Path.Combine(TlsDir, "landisk-root.cer");

    /// <summary>Persistent installation identity (install id + stable <c>.local</c> name).</summary>
    public string InstallIdentityFile => Path.Combine(DataRoot, InstallIdentity.FileName);

    /// <summary>Local root CA private key, sealed with DPAPI for the current Windows user.</summary>
    public string CaProtectedFile => Path.Combine(TlsDir, "landisk-ca.bin");

    /// <summary>Local root CA public certificate, safe to hand to an iPhone.</summary>
    public string CaCerFile => Path.Combine(TlsDir, "landisk-ca.cer");

    /// <summary>Server leaf private key, sealed with DPAPI for the current Windows user.</summary>
    public string LeafProtectedFile => Path.Combine(TlsDir, "landisk-leaf.bin");

    /// <summary>Server leaf public certificate, for diagnostics only.</summary>
    public string LeafCerFile => Path.Combine(TlsDir, "landisk-leaf.cer");

    public string LogsDir => Path.Combine(DataRoot, "logs");
    public string AuditFile => Path.Combine(LogsDir, "audit.jsonl");
    public string ThumbnailDir => Path.Combine(DataRoot, "thumbs");
    public string FallbackStoreDir => Path.Combine(DataRoot, "store");
    public string AdminUrlFile => Path.Combine(DataRoot, "admin-url.txt");
    public string InstanceLockFile => Path.Combine(DataRoot, "instance.lock");

    /// <summary>Directory name created at the root of a volume that hosts shares.</summary>
    public const string VolumeStoreName = ".landisk-data";

    public static AppPaths ForDataRoot(string dataRoot)
    {
        string full = Path.GetFullPath(dataRoot);
        Directory.CreateDirectory(full);
        var paths = new AppPaths { DataRoot = full };
        Directory.CreateDirectory(paths.SecretsDir);
        Directory.CreateDirectory(paths.TlsDir);
        Directory.CreateDirectory(paths.LogsDir);
        Directory.CreateDirectory(paths.FallbackStoreDir);
        return paths;
    }

    public static string DefaultDataRoot()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "LanDisk");
    }

    /// <summary>The per-volume internal store root for a given mount point.</summary>
    public static string VolumeStoreRoot(string volumeMountPoint)
        => Path.Combine(volumeMountPoint, VolumeStoreName);

    /// <summary>Internal store roots that must never be listable or reachable through any share.</summary>
    public IEnumerable<string> InternalRoots()
    {
        yield return DataRoot;
        yield return FallbackStoreDir;

        // The application's own installation directory must never be shareable or uploadable:
        // a file dropped into the static asset folder would otherwise be served back as active
        // content from the application origin.
        string baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDir)) yield return TrimSeparator(Path.GetFullPath(baseDir));

        foreach (string volume in EnumerateVolumeRoots())
        {
            yield return VolumeStoreRoot(volume);
        }
    }

    private static string TrimSeparator(string path)
        => path.Length > 3 ? path.TrimEnd('\\', '/') : path;

    private static IEnumerable<string> EnumerateVolumeRoots()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { yield break; }

        foreach (DriveInfo drive in drives)
        {
            string? root = null;
            try
            {
                if (drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network)
                    root = drive.RootDirectory.FullName;
            }
            catch { }
            if (!string.IsNullOrEmpty(root)) yield return root!;
        }
    }
}

public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static T Read<T>(string path, Func<T> factory) where T : class
    {
        try
        {
            if (!File.Exists(path)) return factory();
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return factory();
            return JsonSerializer.Deserialize<T>(json, Options) ?? factory();
        }
        catch
        {
            // A corrupt state file must not take the whole application down; start from defaults
            // and keep the damaged file for the operator to inspect.
            try { File.Move(path, path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); } catch { }
            return factory();
        }
    }

    public static void Write<T>(string path, T value)
    {
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        string json = JsonSerializer.Serialize(value, Options);

        // 唯一临时名。原先固定用 path + ".tmp"：并发写入会互相踩踏，崩溃后还会留下陈旧 .tmp，
        // 而陈旧 .tmp 又会被后来的写入当成自己的中间文件覆盖一半。
        string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            File.WriteAllText(tmp, json);

            // Windows 上目标文件可能被索引器/杀毒/另一个进程瞬间持有，此时
            // File.Move(tmp, path, overwrite: true) 会抛 UnauthorizedAccessException（实测复现，
            // 约 5% 概率）。这类占用是**瞬时**的，重试即可恢复；不重试会让一次普通的状态写入
            // （设置、传输记录、设备表等）无谓失败。
            const int attempts = 5;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(tmp, path, true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < attempts)
                {
                    Thread.Sleep(20 * attempt);
                }
            }
        }
        finally
        {
            // 成功移动后 tmp 已不存在；失败路径则清掉，不留垃圾。
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    private static readonly JsonSerializerOptions LineOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string SerializeOneLine<T>(T value) => JsonSerializer.Serialize(value, LineOptions);

    public static T? TryParseOneLine<T>(string line) where T : class
    {
        try
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            return JsonSerializer.Deserialize<T>(line, LineOptions);
        }
        catch
        {
            return null;
        }
    }
}
