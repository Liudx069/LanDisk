using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LanDisk.Core.Model;
using LanDisk.Server.Services;

namespace LanDisk.App;

/// <summary>
/// Renders the window's pages to PNG files.
///
/// <para><b>Why this exists.</b> "It looks fine on my machine" is not evidence, and a reviewer cannot open a
/// window on a machine they do not have. This mode drives the real window — the same pages, the same layout,
/// the same data — at each required window size, each DPI scale and both themes, and writes the result to
/// disk. The rendering is WPF's own.</para>
///
/// <para><b>Single source of truth.</b> The pages come from <see cref="PageCatalog"/>, the exact same list the
/// navigation rail builds from. A page that is in the rail but not captured, or a title the rail no longer
/// has, is a hard failure here — this mode must never render the wrong page and still write a PASS row.</para>
///
/// <para><b>Real DPI.</b> A monitor at 150% does not enlarge a 1160 DIP window; it shrinks the logical space
/// the window has. Each combination is therefore laid out in the <c>physical / scale</c> DIP viewport and only
/// the final bitmap is rasterised at the physical pixel size — exactly what WPF does on that monitor.</para>
/// </summary>
internal static class UiCapture
{
    /// <summary>The physical screen sizes and DPI scales the acceptance list asks about.</summary>
    private static readonly (int Width, int Height)[] PhysicalSizes =
    [
        (1366, 768), (1600, 900), (1920, 1080), (2560, 1440)
    ];

    private static readonly double[] Scales = [1.00, 1.25, 1.50, 1.75, 2.00];

    private const string IphonePageTitle = "连接 iPhone";
    private const string NetworkPageTitle = "网络";

    /// <summary>R2 verdict: what the real network page actually rendered.</summary>
    private sealed record R2Verdict(bool VisibleAdapter, bool AcceptedLabel, bool EmptyState, string Where);

    private static R2Verdict? r2;
    private static readonly List<string> networkTextsSeen = [];

    public static bool Requested(string[] args) => IndexOf(args, "--render-ui") >= 0;

    public static async Task<int> RunAsync(string[] args, ServerStatus status)
    {
        string outputDirectory = Value(args, "--render-ui") ?? Path.Combine(Path.GetTempPath(), "landisk-ui");
        Directory.CreateDirectory(outputDirectory);

        var report = new List<string>();
        report.Add("file\ttheme\tscale\tphysical\tpage\tclass\trequested\tdetail\tresult\tms");

        // Worst-case data so the screenshots exercise the busy states (tables with rows, badges, log lines),
        // not just the empty states. Everything goes into this run's scratch data root.
        SeedWorstCaseData();

        var targets = new List<(ShellTheme Theme, double Scale, int PhysicalW, int PhysicalH, int LogicalW, int LogicalH)>();

        // The required matrix, then the 1160×780 design baseline as an extra (it must not replace the matrix).
        foreach (ShellTheme theme in new[] { ShellTheme.Dark, ShellTheme.Light })
        {
            foreach ((int width, int height) in PhysicalSizes)
            {
                foreach (double scale in Scales)
                {
                    targets.Add((theme, scale, width, height, Logical(width, scale), Logical(height, scale)));
                }
            }

            targets.Add((theme, 1.00, 1160, 780, 1160, 780));
        }

        var seenPages = new HashSet<string>();
        int wrongPage = 0;

        foreach ((ShellTheme theme, double scale, int physicalW, int physicalH, int logicalW, int logicalH) in targets)
        {
            // The theme is applied before the window is built so the very first layout uses the palette under test.
            ThemeManager.Apply(theme);

            var window = new MainWindow
            {
                Width = logicalW,
                Height = logicalH,
                StrictNavigation = true,
                WindowState = WindowState.Normal,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000
            };

            window.Show();
            window.OnHostStatusChanged(status);
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

            foreach (PageDescriptor descriptor in PageCatalog.All)
            {
                string page = descriptor.Title;
                seenPages.Add(page);
                try
                {
                    if (page == IphonePageTitle) ArmOnboarding();

                    NativePage? actual = await window.NavigateToAsync(page);

                    // Page identity: the capture is only honest if the page that answered is the page we asked
                    // for, with the exact class the catalogue says. A silent fall-through to the previous page
                    // is exactly the failure this asserts against.
                    bool identityOk = actual is not null
                                      && actual.GetType() == descriptor.PageType
                                      && window.CurrentTitle == page;
                    if (!identityOk)
                    {
                        wrongPage++;
                        report.Add($"-\t{theme.ToString().ToLowerInvariant()}\t{scale:0.00}\t{physicalW}x{physicalH}\t{page}\t{actual?.GetType().Name ?? "null"}\t{window.CurrentTitle}\t页面类型或标题不一致\tFAIL\t0ms");
                        continue;
                    }

                    window.UpdateLayout();
                    await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);

                    // R2：网络页必须依据**结构化**接受状态渲染真实可用的物理网卡。
                    // 现场缺陷是页面一边说"没有可用物理网卡"、一边推荐 https://192.168.2.2:… —— 自相矛盾。
                    // 这里在真实 WPF 视觉树上取文本，作为可机器审计的证据（不做 OCR、不给产品加调试界面）。
                    if (page == NetworkPageTitle)
                    {
                        var texts = new List<string>();
                        CollectTexts(window.PageHost, texts);
                        foreach (string text in texts) networkTextsSeen.Add(text);

                        if (r2 is null)
                        {
                            r2 = new R2Verdict(
                                VisibleAdapter: texts.Any(t => t.Contains("192.168.2.2", StringComparison.Ordinal)),
                                AcceptedLabel: texts.Any(t => t.Contains("用于手机访问", StringComparison.Ordinal)),
                                EmptyState: texts.Any(t => t.Contains("没有可用物理网卡", StringComparison.Ordinal)),
                                Where: $"{theme.ToString().ToLowerInvariant()} {scale:0.00}x {physicalW}x{physicalH}");
                        }
                    }

                    (bool ok, string detail) = Inspect(window.PageHost, logicalW, theme, page);

                    string name = $"{FileSafe(page)}-{theme.ToString().ToLowerInvariant()}-{scale:0.00}x-{physicalW}x{physicalH}.png";
                    string path = Path.Combine(outputDirectory, name);
                    Capture(window, path, scale);
                    report.Add($"{name}\t{theme.ToString().ToLowerInvariant()}\t{scale:0.00}\t{physicalW}x{physicalH}\t{page}\t{actual!.GetType().Name}\t{window.CurrentTitle}\t{detail}\t{(ok ? "PASS" : "FAIL")}\t{(int)0}ms");
                }
                catch (Exception ex)
                {
                    wrongPage++;
                    report.Add($"-\t{theme.ToString().ToLowerInvariant()}\t{scale:0.00}\t{physicalW}x{physicalH}\t{page}\t?\t?\t{ex.Message}\tFAIL\t0ms");
                }
            }

            window.Hide();
            window.Close();
        }

        // UIE17-01: every page was captured at least once (all NavEntries covered), and UIE17-02: an unknown
        // title must fail rather than fall through. Both are enforced here and again by the source-level gates.
        var allTitles = PageCatalog.All.Select(p => p.Title).ToList();
        var missing = allTitles.Where(t => !seenPages.Contains(t)).ToList();

        // The expected combination set is derived from the same loop that produced the targets, so "missing" is
        // a set difference rather than a count comparison. Counting alone cannot tell a dropped combination from
        // a duplicated one, and an unknown must never be reported as zero.
        var expectedKeys = new HashSet<string>();
        foreach ((ShellTheme theme, double scale, int physicalW, int physicalH, int _, int _) in targets)
        {
            foreach (string title in allTitles)
                expectedKeys.Add(Key(theme, scale, physicalW, physicalH, title));
        }

        var actualKeys = new List<string>();
        int failedRows = 0, passedRows = 0;
        foreach (string row in report)
        {
            string[] cells = row.Split('\t');
            // The first element of `report` is the column header. Parsing it as data threw
            // "The input string 'scale' was not in a correct format" and aborted the whole capture.
            if (cells.Length < 10 || cells[2] == "scale") continue;
            if (cells[8] == "PASS") passedRows++;
            else if (cells[8] == "FAIL") failedRows++;
            if (cells[0] == "-") continue;                       // a page-level failure has no screenshot
            actualKeys.Add(Key(ParseTheme(cells[1]), double.Parse(cells[2], System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(cells[3].Split('x')[0]), int.Parse(cells[3].Split('x')[1]), cells[4]));
        }

        var actualSet = new HashSet<string>(actualKeys);
        var duplicateRows = actualKeys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key + "×" + g.Count()).ToList();
        var unexpectedRows = actualSet.Where(k => !expectedKeys.Contains(k)).ToList();

        // UIE17-02 — drive one unknown title through strict navigation to prove it throws.
        bool unknownFails;
        try
        {
            ThemeManager.Apply(ShellTheme.Dark);
            var probe = new MainWindow { StrictNavigation = true, Width = 800, Height = 600, WindowState = WindowState.Normal, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            probe.Show();
            await probe.NavigateToAsync("绝对不存在的页面");
            unknownFails = false;
            probe.Close();
        }
        catch (InvalidOperationException)
        {
            unknownFails = true;
        }

        var summary = new[]
        {
            "NAV_PAGE_COUNT\t" + PageCatalog.All.Count,
            "UI_CAPTURE_PAGE_COUNT\t" + seenPages.Count,
            "UI_CAPTURE_EXPECTED\t" + allTitles.Count,
            "UI_CAPTURE_ACTUAL\t" + seenPages.Count,
            // An empty field is not a result. "no missing pages" is written as 0 so that a reader — and the
            // aggregator — can tell it from "the check did not run".
            "UI_CAPTURE_MISSING\t" + (missing.Count == 0 ? "0" : string.Join("、", missing)),
            "UI_CAPTURE_MISSING_COUNT\t" + missing.Count,
            "UI_CAPTURE_WRONG_PAGE\t" + wrongPage,
            "UI_CAPTURE_DUPLICATES\t" + (duplicateRows.Count == 0 ? "0" : string.Join("、", duplicateRows)),
            "UI_CAPTURE_DUPLICATE_COUNT\t" + duplicateRows.Count,
            "UI_CAPTURE_UNEXPECTED\t" + (unexpectedRows.Count == 0 ? "0" : string.Join("、", unexpectedRows)),
            "UI_CAPTURE_FAIL\t" + failedRows,
            "UI_CAPTURE_PASS\t" + passedRows,
            "UI_CAPTURE_TOTAL\t" + report.Count,
            "UIE17-01\t" + (missing.Count == 0 ? "PASS" : "FAIL"),
            "UIE17-02\t" + (unknownFails ? "PASS" : "FAIL"),
            "UIE17-03\t" + (wrongPage == 0 ? "PASS" : "FAIL"),
            "WINDOW_DPI_MATRIX\t" + PhysicalSizes.Length + " sizes x " + Scales.Length + " scales x 2 themes x " + PageCatalog.All.Count + " pages"
        };

        File.WriteAllLines(Path.Combine(outputDirectory, "UI_CAPTURE.tsv"), report);
        File.WriteAllLines(Path.Combine(outputDirectory, "UI_CAPTURE_SUMMARY.tsv"), summary);

        // "No diagnostics" must be distinguishable from "nothing was inspected". The counters below are what
        // make the difference: a run that examined zero text blocks is not a clean run, it is a run that did
        // not look.
        var environment = new List<string>
        {
            "UI_LAYOUT_TEXTBLOCKS_INSPECTED\t" + inspectedTextBlocks,
            "UI_LAYOUT_COMBINATIONS_RUN\t" + (report.Count - 1),
            "UI_LAYOUT_ASSERTIONS_FAILED\t" + failedRows,
            "UI_LAYOUT_DIAGNOSTIC_ROWS\t" + LayoutDiagnostics.Count,

            // R2：原生网络页的真实渲染结论。夹具为 Ethernet 192.168.2.2/24（网关 192.168.2.1）可用、
            // Hyper-V 172.24.0.1 被排除；因此页面必须显示该地址、且**不得**出现"没有可用物理网卡"。
            "R2_FIXTURE\t" + "physical ethernet 192.168.2.2/24 gw 192.168.2.1 accepted; hyper-v 172.24.0.1 rejected",
            "R2_NATIVE_UI_RENDER\t" + (r2 is { VisibleAdapter: true, EmptyState: false } ? "PASS" : "FAIL"),
            "R2_VISIBLE_ADAPTER\t" + (r2?.VisibleAdapter == true ? "true" : "false"),
            "R2_ACCEPTED_LABEL_VISIBLE\t" + (r2?.AcceptedLabel == true ? "true" : "false"),
            "R2_EMPTY_STATE_VISIBLE\t" + (r2?.EmptyState == true ? "true" : "false"),
            "R2_SAMPLED_AT\t" + (r2?.Where ?? "(网络页未渲染)")
        };

        // 网络页文本原样落盘，供独立复核（不截断、不做 OCR）。
        File.WriteAllLines(Path.Combine(outputDirectory, "UI_CAPTURE_NETWORK_TEXT.tsv"),
            networkTextsSeen.Select(t => t.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ')));
        File.WriteAllLines(Path.Combine(outputDirectory, "UI_CAPTURE_ENVIRONMENT.tsv"), environment);

        // The page catalogue, stamped with this build's identity.
        //
        // The expected page set must not be derived from the results themselves: replacing one page's rows
        // with a page that does not exist keeps both the row count and the page count identical, so a
        // count-only check cannot see it. This file is produced from PageCatalog.All — the product's own list —
        // so it defines the expectation independently of what actually rendered.
        var catalog = new List<string>
        {
            "# version\t" + BuildInfo.Version,
            "# commit\t" + BuildInfo.Commit,
            "# buildId\t" + BuildInfo.BuildId,
            "title\tpageType\tgroup"
        };
        foreach (PageDescriptor descriptor in PageCatalog.All)
        {
            catalog.Add(string.Join('\t', descriptor.Title, descriptor.PageType.Name, descriptor.Group));
        }
        File.WriteAllLines(Path.Combine(outputDirectory, "UI_PAGE_CATALOG.tsv"), catalog);

        // Machine-readable layout evidence.
        //
        // Two kinds of row, in one file:
        //   SUMMARY  one per assertion: how many targets it examined, how many failed, over how many pages.
        //            "0 failures" only means something next to a non-zero target count, so both are written.
        //   FAILURE  one per firing, with the element identity and the numbers behind it.
        //
        // The binding table cites this file, which is what stops REAL11-03/04/05/09 from being "proved" by a
        // navigation test that never measured anything.
        var layout = new List<string>
        {
            "kind\tassertion_id\trequirement\tscopes\ttargets_checked\tfailures\tstatus\tpage\tdetail"
        };
        foreach (var pair in LayoutTallies.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            string requirement = LayoutRequirement.TryGetValue(pair.Key, out string? req) ? req : "UNMAPPED";
            Tally tally = pair.Value;
            layout.Add(string.Join('\t',
                "SUMMARY",
                pair.Key,
                requirement,
                string.Join(",", tally.Scopes.OrderBy(s => s, StringComparer.Ordinal)),
                tally.Targets,
                tally.Failures,
                tally.Failures == 0 && tally.Targets > 0 ? "PASS" : (tally.Targets == 0 ? "FAIL-NO-TARGETS" : "FAIL"),
                string.Empty,
                string.Empty));
        }
        layout.AddRange(LayoutDiagnostics.Select(d => "FAILURE\tLAYOUT-SINGLE-LINE-HEIGHT\t" + (LayoutRequirement.TryGetValue("LAYOUT-SINGLE-LINE-HEIGHT", out string? r2) ? r2 : "UNMAPPED") + "\t\t\t\tFAIL\t" + d));

        // One row per (assertion, page). The binding table needs to show that the page a requirement is about
        // was actually measured, which a global total cannot do.
        foreach (var pair in LayoutPageTallies.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            string[] parts = pair.Key.Split('\u001f');
            string assertion = parts[0];
            string page = parts.Length > 1 ? parts[1] : string.Empty;
            string requirement = LayoutRequirement.TryGetValue(assertion, out string? req) ? req : "UNMAPPED";
            Tally tally = pair.Value;
            layout.Add(string.Join('\t',
                "PAGE",
                assertion,
                requirement,
                page,
                tally.Targets,
                tally.Failures,
                tally.Failures == 0 && tally.Targets > 0 ? "PASS" : (tally.Targets == 0 ? "FAIL-NO-TARGETS" : "FAIL"),
                page,
                string.Empty));
        }

        File.WriteAllLines(Path.Combine(outputDirectory, "UI_LAYOUT_VERIFICATION.tsv"), layout);

        // A layout run where no assertion examined anything is a failed run, not a clean one.
        int assertionsWithoutTargets = LayoutTallies.Values.Count(t => t.Targets == 0);

        Console.WriteLine("UI 截图已生成：" + outputDirectory);
        Console.WriteLine(string.Join(Environment.NewLine, summary));

        // A run that produced screenshots but failed assertions must not exit 0. The exit code used to consider
        // only missing pages, wrong pages and the unknown-title probe, so 5 failing layout combinations still
        // reported success to the caller — "the program ran" was being confused with "the acceptance passed".
        bool clean = missing.Count == 0
                     && wrongPage == 0
                     && unknownFails
                     && failedRows == 0
                     && duplicateRows.Count == 0
                     && unexpectedRows.Count == 0;
        return clean ? 0 : 2;
    }

    private static int Logical(int physical, double scale) => (int)Math.Round(physical / scale);

    private static string FileSafe(string page) => page.Replace(" ", "-").Replace("/", "-");

    /// <summary>
    /// Seeds the scratch runtime with the worst-case data the screenshots must be able to lay out: three
    /// shares (one with a very long path, one with a very long volume id, one online / one missing / one
    /// needs-reconfirm), five devices covering every presence and a long name and user-agent, and fifty audit
    /// lines across categories with long details and failures. This is what stops the capture from "passing by
    /// rendering only the empty states".
    /// </summary>
    private static void SeedWorstCaseData()
    {
        LanDiskRuntime? runtime = App.Instance.Host?.Runtime;
        if (runtime is null) return;

        string root = runtime.Paths.DataRoot;
        string shareRoot = Path.Combine(root, "capture-shares");
        Directory.CreateDirectory(shareRoot);

        string longPath = Path.Combine(shareRoot, "a very long folder name used to prove the layout truncates and does not push columns off the card ".Trim(), "with", "more", "nested", "segments");
        Directory.CreateDirectory(longPath);
        string online = Path.Combine(shareRoot, "在线共享");
        Directory.CreateDirectory(online);

        if (runtime.Shares.Count == 0)
        {
            runtime.Shares.Add(new ShareDefinition
            {
                Id = "share-online",
                DisplayName = "在线共享（照片与文档）",
                RootPath = online,
                VolumeGuidPath = "\\\\?\\Volume{4d1f5c2a-0000-0000-0000-000000000000}\\",
                MountPointAtCreation = online,
                Capabilities = ShareCapabilities.Read | ShareCapabilities.Upload | ShareCapabilities.Rename | ShareCapabilities.Delete,
                State = ShareState.Online,
                StateDetail = "磁盘就绪"
            });

            runtime.Shares.Add(new ShareDefinition
            {
                Id = "share-long",
                DisplayName = "超长路径共享",
                RootPath = longPath,
                VolumeGuidPath = "\\\\?\\Volume{ffffffff-ffff-ffff-ffff-ffffffffffffffff-aaaaaaaa-bbbb-cccc-dddd}\\",
                MountPointAtCreation = longPath,
                Capabilities = ShareCapabilities.Read,
                State = ShareState.Online,
                StateDetail = "磁盘就绪"
            });

            runtime.Shares.Add(new ShareDefinition
            {
                Id = "share-missing",
                DisplayName = "移动硬盘（已拔出）",
                RootPath = Path.Combine(root, "missing-drive", "not", "here"),
                VolumeGuidPath = "\\\\?\\Volume{00000000-0000-0000-0000-000000000000}\\",
                MountPointAtCreation = "D:\\",
                Capabilities = ShareCapabilities.Read,
                State = ShareState.Offline,
                StateDetail = "磁盘未就绪"
            });

            runtime.Shares.Add(new ShareDefinition
            {
                Id = "share-reconfirm",
                DisplayName = "需要重新确认的共享",
                RootPath = online,
                VolumeGuidPath = "\\\\?\\Volume{11111111-2222-3333-4444-555555555555}\\",
                MountPointAtCreation = online,
                Capabilities = ShareCapabilities.Read,
                State = ShareState.IdentityMismatch,
                StateDetail = "磁盘身份与创建时不一致"
            });
        }

        if (runtime.Devices.Count == 0)
        {
            string longName = "一台名字非常长的 iPhone 用来验证设备页的省略号与详情展开不会把列挤坏";
            string longUa = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Mobile/15E148 Safari/604.1";

            var onlineDevice = new DeviceDefinition
            {
                Id = "dev-online",
                Name = "iPhone（客厅）",
                Level = DeviceAccessLevel.ReadWrite,
                Capabilities = DeviceCapability.ExtensionExecute | DeviceCapability.SystemView,
                LastSeenAt = DateTimeOffset.Now,
                LastSeenIp = "192.168.2.23",
                UserAgent = longUa
            };
            runtime.Devices.Add(onlineDevice);

            var recentDevice = new DeviceDefinition
            {
                Id = "dev-recent",
                Name = "iPad（书房）",
                Level = DeviceAccessLevel.ReadOnly,
                LastSeenAt = DateTimeOffset.Now.AddMinutes(-2),
                LastSeenIp = "192.168.2.41",
                UserAgent = "Mozilla/5.0 (iPad; CPU OS 17_5 like Mac OS X) AppleWebKit/605.1.15"
            };
            runtime.Devices.Add(recentDevice);

            var offlineDevice = new DeviceDefinition
            {
                Id = "dev-offline",
                Name = longName,
                Level = DeviceAccessLevel.ReadOnly,
                LastSeenAt = DateTimeOffset.Now.AddDays(-3),
                LastSeenIp = "192.168.2.99",
                UserAgent = longUa
            };
            runtime.Devices.Add(offlineDevice);

            var revokedDevice = new DeviceDefinition
            {
                Id = "dev-revoked",
                Name = "旧手机（已撤销）",
                Level = DeviceAccessLevel.ReadWrite,
                Capabilities = DeviceCapability.FileWrite | DeviceCapability.ExtensionInstall,
                LastSeenAt = DateTimeOffset.Now.AddDays(-30),
                Revoked = true,
                RevokedAt = DateTimeOffset.Now.AddDays(-20),
                UserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X)"
            };
            runtime.Devices.Add(revokedDevice);

            runtime.Devices.Add(new DeviceDefinition
            {
                Id = "dev-capabilities",
                Name = "多能力设备",
                Level = DeviceAccessLevel.ReadWrite,
                Capabilities = DeviceCapability.FileWrite | DeviceCapability.ExtensionInstall | DeviceCapability.ExtensionExecute | DeviceCapability.SystemView,
                LastSeenAt = DateTimeOffset.Now.AddSeconds(-30),
                LastSeenIp = "192.168.2.51",
                UserAgent = "Mozilla/5.0 (Android 14; Pixel)"
            });
        }

        if (runtime.Audit.Tail(1).Count == 0)
        {
            string[] categories = ["device", "share", "file", "auth", "pairing", "update", "theme"];
            for (int i = 0; i < 50; i++)
            {
                bool success = i % 7 != 0;
                runtime.Audit.Write(new AuditEntry
                {
                    At = DateTimeOffset.Now.AddMinutes(-i),
                    Category = categories[i % categories.Length],
                    Action = i % 2 == 0 ? "update" : "operation",
                    DeviceName = i % 3 == 0 ? "iPhone（客厅）" : "本机管理端",
                    DeviceId = i % 3 == 0 ? "dev-online" : null,
                    Source = "local-admin",
                    Detail = success
                        ? "一次非常长的操作详情，用来验证日志页的省略号与行内详情展开：第 " + i + " 条记录，包含足够多的文字以确定它会被截断而不是把表格撑坏"
                        : "操作失败：目标共享已不可用（第 " + i + " 条，模拟真实错误详情）",
                    Success = success
                });
            }
        }

        // The dashboard chart needs plausible non-zero samples: a capture of an idle scratch install would
        // otherwise draw a flat line and prove nothing about the chart's layout. This seam is capture-only.
        for (int i = 0; i < 60; i++)
        {
            double t = i / 60.0;
            runtime.Metrics.InjectSample(
                (double)(2_000_000 + 1_500_000 * Math.Sin(t * Math.PI * 4)),
                (double)(8_000_000 + 5_000_000 * Math.Cos(t * Math.PI * 3)));
        }
    }

    /// <summary>
    /// Puts the wizard into the state a real first connection reaches, so the iPhone page draws its two QR
    /// codes and the square-image assertion has something to check.
    /// </summary>
    private static void ArmOnboarding()
    {
        LanDiskRuntime? runtime = App.Instance.Host?.Runtime;
        if (runtime is null) return;

        runtime.Onboarding.Start();
        if (!runtime.Onboarding.Trusted) runtime.Onboarding.MarkTrusted(runtime.LanAddresses().FirstOrDefault() ?? "192.168.2.2");
        runtime.Onboarding.RecordVerifiedHttpsHost(runtime.Identity.HostName);
    }

    private static void Capture(Window window, string path, double scale)
    {
        var content = (FrameworkElement)window.Content;

        // The layout is measured in DIP; the DPI here is what makes the bitmap match what WPF actually draws
        // on a monitor at that scale (physical = logical × scale).
        int pixelWidth = Math.Max(1, (int)Math.Round(content.ActualWidth * scale));
        int pixelHeight = Math.Max(1, (int)Math.Round(content.ActualHeight * scale));

        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(content);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>An element's rectangle in the coordinate space of <paramref name="root"/>.</summary>
    private static Rect BoundsWithin(FrameworkElement element, FrameworkElement root)
    {
        try
        {
            GeneralTransform transform = element.TransformToAncestor(root);
            return transform.TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        catch (InvalidOperationException)
        {
            return Rect.Empty;
        }
    }

    private static string ButtonLabel(Button button)
        => button.Content switch
        {
            string text when text.Length > 0 => text,
            TextBlock block when !string.IsNullOrWhiteSpace(block.Text) => block.Text,
            _ => "（图标按钮）"
        };
    /// <summary>Collects the visible text under an element, so a gate can assert that a page rendered content.</summary>
    /// <summary>
    /// True when a string would show something. A KVP row separates its columns with an invisible
    /// placeholder, and a placeholder is not a line of text: it grows with the row when a neighbouring
    /// value wraps, which is an alignment decision rather than a layout failure.
    /// </summary>
    private static bool HasVisibleGlyphs(string value)
    {
        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c)) continue;
            UnicodeCategory category = char.GetUnicodeCategory(c);
            if (category is UnicodeCategory.Format or UnicodeCategory.Control or UnicodeCategory.OtherNotAssigned) continue;
            return true;
        }
        return false;
    }

    /// <summary>
    /// How wide the text would need to be to render in full, at the element's own font settings.
    /// <para>Measured by laying out a throwaway TextBlock with infinite width, so it goes through the same text
    /// stack the page itself used. <c>FormattedText</c> was tried first and over-reported by a few DIPs, which
    /// made values like "v1.7.3" look clipped on pages where nothing was wrong — a check that fires on correct
    /// layouts is worse than one that stays quiet. <c>DesiredSize</c> is unusable for the opposite reason: it is
    /// already clamped by the constraint the element was measured with, so it cannot reveal trimming at all.</para>
    /// </summary>
    private static double NaturalTextWidth(TextBlock text)
    {
        try
        {
            var probe = new TextBlock
            {
                Text = text.Text,
                FontFamily = text.FontFamily,
                FontSize = text.FontSize,
                FontStyle = text.FontStyle,
                FontWeight = text.FontWeight,
                FontStretch = text.FontStretch,
                TextWrapping = TextWrapping.NoWrap
            };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return probe.DesiredSize.Width;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static void CollectTexts(DependencyObject root, List<string> into)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock block && !string.IsNullOrWhiteSpace(block.Text)) into.Add(block.Text);
            if (child is System.Windows.Controls.TextBox box && !string.IsNullOrWhiteSpace(box.Text)) into.Add(box.Text);
            CollectTexts(child, into);
        }
    }

    /// <summary>How many text blocks the layout assertions actually examined, across all combinations.</summary>
    private static int inspectedTextBlocks;

    /// <summary>
    /// Per-assertion tallies, so each layout requirement can be bound to a counted result rather than to a
    /// screenshot someone looked at. "Targets" is how many things the assertion actually examined: a run with
    /// zero failures and zero targets did not pass, it did not run.
    /// </summary>
    private sealed class Tally
    {
        public int Targets;
        public int Failures;
        public readonly HashSet<string> Scopes = new(StringComparer.Ordinal);
    }

    private static readonly Dictionary<string, Tally> LayoutTallies = new(StringComparer.Ordinal);

    /// <summary>
    /// Per (assertion, page) tallies.
    ///
    /// <para>A single global total cannot establish that a *particular* page was measured: "4324 values were
    /// checked and none were clipped" is compatible with the page the requirement is about never having been
    /// examined at all. The binding table needs the per-page evidence, so it is recorded here.</para>
    /// </summary>
    private static readonly Dictionary<string, Tally> LayoutPageTallies = new(StringComparer.Ordinal);

    private static void Note(string assertion, string page, int targets, int failures)
    {
        if (!LayoutTallies.TryGetValue(assertion, out Tally? tally))
        {
            tally = new Tally();
            LayoutTallies[assertion] = tally;
        }
        tally.Targets += targets;
        tally.Failures += failures;
        tally.Scopes.Add(page);

        string key = assertion + "\u001f" + page;
        if (!LayoutPageTallies.TryGetValue(key, out Tally? perPage))
        {
            perPage = new Tally();
            LayoutPageTallies[key] = perPage;
        }
        perPage.Targets += targets;
        perPage.Failures += failures;
        perPage.Scopes.Add(page);
    }

    /// <summary>Which requirement each layout assertion is evidence for.</summary>
    private static readonly Dictionary<string, string> LayoutRequirement = new(StringComparer.Ordinal)
    {
        ["LAYOUT-CORE-VALUE-NOT-CLIPPED"] = "REAL11-03",
        ["LAYOUT-VISIBLE-TEXT"] = "REAL11-03",
        ["LAYOUT-OVERFLOW-HORIZONTAL"] = "REAL11-03",
        ["LAYOUT-BUTTON-SPACING"] = "REAL11-04",
        ["LAYOUT-ACTION-OVERLAP"] = "REAL11-05",
        ["LAYOUT-EXTENSION-LONG-CONTENT"] = "REAL11-09",
        ["LAYOUT-FONT-MIN"] = "REAL11-03",
        ["LAYOUT-SINGLE-LINE-HEIGHT"] = "REAL11-03",
        ["LAYOUT-CONTRAST"] = "REAL11-03",
        ["LAYOUT-IMAGE-SQUARE"] = "REAL11-17"
    };

    /// <summary>Diagnostics for the single-line-height assertion, one tab-separated row each.</summary>
    private static readonly List<string> LayoutDiagnostics = new();

    /// <summary>The identity of one captured combination, so expected and actual can be compared as sets.</summary>
    private static string Key(ShellTheme theme, double scale, int physicalW, int physicalH, string page)
        => $"{theme.ToString().ToLowerInvariant()}|{scale:0.00}|{physicalW}x{physicalH}|{page}";

    private static ShellTheme ParseTheme(string value)
        => value.Equals("light", StringComparison.OrdinalIgnoreCase) ? ShellTheme.Light : ShellTheme.Dark;


    /// <summary>
    /// Everything needed to tell "the text really wrapped" from "the element was stretched by its parent" from
    /// "the content is not displayable" — without a rebuild. Built after a failure where the old message printed
    /// a blank sample and nothing else.
    /// </summary>
    private static string DescribeSingleLineText(TextBlock text, FrameworkElement root, string page, ShellTheme theme, int windowWidth)
    {
        var chain = new List<string>();
        for (DependencyObject? cursor = VisualTreeHelper.GetParent(text);
             cursor is not null && chain.Count < 8;
             cursor = VisualTreeHelper.GetParent(cursor))
        {
            chain.Add(cursor is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name)
                ? fe.GetType().Name + "#" + fe.Name
                : cursor.GetType().Name);
        }

        string source = !string.IsNullOrEmpty(text.Text) ? "Text" : (text.Inlines.Count > 0 ? "Inlines" : "empty");
        string inlineText = text.Inlines.Count == 0 ? string.Empty : string.Concat(text.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text));
        string effective = !string.IsNullOrEmpty(text.Text) ? text.Text : inlineText;

        Rect own = BoundsWithin(text, root);
        Rect parent = VisualTreeHelper.GetParent(text) is FrameworkElement p ? BoundsWithin(p, root) : Rect.Empty;
        FrameworkElement? parentElement = VisualTreeHelper.GetParent(text) as FrameworkElement;

        return string.Join('\t',
            page,
            text.GetType().Name,
            text.GetType().Name,
            text.Name ?? string.Empty,
            System.Windows.Automation.AutomationProperties.GetAutomationId(text) ?? string.Empty,
            string.Join(" < ", chain),
            source,
            EscapeText(effective),
            CodePoints(effective),
            text.FontFamily?.Source ?? string.Empty,
            text.FontSize.ToString("0.##"),
            text.LineHeight.ToString("0.##"),
            text.TextWrapping.ToString(),
            text.TextTrimming.ToString(),
            text.ActualWidth.ToString("0.##"),
            text.ActualHeight.ToString("0.##"),
            text.DesiredSize.Width.ToString("0.##"),
            text.DesiredSize.Height.ToString("0.##"),
            $"{own.X:0.#},{own.Y:0.#},{own.Width:0.#},{own.Height:0.#}",
            $"{parent.X:0.#},{parent.Y:0.#},{parent.Width:0.#},{parent.Height:0.#}",
            text.Padding.ToString(),
            text.Margin.ToString(),
            text.HorizontalAlignment.ToString(),
            text.VerticalAlignment.ToString(),
            text.IsVisible.ToString(),
            text.IsEnabled.ToString(),
            (parentElement?.ClipToBounds ?? false).ToString(),
            windowWidth.ToString(),
            (int)(windowWidth * (text.ActualHeight > 0 ? 1 : 1)),
            theme.ToString(),
            inlineText.Length);
    }

    /// <summary>A short form for the per-row detail, naming the element so the row is actionable.</summary>
    private static string DescribeSingleLineTextShort(TextBlock text)
        => $"{text.GetType().Name}{(string.IsNullOrEmpty(text.Name) ? string.Empty : "#" + text.Name)} "
           + $"text=\"{EscapeText(text.Text)}\" codes=[{CodePoints(text.Text)}] "
           + $"h={text.ActualHeight:0.#} w={text.ActualWidth:0.#} font={text.FontSize:0.#} "
           + $"wrap={text.TextWrapping} lineHeight={text.LineHeight:0.##} inlines={text.Inlines.Count}";

    /// <summary>Non-printable characters are shown as escapes so a "blank" sample can still be read.</summary>
    private static string EscapeText(string value)
    {
        var builder = new System.Text.StringBuilder();
        foreach (char c in value ?? string.Empty)
        {
            if (c == ' ') builder.Append('␠');
            else if (char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)
                builder.Append("\\u").Append(((int)c).ToString("X4"));
            else builder.Append(c);
        }
        return builder.ToString();
    }

    private static string CodePoints(string value)
        => string.Join(' ', (value ?? string.Empty).Select(c => "U+" + ((int)c).ToString("X4")));

    private static (bool Ok, string Detail) Inspect(FrameworkElement content, int windowWidth, ShellTheme theme, string page)
    {        var problems = new List<string>();

        double overflow = content.DesiredSize.Width - content.ActualWidth;
            {
                int overflowFailures = (content.ActualWidth > 0 && overflow > 1) ? 1 : 0;
                Note("LAYOUT-OVERFLOW-HORIZONTAL", page, content.ActualWidth > 0 ? 1 : 0, overflowFailures);
                if (overflowFailures > 0) problems.Add($"横向溢出 {overflow:0.#} DIP");
            }

        double worstContrast = double.MaxValue;
        string worstContrastWhere = string.Empty;
        int squareImages = 0;
        int visibleText = 0;

        foreach (DependencyObject node in Descendants(content))
        {
            if (node is TextBlock text)
            {
                if (!text.IsVisible) continue;
                visibleText++;
                inspectedTextBlocks++;

                if (text.FontSize < 12 && !IsIconGlyph(text))
                {
                    var chain = new List<string>();
                    for (DependencyObject? cursor = text; cursor is not null && chain.Count < 5; cursor = VisualTreeHelper.GetParent(cursor))
                    {
                        chain.Add(cursor is FrameworkElement element && !string.IsNullOrEmpty(element.Name)
                            ? element.GetType().Name + "#" + element.Name
                            : cursor.GetType().Name);
                    }

                    string sample = string.IsNullOrWhiteSpace(text.Text) ? "（空）" : text.Text[..Math.Min(12, text.Text.Length)];
                    problems.Add($"字号 {text.FontSize:0.#} DIP 低于 12（{string.Join(" < ", chain)}：“{sample}”）");
                }

                // Whitespace-only text is a spacer, not a line of text: a KVP layout puts one in a row to
                // hold the column, and when a neighbouring value wraps the spacer grows with the row.
                // Flagging that is flagging the alignment, so this is about text a person can read.
                //
                // When it *does* fire, the message has to be enough to identify the element without a rebuild:
                // an earlier version printed only a text sample, and for exactly the case that mattered the
                // sample rendered as blank, so the failure could not be diagnosed from its own evidence.
                if (text.TextWrapping == TextWrapping.NoWrap && text.FontSize > 0 && text.ActualHeight > text.FontSize * 2.2
                    && HasVisibleGlyphs(text.Text))
                {
                    string diagnostic = DescribeSingleLineText(text, content, page, theme, windowWidth);
                    LayoutDiagnostics.Add(diagnostic);
                    problems.Add($"单行文本高度 {text.ActualHeight:0.#} 异常：" + DescribeSingleLineTextShort(text));
                }

                if (string.IsNullOrWhiteSpace(text.Text) || text.FontSize < 12) continue;
                double contrast = ContrastRatio(text.Foreground, BackgroundBehind(text));
                if (contrast < worstContrast)
                {
                    worstContrast = contrast;
                    worstContrastWhere = text.Text[..Math.Min(12, text.Text.Length)];
                }
            }
            else if (node is Image image)
            {
                if (!image.IsVisible) continue;
                if (image.ActualWidth <= 0 || image.ActualHeight <= 0) continue;
                squareImages++;
                double delta = Math.Abs(image.ActualWidth - image.ActualHeight);
                if (delta > 0.5)
                    problems.Add($"图片非正方形 {image.ActualWidth:0.#}x{image.ActualHeight:0.#}");
            }
        }

        if (worstContrast < 3.0 && worstContrastWhere.Length > 0)
            problems.Add($"文字与背景对比度仅 {worstContrast:0.00}：“{worstContrastWhere}”");

        // ---- action collision: no two visible buttons may overlap their rectangles
        //
        // The Owner photographed a device row where 详情 / 只读 / … were drawn on top of each other. The cause
        // was two elements sharing one grid cell, which is invisible in a screenshot review and invisible to a
        // width assertion — both were "within bounds". Comparing rectangles is what actually catches it.
        var buttons = new List<(Rect Bounds, string Label)>();
        foreach (DependencyObject node in Descendants(content))
        {
            if (node is not Button button || !button.IsVisible) continue;
            if (button.ActualWidth <= 0 || button.ActualHeight <= 0) continue;
            if (button.Parent is null) continue;

            Rect bounds = BoundsWithin(button, content);
            if (bounds.IsEmpty) continue;
            buttons.Add((bounds, ButtonLabel(button)));
        }

        int collisions = 0;
        int overlapPairsChecked = 0;
        for (int i = 0; i < buttons.Count; i++)
        {
            for (int j = i + 1; j < buttons.Count; j++)
            {
                overlapPairsChecked++;
                Rect overlap = Rect.Intersect(buttons[i].Bounds, buttons[j].Bounds);
                if (overlap.IsEmpty || overlap.Width * overlap.Height < 1) continue;
                collisions++;
                if (collisions <= 3)
                {
                    problems.Add($"操作控件重叠：{buttons[i].Label} 与 {buttons[j].Label} 相交 " +
                                 $"{overlap.Width:0.#}x{overlap.Height:0.#} DIP");
                }
            }
        }

        // ---- toolbar spacing: adjacent buttons must not touch
        //
        // The Owner reported a share toolbar whose buttons pressed against each other. Overlap detection
        // cannot see that (nothing intersects) and a width assertion cannot see it either, so the gap itself
        // is measured: any two buttons sharing a row and closer than 8 DIP is a layout failure.
        int crowded = 0;
        int spacingPairsChecked = 0;
        string crowdedSample = string.Empty;
        for (int i = 0; i < buttons.Count; i++)
        {
            for (int j = i + 1; j < buttons.Count; j++)
            {
                Rect a = buttons[i].Bounds, b = buttons[j].Bounds;
                double verticalOverlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
                if (verticalOverlap <= Math.Min(a.Height, b.Height) * 0.5) continue;   // not the same row

                spacingPairsChecked++;
                spacingPairsChecked++;
                double gap = a.Right <= b.Left ? b.Left - a.Right : (b.Right <= a.Left ? a.Left - b.Right : -1);
                if (gap < 0) continue;   // overlapping is reported by the collision check above
                if (gap >= 8) continue;

                crowded++;
                if (crowdedSample.Length == 0) crowdedSample = buttons[i].Label + " / " + buttons[j].Label;
            }
        }

        Note("LAYOUT-ACTION-OVERLAP", page, overlapPairsChecked, collisions);
        Note("LAYOUT-BUTTON-SPACING", page, spacingPairsChecked, crowded);
        if (crowded > 0)
            problems.Add($"有 {crowded} 处相邻按钮间距小于 8 DIP（首个：{crowdedSample}）");
        // ---- primary value clipping
        //
        // Only values the UI marks as primary are checked: a metric, a key/value value or a status badge.
        // "HTTPS 已…" is exactly the failure the Owner photographed, and it is a layout failure rather than a
        // long value. Data-table cells are deliberately not tagged — a path or a user agent may abbreviate,
        // with the full text in the row details — so tagging is what keeps this from becoming a check that
        // fires on everything and therefore means nothing.
        int clippedPrimary = 0;
        int primaryCount = 0;
        var taggedLabels = new List<string>();
        foreach (DependencyObject node in Descendants(content))
        {
            if (!Equals((node as FrameworkElement)?.Tag, Ui.PrimaryTag) || !(node as UIElement)!.IsVisible) continue;

            if (node is TextBlock text)
            {
                if (string.IsNullOrWhiteSpace(text.Text)) continue;
                primaryCount++;
                taggedLabels.Add(text.Text);
                if (text.ActualWidth <= 0) continue;

                // The comparison has to be against the width the text NEEDS, not against DesiredSize.
                //
                // DesiredSize is the size the element asked for given the constraint it was measured with, so a
                // value constrained into a narrow column reports a DesiredSize that already fits — and the old
                // `ActualWidth < DesiredSize - 2` test could therefore never fire on the very case it was
                // written for ("HTTPS 已…" trimmed with an ellipsis in a narrow column). Measuring the string
                // itself is what makes the assertion able to fail.
                double natural = NaturalTextWidth(text);
                if (natural > 0 && text.ActualWidth < natural - 2) clippedPrimary++;
            }
            else if (node is TextBox box)
            {
                // A read-only TextBox scrolls instead of trimming, so its own Extent/Viewport is the
                // measurement: extent wider than viewport means the value is not fully readable in place.
                if (string.IsNullOrWhiteSpace(box.Text)) continue;
                if (box.Text.Length > 24) continue;   // a long value may scroll; the tooltip has it
                primaryCount++;
                taggedLabels.Add(box.Text);
                if (box.ViewportWidth > 0 && box.ExtentWidth > box.ViewportWidth + 2) clippedPrimary++;
            }
        }

        string clippedSample = clippedPrimary == 0 ? string.Empty : taggedLabels.FirstOrDefault() ?? string.Empty;

        Note("LAYOUT-CORE-VALUE-NOT-CLIPPED", page, primaryCount, clippedPrimary);
        if (clippedPrimary > 0)
            problems.Add($"有 {clippedPrimary} 处核心状态值被截断（首个：“{clippedSample}”）");
        if (primaryCount == 0)
            problems.Add("页面没有标记任何核心状态值，截断断言等于没跑");

        // The scratch root seeds one extension package whose name, publisher and command list are all
        // deliberately too long. An empty extensions page would sail through every layout assertion above —
        // there would be nothing on it to overflow — so the page has to prove it rendered that content.
        if (page.Contains("扩展", StringComparison.Ordinal))
        {
            var texts = new List<string>();
            CollectTexts(content, texts);
            bool hasPublisher = texts.Any(t => t.Contains("第三方发布者", StringComparison.Ordinal));
            bool hasName = texts.Any(t => t.Contains("名称刻意写长", StringComparison.Ordinal));
            Note("LAYOUT-EXTENSION-LONG-CONTENT", page, (hasPublisher ? 1 : 0) + (hasName ? 1 : 0),
                hasPublisher && hasName ? 0 : 1);
            if (!hasPublisher || !hasName)
                problems.Add("扩展页没有渲染出长名称/长发布者的示例内容，溢出断言等于没跑");
        }

        if (page == IphonePageTitle && squareImages == 0)
            problems.Add("连接 iPhone 页没有渲染出任何二维码图片，方形断言等于没跑");

        // The capture must not pass on an empty page: the worst-case seed guarantees the busy pages have
        // content, and this asserts the page actually drew some text.
        Note("LAYOUT-VISIBLE-TEXT", page, content.ActualWidth > 0 ? 1 : 0, visibleText == 0 ? 1 : 0);
        if (visibleText == 0)
            problems.Add("页面没有渲染出任何可见文字（空页或渲染失败）");

        string detail = problems.Count == 0
            ? $"宽度 {content.ActualWidth:0} DIP / 窗口 {windowWidth} / {theme}，" +
              $"无溢出、无竖排、无操作重叠、无短文本截断、字号合规、最低对比度 {worstContrast:0.0}、" +
              $"正方形图片 {squareImages}、可见文本 {visibleText}、操作控件 {buttons.Count}"
            : string.Join("；", problems.Take(3));

        return (problems.Count == 0, detail);
    }

    private static Color BackgroundBehind(DependencyObject element)
    {
        DependencyObject? node = element;
        while (node is not null)
        {
            Brush? brush = node switch
            {
                Panel panel => panel.Background,
                Border border => border.Background,
                Control control => control.Background,
                _ => null
            };

            if (brush is SolidColorBrush solid && solid.Color.A == 255) return solid.Color;
            node = VisualTreeHelper.GetParent(node);
        }

        return ThemeManager.IsDarkNow ? Color.FromRgb(0x0B, 0x0F, 0x14) : Color.FromRgb(0xF4, 0xF7, 0xFB);
    }

    private static bool IsIconGlyph(TextBlock text)
    {
        for (DependencyObject? cursor = text; cursor is not null; cursor = VisualTreeHelper.GetParent(cursor))
        {
            if (cursor.GetType().Name == "SymbolIcon") return true;
            if (cursor is Window) break;
        }
        return false;
    }

    private static double ContrastRatio(Brush foreground, Color background)
    {
        Color front = foreground is SolidColorBrush solid ? solid.Color : Colors.White;
        double l1 = RelativeLuminance(front);
        double l2 = RelativeLuminance(background);
        (double lighter, double darker) = l1 >= l2 ? (l1, l2) : (l2, l1);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            double v = value / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (DependencyObject grandChild in Descendants(child)) yield return grandChild;
        }
    }

    private static int IndexOf(string[] args, string name)
        => Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? Value(string[] args, string name)
    {
        int index = IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : null;
    }
}
