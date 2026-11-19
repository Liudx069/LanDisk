using LanDisk.Core.Configuration;
using LanDisk.Core.Model;
using LanDisk.Core.Paths;
using LanDisk.Core.Storage;
using LanDisk.Server.Services;
using LanDisk.Update;
using Microsoft.AspNetCore.Http.Features;

namespace LanDisk.Server.Endpoints;

/// <summary>
/// The signed-update surface.
///
/// Everything here is loopback-administrator only: installing software is not something a paired
/// phone may ever do, no matter what capabilities its share has. Uploads are streamed straight to
/// the staging area, verified before anything is kept, and the actual replacement of program files
/// is handed to a separate updater process that outlives this one.
/// </summary>
public static class UpdateEndpoints
{
    public static void MapUpdateEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin");

        // ------------------------------------------------------------------ self check
        // Reachable from loopback without a session: the updater process asks this endpoint whether
        // the freshly restarted instance is healthy, and it has no cookie jar. It exposes nothing
        // that is not already visible on the machine it runs on.
        group.MapGet("/health", (HttpContext context, LanDiskRuntime runtime) =>
        {
            if (!context.Session().IsLoopback)
                return ApiHelpers.Fail(StatusCodes.Status403Forbidden, "LOOPBACK_ONLY", "自检接口只允许在本机访问。");

            HealthReport report = HealthReport.Build(runtime, context);
            return Results.Json(new
            {
                ok = report.Ok,
                degraded = report.Degraded,
                product = report.Product,
                version = report.Version,
                buildCommit = report.BuildCommit,
                buildId = report.BuildId,
                buildTime = BuildInfo.BuildTime,
                startedAt = runtime.StartedAt,
                dataRoot = report.DataRoot,
                adminPort = report.AdminPort,
                lanPort = report.LanPort,
                httpsEnabled = report.HttpsEnabled,
                lanEnabled = report.LanEnabled,
                certificateThumbprint = report.CertificateThumbprint,
                sharesStore = new { ok = report.SharesStore.Ok, count = report.SharesStore.Count, sha256 = report.SharesStore.Sha256, message = report.SharesStore.Message },
                devicesStore = new { ok = report.DevicesStore.Ok, count = report.DevicesStore.Count, sha256 = report.DevicesStore.Sha256, message = report.DevicesStore.Message },
                browseSmoke = new { ok = report.BrowseSmoke.Ok, message = report.BrowseSmoke.Message },
                installBuildInfo = report.InstallBuildInfo is null ? null : new
                {
                    product = report.InstallBuildInfo.Product,
                    version = report.InstallBuildInfo.Version,
                    buildCommit = report.InstallBuildInfo.Commit,
                    buildId = report.InstallBuildInfo.BuildId
                },
                checks = report.Checks.Select(c => new { id = c.Id, status = c.Status, message = c.Message })
            }, ApiHelpers.Json, statusCode: report.Ok ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        });

        // ------------------------------------------------------------------ status
        group.MapGet("/update/status", (HttpContext context, LanDiskRuntime runtime) =>
        {
            if (!AdminEndpoints.RequireAdmin(context, runtime, out IResult? error)) return error!;

            return Results.Json(BuildStatus(runtime), ApiHelpers.Json);
        });

        // ------------------------------------------------------------------ inspect a package already on disk (loopback debug aid)
        group.MapGet("/update/plan", (HttpContext context, LanDiskRuntime runtime) =>
        {
            if (!AdminEndpoints.RequireAdmin(context, runtime, out IResult? error)) return error!;

            (UpdatePlan? plan, string? planError) = runtime.Updates.DescribeStagedPlan();
            if (plan is null)
                return ApiHelpers.Fail(StatusCodes.Status409Conflict, "NO_PLAN", planError ?? "没有待安装的更新包。");

            return Results.Json(new
            {
                filesToWrite = plan.FilesToWrite,
                filesToDelete = plan.FilesToDelete,
                bytesToWrite = plan.BytesToWrite,
                unchangedBytes = plan.UnchangedBytes,
                warnings = plan.Warnings,
                writes = plan.Writes.Select(a => new { path = a.RelativePath, bytes = a.Bytes, exists = a.TargetExists, untracked = a.OverwritingUntracked }),
                deletes = plan.Deletes.Select(a => new { path = a.RelativePath })
            }, ApiHelpers.Json);
        });

        // ------------------------------------------------------------------ upload
        group.MapPost("/update/upload", async (HttpContext context, LanDiskRuntime runtime, CancellationToken cancellationToken) =>
        {
            if (!AdminEndpoints.RequireAdmin(context, runtime, out IResult? error)) return error!;

            // A package is much larger than an ordinary API body; widen the limit for this endpoint only.
            var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false })
                sizeFeature.MaxRequestBodySize = UpdateService.MaxPackageBytes + (16 * 1024 * 1024);

            string? declaredName = context.Request.Headers["X-LanDisk-Package-Name"].ToString();
            ActivityLog(runtime, "update", "upload-start", declaredName);

            try
            {
                StagedUpdate staged = await runtime.Updates.StageAsync(context.Request.Body, declaredName, cancellationToken);

                if (!staged.Ok)
                {
                    ActivityLog(runtime, "update", "upload-rejected", staged.Code);
                    return ApiHelpers.Fail(MapStatus(staged.Code), staged.Code ?? "UPDATE_REJECTED", staged.Message ?? "更新包被拒绝。");
                }

                (UpdatePlan? plan, string? planError) = runtime.Updates.DescribeStagedPlan();
                ActivityLog(runtime, "update", "upload-accepted", staged.Version);

                return Results.Json(new
                {
                    ok = true,
                    version = staged.Version,
                    stagingRoot = staged.StagingRoot,
                    packageSha256 = staged.PackageSha256,
                    signerKeyId = staged.KeyId,
                    releaseNotes = staged.ReleaseNotes,
                    buildCommit = staged.BuildCommit,
                    publishedAt = staged.PublishedAt,
                    fileCount = staged.FileCount,
                    totalBytes = staged.TotalBytes,
                    verifySeconds = Math.Round(staged.VerifySeconds, 2),
                    message = staged.Message,
                    plan = plan is null ? null : new
                    {
                        filesToWrite = plan.FilesToWrite,
                        filesToDelete = plan.FilesToDelete,
                        bytesToWrite = plan.BytesToWrite,
                        warnings = plan.Warnings
                    },
                    planError
                }, ApiHelpers.Json);
            }
            catch (OperationCanceledException)
            {
                ActivityLog(runtime, "update", "upload-cancelled", declaredName);
                return ApiHelpers.Fail(StatusCodes.Status499ClientClosedRequest, "CANCELLED", "上传已取消。");
            }
            catch (Exception ex)
            {
                // Staging touches the network, the staging disk and a hostile archive, so an
                // unexpected failure must still produce a structured answer the page can show:
                // an empty 500 tells the operator nothing and cannot be reported.
                ActivityLog(runtime, "update", "upload-failed", ex.GetType().Name);
                return ApiHelpers.Fail(StatusCodes.Status500InternalServerError, "UPDATE_UPLOAD_FAILED",
                    $"处理更新包时发生内部错误：{ex.GetType().Name} - {ex.Message}");
            }
        });

        // ------------------------------------------------------------------ apply
        group.MapPost("/update/apply", (HttpContext context, LanDiskRuntime runtime, IHostApplicationLifetime lifetime, UpdateApplyRequest? request) =>
        {
            if (!AdminEndpoints.RequireAdmin(context, runtime, out IResult? error)) return error!;

            if (request?.Confirm != true)
                return ApiHelpers.Fail(StatusCodes.Status400BadRequest, "CONFIRM_REQUIRED",
                    "应用更新会重启 LanDisk，需要显式确认。");

            // A restarted instance that cannot answer a loopback health probe within a minute is not
            // going to: waiting longer only delays the rollback the user actually needs.
            //
            // The *bound* ports, not the configured ones: a configured 0 means "pick a free port", so passing
            // it here would tell the updater to restore an endpoint that was never the one in use, and the
            // restarted instance would come back somewhere else while the health check probed port 0.
            ApplyRequest applied = runtime.Updates.RequestApply(
                runtime.BoundHttpPort, TimeSpan.FromSeconds(60), runtime.BoundHttpsPort);

            if (!applied.Accepted)
            {
                ActivityLog(runtime, "update", "apply-rejected", applied.Code);
                return ApiHelpers.Fail(StatusCodes.Status409Conflict, applied.Code ?? "APPLY_REJECTED", applied.Message ?? "无法应用更新。");
            }

            ActivityLog(runtime, "update", "apply-started", applied.TargetVersion);

            // Answer the request first, then stop: the updater is already waiting on our PID and the
            // browser needs a reply before the socket goes away.
            context.Response.OnCompleted(() =>
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1200);
                    lifetime.StopApplication();
                });
                return Task.CompletedTask;
            });

            return Results.Json(new
            {
                ok = true,
                targetVersion = applied.TargetVersion,
                updaterProcessId = applied.UpdaterProcessId,
                message = applied.Message,
                willRestart = true
            }, ApiHelpers.Json);
        });

        // ------------------------------------------------------------------ discard
        group.MapPost("/update/discard", (HttpContext context, LanDiskRuntime runtime) =>
        {
            if (!AdminEndpoints.RequireAdmin(context, runtime, out IResult? error)) return error!;

            runtime.Updates.DiscardPending();
            ActivityLog(runtime, "update", "discard", null);
            return Results.Json(new { ok = true, message = "已放弃待安装的更新包。" }, ApiHelpers.Json);
        });

        // ------------------------------------------------------------------ history
        group.MapGet("/update/history", (HttpContext context, LanDiskRuntime runtime) =>
        {
            if (!AdminEndpoints.RequireAdmin(context, runtime, out IResult? error)) return error!;

            UpdateStateDocument document = runtime.Updates.Store.Read();
            return Results.Json(new
            {
                entries = document.History.Select(h => new
                {
                    fromVersion = h.FromVersion,
                    toVersion = h.ToVersion,
                    outcome = h.Outcome,
                    message = h.Message,
                    backupDir = h.BackupDir,
                    startedAt = h.StartedAt,
                    finishedAt = h.FinishedAt
                })
            }, ApiHelpers.Json);
        });
    }

    // ------------------------------------------------------------------ helpers

    private static object BuildStatus(LanDiskRuntime runtime)
    {
        UpdateService updates = runtime.Updates;
        UpdateStateDocument document = updates.Store.Read();
        bool signatureOk = ReleasePublicKey.SelfCheck(out string embeddedKeyId);

        return new
        {
            currentVersion = BuildInfo.Version,
            installedVersion = string.IsNullOrWhiteSpace(document.InstalledVersion) ? BuildInfo.Version : document.InstalledVersion,
            installRoot = updates.InstallRoot,
            dataRoot = updates.DataRoot,
            updaterAvailable = updates.UpdaterPath is not null,
            updaterPath = updates.UpdaterPath,
            releaseKeyId = embeddedKeyId,
            releaseKeyValid = signatureOk,
            packageExtension = UpdatePackage.FileExtension,
            maxPackageBytes = UpdateService.MaxPackageBytes,
            stage = document.Stage.ToString(),
            stageText = UpdateService.StageText(document.Stage),
            stageMessage = document.StageMessage,
            stageUpdatedAt = document.StageUpdatedAt,
            lastOutcome = document.LastOutcome,
            lastMessage = document.LastMessage,
            lastBackupDir = document.LastBackupDir,
            lastAttemptAt = document.LastAttemptAt,
            pending = document.PendingVersion is null ? null : new
            {
                version = document.PendingVersion,
                stagingRoot = document.PendingStagingRoot,
                packageSha256 = document.PendingPackageSha256,
                signerKeyId = document.PendingKeyId,
                releaseNotes = document.PendingReleaseNotes,
                buildCommit = document.PendingBuildCommit,
                stagedAt = document.StagedAt
            },
            history = document.History.Take(10).Select(h => new
            {
                fromVersion = h.FromVersion,
                toVersion = h.ToVersion,
                outcome = h.Outcome,
                message = h.Message,
                startedAt = h.StartedAt,
                finishedAt = h.FinishedAt
            })
        };
    }

    private static int MapStatus(string? code) => code switch
    {
        "PACKAGE_TOO_LARGE" or "TOO_MANY_FILES" or "FILE_TOO_LARGE" => StatusCodes.Status413PayloadTooLarge,
        _ => StatusCodes.Status400BadRequest
    };

    private static void ActivityLog(LanDiskRuntime runtime, string category, string action, string? detail)
    {
        runtime.Audit.Write(new AuditEntry
        {
            Category = category,
            Action = action,
            Detail = detail,
            Success = !action.EndsWith("-rejected", StringComparison.Ordinal)
                      && !action.EndsWith("-cancelled", StringComparison.Ordinal)
        });
    }

    public sealed record UpdateApplyRequest(bool? Confirm);
}

/// <summary>
/// Structured self check.
///
/// It reports the running product's own view of itself — product, version, build commit, the ports
/// it configured, the certificate it is serving, whether its state stores load, and whether an
/// isolated directory listing works. The updater compares those values against a snapshot taken
/// before the update, so "the update succeeded" is a claim about the restarted product rather than
/// about the updater's own bookkeeping.
/// </summary>
public sealed class HealthReport
{
    public bool Ok { get; init; }
    public bool Degraded { get; init; }
    public required List<HealthCheck> Checks { get; init; }

    public string Product { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string BuildCommit { get; init; } = string.Empty;
    public string BuildId { get; init; } = string.Empty;
    public string DataRoot { get; init; } = string.Empty;

    /// <summary>Configured loopback/management port.</summary>
    public int AdminPort { get; init; }

    /// <summary>Configured LAN/phone port.</summary>
    public int LanPort { get; init; }

    public bool HttpsEnabled { get; init; }
    public bool LanEnabled { get; init; }

    /// <summary>Thumbprint of the certificate actually being served, when HTTPS is enabled.</summary>
    public string? CertificateThumbprint { get; init; }

    public StoreHealth SharesStore { get; init; } = new();
    public StoreHealth DevicesStore { get; init; } = new();
    public BrowseSmokeResult BrowseSmoke { get; init; } = new();

    /// <summary>The build identity recorded in the installation directory, when it is readable.</summary>
    public InstallBuildInfo? InstallBuildInfo { get; init; }

    public sealed record HealthCheck(string Id, string Status, string Message);

    public sealed class StoreHealth
    {
        public bool Ok { get; init; }
        public int Count { get; init; }
        public string? Sha256 { get; init; }
        public string? Message { get; init; }
    }

    public sealed class BrowseSmokeResult
    {
        public bool Ok { get; init; }
        public string Message { get; init; } = string.Empty;
    }

    public static HealthReport Build(LanDiskRuntime runtime, HttpContext context)
    {
        var checks = new List<HealthCheck>();

        // "error" means the build cannot work and the updater must roll back; "warn" means an
        // optional capability is missing and is reported but does not fail the health gate.
        void Check(string id, Func<string?> probe, string severity = "error")
        {
            try
            {
                string? problem = probe();
                checks.Add(problem is null
                    ? new HealthCheck(id, "ok", "正常")
                    : new HealthCheck(id, severity, problem));
            }
            catch (Exception ex)
            {
                checks.Add(new HealthCheck(id, "error", ex.Message));
            }
        }

        AppSettings settings = runtime.Settings;

        // Report the ports the product is actually serving on. A configured 0 means "pick a free
        // port", so the settings value alone would report 0 and tell the updater nothing; the bound
        // URLs carry the real numbers.
        (int adminPort, int lanPort) = ResolveEffectivePorts(runtime, settings);

        // ---- certificate -------------------------------------------------------
        // Reported whenever a certificate exists, not only while HTTPS is enabled: the updater's
        // question is "did the certificate change", which matters even if the operator has HTTPS
        // switched off at the moment.
        string? certificateThumbprint = null;
        DateTimeOffset? certificateNotAfter = null;
        try
        {
            using System.Security.Cryptography.X509Certificates.X509Certificate2? certificate =
                runtime.Certificates.TryLoadCaCertificate();
            if (certificate is not null)
            {
                certificateThumbprint = certificate.Thumbprint;
                certificateNotAfter = certificate.NotAfter.ToUniversalTime();
            }
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException)
        {
            certificateThumbprint = null;
        }

        // ---- data root ---------------------------------------------------------
        Check("data-root", () =>
        {
            if (!Directory.Exists(runtime.Paths.DataRoot)) return "数据目录不存在。";
            string probe = Path.Combine(runtime.Paths.DataRoot, ".health-probe");
            File.WriteAllText(probe, DateTimeOffset.Now.ToString("O"));
            File.Delete(probe);
            return null;
        });

        Check("settings", () =>
        {
            if (!File.Exists(runtime.Paths.SettingsFile)) return null;
            AppSettings reloaded = JsonStore.Read(runtime.Paths.SettingsFile, () => new AppSettings());
            return reloaded.HttpPort > 0 && reloaded.HttpsPort > 0 ? null : "端口配置不可用。";
        });

        // ---- ports -------------------------------------------------------------
        Check("ports", () =>
        {
            if (adminPort is <= 0 or > 65535) return $"管理端口不可用：{adminPort}";
            if (lanPort is <= 0 or > 65535) return $"局域网端口不可用：{lanPort}";
            return null;
        });

        // ---- stores ------------------------------------------------------------
        var sharesStore = new StoreHealth();
        Check("shares-store", () =>
        {
            try
            {
                IReadOnlyList<LanDisk.Core.Model.ShareDefinition> shares = runtime.Shares.All();
                string? hash = HashFileOrNull(runtime.Paths.SharesFile);
                sharesStore = new StoreHealth { Ok = true, Count = shares.Count, Sha256 = hash };
                return null;
            }
            catch (Exception ex)
            {
                sharesStore = new StoreHealth { Ok = false, Message = ex.Message };
                return "共享库无法加载：" + ex.Message;
            }
        });

        var devicesStore = new StoreHealth();
        Check("devices-store", () =>
        {
            try
            {
                IReadOnlyList<LanDisk.Core.Model.DeviceDefinition> devices = runtime.Devices.All();
                string? hash = HashFileOrNull(runtime.Paths.DevicesFile);
                devicesStore = new StoreHealth { Ok = true, Count = devices.Count, Sha256 = hash };
                return null;
            }
            catch (Exception ex)
            {
                devicesStore = new StoreHealth { Ok = false, Message = ex.Message };
                return "配对设备库无法加载：" + ex.Message;
            }
        });

        // ---- listeners ---------------------------------------------------------
        Check("listeners", () =>
        {
            // Filled in by the host once Kestrel has actually bound; an empty list here means the
            // instance cannot be reached yet, which the updater must treat as "not healthy yet".
            return runtime.BoundUrls.Count == 0 ? "监听地址尚未就绪。" : null;
        });

        // ---- HTTPS ------------------------------------------------------------
        Check("certificate", () =>
        {
            if (settings.HttpsEnabled && certificateThumbprint is null)
                return "HTTPS 已启用但证书无法加载。";
            if (certificateNotAfter is not null && certificateNotAfter.Value < DateTime.UtcNow)
                return $"HTTPS 证书已于 {certificateNotAfter.Value:yyyy-MM-dd} 过期。";
            return null;
        });

        // ---- build identity on disk ------------------------------------------
        // Three sources have to agree: the running assembly (version/buildCommit above), the update
        // manifest being installed, and the build-info.json in the installation directory. If the
        // file is missing or names a different build, the installation cannot say what it is.
        InstallBuildInfo? installBuildInfo = InstallBuildInfoFile.TryRead(runtime.Updates.InstallRoot);
        Check("build-info", () =>
        {
            // Absence means "this installation predates the identity file" (or is a development
            // tree); disagreement means the installation is claiming the wrong build, which is the
            // defect this check exists for. Only the latter is an error.
            if (installBuildInfo is null) return null;
            if (!string.Equals(installBuildInfo.Version, BuildInfo.Version, StringComparison.OrdinalIgnoreCase))
                return $"build-info.json 的版本为 {installBuildInfo.Version}，运行中的程序为 {BuildInfo.Version}。";
            if (installBuildInfo.Commit.Length >= 7
                && BuildInfo.Commit.Length >= 7
                && !string.Equals(installBuildInfo.Commit, BuildInfo.Commit, StringComparison.OrdinalIgnoreCase))
            {
                return $"build-info.json 的构建提交为 {installBuildInfo.Commit}，运行中的程序为 {BuildInfo.Commit}。";
            }
            return null;
        }, severity: "error");

        // ---- isolated browse smoke -------------------------------------------
        BrowseSmokeResult browseSmoke = RunBrowseSmoke(runtime);
        Check("browse-smoke", () => browseSmoke.Ok ? null : browseSmoke.Message);

        Check("release-key", () =>
        {
            return ReleasePublicKey.SelfCheck(out _) ? null : "内置更新签名公钥无效，无法验证任何更新包。";
        });

        // A missing updater only disables the auto-update path; the running instance is fine. It is
        // reported as a warning so it never turns into a failed post-update health check and triggers
        // an unnecessary rollback.
        Check("updater", () =>
        {
            return UpdateLayout.FindUpdater(runtime.Updates.InstallRoot) is null
                ? $"安装目录中没有 {UpdateLayout.UpdaterDirectoryName}\\{UpdateLayout.UpdaterFileName}，自动更新与自动回滚不可用。"
                : null;
        }, severity: "warn");

        // A previous failed or rolled back update is history, not a present malfunction: the process
        // answering this request is demonstrably alive. It is surfaced as a warning so the page can
        // explain it without failing the health gate.
        Check("update-state", () =>
        {
            string path = runtime.Updates.Store.StateFilePath;
            if (!File.Exists(path)) return null;
            UpdateStateDocument document = runtime.Updates.Store.Read();
            return document.Stage is UpdateStage.Failed or UpdateStage.RolledBack
                ? $"上一次更新处于“{UpdateService.StageText(document.Stage)}”状态：{document.StageMessage}"
                : null;
        }, severity: "warn");

        // Errors mean "this build cannot work"; warnings are reported but do not fail the check.
        bool ok = !checks.Any(c => c.Status == "error");
        bool degraded = checks.Any(c => c.Status == "warn");

        return new HealthReport
        {
            Ok = ok,
            Degraded = degraded,
            Checks = checks,
            Product = UpdateManifest.ProductName,
            Version = BuildInfo.Version,
            BuildCommit = BuildInfo.Commit,
            BuildId = BuildInfo.BuildId,
            DataRoot = runtime.Paths.DataRoot,
            AdminPort = adminPort,
            LanPort = lanPort,
            HttpsEnabled = settings.HttpsEnabled,
            LanEnabled = settings.LanEnabled,
            CertificateThumbprint = certificateThumbprint,
            SharesStore = sharesStore,
            DevicesStore = devicesStore,
            BrowseSmoke = browseSmoke,
            InstallBuildInfo = installBuildInfo
        };
    }

    /// <summary>
    /// Lists a throw-away directory through the real resolver and browser.
    ///
    /// This is the difference between "the process answers HTTP" and "file browsing works": it
    /// exercises volume identity, path containment and directory enumeration end to end, on a
    /// directory created for the purpose so no user data is read.
    /// </summary>
    private static BrowseSmokeResult RunBrowseSmoke(LanDiskRuntime runtime)
    {
        string smokeRoot = Path.Combine(Path.GetTempPath(), "landisk-health-smoke-" + Guid.NewGuid().ToString("n")[..8]);

        try
        {
            Directory.CreateDirectory(smokeRoot);
            string probeFile = Path.Combine(smokeRoot, "health-probe.txt");
            File.WriteAllText(probeFile, "landisk health probe");

            var share = new LanDisk.Core.Model.ShareDefinition
            {
                Id = "health-smoke",
                DisplayName = "health-smoke",
                RootPath = smokeRoot,
                Capabilities = LanDisk.Core.Model.ShareCapabilities.Read,
                Enabled = true
            };

            // ProbeShare fills in the volume identity and object id, exactly as share creation does.
            LanDisk.Core.Paths.ShareHealth health = runtime.Resolver.ProbeShare(share);
            if (health.State != LanDisk.Core.Model.ShareState.Online)
                return new BrowseSmokeResult { Ok = false, Message = $"隔离目录无法解析：{health.Detail ?? health.State.ToString()}" };

            DirectoryListing listing = runtime.Browser.List(
                share,
                string.Empty,
                LanDisk.Core.Model.DeviceAccessLevel.ReadWrite,
                new BrowseQuery());

            bool found = listing.Entries.Any(e => string.Equals(e.Name, "health-probe.txt", StringComparison.OrdinalIgnoreCase));
            if (!found)
                return new BrowseSmokeResult { Ok = false, Message = "隔离目录列出成功，但没有看到探测文件。" };

            return new BrowseSmokeResult { Ok = true, Message = "隔离目录可解析、可列出。" };
        }
        catch (Exception ex)
        {
            return new BrowseSmokeResult { Ok = false, Message = "隔离浏览自检失败：" + ex.GetType().Name + " - " + ex.Message };
        }
        finally
        {
            try { if (Directory.Exists(smokeRoot)) Directory.Delete(smokeRoot, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The ports this instance is really serving on.
    ///
    /// `HttpPort = 0` in settings means "bind a free port"; reporting the settings value would then
    /// say 0 and the updater would have nothing to compare. The bound URLs contain the resolved
    /// numbers, so they win whenever they are available.
    /// </summary>
    private static (int AdminPort, int LanPort) ResolveEffectivePorts(LanDiskRuntime runtime, AppSettings settings)
    {
        int admin = settings.HttpPort;
        int lan = settings.HttpsPort;

        foreach (string url in runtime.BoundUrls)
        {
            if (!Uri.TryCreate(url.Replace("[::]", "127.0.0.1"), UriKind.Absolute, out Uri? parsed)) continue;

            bool isHttps = string.Equals(parsed.Scheme, "https", StringComparison.OrdinalIgnoreCase);
            bool isLoopback = parsed.Host is "127.0.0.1" or "localhost";

            if (!isHttps && isLoopback) admin = parsed.Port;
            else if (isHttps && isLoopback) lan = parsed.Port;
            else if (isHttps && lan <= 0) lan = parsed.Port;
            else if (!isHttps && admin <= 0) admin = parsed.Port;
        }

        return (admin, lan);
    }

    private static string? HashFileOrNull(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
