using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace LanDisk.Core.Configuration;

/// <summary>
/// The identity an installation keeps for its whole life: a random installation id and the stable
/// <c>.local</c> name derived from it.
///
/// <para>The name exists so that a phone can bookmark <c>https://landisk-xxxxxxxx.local:8892</c>
/// instead of an address that DHCP may hand to somebody else tomorrow. Two properties matter and are
/// both deliberate: the label is generated from a random number rather than from anything about the
/// machine or its owner (no Windows user name, no real name, no serial number), and it is written
/// once and then reused verbatim — a name that changed between restarts would defeat the purpose.</para>
///
/// <para>A name collision with another device on the same link is resolved by moving to the next
/// attempt suffix and persisting that, once. It is never re-rolled on every start.</para>
/// </summary>
public sealed class InstallIdentity
{
    public const string FileName = "install.json";
    public const string LabelPrefix = "landisk-";
    public const string DomainSuffix = ".local";
    public const int MaxLabelLength = 63;

    public int SchemaVersion { get; set; } = 1;
    public string Product { get; set; } = "LanDisk";

    /// <summary>Eight lowercase hex characters, generated once per installation.</summary>
    public string InstallId { get; set; } = string.Empty;

    /// <summary>The host label without the <c>.local</c> domain, e.g. <c>landisk-a1b2c3d4</c>.</summary>
    public string HostLabel { get; set; } = string.Empty;

    /// <summary>0 for the first choice; each collision moves to the next value and stays there.</summary>
    public int NameAttempt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>
    /// True once the one-time move from the 1.3.0 self-signed leaf to the local CA hierarchy has been
    /// recorded. Kept so the migration is performed exactly once and can be reported honestly.
    /// </summary>
    public bool CertificateMigrationDone { get; set; }

    public DateTimeOffset? CertificateMigratedAt { get; set; }

    [JsonIgnore]
    public string HostName => HostLabel + DomainSuffix;

    public string HttpsOrigin(int httpsPort) => $"https://{HostName}:{httpsPort}";

    public static string PathFor(AppPaths paths) => Path.Combine(paths.DataRoot, FileName);

    public static string NewInstallId()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>The host label for a given installation id and attempt.</summary>
    public static string LabelFor(string installId, int attempt)
        => attempt <= 0 ? LabelPrefix + installId : LabelPrefix + installId + "-" + (attempt + 1).ToString();

    /// <summary>
    /// Host labels are limited to what RFC 6762 and DNS allow. Generated labels always satisfy this;
    /// the check exists so a hand-edited <c>install.json</c> cannot inject a name that breaks mDNS.
    /// </summary>
    public static bool IsValidLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return false;
        if (label.Length > MaxLabelLength) return false;
        if (label[0] == '-' || label[^1] == '-') return false;

        foreach (char c in label)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
            if (!ok) return false;
        }

        return true;
    }

    public static InstallIdentity? TryRead(AppPaths paths)
    {
        try
        {
            string path = PathFor(paths);
            if (!File.Exists(path)) return null;

            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;

            InstallIdentity? identity = System.Text.Json.JsonSerializer.Deserialize<InstallIdentity>(json, JsonStore.Options);
            if (identity is null) return null;
            if (string.IsNullOrWhiteSpace(identity.InstallId)) return null;
            if (!IsValidLabel(identity.HostLabel)) return null;
            return identity;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the installation identity, creating it on first run.
    ///
    /// <paramref name="isLabelTaken"/> is consulted before a freshly generated label is accepted, so
    /// the caller can reject a name that is already answered on the local link. The decision is made
    /// once and persisted: later starts reuse the stored label without probing again.
    /// </summary>
    public static InstallIdentity Ensure(AppPaths paths, Func<string, bool>? isLabelTaken = null, Action<string>? log = null)
    {
        InstallIdentity? existing = TryRead(paths);
        if (existing is not null) return existing;

        var identity = new InstallIdentity
        {
            InstallId = NewInstallId(),
            CreatedAt = DateTimeOffset.Now,
            NameAttempt = 0
        };
        identity.HostLabel = LabelFor(identity.InstallId, 0);

        if (isLabelTaken is not null)
        {
            int attempt = 0;
            while (attempt < 8 && isLabelTaken(identity.HostLabel))
            {
                attempt++;
                identity.NameAttempt = attempt;
                identity.HostLabel = LabelFor(identity.InstallId, attempt);
            }
        }

        identity.Write(paths);
        log?.Invoke($"已生成安装标识：{identity.InstallId}，稳定局域网名 {identity.HostName}");
        return identity;
    }

    /// <summary>
    /// Moves to the next persistent name after a collision was detected at runtime. Returns true when
    /// a new name was chosen.
    /// </summary>
    public bool AdoptNextName(AppPaths paths, Action<string>? log = null)
    {
        if (NameAttempt >= 7) return false;

        string previous = HostName;
        NameAttempt++;
        HostLabel = LabelFor(InstallId, NameAttempt);
        Write(paths);
        log?.Invoke($"稳定局域网名与其它设备冲突，已从 {previous} 改为 {HostName}（此后保持不变）");
        return true;
    }

    public void MarkCertificateMigration(DateTimeOffset at)
    {
        CertificateMigrationDone = true;
        CertificateMigratedAt = at;
    }

    public void Write(AppPaths paths)
    {
        Directory.CreateDirectory(paths.DataRoot);
        JsonStore.Write(PathFor(paths), this);
    }
}
