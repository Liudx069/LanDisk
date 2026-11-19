using System.Security.Cryptography.X509Certificates;
using LanDisk.Core.Configuration;
using LanDisk.Core.Model;
using LanDisk.Core.Networking;
using LanDisk.Core.Networking.Dns;
using LanDisk.Core.Paths;
using LanDisk.Core.Security;
using LanDisk.Core.State;
using LanDisk.Core.Storage;
using LanDisk.Server.Endpoints;
using LanDisk.Update;

namespace LanDisk.Server.Services;

/// <summary>
/// Composition root shared by the real host and by the automated test host, so tests exercise
/// exactly the object graph that ships.
/// </summary>
public sealed class LanDiskRuntime : IDisposable
{
    private readonly object _settingsGate = new();
    private readonly object _certificateGate = new();
    private readonly Action<string>? _log;
    private List<string> _lastAddresses = [];

    public LanDiskRuntime(
        AppPaths paths,
        AppSettings settings,
        string adminBootstrapToken,
        string? installRoot = null,
        bool allowBootstrapLoopback = false,
        Action<string>? log = null,
        Func<int, Core.Networking.PhysicalLanSnapshot>? physicalLanSource = null,
        (int DelayMs, int ChunkBytes) downloadThrottle = default,
        string? extensionHostPath = null)
    {
        Paths = paths;
        Settings = settings;
        AdminBootstrapToken = adminBootstrapToken;
        _log = log;

        // One reading of the machine's physical network, shared by binding, the certificate SAN, the overview,
        // the network page, self-check, the operations dashboard and the pairing origin. Seven independent
        // enumerations used to be able to disagree, and the Owner saw the product serve 192.168.2.2 while the
        // network page reported no usable physical adapter — the page was internally inconsistent because it
        // asked the operating system seven times.
        PhysicalLan = new PhysicalLanState(source: physicalLanSource);

        // 仅测试使用的有界下载节流，用于确定"中止发生在服务端 response 写完之前"。生产恒为 (0,0)。
        DownloadThrottle = downloadThrottle;

        // Extensions run in a separate process; this service stores, approves and orchestrates them.
        Extensions = new ExtensionService(paths.DataRoot, extensionHostPath, log);

        Shares = new ShareRegistry(paths.SharesFile);
        Devices = new DeviceRegistry(paths.DevicesFile);
        RecycleIndex = new RecycleIndex(paths.RecycleIndexFile);
        Transfers = new TransferLog(paths.TransferFile);
        Favorites = new FavoriteStore(paths.FavoritesFile);
        Preferences = new PreferenceStore(paths.PreferencesFile);
        Audit = new AuditLog(paths.AuditFile, settings.AuditRetentionEntries);

        Resolver = new PathResolver(paths);
        Locator = new VolumeStoreLocator(paths);
        Operations = new FileOperations(Resolver);
        Browser = new DirectoryBrowser(Resolver);
        Recycle = new RecycleService(RecycleIndex, Locator, Resolver, Operations, Audit);
        Uploads = new UploadService(Resolver, Locator, settings, paths, Audit);
        Thumbnails = new ThumbnailService(paths, settings);
        Batch = new BatchService(Resolver, Operations, Recycle, Browser, settings);
        Scans = new ShareScanCache();
        BatchTickets = new BatchTicketStore();
        Pairing = new PairingService();
        AdminSessions = new AdminSessionStore();
        Certificates = new CertificateService(paths, log);
        Updates = new UpdateService(paths.DataRoot, installRoot ?? AppContext.BaseDirectory, BuildInfo.Version);

        // The installation identity is read (or created) before anything else can need a name for
        // this machine. A collision is resolved later, once mDNS can actually tell us about one.
        Identity = InstallIdentity.Ensure(paths, null, log);
        _lastAddresses = LanAddresses().ToList();

        Onboarding = new OnboardingService(
            paths,
            () => Certificates.ReadCaCerBytes(),
            () => Identity.HostName,
            () => Settings.HttpsPort,
            () => LanAddresses(),
            allowBootstrapLoopback,
            log);

        StartedAt = DateTimeOffset.Now;

        // The appearance the phones should use. Owned by the server so "Windows sets the iPhone theme"
        // needs no interaction with the phone at all.
        MobileTheme = new MobileThemeService(paths.DataRoot, log);

        // Sampled on demand: a closed dashboard costs nothing, which is why this is not a background timer.
        Metrics = new RuntimeMetrics(this);

        // The management operations the HTTP endpoints and the native Windows UI both call. One
        // implementation, one set of validation rules, whichever surface the user is looking at.
        Admin = new AdminOperations(this);
    }

    public AppPaths Paths { get; }
    public AppSettings Settings { get; }

    /// <summary>
    /// The ports this process actually bound, after a configured 0 was resolved to a free port.
    ///
    /// <para><see cref="Settings"/> keeps whatever the user configured, and 0 is a legitimate value meaning
    /// "pick a free port". Anything that has to put the service back where it was — above all the updater's
    /// restart — must therefore use these, not the configured value: passing 0 hands the updater an
    /// unresolved configuration value, and the restarted instance lands on a different port while the health
    /// check probes port 0.</para>
    /// </summary>
    public int BoundHttpPort { get; internal set; }

    /// <inheritdoc cref="BoundHttpPort"/>
    public int BoundHttpsPort { get; internal set; }

    /// <summary>
    /// Set when the explicitly configured LAN bind address no longer exists on any physical adapter and the
    /// server fell back to automatic selection. Non-null is a warning the UI must show: binding nothing used
    /// to degrade silently to a loopback-only listener, which looks exactly like "the phone cannot connect".
    /// </summary>
    public string? BindAddressFallback { get; private set; }

    public void NoteBindAddressFallback(string configured)
    {
        BindAddressFallback = configured;
        _log?.Invoke(
            $"配置的局域网绑定地址 {configured} 不在任何物理网卡上（网卡或 DHCP 已变化），" +
            "已自动回退为当前物理网卡，局域网监听未中断。请到「网络 → 绑定网卡地址」改用自动，或选择当前地址。");
    }

    public string AdminBootstrapToken { get; }
    public DateTimeOffset StartedAt { get; }

    public ShareRegistry Shares { get; }
    public DeviceRegistry Devices { get; }
    public RecycleIndex RecycleIndex { get; }
    public TransferLog Transfers { get; }
    public FavoriteStore Favorites { get; }
    public PreferenceStore Preferences { get; }
    public AuditLog Audit { get; }

    public PathResolver Resolver { get; }
    public VolumeStoreLocator Locator { get; }
    public FileOperations Operations { get; }
    public DirectoryBrowser Browser { get; }
    public RecycleService Recycle { get; }
    public UploadService Uploads { get; }
    public ThumbnailService Thumbnails { get; }
    public BatchService Batch { get; }
    public ShareScanCache Scans { get; }
    public BatchTicketStore BatchTickets { get; }
    public PairingService Pairing { get; }
    public AdminSessionStore AdminSessions { get; }
    public CertificateService Certificates { get; }

    /// <summary>Extension package storage, approval and execution (never loads extension code in-process).</summary>
    public ExtensionService Extensions { get; }

    /// <summary>The single physical-network reading every consumer shares.</summary>
    public PhysicalLanState PhysicalLan { get; }

    /// <summary>Persistent install id and the stable <c>.local</c> name derived from it.</summary>
    public InstallIdentity Identity { get; }

    /// <summary>The first-connection wizard's bootstrap window.</summary>
    public OnboardingService Onboarding { get; }

    /// <summary>The appearance the operator chose for the phones (served to them as colour roles).</summary>
    public MobileThemeService MobileTheme { get; }

    /// <summary>Live counters for the operations dashboard (sampled on demand, not in the background).</summary>
    public RuntimeMetrics Metrics { get; }

    /// <summary>Shared management operations, used by both the HTTP endpoints and the native UI.</summary>
    public AdminOperations Admin { get; }

    /// <summary>mDNS publisher; null when it could not be started.</summary>
    public MdnsResponder? Mdns { get; set; }

    public ServerCertificate? ServerCertificate { get; private set; }

    public CertificateInfo? Certificate { get; private set; }

    /// <summary>Latest leaf, used by Kestrel's per-connection certificate selector.</summary>
    public X509Certificate2? CurrentServerCertificate
    {
        get { lock (_certificateGate) { return ServerCertificate?.Leaf; } }
    }

    /// <summary>
    /// The address set a phone could use right now, best first. Physical Ethernet/Wi-Fi only: a
    /// Hyper-V switch, a WSL bridge or a VPN's 198.18 address are not things a phone can open, and
    /// offering them is what made the stable name fail exactly while a tunnel was up.
    /// </summary>
    public IReadOnlyList<string> LanAddresses()
        => PhysicalLanSelector.PhysicalAddresses().Select(c => c.Address).ToList();

    /// <summary>Every adapter with the reason it was accepted or rejected; the network page shows this.</summary>
    public IReadOnlyList<AdapterVerdict> AdapterReport() => PhysicalLanSelector.Rank(PhysicalLanSelector.Snapshot());

    /// <summary>The shared reading, as the UI and the checks consume it (never a second enumeration).</summary>
    public PhysicalLanSnapshot Physical => PhysicalLan.Recent();

    /// <summary>True when a VPN or tunnel is up, with a description for the InfoBar. Never modified by LanDisk.</summary>
    public bool VpnOrTunnelDetected(out string details)
    {
        PhysicalLanSnapshot snapshot = PhysicalLan.Recent();
        details = snapshot.VpnDetails;
        return snapshot.VpnOrTunnelDetected;
    }

    /// <summary>Signed-update staging, apply and status. See <see cref="UpdateService"/>.</summary>
    public UpdateService Updates { get; }

    /// <summary>Actual bound endpoints, filled in after the host starts.</summary>
    public List<string> BoundUrls { get; } = [];

    /// <summary>
    /// Test-only bounded download throttling: (delayMs, chunkBytes). Zero means no throttling, which is
    /// what every production run has. It exists so a test can guarantee that a client abort lands while the
    /// server is still writing the response body, instead of inferring that from how many bytes the client got.
    /// </summary>
    public (int DelayMs, int ChunkBytes) DownloadThrottle { get; }

    /// <summary>
    /// Resolves the HTTPS identity: the persistent root CA plus a leaf that covers the stable name and
    /// every address a phone might currently use.
    /// </summary>
    public ServerCertificate EnsureCertificates(bool refresh = false)
    {
        lock (_certificateGate)
        {
            if (ServerCertificate is not null && !refresh) return ServerCertificate;

            ServerCertificate issued = Certificates.EnsureServerCertificate(Identity.InstallId, Identity.HostName);
            ServerCertificate = issued;
            Certificate = Certificates.Describe(issued.Leaf, issued.Ca, CertificateService.DescribeReason(issued.Reason));

            // The one-time move off the 1.3.0 self-signed leaf happens after the new hierarchy is
            // proven usable, and only once. The old files are archived rather than deleted.
            if (!Identity.CertificateMigrationDone && Certificates.LegacyLeafPresent)
            {
                Certificates.RetireLegacyCertificate(DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Identity.MarkCertificateMigration(DateTimeOffset.Now);
                Identity.Write(Paths);
                _log?.Invoke("已完成一次性的证书架构迁移：本地 CA + 由它签发的服务器证书。共享、配对与端口均未改动。");
            }

            return issued;
        }
    }

    /// <summary>
    /// Re-checks the LAN address set and the leaf that has to cover it. Returns true when something
    /// changed, so the caller can re-announce over mDNS.
    ///
    /// The root CA is never touched here: an address change renews the leaf only, which is why a phone
    /// does not have to trust anything again after moving between networks.
    /// </summary>
    public bool RefreshNetworkIdentity()
    {
        List<string> current = LanAddresses().ToList();
        bool addressesChanged = !_lastAddresses.SequenceEqual(current, StringComparer.Ordinal);

        ServerCertificate issued = EnsureCertificates(refresh: true);
        bool leafRotated = issued.Reissued;

        if (addressesChanged || leafRotated)
        {
            _lastAddresses = current;
            UpdateAdvertisement();
        }

        return addressesChanged || leafRotated;
    }

    /// <summary>Rebuilds the mDNS advertisement from the current name, address set and version.</summary>
    public void UpdateAdvertisement()
    {
        MdnsResponder? responder = Mdns;
        if (responder is null) return;

        responder.Update(new MdnsAdvertisement(
            Identity.HostName,
            Settings.HttpsPort,
            BuildInfo.Version,
            LanAddresses().ToList()));
    }

    /// <summary>Starts mDNS if it is not running. Returns the reason when it cannot start.</summary>
    public bool StartMdns(out string? error)
    {
        error = null;
        if (Mdns is { IsRunning: true }) return true;

        var advertisement = new MdnsAdvertisement(
            Identity.HostName,
            Settings.HttpsPort,
            BuildInfo.Version,
            LanAddresses().ToList());

        var responder = new MdnsResponder(advertisement, _log);
        responder.ConflictDetected += (_, _) =>
        {
            // A conflict is resolved exactly once, by moving to the next persistent name; the name is
            // never re-rolled on every start.
            if (!Identity.AdoptNextName(Paths, _log)) return;
            responder.Update(new MdnsAdvertisement(
                Identity.HostName, Settings.HttpsPort, BuildInfo.Version, LanAddresses().ToList()));
            _log?.Invoke("mDNS 已按新名称重新发布；服务器证书将在下一次检查时重新签发以覆盖新名称。");
        };

        if (!responder.Start())
        {
            error = responder.LastError;
            responder.Dispose();
            return false;
        }

        Mdns = responder;
        return true;
    }

    public void StopMdns()
    {
        MdnsResponder? responder = Mdns;
        Mdns = null;
        responder?.Dispose();
    }

    public void SaveSettings()
    {
        lock (_settingsGate)
        {
            JsonStore.Write(Paths.SettingsFile, Settings);
        }
    }

    public void SaveSettings(AppSettings replacement)
    {
        lock (_settingsGate)
        {
            Settings.LanEnabled = replacement.LanEnabled;
            Settings.LanBindAddress = replacement.LanBindAddress;
            Settings.HttpsEnabled = replacement.HttpsEnabled;
            Settings.HttpCompatModeEnabled = replacement.HttpCompatModeEnabled;
            Settings.AllowedCidr = replacement.AllowedCidr;
            Settings.HttpPort = replacement.HttpPort;
            Settings.HttpsPort = replacement.HttpsPort;
            Settings.MaxChunkBytes = replacement.MaxChunkBytes;
            Settings.MaxConcurrentUploadsPerDevice = replacement.MaxConcurrentUploadsPerDevice;
            Settings.UploadIdleTimeoutMinutes = replacement.UploadIdleTimeoutMinutes;
            Settings.StagingQuotaBytes = replacement.StagingQuotaBytes;
            Settings.ListingPageSize = replacement.ListingPageSize;
            Settings.TextPreviewMaxBytes = replacement.TextPreviewMaxBytes;
            Settings.InlinePreviewMaxBytes = replacement.InlinePreviewMaxBytes;
            Settings.AuditRetentionEntries = replacement.AuditRetentionEntries;
            Settings.VerboseLogging = replacement.VerboseLogging;
            JsonStore.Write(Paths.SettingsFile, Settings);
        }
    }

    public ShareDefinition? FindShare(string shareId) => Shares.Find(shareId);

    /// <summary>
    /// Drops the cached recursive scan for a share after anything that changes its contents, so
    /// "recent" and cross-folder search reflect an upload, rename or delete immediately instead of
    /// waiting out the cache lifetime.
    /// </summary>
    public void InvalidateScans(string? shareId = null)
    {
        if (string.IsNullOrEmpty(shareId)) Scans.Clear();
        else Scans.Invalidate("recent:" + shareId + ":");
    }

    /// <summary>Effective capability set: share flags narrowed by the device's own level.</summary>
    public static ShareCapabilities EffectiveCapabilities(ShareDefinition share, DeviceAccessLevel level)
    {
        ShareCapabilities effective = share.Capabilities;
        if (level == DeviceAccessLevel.ReadOnly)
            effective &= ShareCapabilities.Read;
        if (!share.Enabled)
            effective = ShareCapabilities.None;
        return effective;
    }

    public static bool Allows(ShareDefinition share, DeviceAccessLevel level, ShareCapabilities required)
        => (EffectiveCapabilities(share, level) & required) == required;

    public void RefreshShareHealth()
    {
        foreach (ShareDefinition share in Shares.All())
        {
            ShareHealth health = Resolver.ProbeShare(share);
            if (share.State != health.State || share.StateDetail != health.Detail)
            {
                Shares.Update(share.Id, s =>
                {
                    s.State = health.State;
                    s.StateDetail = health.Detail;
                });
            }
        }
    }

    public void Dispose()
    {
        // Nothing unmanaged is held directly; upload sessions own file streams and are closed
        // explicitly by the host shutdown path.
        StopMdns();
        Onboarding.Dispose();
        Updates.Dispose();
    }
}
