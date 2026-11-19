using System.Diagnostics;
using LanDisk.Core.Configuration;
using LanDisk.Core.Interop;
using LanDisk.Core.Model;
using LanDisk.Core.Networking;
using LanDisk.Core.Paths;
using LanDisk.Core.Security;
using LanDisk.Server.Services;

namespace LanDisk.Server.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin");

        // The local management page has no login step. A request that genuinely arrives over the
        // loopback socket, from a same-origin browser context, is granted an administrator session
        // directly. The one time token printed at startup remains available as a recovery path
        // (for example when a browser profile blocks cookies), but it is not part of normal use.
        group.MapPost("/session", (HttpContext context, LanDiskRuntime runtime, AdminSessionRequest? request) =>
        {
            RequestSession session = context.Session();
            if (!session.IsLoopback)
                return ApiHelpers.Fail(StatusCodes.Status403Forbidden, "LOOPBACK_ONLY", "管理入口只允许在本机访问。");

            bool tokenSupplied = !string.IsNullOrWhiteSpace(request?.Token)
                                 || !string.IsNullOrWhiteSpace(context.Request.Headers["X-LanDisk-Admin-Token"].ToString());

            if (tokenSupplied)
            {
                string supplied = request?.Token
                                  ?? context.Request.Headers["X-LanDisk-Admin-Token"].ToString();

                if (!SecretTokens.FixedTimeEquals(supplied.Trim(), runtime.AdminBootstrapToken))
                    return ApiHelpers.Fail(StatusCodes.Status401Unauthorized, "ADMIN_TOKEN_INVALID", "管理令牌不正确。");
            }
            else if (!RequestOrigin.IsTrustedBrowserRequest(context, out string reason))
            {
                return ApiHelpers.Fail(StatusCodes.Status403Forbidden, reason,
                    "该请求不是来自本机同源页面，不能自动进入管理端。");
            }

            AdminSession admin = runtime.AdminSessions.Issue(session.RemoteAddress, out string rawToken);
            ApiHelpers.SetSessionCookie(context, SessionKeys.AdminCookie, rawToken, TimeSpan.FromHours(12));
            ApiHelpers.SetCsrfCookie(context, admin.CsrfToken);

            return Results.Json(new
            {
                ok = true,
                csrfToken = admin.CsrfToken,
                expiresAt = admin.ExpiresAt,
                tokenRequired = false
            }, ApiHelpers.Json);
        });

        // Idempotent entry point: a loopback browser can call this on every page load and get a
        // working session without knowing any secret.
        group.MapGet("/session", (HttpContext context, LanDiskRuntime runtime, bool? renew) =>
        {
            RequestSession session = context.Session();
            if (!session.IsLoopback)
                return ApiHelpers.Fail(StatusCodes.Status403Forbidden, "LOOPBACK_ONLY", "管理入口只允许在本机访问。");

            if (session.Admin is not null && renew != true)
            {
                ApiHelpers.SetCsrfCookie(context, session.Admin.CsrfToken);
                return Results.Json(new
                {
                    ok = true,
                    csrfToken = session.Admin.CsrfToken,
                    expiresAt = session.Admin.ExpiresAt,
                    tokenRequired = false
                }, ApiHelpers.Json);
            }

            if (!RequestOrigin.IsTrustedBrowserRequest(context, out string reason))
                return ApiHelpers.Fail(StatusCodes.Status403Forbidden, reason, "该请求不是来自本机同源页面。");

            AdminSession admin = runtime.AdminSessions.Issue(session.RemoteAddress, out string rawToken);
            ApiHelpers.SetSessionCookie(context, SessionKeys.AdminCookie, rawToken, TimeSpan.FromHours(12));
            ApiHelpers.SetCsrfCookie(context, admin.CsrfToken);

            return Results.Json(new
            {
                ok = true,
                csrfToken = admin.CsrfToken,
                expiresAt = admin.ExpiresAt,
                tokenRequired = false
            }, ApiHelpers.Json);
        });

        // Explicit recovery path for the rare case where automatic entry is not available.
        group.MapGet("/session/token-required", (HttpContext context, LanDiskRuntime runtime) =>
        {
            RequestSession session = context.Session();
            if (!session.IsLoopback)
                return ApiHelpers.Fail(StatusCodes.Status403Forbidden, "LOOPBACK_ONLY", "管理入口只允许在本机访问。");

            bool trusted = RequestOrigin.IsTrustedBrowserRequest(context, out string reason);
            return Results.Json(new
            {
                loopback = session.IsLoopback,
                trustedBrowserRequest = trusted,
                reason,
                autoEntryAvailable = trusted,
                fallbackAvailable = true
            }, ApiHelpers.Json);
        });

        group.MapGet("/status", (HttpContext context, LanDiskRuntime runtime) =>
        {
            if (!RequireAdmin(context, runtime, out IResult? error)) return error!;

            return Results.Json(new
            {
                version = BuildInfo.Version,
                buildTime = BuildInfo.BuildTime,
                buildCommit = BuildInfo.Commit,
                buildId = BuildInfo.BuildId,
                startedAt = runtime.StartedAt,
                uptimeSeconds = (long)(DateTimeOffset.Now - runtime.StartedAt).TotalSeconds,
                dataRoot = runtime.Paths.DataRoot,
                environment = new
                {
                    os = Environment.OSVersion.VersionString,
                    architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                    framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    machineName = Environment.MachineName,
                    processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                    is64BitProcess = Environment.Is64BitProcess
                },
                sharing = new
                {
                    shareCount = runtime.Shares.All().Count,
                    enabledShareCount = runtime.Shares.All().Count(s => s.Enabled),
                    deviceCount = runtime.Devices.All().Count(d => !d.Revoked),
                    recycleCount = runtime.Recycle.List().Count
                },
                network = new
                {
                    lanEnabled = runtime.Settings.LanEnabled,
                    httpsEnabled = runtime.Settings.HttpsEnabled,
                    httpCompatModeEnabled = runtime.Settings.HttpCompatModeEnabled,
                    httpPort = runtime.Settings.HttpPort,
                    httpsPort = runtime.Settings.HttpsPort,
                    bindAddress = runtime.Settings.LanBindAddress,
                    allowedCidr = runtime.Settings.AllowedCidr,
                    boundUrls = runtime.BoundUrls
                }
            }, ApiHelpers.Json);
        });

        // ---- shares -------------------------------------------------------

        group.MapGet("/shares", (HttpContext context, LanDiskRuntime runtime) =>
        {
            if (!RequireAdmin(context, runtime, out IResult? error)) return error!;
            return Results.Json(new { shares = runtime.Shares.All().Select(AuthEndpoints.AdminShareDto).ToList() }, ApiHelpers.Json);
        });

        group.MapGet("/volumes", (HttpContext context, LanDiskRuntime runtime) =>
        {
            if (!RequireAdmin(context, runtime, out IResult? error)) return error!;

            var volumes = new List<object>();
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    string root = drive.RootDirectory.FullName;
                    string? guid = Win32FileSystem.TryGetVolumeGuidPath(root);
                    bool isSystem = string.Equals(root.TrimEnd('\\'), Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

                    volumes.Add(new
                    {
                        mountPoint = root,
                        volumeGuidPath = guid,
                        label = SafeLabel(drive),
                        driveType = drive.DriveType.ToString(),
                        fileSystem = SafeFormat(drive),
                        totalBytes = SafeTotal(drive),
                        freeBytes = SafeFree(drive),
                        isReady = SafeReady(drive),
                        isSystemVolume = isSystem,
                        recommendedForWholeVolumeShare = drive.DriveType == DriveType.Removable && SafeReady(drive)
                    });
                }
                catch { }
            }

            return Results.Json(new { volumes }, ApiHelpers.Json);
        });

        // Directory picker for the local operator only. It never exposes file contents.
        group.MapGet("/browse-local", (HttpContext context, LanDiskRuntime runtime, string? path) =>
        {
            if (!RequireAdmin(context, runtime, out IResult? error)) return error!;

            try
            {
                var directories = new List<object>();
                var files = new List<object>();
                string current;

                if (string.IsNullOrWhiteSpace(path))
                {
                    current = string.Empty;
                    foreach (DriveInfo drive in DriveInfo.GetDrives())
                    {
                        try
                        {
                            if (!drive.IsReady) continue;
                            directories.Add(new
                            {
                                name = SafeLabel(drive) + " (" + drive.Name.TrimEnd('\\') + ")",
                                path = drive.RootDirectory.FullName,
                                isDrive = true
                            });
                        }
                        catch { }
                    }
                }
                else
                {
                    current = Path.GetFullPath(path);
                    if (!Directory.Exists(current))
                        return ApiHelpers.Fail(StatusCodes.Status404NotFound, "NOT_FOUND", "目录不存在。");

                    var info = new DirectoryInfo(current);
                    if (info.Parent is not null)
                        directories.Add(new { name = "..", path = info.Parent.FullName, isDrive = false });

                    foreach (DirectoryInfo dir in info.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Take(500))
                    {
                        bool reparse;
                        try { reparse = dir.Attributes.HasFlag(FileAttributes.ReparsePoint); } catch { reparse = false; }
                        directories.Add(new { name = dir.Name, path = dir.FullName, isDrive = false, isLink = reparse });
                    }

                    foreach (FileInfo file in info.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Take(200))
                    {
                        files.Add(new { name = file.Name, length = file.Length });
                    }
                }

                return Results.Json(new { current, directories, files }, ApiHelpers.Json);
            }
            catch (Exception ex)
            {
                return ApiHelpers.MapException(ex);
            }
        });

        group.MapPost("/shares", (HttpContext context, LanDiskRuntime runtime, CreateShareRequest request) =>
        {
            if (!RequireAdmin(context, runtime, out IResult? error)) return error!;

            OperationOutcome<ShareDefinition> outcome = runtime.Admin.CreateShare(
                request.RootPath, request.DisplayName, request.Note,
                request.AllowUpload == true, request.AllowRename == true, request.AllowDelete == true);

            return outcome.Ok
                ? Results.Json(new { share = AuthEndpoints.AdminShareDto(outcome.Value!) }, ApiHelpers.Json)
                : Fail(outcome);
        });

        group.MapPatch("/shares/{id}", (HttpContext context, LanDiskRuntime runtime, string id, UpdateShareRequest request) =>
        {
            if (!RequireAdmin(context, runtime, out IResult? error)) return error!;

            OperationOutcome<ShareDefinition> outcome = runtime.Admin.UpdateShare(id,
                request.DisplayName, request.Enabled, request.Note,
                request.AllowRead, request.AllowUpload, request.AllowRename, request.AllowDelete);

            return outcome.Ok
                ? Results.Json(new { share = AuthEndpoints.AdminShareDto(outcome.Value!) }, ApiHelpers.Json)
                : Fail(outcome);
        });

        // Re-confirms the volume identity after the media was reconnected or the letter changed.
        group.MapPost("/shares/{id}/reconfirm", (HttpContext context, LanDiskRuntime runtime, string id) =>
        {
            if (!RequireAdmin(context, runtime, out IResult? error)) return error!;

            OperationOutcome<ShareDefinition> outcome = runtime.Admin.ReconfirmShare(id);

            return outcome.Ok
                ? Results.Json(new { share = AuthEndpoints.AdminShareDto(outcome.Value!) }, ApiHelpers.Json)
                : Fail(outcome);
        });

        group.MapDelete("/shares/{id}", (HttpContext context, LanDiskRuntime runtime, string id) =>
        {
            if (!RequireAdmin(context, runtime, out IResult? error)) return error!;

            OperationOutcome<int> outcome = runtime.Admin.DeleteShare(id);

            return outcome.Ok
                ? Results.Json(new { ok = true, removedFavorites = outcome.Value }, ApiHelpers.Json)
                : Fail(outcome);
        });

        // ---- operations dashboard -----------------------------------------
        //
        // The numbers a person watches, as opposed to the verdict the updater reads. Sampled on request so a
        // dashboard nobody opened costs nothing.
        group.MapGet("/operations", (HttpContext context, LanDiskRuntime runtime) =>
        {
            if (!RequireAdmin(context, runtime, out IResult? error)) return error!;
            return Results.Json(runtime.Metrics.Snapshot(), ApiHelpers.Json);
        });

        // ---- mobile theme -------------------------------------------------
        //
        // The operator decides how the phones look; the phones pick it up from the authenticated API. A
        // profile is colour roles only, and an unreadable combination is refused with the exact failing
        // pair rather than stored and discovered later on a phone screen.

        group.MapGet("/mobile-theme", (HttpContext context, LanDiskRuntime runtime) =>
        {
            if (!RequireAdmin(context, runtime, out IResult? error)) return error!;
            return Results.Json(runtime.MobileTheme.Choices(), ApiHelpers.Json);
        });

        group.MapPut("/mobile-theme", (HttpContext context, LanDiskRuntime runtime, MobileThemeRequest request) =>
        {
            if (!RequireAdmin(context, runtime, out IResult? error)) return error!;

            (bool ok, ThemeValidation validation, string message) = runtime.MobileTheme.Set(
                request.ProfileId, request.Accent, request.Background, request.Surface, request.Name);

            if (!ok)
            {
                return ApiHelpers.Fail(StatusCodes.Status400BadRequest, "THEME_INVALID",
                    message + (validation.Problems.Count == 0 ? "" : "；" + string.Join("；", validation.Problems)));
            }

            runtime.Audit.Write(new AuditEntry
            {
                Category = "theme",
                Action = "mobile-theme",
                DeviceName = OperationActor.LocalAdminName,
                Source = OperationActor.LocalAdmin.SourceToken,
                Detail = $"profile={runtime.MobileTheme.ProfileId} revision={runtime.MobileTheme.Revision} worstContrast={validation.WorstBody:0.0}",
                Success = true
            });

            return Results.Json(new
            {
                ok = true,
                name = message,
                worstContrast = validation.WorstBody,
                revision = runtime.MobileTheme.Revision
            }, ApiHelpers.Json);
        });

        // ---- devices ------------------------------------------------------
        group.MapGet("/devices", (HttpContext context, LanDiskRuntime runtime) =>
        {
            if (!RequireAdmin(context, runtime, out IResult? error)) return error!;

            return Results.Json(new
            {
                devices = runtime.Devices.All().Select(d => new
                {
                    id = d.Id,
                    name = d.Name,
                    level = d.Level.ToString(),
                    // Administrative reach travels with the device, so the next paired phone starts with
                    // none of it instead of inheriting whatever the last one was given.
                    capabilities = d.Capabilities.ToString(),
                    capabilityList = CapabilityList(d.Capabilities),
                    presence = AuthEndpoints.Presence(d),
                    revoked = d.Revoked,
                    createdAt = d.CreatedAt,
                    lastSeenAt = d.LastSeenAt,
                    lastSeenIp = d.LastSeenIp,
                    userAgent = d.UserAgent
                })
            }, ApiHelpers.Json);
        });

        group.MapPatch("/devices/{id}", (HttpContext context, LanDiskRuntime runtime, string id, UpdateDeviceRequest request) =>
        {
            if (!RequireAdmin(context, runtime, out IResult? error)) return error!;

            // Both surfaces call AdminOperations, so the browser and the native window cannot disagree
            // about what "rename" or "改为读写" does — including the name sanitizer.
            if (request.Name is not null)
            {
                OperationOutcome<DeviceDefinition> renamed = runtime.Admin.RenameDevice(id, request.Name);
                if (!renamed.Ok) return Fail(renamed);
            }

            if (request.Level is not null)
            {
                bool readWrite = string.Equals(request.Level, "readwrite", StringComparison.OrdinalIgnoreCase);
                OperationOutcome<DeviceDefinition> level = runtime.Admin.SetDeviceAccess(id, readWrite);
                if (!level.Ok) return Fail(level);
            }

            if (request.Capabilities is not null)
            {
                OperationOutcome<DeviceDefinition> caps = runtime.Admin.SetDeviceCapabilities(id, request.Capabilities);
                if (!caps.Ok) return Fail(caps);
            }

            DeviceDefinition? device = runtime.Devices.Find(id);
            if (device is null) return ApiHelpers.Fail(StatusCodes.Status404NotFound, "NOT_FOUND", "设备不存在。");

            return Results.Json(new
            {
                device = AuthEndpoints.Describe(device),
                capabilities = CapabilityList(device.Capabilities),
                presence = AuthEndpoints.Presence(device)
            }, ApiHelpers.Json);
        });

        group.MapPost("/devices/{id}/revoke", (HttpContext context, LanDiskRuntime runtime, string id) =>
        {
            if (!RequireAdmin(context, runtime, out IResult? error)) return error!;

            OperationOutcome<AdminOperations.DeviceRevocation> outcome = runtime.Admin.RevokeDevice(id);
            if (!outcome.Ok) return Fail(outcome);

            return Results.Json(new
            {
                ok = true,
                cancelledUploads = outcome.Value!.CancelledUploads,
                note = AdminOperations.DeviceRevocation.Note
            }, ApiHelpers.Json);
        });

        // Permanent removal. This used to delete the record directly, which skipped the token/session
        // rotation, left the device's extension tasks running and wrote no audit entry — a "removal" that was
        // quietly weaker than a revoke. It now runs through AdminOperations so both paths are the same code.
        group.MapDelete("/devices/{id}", (HttpContext context, LanDiskRuntime runtime, string id) =>
        {
            if (!RequireAdmin(context, runtime, out IResult? error)) return error!;

            OperationOutcome<AdminOperations.DeviceRemoval> outcome = runtime.Admin.RemoveDevice(id);
            if (!outcome.Ok) return Fail(outcome);

            AdminOperations.DeviceRemoval removal = outcome.Value!;
            return Results.Json(new
            {
                ok = true,
                removed = removal.DeviceId,
                wasActive = removal.WasActive,
                cancelledUploads = removal.CancelledUploads,
                cancelledTasks = removal.CancelledTasks,
                note = AdminOperations.DeviceRemoval.Note
            }, ApiHelpers.Json);
        });

        // ---- pairing ------------------------------------------------------

        group.MapPost("/pairing", (HttpContext context, LanDiskRuntime runtime) =>
        {
            if (!RequireAdmin(context, runtime, out IResult? error)) return error!;

            PairingRequest request = runtime.Pairing.Create();
            // The link order lives in AdminOperations so the native UI shows exactly the same
            // addresses as the web page, using the same physical-interface rule.
            IReadOnlyList<string> urls = runtime.Admin.PairingUrls(request.Token);

            return Results.Json(new
            {
                id = request.Id,
                token = request.Token,
                shortCode = request.ShortCode,
                expiresAt = request.ExpiresAt,
                url = urls.FirstOrDefault(),
                urls,
                requiresLocalConfirmationForShortCode = true
            }, ApiHelpers.Json);
        });

        group.MapGet("/pairing", (HttpContext context, LanDiskRuntime runtime) =>
        {
            if (!RequireAdmin(context, runtime, out IResult? error)) return error!;

            return Results.Json(new
            {
                requests = runtime.Pairing.Active().Select(p => new
                {
                    id = p.Id,
                    shortCode = p.ShortCode,
                    state = p.State.ToString(),
                    createdAt = p.CreatedAt,
                    expiresAt = p.ExpiresAt,
                    deviceName = p.DeviceName,
                    remoteAddress = p.RemoteAddress,
                    userAgent = p.DeviceUserAgent,
                    awaitingConfirmation = p.State == PairingState.AwaitingLocalConfirmation
                })
            }, ApiHelpers.Json);
        });

        group.MapPost("/pairing/{id}/confirm", (HttpContext context, LanDiskRuntime runtime, string id) =>
        {
            if (!RequireAdmin(context, runtime, out IResult? error)) return error!;

            // The native "确认配对" button calls the same method. It used to call Cancel instead, which
            // meant confirming a phone on the desktop never created a device at all.
            OperationOutcome<DeviceDefinition> outcome = runtime.Admin.ConfirmPairing(id);
            if (!outcome.Ok) return Fail(outcome);

            return Results.Json(new { ok = true, device = AuthEndpoints.Describe(outcome.Value!) }, ApiHelpers.Json);
        });

        group.MapDelete("/pairing/{id}", (HttpContext context, LanDiskRuntime runtime, string id) =>
        {
            if (!RequireAdmin(context, runtime, out IResult? error)) return error!;

            OperationOutcome<bool> outcome = runtime.Admin.RejectPairing(id);
            if (!outcome.Ok) return Fail(outcome);

            return Results.Json(new { ok = true, existed = outcome.Value }, ApiHelpers.Json);
        });

        // ---- network ------------------------------------------------------

        group.MapGet("/network", (HttpContext context, LanDiskRuntime runtime) =>
        {
            if (!RequireAdmin(context, runtime, out IResult? error)) return error!;

            return Results.Json(new
            {
                settings = new
                {
                    lanEnabled = runtime.Settings.LanEnabled,
                    bindAddress = runtime.Settings.LanBindAddress,
                    bindAddressFallback = runtime.BindAddressFallback,
                    httpsEnabled = runtime.Settings.HttpsEnabled,
                    httpCompatModeEnabled = runtime.Settings.HttpCompatModeEnabled,
                    allowedCidr = runtime.Settings.AllowedCidr,
                    httpPort = runtime.Settings.HttpPort,
                    httpsPort = runtime.Settings.HttpsPort
                },
                boundUrls = runtime.BoundUrls,
                interfaces = LanInfo.ListIPv4().Select(n => new
                {
                    name = n.Name,
                    description = n.Description,
                    address = n.Address,
                    subnetMask = n.SubnetMask,
                    isPrivate = n.IsPrivate,
                    status = n.Status,
                    type = n.InterfaceType
                }),
                requiresRestart = false
            }, ApiHelpers.Json);
        });

        group.MapPost("/network", (HttpContext context, LanDiskRuntime runtime, NetworkSettingsRequest request) =>
        {
            if (!RequireAdmin(context, runtime, out IResult? error)) return error!;

            OperationOutcome<AppSettings> outcome = runtime.Admin.UpdateNetworkSettings(
                request.LanEnabled, request.HttpsEnabled, request.HttpCompatModeEnabled,
                request.BindAddress, request.AllowedCidr, request.HttpPort, request.HttpsPort);

            if (!outcome.Ok) return Fail(outcome);

            return Results.Json(new
            {
                ok = true,
                saved = true,
                restartRequired = true,
                message = "网络设置已保存。端口与监听地址在启动时确定，请退出并重新启动 LanDisk 使其生效。",
                settings = outcome.Value
            }, ApiHelpers.Json);
        });

        // ---- TLS ----------------------------------------------------------

        group.MapGet("/tls", (HttpContext context, LanDiskRuntime runtime) =>
        {
            if (!RequireAdmin(context, runtime, out IResult? error)) return error!;

            bool regenerate = context.Request.Query["regenerate"] == "1";
            if (regenerate)
            {
                // Re-issuing the leaf is a routine, non-scary operation now: the root CA — the thing a
                // phone actually trusts — is untouched, so no device has to be reconfigured.
                runtime.Certificates.ResetLeafOnly();
            }

            ServerCertificate issued = runtime.EnsureCertificates(refresh: true);
            CertificateInfo info = runtime.Certificate!;

            return Results.Json(new
            {
                certificate = new
                {
                    subject = info.Subject,
                    issuer = info.Issuer,
                    thumbprint = info.Thumbprint,
                    sha256Fingerprint = info.Sha256Fingerprint,
                    notBefore = info.NotBefore,
                    notAfter = info.NotAfter,
                    dnsNames = info.DnsNames,
                    ipAddresses = info.IpAddresses,
                    isSelfSigned = !info.IsIssuedByLocalCa,
                    issuedByLocalCa = info.IsIssuedByLocalCa,
                    leafReason = info.LeafReason,
                    reissued = issued.Reissued
                },
                rootCa = new
                {
                    subject = info.RootCaSubject,
                    sha256Fingerprint = info.RootCaSha256,
                    notAfter = info.RootCaNotAfter
                },
                stableName = new
                {
                    hostName = runtime.Identity.HostName,
                    installId = runtime.Identity.InstallId,
                    published = runtime.Mdns is { IsRunning: true },
                    httpsUrl = runtime.Identity.HttpsOrigin(runtime.Settings.HttpsPort),
                    error = runtime.Mdns?.LastError
                },
                downloadPath = "/api/admin/tls/cert",
                httpsEnabled = runtime.Settings.HttpsEnabled,
                guidance = new[]
                {
                    "在 iPhone 上安装的是上面的“本地根证书”（LanDisk Local CA），不是服务器证书。",
                    "设置 → 通用 → VPN与设备管理 → 安装描述文件。",
                    "设置 → 通用 → 关于本机 → 证书信任设置 → 打开对 LanDisk Local CA 的完全信任。",
                    "手动安装不等于已信任；必须完成上一步，Safari 才不会报证书错误。",
                    "路由器换了 IP 之后无需重新信任：根证书不变，只有服务器证书会自动重新签发。"
                }
            }, ApiHelpers.Json);
        });

        group.MapGet("/tls/cert", (HttpContext context, LanDiskRuntime runtime) =>
        {
            if (!RequireAdmin(context, runtime, out IResult? error)) return error!;

            // Deliberately the root CA: handing out the leaf would tell the user to trust something
            // that is replaced whenever the LAN address changes.
            byte[]? cer = runtime.Certificates.ReadCaCerBytes();
            if (cer is null)
            {
                runtime.EnsureCertificates();
                cer = runtime.Certificates.ReadCaCerBytes();
            }
            if (cer is null)
                return ApiHelpers.Fail(StatusCodes.Status500InternalServerError, "CERT_UNAVAILABLE", "无法生成证书文件。");

            return Results.File(cer, "application/pkix-cert", "landisk-ca.cer");
        });

        // ---- recycle bin (local only) -------------------------------------

        group.MapGet("/recycle", (HttpContext context, LanDiskRuntime runtime) =>
        {
            if (!RequireAdmin(context, runtime, out IResult? error)) return error!;

            return Results.Json(new
            {
                entries = runtime.Recycle.List().Select(e => new
                {
                    id = e.Id,
                    shareId = e.ShareId,
                    shareName = runtime.Shares.Find(e.ShareId)?.DisplayName ?? "(已删除的共享)",
                    path = e.OriginalRelativePath.Replace('\\', '/'),
                    name = e.DisplayName,
                    isDirectory = e.IsDirectory,
                    length = e.Length,
                    sizeText = PathRules.FormatSize(e.Length),
                    deletedAt = e.DeletedAt,
                    deletedBy = e.DeletedByDeviceName,
                    state = e.State.ToString()
                })
            }, ApiHelpers.Json);
        });

        group.MapPost("/recycle/{id}/purge", (HttpContext context, LanDiskRuntime runtime, string id) =>
        {
            if (!RequireAdmin(context, runtime, out IResult? error)) return error!;

            try
            {
                RecycleEntry? entry = runtime.Recycle.Find(id);
                if (entry is null) return ApiHelpers.Fail(StatusCodes.Status404NotFound, "NOT_FOUND", "回收站条目不存在。");

                runtime.Recycle.Purge(id, OperationActor.LocalAdmin);
                runtime.InvalidateScans();
                return Results.Json(new { ok = true, message = "已永久清空该对象。" }, ApiHelpers.Json);
            }
            catch (Exception ex)
            {
                return ApiHelpers.MapException(ex);
            }
        });

        group.MapPost("/recycle/empty", (HttpContext context, LanDiskRuntime runtime, ConfirmRequest request) =>
        {
            if (!RequireAdmin(context, runtime, out IResult? error)) return error!;

            if (request.Confirm != true)
                return ApiHelpers.Fail(StatusCodes.Status400BadRequest, "CONFIRM_REQUIRED", "永久清空需要在本机管理端再次确认。");

            int purged = 0;
            var failures = new List<string>();

            foreach (RecycleEntry entry in runtime.Recycle.List())
            {
                try
                {
                    runtime.Recycle.Purge(entry.Id, OperationActor.LocalAdmin);
                    purged++;
                }
                catch (Exception ex)
                {
                    failures.Add(entry.DisplayName + ": " + ex.Message);
                }
            }

            return Results.Json(new { ok = failures.Count == 0, purged, failures }, ApiHelpers.Json);
        });

        // ---- diagnostics --------------------------------------------------

        group.MapGet("/audit", (HttpContext context, LanDiskRuntime runtime, int? limit) =>
        {
            if (!RequireAdmin(context, runtime, out IResult? error)) return error!;

            return Results.Json(new
            {
                entries = runtime.Audit.Tail(limit ?? 200).Select(e => new
                {
                    at = e.At,
                    category = e.Category,
                    action = e.Action,
                    deviceName = e.DeviceName,
                    deviceId = e.DeviceId,
                    source = e.Source ?? "device",
                    shareId = e.ShareId,
                    path = e.RelativePath,
                    detail = e.Detail,
                    success = e.Success
                })
            }, ApiHelpers.Json);
        });

        group.MapGet("/diagnostics", (HttpContext context, LanDiskRuntime runtime) =>
        {
            if (!RequireAdmin(context, runtime, out IResult? error)) return error!;

            var checks = new List<object>();
            runtime.RefreshShareHealth();

            checks.Add(new
            {
                id = "shares",
                status = runtime.Shares.All().Count == 0 ? "info" : "ok",
                message = runtime.Shares.All().Count == 0
                    ? "当前没有任何共享。手机端会看到空列表，这是首次启动的默认状态。"
                    : $"共 {runtime.Shares.All().Count} 个共享，其中 {runtime.Shares.All().Count(s => s.Enabled)} 个已启用。"
            });

            foreach (ShareDefinition share in runtime.Shares.All())
            {
                checks.Add(new
                {
                    id = "share:" + share.Id,
                    status = share.State == ShareState.Online ? "ok" : "warn",
                    message = share.State == ShareState.Online
                        ? $"共享“{share.DisplayName}”正常（{share.RootPath}）。"
                        : $"共享“{share.DisplayName}”当前不可用：{share.StateDetail}"
                });
            }

            if (!runtime.Settings.LanEnabled)
            {
                checks.Add(new
                {
                    id = "lan",
                    status = "info",
                    message = "局域网访问未开启，当前只监听本机回环地址，手机无法连接。"
                });
            }
            else if (runtime.Settings.HttpsEnabled)
            {
                checks.Add(new
                {
                    id = "tls",
                    status = "ok",
                    message = "已启用 HTTPS。请确认手机已安装并明确信任证书，否则 Safari 会提示不安全。"
                });
            }
            else if (runtime.Settings.HttpCompatModeEnabled)
            {
                checks.Add(new
                {
                    id = "http-compat",
                    status = "warn",
                    message = "HTTP 兼容模式已开启：同一局域网内的其他人可能截获或篡改传输内容。仅建议在可信局域网中临时使用。"
                });
            }

            checks.Add(new
            {
                id = "firewall",
                status = "info",
                message = "LanDisk 不会自动修改 Windows 防火墙。若手机无法连接，需要在“Windows  Defender 防火墙 → 允许应用通过防火墙”中，为 LanDisk 在“专用网络”放行对应端口。"
            });

            checks.Add(new
            {
                id = "sleep",
                status = "info",
                message = "电脑睡眠或休眠会中断手机访问。请检查电源设置，避免共享期间自动睡眠。"
            });

            return Results.Json(new { checks, boundUrls = runtime.BoundUrls }, ApiHelpers.Json);
        });

        group.MapPost("/shutdown", async (HttpContext context, LanDiskRuntime runtime, IHostApplicationLifetime lifetime) =>
        {
            if (!RequireAdmin(context, runtime, out IResult? error)) return error!;

            runtime.Audit.Write(new AuditEntry
            {
                Category = "lifecycle",
                Action = "shutdown",
                DeviceName = "本机管理端",
                Success = true
            });

            _ = Task.Run(async () =>
            {
                await Task.Delay(400);
                lifetime.StopApplication();
            });

            return Results.Json(new { ok = true, message = "正在退出 LanDisk。" }, ApiHelpers.Json);
        });
    }

    /// <summary>
    /// Turns a refused operation into the same error shape the API has always returned, so the native
    /// UI and the HTTP callers see one vocabulary of failure codes.
    /// </summary>
    private static IResult Fail<T>(OperationOutcome<T> outcome)
    {
        // The status keeps the meaning it had when these checks were inline in the endpoint: a missing
        // object is 404, an expired pairing request is 410, a request in the wrong state is 409.
        int status = outcome.Code switch
        {
            "NOT_FOUND" => StatusCodes.Status404NotFound,
            "SHARE_NOT_FOUND" => StatusCodes.Status404NotFound,
            "PAIRING_EXPIRED" => StatusCodes.Status410Gone,
            "PAIRING_STATE" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        };

        return ApiHelpers.Fail(status, outcome.Code ?? "OPERATION_FAILED", outcome.Message ?? "操作未完成。");
    }

    internal static bool RequireAdmin(HttpContext context, LanDiskRuntime runtime, out IResult? error)    {
        error = null;
        RequestSession session = context.Session();

        if (!session.IsLoopback)
        {
            error = ApiHelpers.Fail(StatusCodes.Status403Forbidden, "LOOPBACK_ONLY", "管理入口只允许在本机访问。");
            return false;
        }

        if (session.Admin is null)
        {
            error = ApiHelpers.Fail(StatusCodes.Status401Unauthorized, "ADMIN_REQUIRED", "需要本机管理会话，请从管理页面重新打开。");
            return false;
        }

        return true;
    }

    private static string SafeLabel(DriveInfo drive)
    {
        try { return string.IsNullOrEmpty(drive.VolumeLabel) ? "本地磁盘" : drive.VolumeLabel; }
        catch { return "本地磁盘"; }
    }

    private static string SafeFormat(DriveInfo drive)
    {
        try { return drive.DriveFormat; } catch { return "unknown"; }
    }

    private static long SafeTotal(DriveInfo drive)
    {
        try { return drive.TotalSize; } catch { return 0; }
    }

    private static long SafeFree(DriveInfo drive)
    {
        try { return drive.AvailableFreeSpace; } catch { return 0; }
    }

    private static bool SafeReady(DriveInfo drive)
    {
        try { return drive.IsReady; } catch { return false; }
    }

    /// <summary>The granted capabilities as a stable list of names, for both surfaces to display.</summary>
    internal static IReadOnlyList<string> CapabilityList(DeviceCapability capabilities)
        => Enum.GetValues<DeviceCapability>()
            .Where(c => c != DeviceCapability.None && capabilities.HasFlag(c))
            .Select(c => c.ToString())
            .ToList();

    public sealed record AdminSessionRequest(string? Token);

    /// <summary>
    /// A mobile theme choice: either a built-in profile id, or <c>custom</c> plus the three colours the
    /// operator picked (everything else is derived and validated server-side).
    /// </summary>
    public sealed record MobileThemeRequest(string? ProfileId, string? Accent, string? Background, string? Surface, string? Name);
    public sealed record CreateShareRequest(string? RootPath, string? DisplayName, bool? AllowUpload, bool? AllowRename, bool? AllowDelete, string? Note);
    public sealed record UpdateShareRequest(string? DisplayName, bool? Enabled, bool? AllowRead, bool? AllowUpload, bool? AllowRename, bool? AllowDelete, string? Note);
    /// <summary>
    /// A device edit. <paramref name="Capabilities"/> is the complete new set (not a delta) so a client
    /// cannot accidentally leave an unwanted capability switched on by omission.
    /// </summary>
    public sealed record UpdateDeviceRequest(string? Name, string? Level, string[]? Capabilities);
    public sealed record NetworkSettingsRequest(bool? LanEnabled, string? BindAddress, bool? HttpsEnabled, bool? HttpCompatModeEnabled, string? AllowedCidr, int? HttpPort, int? HttpsPort);
    public sealed record ConfirmRequest(bool? Confirm);
}

public static class BuildInfo
{
    public static string Version => BuildIdentity.AssemblyVersion;

    public static string BuildTime => BuildIdentity.BuiltAtUtc;

    /// <summary>Exact commit this build came from; empty for a development build.</summary>
    public static string Commit => BuildIdentity.Commit;

    public static string BuildId => BuildIdentity.BuildId;
}