using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LanDisk.Core.Configuration;
using LanDisk.Core.Networking;
using LanDisk.Server.Services;
using LanDisk.Update;
// WPF-UI ships controls with the same simple names as WPF itself (Button, TextBlock, ...). Importing
// the whole namespace would make every one of those ambiguous, so only the types actually used are
// brought in by name.
using InfoBar = Wpf.Ui.Controls.InfoBar;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;
using Card = Wpf.Ui.Controls.Card;

namespace LanDisk.App;

/// <summary>HTTPS / 安全：本地根 CA、服务器证书，以及一次性的“重置证书”危险动作。</summary>
internal sealed class HttpsPage : NativePage
{
    private readonly StackPanel _body = new();

    public override UIElement Build()
    {
        Root.Children.Add(Ui.Title("HTTPS / 安全"));
        Root.Children.Add(Ui.Body("iPhone 信任的是 LanDisk 本地根证书。根证书不变，所以换 IP、续期、升级都不需要重新信任。", secondary: true));
        Root.Children.Add(_body);
        return Root;
    }

    public override Task RefreshAsync()
    {
        _body.Children.Clear();

        LanDiskRuntime runtime = Runtime;
        runtime.EnsureCertificates();

        CertificateInfo? certificate = runtime.Certificate;
        if (certificate is null)
        {
            _body.Children.Add(Ui.Notice("尚未生成证书。", InfoBarSeverity.Warning));
            return Task.CompletedTask;
        }

        _body.Children.Add(Ui.Panel("本地根证书（iPhone 需要信任的就是它）",
            Ui.KeyValues(
                ("主体", certificate.RootCaSubject),
                ("SHA-256 指纹", certificate.RootCaSha256),
                ("有效期至", certificate.RootCaNotAfter.ToLocalTime().ToString("yyyy-MM-dd"))),
            Ui.Body("私钥保存在本机数据目录，用当前 Windows 用户的 DPAPI 封存，任何界面都不会显示或导出它。", secondary: true)));

        _body.Children.Add(Ui.Panel("服务器证书",
            Ui.KeyValues(
                ("主体", certificate.Subject),
                ("签发者", certificate.Issuer),
                ("SHA-256 指纹", certificate.Sha256Fingerprint),
                ("有效期至", certificate.NotAfter.ToLocalTime().ToString("yyyy-MM-dd")),
                ("覆盖名称", string.Join("、", certificate.DnsNames)),
                ("覆盖地址", string.Join("、", certificate.IpAddresses)),
                ("本次签发原因", certificate.LeafReason))));

        _body.Children.Add(Ui.Panel("危险操作",
            Ui.Body("重置本地根证书会让所有已安装该证书的 iPhone 必须重新安装并重新开启“完全信任”。" +
                    "共享目录与已配对设备不受影响。", secondary: true),
            Ui.Action("重置本地根证书（需要手机重新信任）", async () =>
            {
                if (!NativeDialogs.Confirm("重置本地根证书",
                        "确定要重置吗？\n\n所有 iPhone 都需要重新安装证书并重新打开“完全信任”，否则 Safari 会报证书错误。", "重置"))
                {
                    return;
                }

                runtime.Certificates.ResetCa(runtime.Identity.InstallId);
                runtime.EnsureCertificates(refresh: true);
                NativeDialogs.Info("已重置", "本地根证书已更换，请在所有 iPhone 上重新安装并开启完全信任。");
                await RefreshAsync();
            })));

        return Task.CompletedTask;
    }
}

/// <summary>回收站：手机端删除的内容先进入这里，可以恢复或永久删除。</summary>
internal sealed class RecyclePage : NativePage
{
    private readonly StackPanel _body = new();

    public override UIElement Build()
    {
        Root.Children.Add(Ui.Title("回收站"));
        Root.Children.Add(Ui.Body("手机端删除的文件会先进到这里，不会立刻从磁盘上消失。", secondary: true));
        Root.Children.Add(Ui.Row(Ui.Action("刷新", RefreshAsync)));
        Root.Children.Add(_body);
        return Root;
    }

    public override Task RefreshAsync()
    {
        _body.Children.Clear();

        LanDiskRuntime runtime = Runtime;
        var entries = runtime.Recycle.List(e => e.State == Core.Model.RecycleState.Committed);

        // A headline count, so this list page still states one primary value (and the UI capture's
        // "no primary value was tagged" assertion stays a real check rather than being switched off).
        _body.Children.Add(Ui.StatusBadge("共 " + entries.Count + " 项可恢复", entries.Count > 0 ? "accent" : "info"));

        _body.Children.Add(Ui.TableRows(
            ["名称", "原位置", "大小", "删除时间", "删除者"],
            entries.OrderByDescending(e => e.DeletedAt)
                .Select(e => (IReadOnlyList<string>)new[]
                {
                    e.DisplayName,
                    e.OriginalRelativePath,
                    Presenters.FormatBytes(e.Length),
                    e.DeletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    e.DeletedByDeviceName
                }).ToList()));

        foreach (Core.Model.RecycleEntry entry in entries.Take(20))
        {
            Core.Model.RecycleEntry captured = entry;
            _body.Children.Add(Ui.Row(
                new TextBlock { Text = captured.DisplayName, FontSize = Ui.BodySize, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) },
                Ui.Action("恢复", async () =>
                {
                    // The actor is the local administrator, not "whichever phone is first in the list".
                    // The old code passed runtime.Devices.All().First(), so restoring on a machine with no
                    // paired device threw, and with one it credited that phone with the administrator's work.
                    await DevicesPage.RunAsync(
                        () => Runtime.Admin.RestoreRecycleEntry(captured.Id, Core.Model.OperationActor.LocalAdmin),
                        "无法恢复");
                    await RefreshAsync();
                }),
                Ui.Action("永久删除", async () =>
                {
                    if (!NativeDialogs.Confirm("永久删除", "“" + captured.DisplayName + "”将被永久删除，无法恢复。", "永久删除")) return;
                    await DevicesPage.RunAsync(() => Runtime.Admin.PurgeRecycleEntry(captured.Id), "无法永久删除");
                    await RefreshAsync();
                })));
        }

        return Task.CompletedTask;
    }
}

/// <summary>系统更新：选择 .lupd、验签、安装并重启；回滚结果由更新程序记录。</summary>
internal sealed class UpdatePage : NativePage
{
    private readonly StackPanel _body = new();

    public override UIElement Build()
    {
        Root.Children.Add(Ui.Title("系统更新"));
        Root.Children.Add(Ui.Body("更新包是经过发布签名校验的 .lupd 文件。安装过程中服务会重启，失败会自动回滚。", secondary: true));
        Root.Children.Add(_body);
        return Root;
    }

    public override Task RefreshAsync()
    {
        _body.Children.Clear();

        LanDiskRuntime runtime = Runtime;
        var store = runtime.Updates.Store.Read();

        _body.Children.Add(Ui.Panel("当前版本",
            Ui.KeyValues(
                ("运行中的版本", runtime.Updates.CurrentVersion),
                ("构建提交", runtime.Updates.CurrentCommit),
                ("安装目录版本", store.InstalledVersion),
                ("安装目录提交", store.InstalledCommit),
                ("上次结果", string.IsNullOrEmpty(store.LastOutcome) ? "—" : store.LastOutcome + "：" + (store.LastMessage ?? "")))));

        if (!string.IsNullOrEmpty(store.PendingVersion))
        {
            _body.Children.Add(Ui.Panel("待安装",
                Ui.KeyValues(
                    ("版本", store.PendingVersion),
                    ("构建提交", store.PendingBuildCommit ?? "—"),
                    ("包 SHA256", store.PendingPackageSha256 ?? "—"),
                    ("签名者", store.PendingKeyId ?? "—")),
                Ui.Row(
                    Ui.Action("安装并重启", async () =>
                    {
                        if (!NativeDialogs.Confirm("安装更新",
                                "LanDisk 将停止服务、替换程序文件并重新启动。失败时会自动回滚到当前版本。", "安装")) return;
                        // Bound ports, not configured: a configured 0 means "pick a free port", and passing it
                        // would tell the updater to restore an endpoint that was never in use.
                        runtime.Updates.RequestApply(runtime.BoundHttpPort, TimeSpan.FromSeconds(120), runtime.BoundHttpsPort);
                    }, primary: true),
                    Ui.Action("丢弃", async () =>
                    {
                        runtime.Updates.DiscardPending();
                        await RefreshAsync();
                    }))));
        }

        _body.Children.Add(Ui.Row(
            Ui.Action("选择更新包…", async () =>
            {
                string? file = NativeDialogs.PickUpdatePackage(Path.Combine(runtime.Paths.DataRoot, "updates"));
                if (file is null) return;

                try
                {
                    await using FileStream stream = File.OpenRead(file);
                    StagedUpdate staged = await runtime.Updates.StageAsync(stream, Path.GetFileName(file));
                    NativeDialogs.Info(staged.Ok ? "更新包已校验" : "更新包被拒绝",
                        staged.Ok ? $"版本 {staged.Version}，签名者 {staged.KeyId}。" : (staged.Message ?? "校验未通过。"));
                }
                catch (Exception ex)
                {
                    NativeDialogs.Info("无法读取更新包", ex.Message);
                }

                await RefreshAsync();
            }, primary: true),
            Ui.Action("刷新", RefreshAsync)));

        return Task.CompletedTask;
    }
}

/// <summary>自检：把“为什么不工作”直接说出来。</summary>
internal sealed class SelfCheckPage : NativePage
{
    private readonly StackPanel _body = new();

    public override UIElement Build()
    {
        Root.Children.Add(Ui.PageHeader(
            "自检",
            "分组给出结论，技术细节收在展开区里。任何一项不通过都会说明原因和影响，而不是只写“失败”。",
            Ui.Action("重新检查", RefreshAsync)));
        Root.Children.Add(_body);
        return Root;
    }

    public override Task RefreshAsync()
    {
        _body.Children.Clear();

        IReadOnlyList<(string Item, string State, string Detail)> rows = Presenters.SelfCheck(Runtime);

        int pass = rows.Count(r => r.State == "通过");
        int warn = rows.Count(r => r.State is "提示" or "不可用" or "未启用");
        int fail = rows.Count - pass - warn;

        var badges = new WrapPanel { Margin = new Thickness(0, 0, 0, Tokens.Space4) };
        badges.Children.Add(Ui.StatusBadge(pass + " 项正常", "success"));
        if (warn > 0) badges.Children.Add(Ui.StatusBadge(warn + " 项需要注意", "warning"));
        if (fail > 0) badges.Children.Add(Ui.StatusBadge(fail + " 项错误", "danger"));

        string headline = fail > 0
            ? "有 " + fail + " 项没有通过，手机可能无法连接。"
            : warn > 0
                ? "核心功能可用，但有 " + warn + " 项需要注意。"
                : "全部检查通过，手机可以正常连接。";
        badges.Children.Add(Ui.StatusBadge(headline, fail > 0 ? "danger" : warn > 0 ? "warning" : "success"));
        _body.Children.Add(badges);

        // Grouped, because "which part of the product is unhappy" is the question a user actually has.
        var groups = new (string Group, string[] Items)[]
        {
            ("服务与存储", ["服务", "物理磁盘", "共享"]),
            ("网络", ["物理局域网", "VPN / TUN", "被排除的网卡"]),
            ("HTTPS 与安全", ["HTTPS", "证书", "防火墙"]),
            ("发现与更新", ["稳定名称 (mDNS)", "系统更新"])
        };

        var grouped = new HashSet<string>();
        foreach ((string group, string[] items) in groups)
        {
            List<(string Item, string State, string Detail)> matched = [.. rows.Where(r => items.Contains(r.Item))];
            if (matched.Count == 0) continue;
            foreach (var row in matched) grouped.Add(row.Item);
            _body.Children.Add(BuildGroup(group, matched));
        }

        List<(string Item, string State, string Detail)> others = [.. rows.Where(r => !grouped.Contains(r.Item))];
        if (others.Count > 0) _body.Children.Add(BuildGroup("其他", others));

        return Task.CompletedTask;
    }

    private static Border BuildGroup(string group, IReadOnlyList<(string Item, string State, string Detail)> rows)
    {
        var content = new StackPanel();
        foreach ((string item, string state, string detail) in rows)
        {
            (string text, string tone) = state switch
            {
                "通过" => ("正常", "success"),
                "提示" or "不可用" or "未启用" => ("注意", "warning"),
                _ => ("错误", "danger")
            };

            var line = new Grid { Margin = new Thickness(0, 0, 0, Tokens.Space2) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var name = new TextBlock
            {
                Text = item,
                FontSize = Tokens.Body,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Ui.Brush("TextPrimary")
            };
            Grid.SetColumn(name, 0);
            line.Children.Add(name);

            Border badge = Ui.StatusBadge(text, tone);
            Grid.SetColumn(badge, 1);
            line.Children.Add(badge);

            // One sentence at the main level, the full technical detail behind "详情" — the old page printed
            // the entire adapter list and every URL inline, which is what made it unreadable.
            string summary = Summarize(detail);
            var summaryText = new TextBlock
            {
                Text = summary,
                FontSize = Tokens.Body,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Ui.Brush("TextSecondary")
            };
            Grid.SetColumn(summaryText, 2);
            line.Children.Add(summaryText);

            content.Children.Add(line);

            if (summary != detail)
            {
                var details = new Expander
                {
                    Header = "技术细节",
                    FontSize = Tokens.Caption,
                    Margin = new Thickness(170, 0, 0, Tokens.Space2),
                    IsExpanded = false,
                    Content = new TextBlock
                    {
                        Text = detail,
                        FontSize = Tokens.Caption,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Ui.Brush("TextTertiary"),
                        Margin = new Thickness(0, Tokens.Space1, 0, Tokens.Space2)
                    }
                };
                content.Children.Add(details);
            }
        }

        return Ui.Card(group, null, content);
    }

    /// <summary>The first sentence of a detail, capped so it fits one line and reads as a status.</summary>
    private static string Summarize(string detail)
    {
        string first = detail.Split('。')[0];
        return first.Length <= 90 ? first : first[..90] + "…";
    }
}

/// <summary>日志：真正的日志中心 — 筛选、搜索、大数据量与逐条详情。</summary>
internal sealed class LogsPage : NativePage
{
    private readonly StackPanel _body = new();
    private readonly ComboBox _level = new() { Width = 110 };
    private readonly ComboBox _category = new() { Width = 150 };
    private readonly ComboBox _range = new() { Width = 130 };
    private readonly TextBox _search = new() { Width = 220, Padding = new Thickness(8, 5, 8, 5) };
    private int _limit = 200;

    public override UIElement Build()
    {
        _level.ItemsSource = new[] { "全部级别", "成功", "失败" };
        _level.SelectedIndex = 0;
        _category.ItemsSource = new[] { "全部分类", "device", "pairing", "share", "file", "auth", "theme", "extension", "transfer", "update", "system" };
        _category.SelectedIndex = 0;
        _range.ItemsSource = new[] { "全部时间", "最近 1 小时", "最近 24 小时", "最近 7 天" };
        _range.SelectedIndex = 0;
        _search.ToolTip = "搜索类别、操作、来源或详情";

        foreach (var control in new Control[] { _level, _category, _range })
        {
            control.Margin = new Thickness(0, 0, Tokens.Space2, 0);
            // WPF-UI's combo template carries its own (smaller) text style; the product floor is 12 DIP, and
            // the layout gate asserts it on every rendered TextBlock, so the size is stated here explicitly.
            control.FontSize = Tokens.Body;
        }
        _search.Margin = new Thickness(0, 0, Tokens.Space2, 0);
        _search.FontSize = Tokens.Body;
        _level.SelectionChanged += async (_, _) => await RefreshAsync();
        _category.SelectionChanged += async (_, _) => await RefreshAsync();
        _range.SelectionChanged += async (_, _) => await RefreshAsync();
        _search.TextChanged += async (_, _) => await RefreshAsync();

        Root.Children.Add(Ui.PageHeader(
            "日志",
            "记录本机管理与手机端的每一次操作。密码、令牌、Cookie 与私钥永远不会写入日志；详情按行展开，不会把表格挤坏。",
            Ui.Action("刷新", RefreshAsync),
            Ui.Action("打开日志目录", () =>
            {
                NativeDialogs.OpenFolder(Runtime.Paths.LogsDir);
                return Task.CompletedTask;
            })));
        Root.Children.Add(_body);
        return Root;
    }

    public override Task RefreshAsync()
    {
        _body.Children.Clear();

        List<LogRow> all = [.. Presenters.Audit(Runtime, 2000)];
        IEnumerable<LogRow> filtered = all;

        // A headline count: this list page still has one primary value, which keeps the UI capture's
        // "no primary value was tagged" assertion a real check rather than one that is switched off.
        _body.Children.Add(Ui.StatusBadge("共 " + all.Count + " 条记录", all.Count > 0 ? "accent" : "info"));

        string? level = _level.SelectedItem as string;
        if (level is "成功") filtered = filtered.Where(r => r.结果 == "成功");
        if (level is "失败") filtered = filtered.Where(r => r.结果 != "成功");

        string? category = _category.SelectedItem as string;
        if (category is not null && category != "全部分类") filtered = filtered.Where(r => r.类别 == category);

        string? range = _range.SelectedItem as string;
        DateTimeOffset cutoff = range switch
        {
            "最近 1 小时" => DateTimeOffset.Now.AddHours(-1),
            "最近 24 小时" => DateTimeOffset.Now.AddDays(-1),
            "最近 7 天" => DateTimeOffset.Now.AddDays(-7),
            _ => DateTimeOffset.MinValue
        };
        if (cutoff > DateTimeOffset.MinValue)
        {
            // The presenter prints local time; "最近 N" is a wall-clock question, so compare as text dates.
            string prefix = cutoff.ToLocalTime().ToString("MM-dd");
            filtered = filtered.Where(r => string.CompareOrdinal(r.时间[..5], prefix) >= 0);
        }

        string needle = _search.Text?.Trim() ?? string.Empty;
        if (needle.Length > 0)
        {
            filtered = filtered.Where(r =>
                r.类别.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                r.操作.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                r.来源.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                r.详情.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        List<LogRow> list = [.. filtered];
        List<LogRow> page = [.. list.Take(_limit)];

        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, Tokens.Space3) };
        toolbar.Children.Add(Ui.Caption("级别"));
        toolbar.Children.Add(_level);
        toolbar.Children.Add(Ui.Caption("分类"));
        toolbar.Children.Add(_category);
        toolbar.Children.Add(Ui.Caption("时间"));
        toolbar.Children.Add(_range);
        toolbar.Children.Add(Ui.Caption("搜索"));
        toolbar.Children.Add(_search);
        _body.Children.Add(toolbar);

        _body.Children.Add(Ui.Card(null,
            $"共 {list.Count} 条匹配，显示最近 {page.Count} 条" + (list.Count > page.Count ? "（点「显示更多」继续）" : ""),
            AdaptiveTable.Build(
            [
                new AdaptiveTable.Column("时间", 130, 0, Mono: true),
                new AdaptiveTable.Column("类别", 100, 1),
                new AdaptiveTable.Column("操作", 170, 0),
                new AdaptiveTable.Column("来源", 140, 1),
                new AdaptiveTable.Column("摘要", 320, 0),
                new AdaptiveTable.Column("结果", 70, 0)
            ],
            [.. page.Select(r => new AdaptiveTable.Row
            {
                Cells =
                [
                    AdaptiveTable.Cell.Of(r.时间),
                    AdaptiveTable.Cell.Of(r.类别),
                    AdaptiveTable.Cell.Of(r.操作),
                    AdaptiveTable.Cell.Of(r.来源),
                    AdaptiveTable.Cell.Of(r.详情)
                ],
                Extra = [("完整详情", r.详情), ("结果", r.结果)]
            })],
            Ui.EmptyState("没有匹配的日志", "换一个级别、分类或关键词试试。"))));

        if (list.Count > page.Count)
        {
            _body.Children.Add(Ui.Row(Ui.Action($"显示更多（当前 {page.Count} / {list.Count}）", async () =>
            {
                _limit += 500;
                await RefreshAsync();
            })));
        }

        _body.Children.Add(Ui.Body("日志文件：" + Path.Combine(Runtime.Paths.LogsDir, "landisk.log"), secondary: true));
        return Task.CompletedTask;
    }
}

/// <summary>设置：开关机启动、主题、以及唯一允许打开浏览器的恢复入口。</summary>
internal sealed class SettingsPage : NativePage
{
    private readonly StackPanel _body = new();

    public override UIElement Build()
    {
        Root.Children.Add(Ui.PageHeader("设置",
            "外观、运行配置与恢复入口。这里显示的“当前生效值”就是服务正在使用的值，不是一份单独的界面配置。"));
        Root.Children.Add(_body);
        return Root;
    }

    public override Task RefreshAsync()
    {
        _body.Children.Clear();

        LanDiskRuntime runtime = Runtime;
        ShellPreferences preferences = ShellPreferences.Load(runtime.Paths.DataRoot);

        _body.Children.Add(Ui.Panel("启动",
            Ui.Body("开机启动使用当前用户的启动项，不需要管理员权限。", secondary: true),
            Ui.Row(
                Ui.Action(AutoStart.IsEnabled() ? "关闭开机启动" : "开启开机启动", async () =>
                {
                    if (AutoStart.IsEnabled()) AutoStart.Disable(out _);
                    else AutoStart.Enable(out string? error);
                    await RefreshAsync();
                }),
                Ui.Action("删除快捷方式/开机启动的检查", async () =>
                {
                    NativeDialogs.Info("当前状态",
                        "开机启动：" + (AutoStart.IsEnabled() ? "已开启" : "未开启") + "\n" +
                        "启动命令：" + (AutoStart.RegisteredCommand() ?? "—"));
                    await Task.CompletedTask;
                }))));

        // ---- Windows 外观：命名主题（含自定义），名称保存在 shell.json
        string currentProfile = preferences.HasKnownThemeProfile ? preferences.ThemeProfile : "follow-system";
        ThemeProfile resolved = ThemeCatalog.Resolve(currentProfile, ThemeManager.IsDarkNow);
        _body.Children.Add(Ui.Card("Windows 外观",
            "命名主题决定窗口与页面的全部颜色；「跟随系统」只在深色与浅色之间切换产品内置的两套配色。",
            Ui.KeyValues(
                ("当前主题", resolved.Name + "（" + (resolved.IsLight ? "浅色" : "深色") + "）"),
                ("主题 ID", currentProfile),
                ("对比度", ThemeCatalog.Validate(resolved).Summary)),
            Ui.Row([.. ThemeCatalog.Windows.Select(profile => Ui.Action(
                profile.Id == currentProfile ? "• " + profile.Name : profile.Name,
                async () =>
                {
                    ThemeManager.ApplyProfile(profile.Id);
                    preferences.ThemeProfile = profile.Id;
                    preferences.Save(runtime.Paths.DataRoot);
                    await RefreshAsync();
                },
                primary: profile.Id == currentProfile))]),
            Ui.Row(Ui.Action("自定义主题…", async () =>
            {
                await EditCustomThemeAsync(runtime, preferences);
                await RefreshAsync();
            }),
            Ui.Action("恢复默认（跟随系统）", async () =>
            {
                ThemeManager.ApplyProfile("follow-system");
                preferences.ThemeProfile = "follow-system";
                preferences.Save(runtime.Paths.DataRoot);
                await RefreshAsync();
            }))));

        // ---- iPhone 外观：由本机决定，手机从认证 API 读取
        MobileThemeChoices choices = runtime.MobileTheme.ChoicesModel();
        _body.Children.Add(Ui.Card("iPhone 外观",
            "这台电脑决定手机的配色，手机在下次加载或心跳时自动应用，不需要在手机上操作。",
            Ui.KeyValues(
                ("当前主题", choices.Profiles.FirstOrDefault(p => p.Id == choices.Current)?.Name ?? choices.Current),
                ("主题 ID", choices.Current),
                ("修订号", choices.Revision.ToString() + "（手机按这个数字判断是否需要重新获取）")),
            Ui.Row([.. choices.Profiles.Select(profile => Ui.Action(
                profile.Id == choices.Current ? "• " + profile.Name : profile.Name,
                async () =>
                {
                    (bool ok, ThemeValidation validation, string message) = runtime.MobileTheme.Set(profile.Id, null, null, null, null);
                    if (!ok) NativeDialogs.Info("无法应用", message);
                    else NativeDialogs.Info("已应用到手机", message + "\n" + validation.Summary);
                    await RefreshAsync();
                },
                primary: profile.Id == choices.Current))]),
            Ui.Row(Ui.Action("自定义 iPhone 主题…", async () =>
            {
                await EditCustomMobileThemeAsync(runtime);
                await RefreshAsync();
            }),
            Ui.Action("恢复默认（跟随 iPhone 系统）", async () =>
            {
                runtime.MobileTheme.Set("follow-iphone-system", null, null, null, null);
                await RefreshAsync();
            }))));

        // ---- 运行配置：生效值 vs 持久化值，以及是否需要重启
        _body.Children.Add(Ui.Card("运行配置",
            "「当前生效值」来自正在运行的服务；「持久化值」来自 settings.json。两者不同表示需要重启服务才会生效。",
            AdaptiveTable.Build(
            [
                new AdaptiveTable.Column("配置项", 200, 0),
                new AdaptiveTable.Column("当前生效值", 160, 0, Mono: true),
                new AdaptiveTable.Column("持久化值", 160, 0, Mono: true),
                new AdaptiveTable.Column("需要重启", 100, 1)
            ],
            [.. RuntimeConfigRows(runtime, preferences).Select(row => new AdaptiveTable.Row
            {
                Cells =
                [
                    AdaptiveTable.Cell.Of(row.Name),
                    AdaptiveTable.Cell.Of(row.Effective),
                    AdaptiveTable.Cell.Of(row.Persisted),
                    AdaptiveTable.Cell.Of(row.RestartRequired)
                ],
                Extra = [("说明", row.Note)]
            })]),
            Ui.Row(Ui.Action("重新启动服务以应用", async () =>
            {
                if (App.Instance.Host is null) return;
                await App.Instance.Host.RestartAsync();
                await RefreshAsync();
            }))));

        _body.Children.Add(Ui.Panel("关于",
            Ui.KeyValues(
                ("版本", BuildInfo.Version),
                ("构建提交", BuildInfo.Commit),
                ("数据目录", runtime.Paths.DataRoot),
                ("安装目录", AppContext.BaseDirectory),
                ("界面", "原生 WPF + WPF-UI（MIT）"))));

        _body.Children.Add(Ui.Panel("高级",
            Ui.Body("下面的入口会打开系统浏览器。它是故障恢复与调试用的备用管理界面，" +
                    "日常操作不需要它：正常使用请留在本窗口内完成。", secondary: true),
            Ui.Row(Ui.Action("打开 Web 管理恢复页（浏览器）", async () =>
            {
                string? adminUrl = App.Instance.Host?.Status.AdminUrl;
                if (adminUrl is null)
                {
                    NativeDialogs.Info("暂不可用", "本机管理地址尚未就绪。");
                    return;
                }

                if (!NativeDialogs.Confirm("打开浏览器",
                        "即将在系统浏览器中打开 LanDisk 的 Web 管理页面。\n\n" +
                        "该页面仅用于故障恢复与调试。确定继续吗？", "打开浏览器"))
                {
                    return;
                }

                try { Process.Start(new ProcessStartInfo(adminUrl) { UseShellExecute = true }); }
                catch (Exception ex) { NativeDialogs.Info("无法打开浏览器", ex.Message); }
                await Task.CompletedTask;
            }))));

        return Task.CompletedTask;
    }

    /// <summary>
    /// The effective-versus-persisted table.
    ///
    /// <para>The point of this table is that a settings page which shows only what was saved is a settings page
    /// that can lie: ports in particular are bound at start, so a saved change that has not been restarted is
    /// not in force. Each row says which of the two it is showing and whether a restart is what closes the
    /// gap.</para>
    /// </summary>
    private static IEnumerable<(string Name, string Effective, string Persisted, string RestartRequired, string Note)> RuntimeConfigRows(
        LanDiskRuntime runtime, ShellPreferences preferences)
    {
        AppSettings persisted = JsonStore.Read(runtime.Paths.SettingsFile, static () => new AppSettings());
        bool pending = persisted.HttpPort != runtime.Settings.HttpPort
                       || persisted.HttpsPort != runtime.Settings.HttpsPort
                       || persisted.LanEnabled != runtime.Settings.LanEnabled
                       || persisted.HttpsEnabled != runtime.Settings.HttpsEnabled;

        yield return ("HTTP 端口（管理端，仅本机）", runtime.Settings.HttpPort.ToString(), persisted.HttpPort.ToString(),
            persisted.HttpPort == runtime.Settings.HttpPort ? "否" : "是", "改动只有重启后才会绑定");
        yield return ("HTTPS 端口（手机访问）", runtime.Settings.HttpsPort.ToString(), persisted.HttpsPort.ToString(),
            persisted.HttpsPort == runtime.Settings.HttpsPort ? "否" : "是", "手机上的地址会随之改变");
        yield return ("局域网访问", runtime.Settings.LanEnabled ? "已开启" : "已关闭", persisted.LanEnabled ? "已开启" : "已关闭",
            persisted.LanEnabled == runtime.Settings.LanEnabled ? "否" : "是", "关闭后手机无法连接");
        yield return ("HTTPS", runtime.Settings.HttpsEnabled ? "已启用" : "未启用", persisted.HttpsEnabled ? "已启用" : "未启用",
            persisted.HttpsEnabled == runtime.Settings.HttpsEnabled ? "否" : "是", "配对与信任依赖 HTTPS");
        yield return ("稳定名称", runtime.Identity.HostName, runtime.Identity.HostName, "否", "由安装 id 派生，升级后保持不变");
        yield return ("mDNS 发现", runtime.Mdns is { IsRunning: true } ? "运行中" : "未运行", runtime.Mdns is { IsRunning: true } ? "运行中" : "未运行",
            "否", "5353/UDP 发布稳定名称；未运行不影响 IP 地址访问");
        yield return ("详细日志", runtime.Settings.VerboseLogging ? "已开启" : "已关闭", persisted.VerboseLogging ? "已开启" : "已关闭",
            "否", "写入 logs/landisk.log，不记录任何凭据");
        yield return ("审计保留条数", runtime.Settings.AuditRetentionEntries.ToString(), persisted.AuditRetentionEntries.ToString(),
            "否", "日志页读取的就是这份记录");
        yield return ("每设备并发上传", runtime.Settings.MaxConcurrentUploadsPerDevice.ToString(), persisted.MaxConcurrentUploadsPerDevice.ToString(),
            "否", "超出会在初始化上传时被拒绝");
        yield return ("列表分页大小", runtime.Settings.ListingPageSize.ToString(), persisted.ListingPageSize.ToString(),
            "否", "手机浏览目录时每页条目数");
        yield return ("最大分块字节", runtime.Settings.MaxChunkBytes.ToString(), persisted.MaxChunkBytes.ToString(),
            "否", "上传分块上限，两端都会读取");
        yield return ("Windows 主题", ThemeCatalog.Resolve(preferences.ThemeProfile, ThemeManager.IsDarkNow).Name,
            preferences.ThemeProfile, "否", "只影响本窗口，不影响手机");
        yield return ("iPhone 主题", runtime.MobileTheme.ProfileId, runtime.MobileTheme.ProfileId, "否", "手机在下次心跳时应用");
        yield return ("配置来源", runtime.Paths.SettingsFile, runtime.Paths.SettingsFile, "否", pending ? "存在未重启的改动" : "无未应用的改动");
    }

    /// <summary>
    /// The custom theme editor: accent, background and surface, everything else derived and validated.
    ///
    /// A combination that cannot be read is refused with the specific failing pair rather than saved and
    /// discovered on screen, which is what stops "custom theme" from meaning "white text on white".
    /// </summary>
    private static async Task EditCustomThemeAsync(LanDiskRuntime runtime, ShellPreferences preferences)
    {
        ThemeProfile? saved = await ShowThemeEditorAsync(
            "自定义 Windows 主题",
            "只需要三个颜色：强调色、背景色、卡片表面色；其余（悬停、按下、边框、文字）自动派生，保存前做对比度检查。",
            preferences.CustomTheme?.Roles.GetValueOrDefault("accent") ?? "#FF45C8FF",
            preferences.CustomTheme?.Roles.GetValueOrDefault("window") ?? "#FF0B0F14",
            preferences.CustomTheme?.Roles.GetValueOrDefault("surface") ?? "#FF111821");
        if (saved is null) return;

        preferences.ThemeProfile = "custom";
        preferences.CustomTheme = saved;
        preferences.Save(runtime.Paths.DataRoot);
        ThemeManager.ApplyCustom(saved);
        NativeDialogs.Info("已应用自定义主题", ThemeCatalog.Validate(saved).Summary);
        await Task.CompletedTask;
    }

    /// <summary>
    /// The Windows-side editor for a custom <i>phone</i> theme: the operator picks the same three colours and
    /// the server derives and validates the rest, then bumps the revision so the paired phones pick it up on
    /// their next heartbeat.
    /// </summary>
    private static async Task EditCustomMobileThemeAsync(LanDiskRuntime runtime)
    {
        ThemeProfile? currentCustom = runtime.MobileTheme.ChoicesModel().Custom;
        ThemeProfile? saved = await ShowThemeEditorAsync(
            "自定义 iPhone 主题",
            "手机的配色由这台电脑决定。选三个颜色，其余自动派生；保存后手机在下一次心跳时自动应用。",
            currentCustom?.Roles.GetValueOrDefault("accent") ?? "#FF45C8FF",
            currentCustom?.Roles.GetValueOrDefault("window") ?? "#FF0B0F14",
            currentCustom?.Roles.GetValueOrDefault("surface") ?? "#FF111821");
        if (saved is null) return;

        (bool ok, ThemeValidation validation, string message) = runtime.MobileTheme.Set(
            "custom", saved.Role("accent"), saved.Role("window"), saved.Role("surface"), saved.Name);
        if (!ok) NativeDialogs.Info("无法保存", message);
        else NativeDialogs.Info("已应用到手机", message + "\n" + validation.Summary + "（修订号 +1，手机心跳后自动刷新）");
        await Task.CompletedTask;
    }

    /// <summary>
    /// The shared editor behind both the Windows and the phone custom themes: three labelled fields plus a
    /// live preview card, and the contrast gate on save. Returns the derived profile, or null when cancelled.
    /// </summary>
    private static async Task<ThemeProfile?> ShowThemeEditorAsync(
        string title, string hint, string accentHex, string backgroundHex, string surfaceHex)
    {
        var window = new Window
        {
            Title = title,
            Width = 560,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            Background = Ui.Brush("Window")
        };

        ThemeField accent = Field("强调色", accentHex);
        ThemeField background = Field("背景色", backgroundHex);
        ThemeField surface = Field("卡片表面色", surfaceHex);

        var preview = new Border
        {
            Height = 150,
            Margin = new Thickness(0, Tokens.Space4, 0, 0),
            CornerRadius = new CornerRadius(Tokens.RadiusLarge),
            Background = Ui.Brush("Window"),
            Child = new TextBlock { Text = "预览", FontSize = Tokens.Caption, Foreground = Ui.Brush("TextTertiary"), Margin = new Thickness(Tokens.Space2) }
        };

        void RefreshPreview()
        {
            ThemeProfile candidate;
            try
            {
                candidate = ThemeCatalog.Custom(accent.Input.Text, background.Input.Text, surface.Input.Text, "预览");
            }
            catch
            {
                candidate = ThemeCatalog.Custom("#FF45C8FF", "#FF0B0F14", "#FF111821", "预览");
            }
            preview.Child = ThemePreviewCard(candidate);
        }

        accent.Input.TextChanged += (_, _) => RefreshPreview();
        background.Input.TextChanged += (_, _) => RefreshPreview();
        surface.Input.TextChanged += (_, _) => RefreshPreview();
        RefreshPreview();

        var stack = new StackPanel { Margin = new Thickness(Tokens.Space5) };
        stack.Children.Add(Ui.Body(hint));
        stack.Children.Add(accent.Root);
        stack.Children.Add(background.Root);
        stack.Children.Add(surface.Root);
        stack.Children.Add(preview);

        bool confirmed = false;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, Tokens.Space4, 0, 0) };
        buttons.Children.Add(Ui.Action("保存并应用", () => { confirmed = true; window.Close(); return Task.CompletedTask; }, primary: true));
        buttons.Children.Add(Ui.Action("取消", () => { window.Close(); return Task.CompletedTask; }));
        stack.Children.Add(buttons);

        window.Content = stack;
        window.ShowDialog();
        if (!confirmed) return null;

        ThemeProfile custom;
        try
        {
            custom = ThemeCatalog.Custom(accent.Input.Text, background.Input.Text, surface.Input.Text, "自定义");
        }
        catch (FormatException)
        {
            NativeDialogs.Info("颜色格式不正确", "颜色应为 #AARRGGBB 或 #RRGGBB 形式的十六进制。");
            return null;
        }

        ThemeValidation validation = ThemeCatalog.Validate(custom);
        if (!validation.Ok)
        {
            NativeDialogs.Info("这套配色不可读，未保存",
                validation.Summary + "\n\n请调整背景与文字/强调色的明暗关系后重试。");
            return null;
        }

        return custom;
    }

    /// <summary>A small card that previews the profile's background, surface, accent and both text colours.</summary>
    private static Border ThemePreviewCard(ThemeProfile profile)
    {
        string background = profile.Role("window");
        string surface = profile.Role("surface");
        string accent = profile.Role("accent");
        string primary = profile.Role("textPrimary");
        string secondary = profile.Role("textSecondary");

        TextBlock Text(string s, string hex, double size = Tokens.Body, bool bold = false)
            => new() { Text = s, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Solid(hex) };

        var card = new Border
        {
            Background = Solid(background),
            CornerRadius = new CornerRadius(Tokens.RadiusLarge),
            Padding = new Thickness(Tokens.Space3),
            BorderBrush = Solid(profile.Role("border")),
            BorderThickness = new Thickness(1)
        };

        var inner = new Border
        {
            Background = Solid(surface),
            CornerRadius = new CornerRadius(Tokens.RadiusMedium),
            Padding = new Thickness(Tokens.Space3)
        };

        var content = new StackPanel();
        content.Children.Add(Text("标题文字", primary, Tokens.Subtitle, bold: true));
        content.Children.Add(Text("次要说明文字", secondary));
        content.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, Tokens.Space2, 0, 0),
            Children =
            {
                new Border
                {
                    Background = Solid(accent), CornerRadius = new CornerRadius(Tokens.RadiusSmall),
                    Padding = new Thickness(Tokens.Space3, 3, Tokens.Space3, 3),
                    Child = Text("按钮", "#FF000000", Tokens.Caption, bold: true)
                },
                new Border
                {
                    Background = Solid(profile.Role("success")), CornerRadius = new CornerRadius(Tokens.RadiusSmall),
                    Padding = new Thickness(Tokens.Space2, 3, Tokens.Space2, 3), Margin = new Thickness(Tokens.Space2, 0, 0, 0),
                    Child = Text("正常", "#FF000000", Tokens.Caption)
                },
                new Border
                {
                    Background = Solid(profile.Role("warning")), CornerRadius = new CornerRadius(Tokens.RadiusSmall),
                    Padding = new Thickness(Tokens.Space2, 3, Tokens.Space2, 3), Margin = new Thickness(Tokens.Space2, 0, 0, 0),
                    Child = Text("注意", "#FF000000", Tokens.Caption)
                }
            }
        });
        inner.Child = content;
        card.Child = inner;
        return card;
    }

    private static SolidColorBrush Solid(string hex)
    {
        (byte r, byte g, byte b) = ThemePalette.Rgb(hex);
        return new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    private sealed record ThemeField(StackPanel Root, TextBox Input);

    private static ThemeField Field(string label, string value)
    {
        var panel = new StackPanel { Margin = new Thickness(0, Tokens.Space2, 0, 0) };
        // The label is part of the field and is actually returned: the old helper built the caption and the
        // box together but returned only the box, so the field labels were silently dropped and the editor
        // showed three unlabelled inputs.
        panel.Children.Add(Ui.Caption(label));
        var box = new TextBox
        {
            Text = value,
            FontFamily = new FontFamily(Tokens.MonoStack),
            Padding = new Thickness(6, 4, 6, 4),
            Background = Ui.Brush("Control"),
            Foreground = Ui.Brush("TextPrimary"),
            BorderBrush = Ui.Brush("Border")
        };
        panel.Children.Add(box);
        return new ThemeField(panel, box);
    }

    /// <summary>Switches the theme and remembers it for the next start.</summary>
    private static void ApplyTheme(ShellTheme theme)
    {
        ThemeManager.Apply(theme);
        ThemeManager.Save(App.ResolveDataRoot(Environment.GetCommandLineArgs()), theme);
    }
}
