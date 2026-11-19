using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LanDisk.Core.Configuration;
using LanDisk.Core.Interop;

namespace LanDisk.Core.Networking;

/// <summary>Why the server leaf certificate was replaced, in terms a person can act on.</summary>
public enum LeafReissueReason
{
    None = 0,
    Missing,
    NotIssuedByLocalCa,
    ExpiringSoon,
    AddressesChanged,
    PrivateKeyMissing,
    Unreadable
}

/// <summary>The server certificate plus the authority it chains to.</summary>
public sealed class ServerCertificate
{
    public required X509Certificate2 Leaf { get; init; }
    public required X509Certificate2 Ca { get; init; }
    public required LeafReissueReason Reason { get; init; }

    public bool Reissued => Reason != LeafReissueReason.None;
}

/// <summary>
/// LanDisk's own small certificate authority.
///
/// <para><b>Why a CA at all.</b> A phone that trusts one long-lived root certificate keeps trusting
/// LanDisk when the LAN address changes, when the leaf is renewed and when the product is updated.
/// The 1.3.0 design instead pinned a three-year self-signed leaf to whatever DHCP address the machine
/// had on the day it was installed, so moving to a different network meant re-doing the whole trust
/// ritual. This type separates the two roles: a root that is created once and never rotates, and a
/// short-lived leaf that is re-issued whenever the addresses it must cover change.</para>
///
/// <para><b>Key handling.</b> Both private keys live in files sealed with DPAPI for the current
/// Windows user (<see cref="Dpapi"/>). The root key is never written anywhere else: not into the
/// release, the source package, the review bundle, the web UI, the QR code or the log. Only the root
/// <i>public</i> certificate is ever exported, because that is the object a phone has to trust.</para>
///
/// <para>Rotation is deliberately narrow: the root changes only when it has expired, when its private
/// key is unusable, or when the user explicitly asks to reset it — never because of a version update,
/// an address change or a leaf renewal.</para>
/// </summary>
public sealed class LocalCertificateAuthority
{
    /// <summary>Root lifetime. Long enough that "trust this once" really is once.</summary>
    public static readonly TimeSpan CaLifetime = TimeSpan.FromDays(3650);

    /// <summary>
    /// Leaf lifetime. Kept under 398 days so the leaf stays valid under the CA/Browser-Forum style
    /// maximum that Safari applies, even though a private root is not bound by it.
    /// </summary>
    public static readonly TimeSpan LeafLifetime = TimeSpan.FromDays(397);

    /// <summary>Renew this long before the leaf expires, so a renewal never races an expiry.</summary>
    public static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    public const string CaCommonNamePrefix = "LanDisk Local CA ";

    private readonly AppPaths _paths;
    private readonly Action<string>? _log;

    public LocalCertificateAuthority(AppPaths paths, Action<string>? log = null)
    {
        _paths = paths;
        _log = log;
    }

    public string? LastError { get; private set; }

    /// <summary>True when a legacy 1.3.0 self-signed certificate is still on disk.</summary>
    public bool LegacyCertificatePresent => File.Exists(_paths.TlsPfxFile);

    // ------------------------------------------------------------------ root CA

    public X509Certificate2? TryLoadCa()
    {
        try
        {
            if (!File.Exists(_paths.CaProtectedFile)) return null;

            byte[] sealedPfx = File.ReadAllBytes(_paths.CaProtectedFile);
            byte[] pfx = Dpapi.Unprotect(sealedPfx);
            var ca = X509CertificateLoader.LoadPkcs12(pfx, null,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);

            if (!ca.HasPrivateKey)
            {
                ca.Dispose();
                LastError = "本地 CA 文件里没有私钥。";
                return null;
            }

            if (ca.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddDays(1))
            {
                ca.Dispose();
                LastError = "本地 CA 已过期。";
                return null;
            }

            return ca;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    /// <summary>Loads the root CA, creating it if this is the first run. Never rotates an existing one.</summary>
    public X509Certificate2 EnsureCa(string installId)
    {
        X509Certificate2? existing = TryLoadCa();
        if (existing is not null)
        {
            // Keep the exported public certificate in step with the authority on disk.
            TryWriteCer(existing, _paths.CaCerFile);
            return existing;
        }

        X509Certificate2 created = CreateCa(installId);
        return created;
    }

    public X509Certificate2 CreateCa(string installId)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var request = new CertificateRequest(
            new X500DistinguishedName("CN=" + CaCommonNamePrefix + installId),
            key,
            HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        DateTimeOffset notAfter = DateTimeOffset.UtcNow.Add(CaLifetime);

        using X509Certificate2 generated = request.CreateSelfSigned(notBefore, notAfter);

        // Reload through PKCS#12 so the resulting object owns a usable private key on every run,
        // then seal those exact bytes with DPAPI.
        byte[] pfx = generated.Export(X509ContentType.Pfx);
        WriteSealed(_paths.CaProtectedFile, pfx);
        TryWriteCer(generated, _paths.CaCerFile);

        var loaded = X509CertificateLoader.LoadPkcs12(pfx, null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);

        _log?.Invoke($"已创建本地根 CA（{CaCommonNamePrefix}{installId}，有效期至 {generated.NotAfter:yyyy-MM-dd}）");
        return loaded;
    }

    /// <summary>
    /// Replaces the root CA. Only reachable from an explicit user action, because every phone that
    /// trusted the old root has to trust the new one by hand afterwards.
    /// </summary>
    public X509Certificate2 ResetCa(string installId, bool keepBackup = true)
    {
        if (keepBackup && File.Exists(_paths.CaProtectedFile))
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try { File.Copy(_paths.CaProtectedFile, _paths.CaProtectedFile + ".reset-" + stamp, true); }
            catch { }
        }

        SafelyDelete(_paths.CaProtectedFile);
        SafelyDelete(_paths.CaCerFile);
        SafelyDelete(_paths.LeafProtectedFile);
        SafelyDelete(_paths.LeafCerFile);

        _log?.Invoke("用户请求重置安全证书：旧的本地 CA 已被替换，手机需要重新信任新的 CA。");
        return CreateCa(installId);
    }

    // ------------------------------------------------------------------ leaf

    /// <summary>
    /// Returns the server certificate for the given stable name and LAN addresses, re-issuing the leaf
    /// when the one on disk cannot serve them. The root CA is never touched here.
    /// </summary>
    public ServerCertificate EnsureLeaf(
        X509Certificate2 ca,
        string stableHostName,
        IReadOnlyCollection<string> dnsNames,
        IReadOnlyCollection<string> ipAddresses)
    {
        List<string> requiredDns = Normalise([stableHostName, .. dnsNames]);
        List<IPAddress> requiredIps = NormaliseIps(ipAddresses);

        (X509Certificate2? existing, LeafReissueReason reason) = TryLoadLeaf(ca, requiredDns, requiredIps);
        if (existing is not null) return new ServerCertificate { Leaf = existing, Ca = ca, Reason = LeafReissueReason.None };

        X509Certificate2 issued = IssueLeaf(ca, stableHostName, requiredDns, requiredIps);
        _log?.Invoke($"已由本地 CA 签发服务器证书（{DescribeReason(reason)}，SAN：{string.Join(", ", requiredDns.Concat(requiredIps.Select(i => i.ToString())))}）");

        return new ServerCertificate { Leaf = issued, Ca = ca, Reason = reason };
    }

    public X509Certificate2 IssueLeaf(
        X509Certificate2 ca,
        string commonName,
        IReadOnlyCollection<string> dnsNames,
        IReadOnlyCollection<IPAddress> ipAddresses)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var request = new CertificateRequest(
            new X500DistinguishedName("CN=" + commonName),
            key,
            HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], false)); // serverAuth

        var san = new SubjectAlternativeNameBuilder();
        foreach (string name in dnsNames) san.AddDnsName(name);
        foreach (IPAddress ip in ipAddresses) san.AddIpAddress(ip);
        request.CertificateExtensions.Add(san.Build());

        X509SubjectKeyIdentifierExtension? caSki = ca.Extensions
            .OfType<X509SubjectKeyIdentifierExtension>()
            .FirstOrDefault();
        if (caSki is not null)
        {
            request.CertificateExtensions.Add(
                X509AuthorityKeyIdentifierExtension.CreateFromSubjectKeyIdentifier(caSki));
        }

        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        DateTimeOffset notAfter = DateTimeOffset.UtcNow.Add(LeafLifetime);
        byte[] serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F; // keep the serial positive

        using X509Certificate2 issued = request.Create(ca, notBefore, notAfter, serial);
        using X509Certificate2 withKey = issued.CopyWithPrivateKey(key);

        byte[] pfx = withKey.Export(X509ContentType.Pfx);
        WriteSealed(_paths.LeafProtectedFile, pfx);
        TryWriteCer(issued, _paths.LeafCerFile);

        return X509CertificateLoader.LoadPkcs12(pfx, null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
    }

    private (X509Certificate2? Leaf, LeafReissueReason Reason) TryLoadLeaf(
        X509Certificate2 ca,
        IReadOnlyCollection<string> requiredDns,
        IReadOnlyCollection<IPAddress> requiredIps)
    {
        if (!File.Exists(_paths.LeafProtectedFile)) return (null, LeafReissueReason.Missing);

        X509Certificate2 leaf;
        try
        {
            byte[] pfx = Dpapi.Unprotect(File.ReadAllBytes(_paths.LeafProtectedFile));
            leaf = X509CertificateLoader.LoadPkcs12(pfx, null,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
        }
        catch
        {
            return (null, LeafReissueReason.Unreadable);
        }

        if (!leaf.HasPrivateKey)
        {
            leaf.Dispose();
            return (null, LeafReissueReason.PrivateKeyMissing);
        }

        if (!string.Equals(leaf.Issuer, ca.Subject, StringComparison.Ordinal))
        {
            leaf.Dispose();
            return (null, LeafReissueReason.NotIssuedByLocalCa);
        }

        if (leaf.NotAfter.ToUniversalTime() <= DateTime.UtcNow.Add(RenewBefore))
        {
            leaf.Dispose();
            return (null, LeafReissueReason.ExpiringSoon);
        }

        if (IssuerChanged(leaf, ca))
        {
            leaf.Dispose();
            return (null, LeafReissueReason.NotIssuedByLocalCa);
        }

        (List<string> presentDns, List<string> presentIps) = ReadSubjectAlternativeNames(leaf);
        if (!Covers(presentDns, requiredDns) || !Covers(presentIps, requiredIps.Select(i => i.ToString())))
        {
            leaf.Dispose();
            return (null, LeafReissueReason.AddressesChanged);
        }

        return (leaf, LeafReissueReason.None);
    }

    /// <summary>
    /// Detects that the leaf was signed by a different root even though the issuer strings match —
    /// the case that would otherwise let a stale leaf keep serving after an explicit CA reset.
    ///
    /// The comparison is the authority key identifier of the leaf against the subject key identifier
    /// of the current root: a direct, cryptographic link that does not depend on the platform's chain
    /// engine or on the issuer's display name being unique.
    /// </summary>
    private static bool IssuerChanged(X509Certificate2 leaf, X509Certificate2 ca)
    {
        byte[]? caKeyId = SubjectKeyIdentifierOf(ca);
        byte[]? leafKeyId = leaf.Extensions.OfType<X509AuthorityKeyIdentifierExtension>().FirstOrDefault()?.KeyIdentifier?.ToArray();

        if (caKeyId is null || leafKeyId is null) return false;
        return !caKeyId.AsSpan().SequenceEqual(leafKeyId);
    }

    private static byte[]? SubjectKeyIdentifierOf(X509Certificate2 cert)
    {
        string? hex = cert.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault()?.SubjectKeyIdentifier;
        if (string.IsNullOrWhiteSpace(hex)) return null;

        try { return Convert.FromHexString(hex); }
        catch { return null; }
    }

    public static (List<string> DnsNames, List<string> IpAddresses) ReadSubjectAlternativeNames(X509Certificate2 cert)
    {
        var dns = new List<string>();
        var ips = new List<string>();

        foreach (X509Extension extension in cert.Extensions)
        {
            if (extension is not X509SubjectAlternativeNameExtension san) continue;
            foreach (string name in san.EnumerateDnsNames()) dns.Add(name.ToLowerInvariant());
            foreach (IPAddress ip in san.EnumerateIPAddresses()) ips.Add(ip.ToString());
        }

        return (dns, ips);
    }

    // ------------------------------------------------------------------ helpers

    public static string Fingerprint(X509Certificate2 cert)
        => Convert.ToHexStringLower(SHA256.HashData(cert.RawData));

    private static string DescribeReason(LeafReissueReason reason) => reason switch
    {
        LeafReissueReason.Missing => "首次签发",
        LeafReissueReason.NotIssuedByLocalCa => "原证书不是由当前本地 CA 签发",
        LeafReissueReason.ExpiringSoon => "原证书即将到期",
        LeafReissueReason.AddressesChanged => "局域网地址已变化，需要覆盖新地址",
        LeafReissueReason.PrivateKeyMissing => "原证书缺少私钥",
        LeafReissueReason.Unreadable => "原证书无法读取",
        _ => "需要重新签发"
    };

    private static bool Covers(IReadOnlyCollection<string> present, IEnumerable<string> required)
        => required.All(r => present.Contains(r, StringComparer.OrdinalIgnoreCase));

    private static List<string> Normalise(IEnumerable<string> names)
        => names.Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    private static List<IPAddress> NormaliseIps(IEnumerable<string> addresses)
    {
        var result = new List<IPAddress>();
        foreach (string text in addresses)
        {
            if (!IPAddress.TryParse(text, out IPAddress? ip)) continue;
            if (!result.Contains(ip)) result.Add(ip);
        }
        return result;
    }

    private static void WriteSealed(string path, byte[] plaintext)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] sealedBytes = Dpapi.Protect(plaintext, "LanDisk local certificate authority key");
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, sealedBytes);
        File.Move(temp, path, overwrite: true);
        TryRestrictAcl(path);
    }

    private static void TryWriteCer(X509Certificate2 cert, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            byte[] cer = cert.Export(X509ContentType.Cert);
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, cer);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // The public certificate is a convenience for the onboarding download; a failure here
            // must not stop the server from starting.
        }
    }

    private static void SafelyDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    internal static void TryRestrictAcl(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return;

            System.Security.AccessControl.FileSecurity security = info.GetAccessControl();
            security.SetAccessRuleProtection(true, false);
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            if (identity is not null)
            {
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    identity, System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.AccessControlType.Allow));
            }
            info.SetAccessControl(security);
        }
        catch
        {
            // Best effort: the tls directory already inherits the user profile ACL.
        }
    }
}
