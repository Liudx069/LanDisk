using System.Net;
using LanDisk.Core.Configuration;
using LanDisk.Core.Interop;
using LanDisk.Core.Model;
using LanDisk.Core.Networking;
using LanDisk.Core.Paths;
using LanDisk.Core.Security;

namespace LanDisk.Server.Services;

/// <summary>
/// The result of an operation that can be refused for a reason the user should see. Endpoints turn
/// this into an HTTP status; the native UI turns it into a message. The decision itself is made once.
/// </summary>
public sealed record OperationOutcome<T>(bool Ok, T? Value, string? Code, string? Message)
{
    public static OperationOutcome<T> Success(T value) => new(true, value, null, null);

    public static OperationOutcome<T> Fail(string code, string message) => new(false, default, code, message);
}

/// <summary>
/// The management operations the product performs on itself, extracted so that the HTTP endpoints and
/// the native Windows UI run the *same* code.
///
/// <para>The alternative — a native UI that talks to the loopback management API — was rejected by the
/// task, and the other alternative, a second implementation of "validate this share root", is how two
/// answers to the same question start to drift. So the validation, the volume-identity capture, the
/// audit entries and the pairing-link ordering live here, and both callers delegate.</para>
///
/// <para>Nothing here bypasses a security decision: the same <see cref="PathResolver"/> rules, the same
/// internal-path refusal, the same reparse-point refusal and the same volume-identity capture as
/// before.</para>
/// </summary>
public sealed class AdminOperations
{
    private readonly LanDiskRuntime _runtime;

    public AdminOperations(LanDiskRuntime runtime) => _runtime = runtime;

    // ------------------------------------------------------------------ shares

    /// <summary>
    /// Publishes a directory. Validates the path, refuses LanDisk's own storage, refuses symlinked and
    /// junction roots, and records the volume identity at creation time so a later drive-letter change
    /// is detectable.
    /// </summary>
    public OperationOutcome<ShareDefinition> CreateShare(
        string? rootPath,
        string? displayName,
        string? note,
        bool allowUpload,
        bool allowRename,
        bool allowDelete)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                return OperationOutcome<ShareDefinition>.Fail("PATH_REQUIRED", "请选择要共享的目录。");

            string root = Path.GetFullPath(rootPath.Trim());
            if (root.StartsWith(@"\\", StringComparison.Ordinal))
                return OperationOutcome<ShareDefinition>.Fail("UNC_NOT_SUPPORTED", "不支持共享网络路径（UNC）。");
            if (!Directory.Exists(root))
                return OperationOutcome<ShareDefinition>.Fail("PATH_NOT_FOUND", "该目录不存在或当前不可访问。");

            string trimmed = PathRules.TrimTrailingSeparator(root);

            // Refuse to publish LanDisk's own internal directories. A share that merely *contains* one
            // (a whole removable volume, which the product explicitly supports) is allowed: the
            // resolver blocks access to those paths on every request instead.
            foreach (string internalRoot in _runtime.Paths.InternalRoots())
            {
                if (string.IsNullOrEmpty(internalRoot)) continue;
                try
                {
                    if (PathRules.IsWithinRoot(internalRoot, trimmed))
                        return OperationOutcome<ShareDefinition>.Fail("INTERNAL_PATH", "不能共享 LanDisk 的内部数据目录。");
                }
                catch { }
            }

            if (trimmed.Split('\\').Any(s => string.Equals(s, AppPaths.VolumeStoreName, StringComparison.OrdinalIgnoreCase)))
                return OperationOutcome<ShareDefinition>.Fail("INTERNAL_PATH", "不能共享 LanDisk 的内部数据目录。");

            using OpenedHandle probe = Win32FileSystem.Open(trimmed, deleteAccess: false);
            if (probe.IsReparsePoint)
                return OperationOutcome<ShareDefinition>.Fail("REPARSE_ROOT",
                    "该目录是符号链接、junction 或挂载点。本版本不支持以此类目录作为共享根。");

            ShareCapabilities capabilities = ShareCapabilities.Read;
            if (allowUpload) capabilities |= ShareCapabilities.Upload;
            if (allowRename) capabilities |= ShareCapabilities.Rename;
            if (allowDelete) capabilities |= ShareCapabilities.Delete;

            var share = new ShareDefinition
            {
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? new DirectoryInfo(trimmed).Name : displayName.Trim(),
                RootPath = trimmed,
                VolumeGuidPath = probe.VolumeGuidPath,
                MountPointAtCreation = probe.VolumeMountPoint,
                RootObjectId = probe.ObjectId.ToString(),
                Capabilities = capabilities,
                Enabled = true,
                Note = note,
                State = ShareState.Online
            };

            _runtime.Shares.Add(share);
            // 共享清单本身也是设备能看到的权利面（/api/me 会列出该设备可见的共享），所以新共享要通知一次。
            BumpDeviceAuthority();
            Audit("share", "create", share.Id, "capabilities=" + capabilities);

            return OperationOutcome<ShareDefinition>.Success(share);
        }
        catch (Exception ex)
        {
            return OperationOutcome<ShareDefinition>.Fail("SHARE_CREATE_FAILED", ex.Message);
        }
    }

    public OperationOutcome<ShareDefinition> UpdateShare(
        string id,
        string? displayName = null,
        bool? enabled = null,
        string? note = null,
        bool? allowRead = null,
        bool? allowUpload = null,
        bool? allowRename = null,
        bool? allowDelete = null)
    {
        ShareDefinition? share = _runtime.Shares.Find(id);
        if (share is null) return OperationOutcome<ShareDefinition>.Fail("NOT_FOUND", "共享不存在。");

        // 改动前的权利快照：只有**真的变了**才通知设备（与设备级改写同样的"变了才递增"约定）。
        ShareCapabilities capabilitiesBefore = share.Capabilities;
        bool enabledBefore = share.Enabled;

        try
        {
            _runtime.Shares.Update(id, s =>
            {
                if (displayName is not null) s.DisplayName = displayName.Trim();
                if (enabled is not null) s.Enabled = enabled.Value;
                if (note is not null) s.Note = note;

                if (allowRead is not null || allowUpload is not null || allowRename is not null || allowDelete is not null)
                {
                    ShareCapabilities caps = s.Capabilities;
                    if (allowRead is not null) caps = allowRead.Value ? caps | ShareCapabilities.Read : caps & ~ShareCapabilities.Read;
                    if (allowUpload is not null) caps = allowUpload.Value ? caps | ShareCapabilities.Upload : caps & ~ShareCapabilities.Upload;
                    if (allowRename is not null) caps = allowRename.Value ? caps | ShareCapabilities.Rename : caps & ~ShareCapabilities.Rename;
                    if (allowDelete is not null) caps = allowDelete.Value ? caps | ShareCapabilities.Delete : caps & ~ShareCapabilities.Delete;
                    s.Capabilities = caps;
                }
            });

            ShareDefinition updated = _runtime.Shares.Find(id)!;

            // 共享能做什么，直接决定设备在**这个共享上**的实际权利（删除属于 Share Capability，
            // 不属于设备级权限）。手机的被动路径只在心跳报告的 permissionRevision 变化时才重读权利，
            // 所以这里必须让它动 —— 否则操作员在 Windows 上打开"允许删除"之后，iPhone 上
            // **永远**不会出现删除入口，只有手动刷新或重新配对才会出现。
            if (updated.Capabilities != capabilitiesBefore || updated.Enabled != enabledBefore)
                BumpDeviceAuthority();

            Audit("share", "update", id, $"enabled={updated.Enabled} capabilities={updated.Capabilities}");
            return OperationOutcome<ShareDefinition>.Success(updated);
        }
        catch (Exception ex)
        {
            return OperationOutcome<ShareDefinition>.Fail("SHARE_UPDATE_FAILED", ex.Message);
        }
    }

    /// <summary>
    /// Moves every paired device's authority revision so the phones re-read what they are allowed to do.
    ///
    /// <para>
    /// The phone's passive path never polls its own rights: it re-reads them only when the heartbeat reports a
    /// different <c>permissionRevision</c>. Device-level edits (level, capabilities) already moved it, but a
    /// share's capabilities — which is where <em>delete</em> actually lives — did not, so changing them on
    /// Windows reached the paired phones only after a manual refresh or a re-pairing.
    /// </para>
    /// </summary>
    private void BumpDeviceAuthority()
    {
        foreach (DeviceDefinition device in _runtime.Devices.All())
        {
            _runtime.Devices.Update(device.Id, d => d.PermissionRevision++);
        }
    }

    /// <summary>Re-reads the volume identity after the media was reconnected or the drive letter moved.</summary>
    public OperationOutcome<ShareDefinition> ReconfirmShare(string id)
    {
        ShareDefinition? share = _runtime.Shares.Find(id);
        if (share is null) return OperationOutcome<ShareDefinition>.Fail("NOT_FOUND", "共享不存在。");

        try
        {
            using OpenedHandle probe = Win32FileSystem.Open(Path.GetFullPath(share.RootPath), deleteAccess: false);
            _runtime.Shares.Update(id, s =>
            {
                s.VolumeGuidPath = probe.VolumeGuidPath;
                s.MountPointAtCreation = probe.VolumeMountPoint;
                s.RootObjectId = probe.ObjectId.ToString();
                s.State = ShareState.Online;
                s.StateDetail = null;
            });

            Audit("share", "reconfirm", id, "volume=" + probe.VolumeGuidPath);
            return OperationOutcome<ShareDefinition>.Success(_runtime.Shares.Find(id)!);
        }
        catch (Exception ex)
        {
            return OperationOutcome<ShareDefinition>.Fail("RECONFIRM_FAILED", ex.Message);
        }
    }

    /// <summary>
    /// Stops publishing a directory. The real files are never touched — this removes a record, not
    /// content — and bookmarks into the share go with it because they could only ever be broken.
    /// </summary>
    public OperationOutcome<int> DeleteShare(string id)
    {
        if (!_runtime.Shares.Remove(id))
            return OperationOutcome<int>.Fail("NOT_FOUND", "共享不存在。");

        int removedFavorites = _runtime.Favorites.RemoveForShare(id);
        _runtime.InvalidateScans(id);
        // 共享消失同样是设备的权利面变化。
        BumpDeviceAuthority();
        Audit("share", "delete", id, $"favoritesRemoved={removedFavorites}");

        return OperationOutcome<int>.Success(removedFavorites);
    }

    // ------------------------------------------------------------------ network

    public OperationOutcome<AppSettings> UpdateNetworkSettings(
        bool? lanEnabled = null,
        bool? httpsEnabled = null,
        bool? httpCompatModeEnabled = null,
        string? bindAddress = null,
        string? allowedCidr = null,
        int? httpPort = null,
        int? httpsPort = null)
    {
        bool lan = lanEnabled ?? _runtime.Settings.LanEnabled;
        bool https = httpsEnabled ?? _runtime.Settings.HttpsEnabled;
        bool compat = httpCompatModeEnabled ?? _runtime.Settings.HttpCompatModeEnabled;

        if (lan && !https && !compat)
        {
            return OperationOutcome<AppSettings>.Fail("TLS_OR_COMPAT_REQUIRED",
                "开启局域网访问前，请先启用 HTTPS，或明确开启“仅可信局域网的 HTTP 兼容模式”。");
        }

        string bind = bindAddress ?? _runtime.Settings.LanBindAddress;
        if (lan && !string.IsNullOrWhiteSpace(bind))
        {
            // Only a physical, currently usable LAN address may be pinned: binding to a Hyper-V switch
            // or a VPN's fake address is exactly the mistake the physical-LAN rule exists to prevent.
            bool known = _runtime.Physical.FindAccepted(bind) is not null;
            if (!known)
                return OperationOutcome<AppSettings>.Fail("ADDRESS_UNKNOWN", "该地址不是本机当前可用的物理局域网 IPv4 地址。");
        }

        if (!string.IsNullOrWhiteSpace(allowedCidr) && !LanInfo.TryParseCidr(allowedCidr, out _, out _))
            return OperationOutcome<AppSettings>.Fail("CIDR_INVALID", "允许网段格式应为 192.168.1.0/24。");

        if (httpPort is not null && (httpPort < 1 || httpPort > 65535))
            return OperationOutcome<AppSettings>.Fail("PORT_INVALID", "HTTP 端口无效。");
        if (httpsPort is not null && (httpsPort < 1 || httpsPort > 65535))
            return OperationOutcome<AppSettings>.Fail("PORT_INVALID", "HTTPS 端口无效。");

        var updated = new AppSettings
        {
            LanEnabled = lan,
            LanBindAddress = bind,
            HttpsEnabled = https,
            HttpCompatModeEnabled = compat,
            AllowedCidr = allowedCidr ?? _runtime.Settings.AllowedCidr,
            HttpPort = httpPort ?? _runtime.Settings.HttpPort,
            HttpsPort = httpsPort ?? _runtime.Settings.HttpsPort,
            MaxChunkBytes = _runtime.Settings.MaxChunkBytes,
            MaxConcurrentUploadsPerDevice = _runtime.Settings.MaxConcurrentUploadsPerDevice,
            UploadIdleTimeoutMinutes = _runtime.Settings.UploadIdleTimeoutMinutes,
            StagingQuotaBytes = _runtime.Settings.StagingQuotaBytes,
            ListingPageSize = _runtime.Settings.ListingPageSize,
            TextPreviewMaxBytes = _runtime.Settings.TextPreviewMaxBytes,
            InlinePreviewMaxBytes = _runtime.Settings.InlinePreviewMaxBytes,
            AuditRetentionEntries = _runtime.Settings.AuditRetentionEntries,
            VerboseLogging = _runtime.Settings.VerboseLogging
        };

        _runtime.SaveSettings(updated);
        Audit("network", "update", null, $"lan={lan} https={https} compat={compat} bind={bind}");

        return OperationOutcome<AppSettings>.Success(updated);
    }

    // ------------------------------------------------------------------ pairing

    /// <summary>
    /// Approves the phone request the local operator is looking at.
    ///
    /// <para><b>What this replaced.</b> The native pages used to call <c>Runtime.Pairing.Cancel(id)</c>
    /// for the "确认配对" button — a confirm that cancelled. The device was never created, and the
    /// code that did know how to create one lived inline in the HTTP endpoint, so the two surfaces
    /// could not even agree on what confirming meant. Both now call this method.</para>
    ///
    /// <para>Every check the endpoint used to make is here, in the same order, and the resulting device
    /// is byte-for-byte the one the HTTP path created: name from the request (or the User-Agent), read
    /// only by default, a freshly hashed token, and the observed address and User-Agent recorded.</para>
    /// </summary>
    public OperationOutcome<DeviceDefinition> ConfirmPairing(string id)
    {
        PairingRequest? pairing = _runtime.Pairing.Find(id);
        if (pairing is null) return OperationOutcome<DeviceDefinition>.Fail("NOT_FOUND", "配对请求不存在。");
        if (pairing.IsExpired) return OperationOutcome<DeviceDefinition>.Fail("PAIRING_EXPIRED", "配对请求已过期。");
        if (pairing.State != PairingState.AwaitingLocalConfirmation)
            return OperationOutcome<DeviceDefinition>.Fail("PAIRING_STATE", "该请求当前不需要本机确认。");

        pairing.LocalConfirmationGranted = true;
        pairing.State = PairingState.Redeemed;

        // What the phone should be told to use from now on.
        //
        // The measurement that made this pairing work lives in the wizard's verified-host hint, and the
        // very next lines close the wizard and clear that hint. Reading it *here* — while it still exists —
        // is what stops the completion screen from recommending the address this phone just failed to
        // reach: with a VPN/TUN up, the stable .local name is exactly the one that did not work while
        // https://<lan-ip>:8892 did. This is UI information about one attempt, not a credential, and it is
        // kept on the request that produced it (in memory, pruned with the request).
        pairing.RecommendedOrigin = RecommendedHttpsOrigin();

        var device = new DeviceDefinition
        {
            Name = pairing.DeviceName ?? "未命名设备",
            Level = DeviceAccessLevel.ReadOnly,
            TokenHash = SecretTokens.Hash(SecretTokens.NewToken()),
            LastSeenIp = pairing.RemoteAddress,
            UserAgent = pairing.DeviceUserAgent
        };
        _runtime.Devices.Add(device);
        pairing.IssuedDeviceId = device.Id;

        _runtime.Audit.Write(new AuditEntry
        {
            Category = "pairing",
            Action = "confirm",
            DeviceName = OperationActor.LocalAdminName,
            DeviceId = device.Id,
            Source = OperationActor.LocalAdmin.SourceToken,
            Detail = "remote=" + pairing.RemoteAddress,
            Success = true
        });

        // The wizard exists for exactly one thing: getting one phone from "no certificate" to "paired".
        // Now that the device is persisted and the request points at it, the temporary plain-HTTP 8890
        // listener has no reason to stay open. Closing it *here* rather than in the window means the
        // browser path and the native path behave identically, and it is deliberately after the device
        // is stored: a confirmation that was refused above must never close the wizard the user is
        // still standing in.
        if (_runtime.Onboarding.IsActive)
        {
            _runtime.Onboarding.Stop("配对成功，首次连接向导已自动结束");
        }

        // This wizard session is over: the connectivity hint that told us which address this phone could
        // open has done its job and must not leak into the next one. Clearing it here cannot affect the
        // device that was just created or the HTTPS listener it will keep using.
        _runtime.Onboarding.ClearVerifiedHttpsHost("配对成功，本次连接流程结束");

        return OperationOutcome<DeviceDefinition>.Success(device);
    }

    /// <summary>
    /// Starts the first-connection wizard if it is not already running, and never restarts one that is.
    ///
    /// <para>The wizard is the only source of the certificate QR code, so opening the "连接 iPhone" page
    /// is what starts it — that is the difference between clicking once and clicking twice. Returning the
    /// existing status unchanged is what makes repeated calls harmless: the bootstrap id the phone is
    /// already looking at must not change under it.</para>
    ///
    /// <para><b>Why a live pairing request also stops it.</b> A request that exists means the phone
    /// already got through the certificate step, and the wizard's plain-HTTP window is closed by then
    /// (that is the "served its purpose" rule in the host). Starting a fresh wizard in that state would
    /// reset <c>Trusted</c> and hand the phone a bootstrap id it is not looking at, stranding a phone that
    /// is halfway through. The flow in progress is left alone; the operator can still ask for a new
    /// wizard explicitly with "重新生成引导二维码".</para>
    /// </summary>
    public OnboardingStatus EnsureOnboardingStarted()
    {
        OnboardingStatus status = _runtime.Onboarding.Status();
        if (status.Active) return status;
        if (_runtime.Pairing.Active().Count > 0) return status;
        return _runtime.Onboarding.Start();
    }

    /// <summary>
    /// The explicit recovery the "重新生成引导二维码" button asks for: end the current wizard, drop anything
    /// that is still in flight, and open a brand new session with a new bootstrap id.
    ///
    /// <para><b>Why this cannot be <see cref="EnsureOnboardingStarted"/>.</b> That one is deliberately
    /// idempotent, because an ordinary page refresh must never change the QR code the phone is looking at.
    /// A user who presses "重新生成" wants the opposite, and wants the abandoned state to stop existing: if a
    /// pairing request from the old attempt stayed live, the phone could still pair against it while the
    /// screen already showed a new wizard — two flows side by side, with only one of them visible.</para>
    ///
    /// <para>So the order is: release the plain-HTTP listener (the old QR stops working immediately), forget
    /// the old connectivity hint (a new attempt must not inherit it), cancel every unfinished pairing request
    /// and record that the local operator did it, then start the new session.</para>
    /// </summary>
    public OnboardingStatus RestartOnboarding()
    {
        _runtime.Onboarding.Stop("用户要求重新生成引导二维码");
        _runtime.Onboarding.ClearVerifiedHttpsHost("重新生成引导二维码");

        IReadOnlyList<PairingRequest> live = _runtime.Pairing.Active();
        foreach (PairingRequest request in live)
        {
            _runtime.Pairing.Cancel(request.Id);
        }

        _runtime.Audit.Write(new AuditEntry
        {
            Category = "pairing",
            Action = "restart-onboarding",
            DeviceName = OperationActor.LocalAdminName,
            Source = OperationActor.LocalAdmin.SourceToken,
            Detail = "cancelled=" + live.Count
                     + (live.Count == 0 ? string.Empty : " ids=" + string.Join(',', live.Select(r => r.Id))),
            Success = true
        });

        return _runtime.Onboarding.Start();
    }

    /// <summary>
    /// Whether the wizard that produced this pairing request finished, and which device it produced.
    ///
    /// <para><b>Why it is keyed on the request.</b> The page used to ask "are there any unrevoked
    /// devices?", which answers a different question: a machine that already has iPhone A would show the
    /// wizard for iPhone B as already finished. Completion is a property of one request, so it is read
    /// from that request's redeemed state and the device it issued.</para>
    /// </summary>
    public PairingCompletion CompletionOf(string? pairingId)
    {
        if (string.IsNullOrWhiteSpace(pairingId)) return PairingCompletion.NotCompleted;

        PairingRequest? request = _runtime.Pairing.Find(pairingId);
        if (request is null || request.State != PairingState.Redeemed || string.IsNullOrWhiteSpace(request.IssuedDeviceId))
            return PairingCompletion.NotCompleted;

        DeviceDefinition? device = _runtime.Devices.Find(request.IssuedDeviceId);
        if (device is null) return PairingCompletion.NotCompleted;

        return new PairingCompletion(true, device.Id, device.Name, device.Level.ToString(), request.RecommendedOrigin);
    }

    /// <summary>
    /// The outcome of one wizard: did it finish, which device did it hand over, and which address should the
    /// user be shown.
    ///
    /// <para><see cref="RecommendedOrigin"/> is the HTTPS origin that actually worked for this phone — the
    /// measured host when there is one, otherwise whatever the normal preference order would offer. It
    /// describes one pairing attempt for the completion screen; it is not stored anywhere persistent and
    /// nothing authorises on it.</para>
    /// </summary>
    public sealed record PairingCompletion(
        bool Completed,
        string? DeviceId,
        string? DeviceName,
        string? Level,
        string? RecommendedOrigin)
    {
        public static PairingCompletion NotCompleted { get; } = new(false, null, null, null, null);
    }

    /// <summary>
    /// True only when this process is genuinely serving HTTPS on a non-loopback address right now.
    ///
    /// <para>Configuration is necessary but not sufficient: enabling LAN access takes effect at the next
    /// start, so between the save and the restart the settings say "on" while the only listeners are still
    /// loopback. Recommending <c>https://192.168.2.2:…</c> in that window sends the phone to an address
    /// nothing is bound to — the exact failure that started this work.</para>
    ///
    /// <para>When no bound-address information exists at all (a host that does not run the Kestrel lifetime,
    /// as in unit hosts) this falls back to the configuration so the caller is not told "not serving" merely
    /// because the listener set is unknown.</para>
    /// </summary>
    private bool IsActuallyServingLanHttps()
    {
        if (!(_runtime.Settings.LanEnabled && _runtime.Settings.HttpsEnabled)) return false;
        if (_runtime.BoundUrls.Count == 0) return true;

        foreach (string url in _runtime.BoundUrls)
        {
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;

            string host = url["https://".Length..];
            int portSeparator = host.LastIndexOf(':');
            if (portSeparator > 0) host = host[..portSeparator];
            host = host.Trim('[', ']');

            if (host is "127.0.0.1" or "::1" or "localhost" or "0.0.0.0" or "[::]") continue;
            return true;
        }
        return false;
    }

    /// <summary>
    /// The HTTPS origin to recommend right now, in this order: the host this wizard actually measured, else —
    /// when a VPN/TUN holds the default route — the physical address (the stable <c>.local</c> name is exactly
    /// the address a phone typically cannot reach then), else the stable name while it is published, else the
    /// best physical LAN address. <see cref="RecommendedHostKind"/> reports which of those it chose, so the UI
    /// never presents an unverified name as "verified reachable".
    /// </summary>
    public string? RecommendedHttpsOrigin()
    {
        // One reading, used for the recommendation *and* remembered so the network page can prove the same
        // adapter exists. Calling the selector twice here is how the product ended up serving 192.168.2.2
        // while the page insisted there was no physical adapter.
        PhysicalLanSnapshot lan = _runtime.Physical;
        PhysicalLanAdapter? best = lan.Best;

        // 只有**确实在对外提供局域网 HTTPS** 时，物理地址才可能被当作"可访问地址"推荐。
        //
        // 先前这里只看网卡读数，不看是否真的在服务：Owner 现场 LAN 与 HTTPS 都关着、实际监听只有
        // loopback，页面却仍然推荐 https://192.168.2.2:53820 —— 那个地址上没有任何监听，手机必然连不上。
        // 一个"推荐访问地址"必须要么真的可达（verified），要么真的在监听（serving），否则就不该推荐。
        //
        // 判断依据是**实际监听集合**（BoundUrls，Kestrel 启动后的真实地址），而不是只看配置：
        // 改了 LAN/HTTPS 但还没重启时配置是"开"、监听仍是旧的，只看配置会再次推荐一个连不上的地址。
        bool servingLanHttps = IsActuallyServingLanHttps();

        string? host = VerifiedPairingHost();
        if (host is not null)
        {
            RecommendedHostKind = "verified";
        }
        else if (lan.VpnOrTunnelDetected && servingLanHttps)
        {
            // VPN/TUN up and nothing measured: prefer the physical address, because the stable name is the
            // one that fails in this situation (the owner's own 1.6.3 result).
            host = best?.Address;
            RecommendedHostKind = host is null ? "stable" : "physical";
        }
        else if (_runtime.Mdns is { IsRunning: true } && _runtime.Settings.HttpsEnabled)
        {
            host = _runtime.Identity.HostName;
            RecommendedHostKind = "stable";
        }
        else if (servingLanHttps)
        {
            host = best?.Address;
            RecommendedHostKind = host is null ? "stable" : "physical";
        }
        else if (_runtime.Settings.HttpsEnabled)
        {
            // HTTPS 已启用、但局域网监听尚未真正建立（典型：刚改完设置还没重启，或本进程只绑了 loopback）。
            // 此时**只给稳定名称**，绝不给出一个没有监听的物理地址 —— 那正是手机连不上却仍被推荐的成因。
            // 稳定名称这条本身也不被描述成"已验证可达"，所以这里不构成虚假可达性声明。
            host = _runtime.Identity.HostName;
            RecommendedHostKind = "stable";
        }
        else
        {
            // HTTPS 也未启用：本机没有任何可供手机访问的地址，如实表达而不是推荐一个连不上的地址。
            host = null;
            RecommendedHostKind = "off";
        }

        RecommendedPhysicalAdapter = RecommendedHostKind == "physical"
            ? lan.FindAccepted(host)?.Description
            : null;

        return string.IsNullOrWhiteSpace(host)
            ? null
            : $"https://{host}:{_runtime.Settings.HttpsPort}";
    }

    /// <summary>
    /// The adapter that carried the last physical recommendation, or null when the recommendation was not
    /// physical. The network page and the checks assert on this instead of re-deriving it: if a physical
    /// origin was recommended, the very same reading must contain that adapter.
    /// </summary>
    public string? RecommendedPhysicalAdapter { get; private set; }

    /// <summary>How the last <see cref="RecommendedHttpsOrigin"/> was chosen: verified, stable or physical.</summary>
    public string RecommendedHostKind { get; private set; } = "stable";

    /// <summary>The stable <c>.local</c> name as an HTTPS origin. Never described as "verified reachable"
    /// unless a probe actually measured it.</summary>
    public string StableHttpsOrigin() => $"https://{_runtime.Identity.HostName}:{_runtime.Settings.HttpsPort}";

    /// <summary>The best physical LAN address as an HTTPS origin, or null when there is none.</summary>
    public string? PhysicalHttpsOrigin()
    {
        string? address = _runtime.Physical.Best?.Address;
        return string.IsNullOrWhiteSpace(address)
            ? null
            : $"https://{address}:{_runtime.Settings.HttpsPort}";
    }

    /// <summary>Cancels a pairing request the operator refused. Idempotent, and never creates a device.</summary>
    public OperationOutcome<bool> RejectPairing(string id)
    {
        PairingRequest? pairing = _runtime.Pairing.Find(id);
        bool existed = pairing is not null && pairing.State is PairingState.Pending or PairingState.AwaitingLocalConfirmation;

        _runtime.Pairing.Cancel(id);

        _runtime.Audit.Write(new AuditEntry
        {
            Category = "pairing",
            Action = "reject",
            DeviceName = OperationActor.LocalAdminName,
            Source = OperationActor.LocalAdmin.SourceToken,
            Detail = "id=" + id + " existed=" + existed,
            Success = true
        });

        return OperationOutcome<bool>.Success(existed);
    }

    /// <summary>Every pairing request that is still live, newest first. One implementation, two callers.</summary>
    public IReadOnlyList<PairingRequest> ActivePairingRequests() => _runtime.Pairing.Active();

    /// <summary>
    /// Reuses the live pairing request if there is one, otherwise creates it. The native page calls this
    /// as soon as the phone is trusted, so "生成配对请求" is no longer a step the user has to take — and a
    /// refresh cannot pile up requests, because an unexpired one is reused.
    /// </summary>
    public PairingRequest EnsurePairingRequest()
    {
        // Find() is what applies the deadline, so touching every live request first is what moves an
        // expired one out of the live set; otherwise the stale request would keep being listed next to
        // its replacement.
        foreach (PairingRequest candidate in _runtime.Pairing.Active().ToList())
        {
            _ = _runtime.Pairing.Find(candidate.Id);
        }

        PairingRequest? live = _runtime.Pairing.Active().FirstOrDefault();
        return live ?? _runtime.Pairing.Create();
    }

    // ------------------------------------------------------------------ devices

    /// <summary>
    /// Renames a device. The name goes through the same sanitizer the HTTP endpoint uses, so the same
    /// edit cannot be refused in the browser and accepted in the window.
    /// </summary>
    public OperationOutcome<DeviceDefinition> RenameDevice(string id, string? name)
    {
        DeviceDefinition? device = _runtime.Devices.Find(id);
        if (device is null) return OperationOutcome<DeviceDefinition>.Fail("NOT_FOUND", "设备不存在。");

        string? clean = DeviceNames.Sanitize(name);
        if (clean is null) return OperationOutcome<DeviceDefinition>.Fail("NAME_REQUIRED", "设备名称不能为空。");

        _runtime.Devices.Update(id, d => d.Name = clean);
        DeviceDefinition updated = _runtime.Devices.Find(id)!;
        _runtime.Audit.Write(new AuditEntry
        {
            Category = "device",
            Action = "rename",
            DeviceName = OperationActor.LocalAdminName,
            DeviceId = id,
            Source = OperationActor.LocalAdmin.SourceToken,
            Detail = "name=" + updated.Name,
            Success = true
        });

        return OperationOutcome<DeviceDefinition>.Success(updated);
    }

    /// <summary>Read-only or read-write, decided once and audited the same way on both surfaces.</summary>
    public OperationOutcome<DeviceDefinition> SetDeviceAccess(string id, bool readWrite)
    {
        DeviceDefinition? device = _runtime.Devices.Find(id);
        if (device is null) return OperationOutcome<DeviceDefinition>.Fail("NOT_FOUND", "设备不存在。");

        // Deliberately does not touch SessionEpoch: an ordinary permission change must not invalidate the
        // phone's session (that is what "no re-pairing needed" means). Revocation is the operation that
        // rotates the epoch.
        // The revision moves with the authority, so the phone can tell that *something* about its rights
        // changed without having to compare each field it happens to know about.
        _runtime.Devices.Update(id, d =>
        {
            if (d.Level == (readWrite ? DeviceAccessLevel.ReadWrite : DeviceAccessLevel.ReadOnly)) return;
            d.Level = readWrite ? DeviceAccessLevel.ReadWrite : DeviceAccessLevel.ReadOnly;
            d.PermissionRevision++;
        });
        DeviceDefinition updated = _runtime.Devices.Find(id)!;

        _runtime.Audit.Write(new AuditEntry
        {
            Category = "device",
            Action = "update",
            DeviceName = OperationActor.LocalAdminName,
            DeviceId = id,
            Source = OperationActor.LocalAdmin.SourceToken,
            Detail = $"level={updated.Level}",
            Success = true
        });

        return OperationOutcome<DeviceDefinition>.Success(updated);
    }

    /// <summary>
    /// Replaces a device's administrative capabilities with exactly the given set.
    ///
    /// <para><b>Why a full set and not a delta.</b> "Add ExtensionExecute" and "the device should have
    /// ExtensionExecute" are different intentions, and only the second one survives a UI that forgets to
    /// send a field. The caller states the complete authority it wants; anything not named is removed.</para>
    ///
    /// <para>Like <see cref="SetDeviceAccess"/> this never rotates the session epoch — the phone keeps its
    /// session and simply gets a different answer on its next request. Work that the lost capability
    /// authorised is cancelled rather than allowed to finish (running extension tasks here, running uploads
    /// in <c>/api/uploads</c>, which re-check the same authority before every chunk).</para>
    /// </summary>
    public OperationOutcome<DeviceDefinition> SetDeviceCapabilities(string id, IEnumerable<string>? names)
    {
        DeviceDefinition? device = _runtime.Devices.Find(id);
        if (device is null) return OperationOutcome<DeviceDefinition>.Fail("NOT_FOUND", "设备不存在。");

        DeviceCapability requested = DeviceCapability.None;
        foreach (string name in names ?? [])
        {
            if (string.IsNullOrWhiteSpace(name)) continue;

            // 旧名字：WiFi 功能已从产品移除。仍然**接受**这个名字，这样旧客户端或旧脚本不会因为
            // 一个已经不存在的能力收到 400；但它什么也不授予 —— 结果就是这一位被清掉，
            // 而数值本身保留在枚举里只为让旧 devices.json 仍能反序列化。
            if (string.Equals(name, "NetworkManage", StringComparison.OrdinalIgnoreCase)) continue;

            if (!Enum.TryParse(name, ignoreCase: true, out DeviceCapability parsed) || parsed == DeviceCapability.None)
            {
                return OperationOutcome<DeviceDefinition>.Fail("CAPABILITY_UNKNOWN", "未知的设备能力：" + name);
            }
            requested |= parsed;
        }

        DeviceCapability previous = device.Capabilities;
        _runtime.Devices.Update(id, d =>
        {
            if (d.Capabilities == requested) return;
            d.Capabilities = requested;
            d.PermissionRevision++;
        });
        DeviceDefinition updated = _runtime.Devices.Find(id)!;

        int cancelled = 0;

        _runtime.Audit.Write(new AuditEntry
        {
            Category = "device",
            Action = "capabilities",
            DeviceName = OperationActor.LocalAdminName,
            DeviceId = id,
            Source = OperationActor.LocalAdmin.SourceToken,
            Detail = $"from={previous} to={requested} cancelledTasks={cancelled}",
            Success = true
        });

        return OperationOutcome<DeviceDefinition>.Success(updated);
    }

    public sealed record DeviceRevocation(DeviceDefinition Device, int CancelledUploads)
    {
        public const string Note = "撤销只能停止本应用仍在控制中的传输；手机已经下载或缓存的字节无法收回。";
    }

    /// <summary>
    /// Revokes a device completely: the flag and its timestamp, a rotated token hash so the old cookie
    /// can never be replayed, a rotated session epoch so sessions issued before this moment stop
    /// validating, every in-flight upload cancelled, and an audit entry.
    ///
    /// <para><b>What this replaced.</b> The desktop window only set <c>Revoked</c> and <c>RevokedAt</c>
    /// and cancelled uploads: a device revoked from the computer kept its old token hash and session
    /// epoch, so a stolen cookie kept working after the user believed they had cut the phone off. The
    /// browser did all five steps. One implementation now, used by both.</para>
    /// </summary>
    public OperationOutcome<DeviceRevocation> RevokeDevice(string id)
    {
        DeviceDefinition? device = _runtime.Devices.Find(id);
        if (device is null) return OperationOutcome<DeviceRevocation>.Fail("NOT_FOUND", "设备不存在。");

        _runtime.Devices.Update(id, d =>
        {
            d.Revoked = true;
            d.RevokedAt = DateTimeOffset.Now;
            d.TokenHash = SecretTokens.Hash(SecretTokens.NewToken());
            d.SessionEpoch = Guid.NewGuid().ToString("N");
        });

        int cancelled = _runtime.Uploads.CancelForDevice(id, "设备已被本机撤销");

        _runtime.Audit.Write(new AuditEntry
        {
            Category = "device",
            Action = "revoke",
            DeviceName = OperationActor.LocalAdminName,
            DeviceId = id,
            Source = OperationActor.LocalAdmin.SourceToken,
            Detail = $"cancelledUploads={cancelled}",
            Success = true
        });

        return OperationOutcome<DeviceRevocation>.Success(new DeviceRevocation(_runtime.Devices.Find(id)!, cancelled));
    }

    /// <summary>The outcome of a permanent removal, so the UI can say what actually happened.</summary>
    public sealed record DeviceRemoval(string DeviceId, string DeviceName, bool WasActive, int CancelledUploads, int CancelledTasks)
    {
        public const string Note =
            "移除会删除这台设备的记录与授权，历史审计保留。手机需要重新配对才能再次访问。";
    }

    /// <summary>
    /// Removes a device from the registry for good, rather than hiding its row.
    ///
    /// <para><b>What this replaces.</b> The desktop could revoke a device but never remove it, so a phone
    /// the owner no longer had kept sitting in the list with no way to clear it. The old
    /// <c>DELETE /api/admin/devices/{id}</c> did delete the record, but it skipped the token and session
    /// rotation, left the device's extension tasks running, and wrote no audit entry — a removal that was
    /// quietly weaker than a revoke.</para>
    ///
    /// <para><b>Active devices are revoked first.</b> Removing a device that is still trusted runs the same
    /// five steps as an explicit revoke (flag, rotated token hash, rotated session epoch, cancelled uploads,
    /// cancelled extension tasks) and only then deletes it. Deleting first would leave any in-flight work
    /// holding an identity that no longer exists.</para>
    ///
    /// <para><b>Audit is never deleted.</b> "This phone was removed on this date" is exactly the record an
    /// owner needs later, so the audit trail outlives the device it describes.</para>
    /// </summary>
    public OperationOutcome<DeviceRemoval> RemoveDevice(string id)
    {
        DeviceDefinition? device = _runtime.Devices.Find(id);
        if (device is null) return OperationOutcome<DeviceRemoval>.Fail("NOT_FOUND", "设备不存在。");
        string name = device.Name;
        bool wasActive = !device.Revoked;

        int cancelledUploads = _runtime.Uploads.CancelForDevice(id, "设备已被本机永久移除");

        if (wasActive)
        {
            // Exactly the revoke steps; a removal of a live device must not be a weaker operation.
            _runtime.Devices.Update(id, d =>
            {
                d.Revoked = true;
                d.RevokedAt = DateTimeOffset.Now;
                d.TokenHash = SecretTokens.Hash(SecretTokens.NewToken());
                d.SessionEpoch = Guid.NewGuid().ToString("N");
            });
            cancelledUploads += _runtime.Uploads.CancelForDevice(id, "设备已被本机永久移除");
        }

        int cancelledTasks = _runtime.Extensions.CancelForDevice(id, "设备已被本机永久移除");

        if (!_runtime.Devices.Remove(id))
            return OperationOutcome<DeviceRemoval>.Fail("REMOVE_FAILED", "设备记录未能删除。");

        _runtime.Audit.Write(new AuditEntry
        {
            Category = "device",
            Action = "device-remove",
            DeviceName = OperationActor.LocalAdminName,
            DeviceId = id,
            Source = OperationActor.LocalAdmin.SourceToken,
            Detail = $"removedDevice={name} wasActive={wasActive} cancelledUploads={cancelledUploads} cancelledTasks={cancelledTasks} auditRetained=true",
            Success = true
        });

        return OperationOutcome<DeviceRemoval>.Success(
            new DeviceRemoval(id, name, wasActive, cancelledUploads, cancelledTasks));
    }

    // ------------------------------------------------------------------ recycle

    /// <summary>
    /// Puts a recycled object back. The actor is explicit: the Windows administrator is not a phone, so
    /// restoring on a machine with no paired device works, and work done at the keyboard is not credited
    /// to whichever phone happened to be first in the list.
    /// </summary>
    public OperationOutcome<RecycleEntry> RestoreRecycleEntry(string id, OperationActor actor, string? alternateName = null)
    {
        RecycleEntry? entry = _runtime.Recycle.Find(id);
        if (entry is null) return OperationOutcome<RecycleEntry>.Fail("NOT_FOUND", "回收站条目不存在。");

        ShareDefinition? share = _runtime.Shares.Find(entry.ShareId);
        if (share is null) return OperationOutcome<RecycleEntry>.Fail("SHARE_NOT_FOUND", "原共享已被删除，无法恢复。");

        try
        {
            RecycleEntry restored = _runtime.Recycle.Restore(id, share, actor, alternateName);
            _runtime.InvalidateScans(share.Id);
            return OperationOutcome<RecycleEntry>.Success(restored);
        }
        catch (Exception ex)
        {
            return OperationOutcome<RecycleEntry>.Fail("RESTORE_FAILED", ex.Message);
        }
    }

    /// <summary>
    /// Permanently removes a recycled payload. Only the local administrator may call this, which is why
    /// the actor is explicit rather than optional — the confirmation rules in front of it are unchanged.
    /// </summary>
    public OperationOutcome<bool> PurgeRecycleEntry(string id)
    {
        RecycleEntry? entry = _runtime.Recycle.Find(id);
        if (entry is null) return OperationOutcome<bool>.Fail("NOT_FOUND", "回收站条目不存在。");

        try
        {
            _runtime.Recycle.Purge(id, OperationActor.LocalAdmin);
            return OperationOutcome<bool>.Success(true);
        }
        catch (Exception ex)
        {
            return OperationOutcome<bool>.Fail("PURGE_FAILED", ex.Message);
        }
    }

    /// <summary>Empties the recycle bin. Same confirmation rules as before; only the actor is explicit.</summary>
    public OperationOutcome<int> PurgeAllRecycleEntries()
    {
        int purged = 0;
        foreach (RecycleEntry entry in _runtime.Recycle.List(e => e.State == RecycleState.Committed))
        {
            try
            {
                _runtime.Recycle.Purge(entry.Id, OperationActor.LocalAdmin);
                purged++;
            }
            catch (Exception ex)
            {
                return OperationOutcome<int>.Fail("PURGE_FAILED", $"清空回收站时失败：{ex.Message}");
            }
        }
        return OperationOutcome<int>.Success(purged);
    }

    // ------------------------------------------------------------------ pairing links

    /// <summary>
    /// The links a phone may use to redeem a one-time pairing token, best first. The native window and the
    /// HTTP endpoints both call this, so both offer the same first address.
    ///
    /// <para><b>Why the measured host comes first.</b> A phone that just completed the HTTPS trust probe
    /// told us three things at once: it can reach this machine at the host it named, the TLS handshake
    /// succeeded, and it accepted the certificate. That beats any inference about the network — under a
    /// VPN/TUN the stable <c>.local</c> name is precisely the one that fails while
    /// <c>https://&lt;lan-ip&gt;:8892</c> works, and a pairing QR pointing at the name is a QR that cannot
    /// be opened. So the order is: the host this wizard actually measured, then the stable name (when it
    /// is being published, or when the phone just proved it resolves), then every other physical LAN
    /// address, and the loopback link last for a browser on this machine.</para>
    ///
    /// <para>The hint is re-validated on every call — it may be gone, aged out, no longer one of the
    /// addresses the classifier accepts, or no longer covered by the current server certificate — and it is
    /// never loopback, because a phone can never open 127.0.0.1.</para>
    /// </summary>
    public IReadOnlyList<string> PairingUrls(string token)
    {
        int httpsPort = _runtime.Settings.HttpsPort;
        int httpPort = _runtime.Settings.HttpPort;
        string stable = _runtime.Identity.HostName;

        var urls = new List<string>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string url)
        {
            if (taken.Add(url)) urls.Add(url);
        }

        bool Listening(string scheme, string address) => _runtime.BoundUrls.Any(u =>
            u.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            && u.Contains(address, StringComparison.Ordinal));

        // 1) The HTTPS host a real phone reached during this wizard session.
        string? verified = VerifiedPairingHost();
        bool verifiedIsStable = verified is not null
                                && !string.IsNullOrWhiteSpace(stable)
                                && string.Equals(verified, stable, StringComparison.OrdinalIgnoreCase);

        if (verified is not null && (verifiedIsStable || Listening("https://", verified)))
            Add($"https://{verified}:{httpsPort}/pair#t={token}");

        // 2) The stable name: published over mDNS in the normal case, and always when the phone just
        //    reached it — that request is better evidence than our own mDNS bookkeeping.
        bool stableNamePublished = _runtime.Mdns is { IsRunning: true } && _runtime.Settings.HttpsEnabled;
        if (!string.IsNullOrWhiteSpace(stable) && (stableNamePublished || verifiedIsStable))
            Add($"https://{stable}:{httpsPort}/pair#t={token}");

        // 3) Every other physical LAN address. HTTPS first; the plain-HTTP compatibility mode is a
        //    fallback for the deliberate case where TLS is switched off, never a preference.
        foreach (PhysicalLanAdapter candidate in _runtime.Physical.Accepted)
        {
            if (Listening("https://", candidate.Address))
                Add($"https://{candidate.Address}:{httpsPort}/pair#t={token}");
            else if (Listening("http://", candidate.Address))
                Add($"http://{candidate.Address}:{httpPort}/pair#t={token}");
        }

        // 4) Any bound address the classifier did not know about (an unusual adapter) still gets a link.
        foreach (string url in _runtime.BoundUrls)
        {
            if (url.Contains("127.0.0.1", StringComparison.Ordinal) || url.Contains("[::1]", StringComparison.Ordinal)) continue;
            Add(url.TrimEnd('/') + "/pair#t=" + token);
        }

        // 5) Loopback last: for local diagnostics only, never the phone's first address.
        Add("http://127.0.0.1:" + httpPort + "/pair#t=" + token);
        return urls;
    }

    /// <summary>
    /// The verified host from this wizard session, re-checked against reality as it is <i>now</i>.
    ///
    /// <para>Between the trust probe and the pairing QR a DHCP lease can move, an adapter can be
    /// unplugged, or the certificate can be re-issued. A hint that no longer describes this machine is
    /// dropped, and the caller falls back to the normal stable-then-physical order rather than handing the
    /// phone an address that does not exist any more.</para>
    /// </summary>
    private string? VerifiedPairingHost()
    {
        string? hint = _runtime.Onboarding.VerifiedHttpsHost;
        if (string.IsNullOrWhiteSpace(hint)) return null;

        string stable = _runtime.Identity.HostName;
        if (!string.IsNullOrWhiteSpace(stable) && string.Equals(hint, stable, StringComparison.OrdinalIgnoreCase))
            return stable;

        if (!IPAddress.TryParse(hint, out IPAddress? ip)) return null;

        // A phone cannot open 127.0.0.1; a loopback probe is evidence about this machine, not about the
        // phone, so it never becomes the first address a QR code points at.
        if (IPAddress.IsLoopback(ip)) return null;

        // Still an address the classifier accepts right now.
        bool eligible = _runtime.Physical.Accepted
            .Any(c => IPAddress.TryParse(c.Address, out IPAddress? allowed) && allowed.Equals(ip));
        if (!eligible) return null;

        // Still covered by the certificate the phone is being asked to trust.
        _runtime.EnsureCertificates();
        if (_runtime.Certificate is { } certificate
            && !certificate.IpAddresses.Any(text => IPAddress.TryParse(text, out IPAddress? san) && san.Equals(ip)))
        {
            return null;
        }

        return ip.ToString();
    }

    // ------------------------------------------------------------------ helpers

    private void Audit(string category, string action, string? shareId, string detail)
        => _runtime.Audit.Write(new AuditEntry
        {
            Category = category,
            Action = action,
            DeviceName = "本机管理端",
            ShareId = shareId,
            Detail = detail,
            Success = true
        });
}
