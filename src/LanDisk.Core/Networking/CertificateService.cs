using System.Net;
using System.Security.Cryptography.X509Certificates;
using LanDisk.Core.Configuration;
using LanDisk.Core.Security;

namespace LanDisk.Core.Networking;

public sealed class CertificateInfo
{
    public required string Subject { get; init; }
    public required string Issuer { get; init; }
    public required string Thumbprint { get; init; }
    public required string Sha256Fingerprint { get; init; }
    public required DateTimeOffset NotBefore { get; init; }
    public required DateTimeOffset NotAfter { get; init; }
    public required IReadOnlyList<string> DnsNames { get; init; }
    public required IReadOnlyList<string> IpAddresses { get; init; }

    /// <summary>Path of the root CA certificate a phone has to trust.</summary>
    public required string CaCerPath { get; init; }

    public required string LeafCerPath { get; init; }

    /// <summary>True when the leaf chains to LanDisk's own root rather than to itself.</summary>
    public required bool IsIssuedByLocalCa { get; init; }

    /// <summary>SHA-256 of the root CA, i.e. the value a user compares when trusting it.</summary>
    public required string RootCaSha256 { get; init; }

    public required string RootCaSubject { get; init; }
    public required DateTimeOffset RootCaNotAfter { get; init; }

    /// <summary>Why the current leaf was issued; empty means it was reused as-is.</summary>
    public required string LeafReason { get; init; }
}

/// <summary>
/// Owns the HTTPS identity of this installation: a persistent local root CA plus a short-lived leaf
/// that covers the stable <c>.local</c> name and whatever LAN addresses the machine currently has.
///
/// <para>The private keys never leave the machine and are stored DPAPI-sealed for the current Windows
/// user. The only certificate this type ever exports for download is the root CA's public part —
/// which is exactly the object an iPhone has to be asked to trust.</para>
/// </summary>
public sealed class CertificateService
{
    private readonly AppPaths _paths;
    private readonly LocalCertificateAuthority _ca;
    private readonly Action<string>? _log;

    public CertificateService(AppPaths paths, Action<string>? log = null)
    {
        _paths = paths;
        _log = log;
        _ca = new LocalCertificateAuthority(paths, log);
    }

    public string? LastError => _ca.LastError;

    /// <summary>Human readable reason a leaf was issued, for the status endpoint and the log.</summary>
    public static string DescribeReason(LeafReissueReason reason) => reason switch
    {
        LeafReissueReason.None => "沿用现有证书",
        LeafReissueReason.Missing => "首次签发",
        LeafReissueReason.NotIssuedByLocalCa => "原证书不是由当前本地 CA 签发",
        LeafReissueReason.ExpiringSoon => "原证书即将到期",
        LeafReissueReason.AddressesChanged => "局域网地址已变化，需要覆盖新地址",
        LeafReissueReason.PrivateKeyMissing => "原证书缺少私钥",
        LeafReissueReason.Unreadable => "原证书无法读取",
        _ => "需要重新签发"
    };

    /// <summary>True when a 1.3.0 style self-signed leaf is still present on disk.</summary>
    public bool LegacyLeafPresent => _ca.LegacyCertificatePresent;

    public X509Certificate2 EnsureCa(string installId) => _ca.EnsureCa(installId);

    /// <summary>
    /// The root CA, or null when it has not been created yet.
    ///
    /// This — not the leaf — is the installation's stable HTTPS identity, so it is what the update
    /// snapshot compares across an upgrade: a renewed leaf is expected, a different root is not.
    /// </summary>
    public X509Certificate2? TryLoadCaCertificate() => _ca.TryLoadCa();

    public X509Certificate2 ResetCa(string installId) => _ca.ResetCa(installId);

    /// <summary>
    /// Drops the server leaf only, leaving the root CA in place. This is the ordinary "renew the
    /// server certificate" action: no phone has to trust anything again, because the object that was
    /// trusted did not move.
    /// </summary>
    public void ResetLeafOnly()
    {
        foreach (string path in new[] { _paths.LeafProtectedFile, _paths.LeafCerFile })
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { _log?.Invoke("删除旧服务器证书失败：" + ex.Message); }
        }
    }

    /// <summary>
    /// The complete set of names a phone may use to reach this machine over HTTPS. The stable name
    /// comes first because it is the one that survives an address change.
    /// </summary>
    public static (List<string> DnsNames, List<string> IpAddresses) DesiredNames(
        string stableHostName,
        IReadOnlyCollection<string>? lanAddresses = null)
    {
        var dns = new List<string>();
        if (!string.IsNullOrWhiteSpace(stableHostName)) dns.Add(stableHostName.Trim().ToLowerInvariant());
        dns.Add("localhost");

        // Kept for continuity: an address that worked before this release keeps working. The Windows
        // machine name is not the user's name and is not what the stable name is derived from — that
        // is random precisely so the phone's trust does not depend on anything about its owner.
        string machine = Environment.MachineName;
        if (!string.IsNullOrWhiteSpace(machine)) dns.Add(machine.ToLowerInvariant());

        var ips = new List<string> { IPAddress.Loopback.ToString(), IPAddress.IPv6Loopback.ToString() };

        if (lanAddresses is not null)
        {
            // An explicit address list is a requirement, not a filter: whatever the caller says a phone
            // may use has to end up in the SAN, or the certificate would not cover the address the user
            // was told to open.
            foreach (string text in lanAddresses)
            {
                if (IPAddress.TryParse(text, out IPAddress? ip) && !ips.Contains(ip.ToString()))
                    ips.Add(ip.ToString());
            }
        }
        else
        {
            // Physical interfaces only. Putting a Hyper-V switch address or a VPN's fake 198.18 address
            // into the certificate would advertise reachability that does not exist — and the user's
            // phone would be the thing that found out.
            foreach (AdapterVerdict candidate in PhysicalLanSelector.PhysicalAddresses())
            {
                if (!ips.Contains(candidate.Address)) ips.Add(candidate.Address);
            }
        }

        return (dns, ips);
    }

    /// <summary>
    /// Returns the certificate Kestrel should serve, creating or renewing the leaf when needed.
    /// Repeated calls with the same inputs reuse the same leaf, which is what makes the trust a
    /// phone granted survive restarts.
    /// </summary>
    public ServerCertificate EnsureServerCertificate(
        string installId,
        string stableHostName,
        IReadOnlyCollection<string>? lanAddresses = null)
    {
        X509Certificate2 ca = _ca.EnsureCa(installId);
        (List<string> dns, List<string> ips) = DesiredNames(stableHostName, lanAddresses);
        return _ca.EnsureLeaf(ca, stableHostName, dns, ips);
    }

    /// <summary>Reads the current leaf without changing anything. Used by status endpoints.</summary>
    public X509Certificate2? TryLoadLeaf()
    {
        try
        {
            if (!File.Exists(_paths.LeafProtectedFile)) return null;
            byte[] pfx = Interop.Dpapi.Unprotect(File.ReadAllBytes(_paths.LeafProtectedFile));
            return X509CertificateLoader.LoadPkcs12(pfx, null,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
        }
        catch (Exception ex)
        {
            _log?.Invoke("读取服务器证书失败：" + ex.Message);
            return null;
        }
    }

    public CertificateInfo Describe(X509Certificate2 leaf, X509Certificate2 ca, string leafReason)    {
        (List<string> dns, List<string> ips) = LocalCertificateAuthority.ReadSubjectAlternativeNames(leaf);

        return new CertificateInfo
        {
            Subject = leaf.Subject,
            Issuer = leaf.Issuer,
            Thumbprint = leaf.Thumbprint,
            Sha256Fingerprint = SecretTokens.CertificateFingerprint(leaf.RawData),
            NotBefore = leaf.NotBefore.ToUniversalTime(),
            NotAfter = leaf.NotAfter.ToUniversalTime(),
            DnsNames = dns,
            IpAddresses = ips,
            CaCerPath = _paths.CaCerFile,
            LeafCerPath = _paths.LeafCerFile,
            IsIssuedByLocalCa = !string.Equals(leaf.Subject, leaf.Issuer, StringComparison.Ordinal),
            RootCaSha256 = SecretTokens.CertificateFingerprint(ca.RawData),
            RootCaSubject = ca.Subject,
            RootCaNotAfter = ca.NotAfter.ToUniversalTime(),
            LeafReason = leafReason
        };
    }

    /// <summary>
    /// The public certificate offered to phones: the root CA. Downloading the leaf would be useless,
    /// because trusting a leaf does not make the next leaf trusted.
    /// </summary>
    public byte[]? ReadCaCerBytes()
        => File.Exists(_paths.CaCerFile) ? File.ReadAllBytes(_paths.CaCerFile) : null;

    public byte[]? ReadLeafCerBytes()
        => File.Exists(_paths.LeafCerFile) ? File.ReadAllBytes(_paths.LeafCerFile) : null;

    /// <summary>Backwards-compatible alias: what the admin page has always called "the certificate".</summary>
    public byte[]? ReadCerBytes() => ReadCaCerBytes() ?? ReadLeafCerBytes();

    /// <summary>
    /// Removes the 1.3.0 self-signed material once the new hierarchy is in place. The old files are
    /// kept as a timestamped copy so nothing about the previous trust state is silently destroyed.
    /// </summary>
    public bool RetireLegacyCertificate(string stamp)
    {
        if (!LegacyLeafPresent) return false;

        try
        {
            string backup = Path.Combine(_paths.TlsDir, $"legacy-1.3.0-{stamp}");
            Directory.CreateDirectory(backup);
            foreach (string name in new[] { "landisk.pfx", "landisk-root.cer" })
            {
                string source = Path.Combine(_paths.TlsDir, name);
                if (File.Exists(source)) File.Move(source, Path.Combine(backup, name), overwrite: true);
            }

            _log?.Invoke("旧的自签服务器证书已归档到 " + backup + "；此后的 HTTPS 由本地 CA 签发。");
            return true;
        }
        catch (Exception ex)
        {
            _log?.Invoke("归档旧证书失败：" + ex.Message);
            return false;
        }
    }
}
