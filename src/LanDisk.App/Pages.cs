using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LanDisk.Core.Model;
using LanDisk.Core.Networking;
using LanDisk.Core.Security;
using System.IO;
// WPF-UI ships controls with the same simple names as WPF itself (Button, TextBlock, ...). Importing
// the whole namespace would make every one of those ambiguous, so only the types actually used are
// brought in by name.
using InfoBar = Wpf.Ui.Controls.InfoBar;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;
using Card = Wpf.Ui.Controls.Card;

namespace LanDisk.App;

/// <summary>概览：服务的状态、手机该用的地址、以及需要用户注意的事情。</summary>
internal sealed class OverviewPage : NativePage
{
    private readonly StackPanel _body = new();

    public override UIElement Build()
    {
        Root.Children.Add(Ui.PageHeader("LanDisk",
            "把这台电脑上的共享目录提供给同一局域网中的 iPhone。"));
        Root.Children.Add(_body);
        return Root;
    }

    public override Task RefreshAsync()
    {
        _body.Children.Clear();

        OverviewModel model;
        try
        {
            model = Presenters.Overview(Runtime);
        }
        catch (Exception ex)
        {
            _body.Children.Add(Ui.Notice("无法读取运行状态：" + ex.Message, InfoBarSeverity.Error));
            return Task.CompletedTask;
        }

        OperationsSnapshot? snapshot;
        try
        {
            snapshot = Runtime.Metrics.Snapshot();
        }
        catch
        {
            snapshot = null;
        }

        // Headline first: one line that says whether anything is wrong, before any number.
        string headline = !model.LanEnabled
            ? "局域网访问尚未开启"
            : snapshot is null || snapshot.Warnings.Count == 0
                ? "运行正常"
                : "运行正常，" + snapshot.Warnings.Count + " 条需要注意的记录";
        string headlineTone = !model.LanEnabled ? "warning" : snapshot is null || snapshot.Warnings.Count == 0 ? "success" : "warning";

        var headlineRow = new WrapPanel { Margin = new Thickness(0, 0, 0, Tokens.Space4) };
        headlineRow.Children.Add(Ui.StatusBadge(headline, headlineTone));
        if (snapshot is not null)
        {
            headlineRow.Children.Add(Ui.StatusBadge(snapshot.Devices.Online + " 台在线设备", snapshot.Devices.Online > 0 ? "success" : "info"));
            headlineRow.Children.Add(Ui.StatusBadge("存储可用 " + OperationsPage.Bytes(snapshot.Storage.FreeBytes), "info"));
            headlineRow.Children.Add(Ui.StatusBadge("当前传输 " + snapshot.Transfers.ActiveCount, snapshot.Transfers.ActiveCount > 0 ? "accent" : "info"));
        }
        _body.Children.Add(headlineRow);

        // The six core cards, in the order a person checks them. Cards wrap into as many columns as the
        // available width allows (1–4), so a 683 DIP viewport still shows them one or two per row instead of
        // squeezing six into a grid that would force the numbers to wrap.
        var cards = new WrapPanel { Margin = new Thickness(0, 0, 0, Tokens.Space2) };

        AddMetric(cards, "在线设备",
            snapshot is null ? model.DeviceCount + " 台已配对" : snapshot.Devices.Online + " 在线",
            snapshot is null ? "—" : snapshot.Devices.RecentlyActive + " 最近活动 · " + snapshot.Devices.Total + " 已配对");
        AddMetric(cards, "共享", model.EnabledShareCount + " / " + model.ShareCount, "已启用的共享目录");
        AddMetric(cards, "存储空间",
            snapshot is null ? "—" : OperationsPage.Bytes(snapshot.Storage.FreeBytes) + " 可用",
            snapshot is null ? "—" : "容量 " + OperationsPage.Bytes(snapshot.Storage.TotalBytes));
        AddMetric(cards, "当前传输",
            snapshot is null ? "—" : snapshot.Transfers.ActiveCount + " 个进行中",
            snapshot is null ? "—" : "今日 ↑ " + OperationsPage.Bytes(snapshot.Transfers.TodayUploadBytes) + " · ↓ " + OperationsPage.Bytes(snapshot.Transfers.TodayDownloadBytes));
        AddMetric(cards, "网络",
            model.PhysicalAddresses.Count > 0 ? model.PhysicalAddresses[0] : "无可用地址",
            model.PhysicalAddresses.Count > 1
                ? "另有 " + (model.PhysicalAddresses.Count - 1) + " 个地址（见网络页）"
                : "物理以太网 / Wi-Fi");
        AddMetric(cards, "安全",
            model.HttpsEnabled ? "HTTPS 已开启" : "HTTPS 未开启",
            model.HttpsEnabled ? "证书由本机 CA 签发" : "手机侧无法安全配对");
        _body.Children.Add(cards);

        if (model.VpnDetected)
        {
            _body.Children.Add(Ui.Card("VPN / 隧道提示", null, Ui.Body(
                "检测到 VPN/TUN：" + model.VpnDetails + "。LanDisk 只使用物理 Ethernet/Wi-Fi，也不会修改任何 VPN 或路由设置。" +
                "若手机连不上，请在 VPN 客户端里开启“允许局域网 / 绕过本地网络”，或使用网络页里的物理地址。")));
        }

        if (!model.LanEnabled)
        {
            _body.Children.Add(Ui.Notice(
                "局域网访问尚未开启，手机现在无法连接。请在“网络”页启用 HTTPS 与局域网访问后重新启动服务。",
                InfoBarSeverity.Warning));
        }

        _body.Children.Add(Ui.Row(
            Ui.Action("连接 iPhone", async () => await App.Instance.Shell!.NavigateToAsync("连接 iPhone"), primary: true),
            Ui.Action("运营看板", async () => await App.Instance.Shell!.NavigateToAsync("运营看板")),
            Ui.Action("自检", async () => await App.Instance.Shell!.NavigateToAsync("自检")),
            Ui.Action("日志", async () => await App.Instance.Shell!.NavigateToAsync("日志"))));

        // Engineering detail (URLs, ports, data root, commit) belongs behind a disclosure, not at the top of
        // the page: it is what made the overview read like a diagnostic dump.
        _body.Children.Add(Ui.Card("连接与工程信息", "监听地址、端口、数据目录与构建信息",
            Ui.KeyValues(
                ("推荐访问地址", Runtime.Admin.RecommendedHttpsOrigin() ?? model.HttpsUrl),
                ("稳定名称", model.HostName + (Runtime.Mdns is { IsRunning: true } ? "（已发布）" : "（未发布）")),
                ("HTTPS", model.HttpsEnabled ? "已启用，端口 " + Runtime.Settings.HttpsPort : "未启用"),
                ("管理端", "http://127.0.0.1:" + Runtime.Settings.HttpPort + "/admin（高级恢复入口）"),
                ("物理局域网", model.PhysicalAddresses.Count > 0 ? string.Join("、", model.PhysicalAddresses) : "—"),
                ("下一步", model.IphoneStep),
                ("版本", model.Version + "（提交 " + (model.BuildCommit.Length > 8 ? model.BuildCommit[..8] : model.BuildCommit) + "）"),
                ("数据目录", model.DataRoot))));

        _body.Children.Add(Ui.Card("最近事件", "完整历史见日志页",
            AdaptiveTable.Build(
            [
                new AdaptiveTable.Column("时间", 130, 0, Mono: true),
                new AdaptiveTable.Column("类别", 100, 1),
                new AdaptiveTable.Column("操作", 150, 0),
                new AdaptiveTable.Column("摘要", 320, 0),
                new AdaptiveTable.Column("结果", 70, 0)
            ],
            [.. Presenters.Audit(Runtime, 12).Select(r => new AdaptiveTable.Row
            {
                Cells =
                [
                    AdaptiveTable.Cell.Of(r.时间),
                    AdaptiveTable.Cell.Of(r.类别),
                    AdaptiveTable.Cell.Of(r.操作),
                    AdaptiveTable.Cell.Of(r.详情)
                ],
                Extra = [("来源", r.来源), ("结果", r.结果), ("完整详情", r.详情)]
            })])));

        return Task.CompletedTask;
    }

    private static void AddMetric(Panel panel, string label, string value, string detail)
    {
        var stack = new StackPanel();
        stack.Children.Add(Ui.Caption(label));
        stack.Children.Add(Ui.Metric(value));
        stack.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = Tokens.Caption,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = Ui.Brush("TextSecondary")
        });

        var card = new Border
        {
            Background = Ui.Brush("Surface"),
            BorderBrush = Ui.Brush("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Tokens.RadiusLarge),
            Padding = new Thickness(Tokens.Space4, Tokens.Space3, Tokens.Space3, Tokens.Space3),
            Margin = new Thickness(0, 0, Tokens.Space3, Tokens.Space3),
            Width = 210,
            Child = stack
        };
        panel.Children.Add(card);
    }
}

/// <summary>共享：查看、添加、改权限、重新确认磁盘、停止共享。</summary>
internal sealed class SharesPage : NativePage
{
    private readonly StackPanel _body = new();

    public override UIElement Build()
    {
        Root.Children.Add(Ui.PageHeader(
            "文件与共享",
            "只有这里列出的目录才会被手机看到。停止共享只会删除记录，不会删除磁盘上的任何文件。",
            Ui.Action("添加共享…", AddShareAsync, primary: true),
            Ui.Action("刷新", RefreshAsync)));
        Root.Children.Add(_body);
        return Root;
    }

    private async Task AddShareAsync()
    {
        string? folder = NativeDialogs.PickFolder("选择要共享给 iPhone 的目录");
        if (folder is null) return;

        // A whole volume is supported but is worth a second look: it publishes everything on that disk.
        bool wholeVolume = string.Equals(Path.GetPathRoot(folder)?.TrimEnd('\\'), folder.TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
        if (wholeVolume && MessageBox.Show(
                "你选择的是整个磁盘（" + folder + "）。这会把该磁盘上的全部内容共享给已配对的设备。\n\n继续吗？",
                "共享整个磁盘", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        OperationOutcome<ShareDefinition> outcome = Runtime.Admin.CreateShare(
            folder, displayName: null, note: null, allowUpload: true, allowRename: true, allowDelete: true);

        if (!outcome.Ok) NativeDialogs.Info("无法添加共享", outcome.Message ?? "操作未完成。");
        await RefreshAsync();
    }

    public override Task RefreshAsync()
    {
        _body.Children.Clear();

        try
        {
            var shares = Runtime.Shares.All();

            if (shares.Count == 0)
            {
                _body.Children.Add(Ui.EmptyState("还没有共享任何目录",
                    "点「添加共享…」选择一个目录，它就会出现在手机的文件页里。"));
                return Task.CompletedTask;
            }

            List<AdaptiveTable.Row> rows = [];
            foreach (ShareDefinition share in shares)
            {
                ShareDefinition captured = share;
                (string state, string tone) = DescribeShareState(share);
                (string capacity, string free) = Capacity(share.RootPath);

                List<UIElement> actions =
                [
                    Ui.Action("打开", () =>
                    {
                        // Folder only: the helper refuses anything that looks like a URL, so this can never
                        // become a second way to reach a browser (there is exactly one, and it is labelled).
                        NativeDialogs.OpenFolder(captured.RootPath);
                        return Task.CompletedTask;
                    }),
                    Ui.Action(captured.Enabled ? "停止提供" : "重新提供", async () =>
                    {
                        Runtime.Admin.UpdateShare(captured.Id, enabled: !captured.Enabled);
                        await RefreshAsync();
                    }),
                    Ui.Action(captured.Capabilities.HasFlag(ShareCapabilities.Upload) ? "禁止上传" : "允许上传", async () =>
                    {
                        bool allow = !captured.Capabilities.HasFlag(ShareCapabilities.Upload);
                        Runtime.Admin.UpdateShare(captured.Id, allowUpload: allow);
                        await RefreshAsync();
                    }),
                    Ui.Action("允许重命名", async () =>
                    {
                        bool allow = !captured.Capabilities.HasFlag(ShareCapabilities.Rename);
                        Runtime.Admin.UpdateShare(captured.Id, allowRename: allow);
                        await RefreshAsync();
                    }),
                    Ui.Action("允许删除", async () =>
                    {
                        bool allow = !captured.Capabilities.HasFlag(ShareCapabilities.Delete);
                        Runtime.Admin.UpdateShare(captured.Id, allowDelete: allow);
                        await RefreshAsync();
                    }),
                    Ui.Action("重新确认磁盘", async () =>
                    {
                        OperationOutcome<ShareDefinition> outcome = Runtime.Admin.ReconfirmShare(captured.Id);
                        if (!outcome.Ok) NativeDialogs.Info("无法重新确认", outcome.Message ?? "操作未完成。");
                        await RefreshAsync();
                    }),
                    Ui.Action("停止共享", async () =>
                    {
                        if (!NativeDialogs.Confirm("停止共享",
                                "将停止共享“" + captured.DisplayName + "”。\n\n" +
                                "磁盘上的文件不会被删除，只是不再提供给手机。", "停止共享"))
                        {
                            return;
                        }

                        Runtime.Admin.DeleteShare(captured.Id);
                        await RefreshAsync();
                    })
                ];

                rows.Add(new AdaptiveTable.Row
                {
                    Cells =
                    [
                        new AdaptiveTable.Cell(share.DisplayName),
                        new AdaptiveTable.Cell(Shorten(share.RootPath), share.RootPath),
                        new AdaptiveTable.Cell(state),
                        new AdaptiveTable.Cell(Presenters.Capabilities(share.Capabilities)),
                        new AdaptiveTable.Cell(capacity),
                        new AdaptiveTable.Cell(free)
                    ],
                    Actions = actions,
                    // The volume identity is exactly the value that used to push every other column off the
                    // card, so it lives here: complete, selectable, and out of the table's way.
                    DetailContent = Ui.KeyValues(
                        ("卷身份", share.VolumeGuidPath),
                        ("完整路径", share.RootPath),
                        ("状态详情", string.IsNullOrWhiteSpace(share.StateDetail) ? "—" : share.StateDetail),
                        ("创建时的挂载点", share.MountPointAtCreation),
                        ("根对象 ID", share.RootObjectId),
                        ("创建时间", share.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
                        ("备注", string.IsNullOrWhiteSpace(share.Note) ? "—" : share.Note!)),
                    Extra =
                    [
                        ("共享 ID", share.Id),
                        ("启用", share.Enabled ? "是" : "否")
                    ]
                });
            }

            var legend = new WrapPanel { Margin = new Thickness(0, 0, 0, Tokens.Space2) };
            legend.Children.Add(Ui.StatusBadge("Online", "success"));
            legend.Children.Add(Ui.StatusBadge("Missing", "danger"));
            legend.Children.Add(Ui.StatusBadge("Offline", "warning"));
            legend.Children.Add(Ui.StatusBadge("Needs Reconfirm", "warning"));

            _body.Children.Add(Ui.Card("共享目录",
                "列宽会随窗口收缩：卷身份、根对象 ID 等长内容在「详情」里，永远不会把状态和操作挤出卡片",
                legend,
                AdaptiveTable.Build(
                [
                    new AdaptiveTable.Column("共享名称", 150, 0),
                    new AdaptiveTable.Column("路径", 220, 0, Mono: true),
                    new AdaptiveTable.Column("状态", 110, 0),
                    new AdaptiveTable.Column("权限", 150, 1),
                    new AdaptiveTable.Column("容量", 100, 1),
                    new AdaptiveTable.Column("可用", 100, 0)
                ], rows)));
        }
        catch (Exception ex)
        {
            _body.Children.Add(Ui.Notice("读取共享列表失败：" + ex.Message, InfoBarSeverity.Error));
        }

        return Task.CompletedTask;
    }

    /// <summary>The share state in the product's own words, with the tone the badge should use.</summary>
    private static (string State, string Tone) DescribeShareState(ShareDefinition share)
    {
        if (!share.Enabled) return ("已停止", "warning");

        // The four states the product shows, in the order they matter to a user: is the disk there, is it the
        // same disk, does the service still have access, is it being served at all.
        if (!Directory.Exists(share.RootPath)) return ("Missing", "danger");

        return share.State switch
        {
            ShareState.Online => ("Online", "success"),
            ShareState.IdentityMismatch => ("Needs Reconfirm", "danger"),
            ShareState.PermissionDenied => ("无权限", "danger"),
            ShareState.Offline => ("Offline", "warning"),
            _ => ("未知", "info")
        };
    }

    private static (string Capacity, string Free) Capacity(string rootPath)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(rootPath) ?? "C:\\");
            if (!drive.IsReady) return ("—", "磁盘未就绪");
            return (Presenters.FormatBytes(drive.TotalSize), Presenters.FormatBytes(drive.AvailableFreeSpace) + " 可用");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return ("—", "—");
        }
    }

    /// <summary>Keeps the head and tail of a long path: the middle is what a reader can infer.</summary>
    internal static string Shorten(string path, int head = 26, int tail = 26)
    {
        if (path.Length <= head + tail + 3) return path;
        return path[..head] + "…" + path[^tail..];
    }
}

/// <summary>设备：已配对终端的权限、最近访问、撤销与重命名。</summary>
internal sealed class DevicesPage : NativePage
{
    private readonly StackPanel _body = new();

    public override UIElement Build()
    {
        Root.Children.Add(Ui.PageHeader(
            "设备",
            "只有在这里出现过的设备才能访问共享。在线状态来自真实访问记录（15 秒心跳、45 秒内算在线），权限与能力修改下一次请求立即生效，不需要重新配对。",
            Ui.Action("刷新", RefreshAsync)));
        Root.Children.Add(_body);
        return Root;
    }

    public override Task RefreshAsync()
    {
        _body.Children.Clear();

        try
        {
            var devices = Runtime.Devices.All();
            DateTimeOffset now = DateTimeOffset.Now;

            // Devices waiting for the operator's confirmation are cards, not rows: they need a decision.
            foreach (PairingRequest pending in Runtime.Pairing.Active())
            {
                var card = new StackPanel();
                card.Children.Add(Ui.Body("待确认的配对请求：" + (pending.DeviceName ?? "未命名设备")));
                card.Children.Add(Ui.KeyValues(
                    ("剩余有效时间", Math.Max(0, (int)(pending.ExpiresAt - now).TotalSeconds) + " 秒"),
                    ("短码", pending.ShortCode),
                    ("来源", pending.RemoteAddress ?? "—")));
                card.Children.Add(Ui.Row(
                    Ui.Action("确认", async () => await ConfirmAsync(pending.Id), primary: true),
                    Ui.Action("拒绝", async () => await RejectAsync(pending.Id))));
                _body.Children.Add(Ui.Panel("待确认", card));
            }

            // Summary line first: the operator wants "is anyone connected" before reading rows.
            int online = devices.Count(d => DeviceActivity.Classify(d, now) == DevicePresence.Online);
            int recent = devices.Count(d => DeviceActivity.Classify(d, now) == DevicePresence.RecentlyActive);
            var badges = new WrapPanel { Margin = new Thickness(0, 0, 0, Tokens.Space3) };
            badges.Children.Add(Ui.StatusBadge(online + " 台在线", online > 0 ? "success" : "info"));
            badges.Children.Add(Ui.StatusBadge(recent + " 台最近活动", recent > 0 ? "accent" : "info"));
            badges.Children.Add(Ui.StatusBadge((devices.Count - online - recent) + " 台离线", "info"));
            _body.Children.Add(badges);

            if (devices.Count == 0)
            {
                _body.Children.Add(Ui.EmptyState("还没有配对过设备",
                    "打开「连接 iPhone」生成二维码，用手机扫码并在本机确认，设备就会出现在这里。"));
                return Task.CompletedTask;
            }

            List<AdaptiveTable.Row> rows = [];
            foreach (DeviceDefinition device in devices)
            {
                DeviceDefinition captured = device;
                DevicePermissions permissions = DevicePermissions.For(device);
                DevicePresence presence = DeviceActivity.Classify(device, now);

                List<UIElement> actions =
                [
                    Ui.Action(device.Level == DeviceAccessLevel.ReadOnly ? "改为读写" : "改为只读", async () =>
                    {
                        await RunAsync(() => Runtime.Admin.SetDeviceAccess(captured.Id,
                            captured.Level == DeviceAccessLevel.ReadOnly), "无法修改权限");
                        await RefreshAsync();
                    }),
                    Ui.Action("管理能力…", async () =>
                    {
                        await EditCapabilitiesAsync(captured);
                        await RefreshAsync();
                    }),
                    Ui.Action("重命名…", async () =>
                    {
                        string? name = Prompt("重命名设备", captured.Name);
                        if (string.IsNullOrWhiteSpace(name)) return;
                        await RunAsync(() => Runtime.Admin.RenameDevice(captured.Id, name), "无法重命名");
                        await RefreshAsync();
                    }),
                    captured.Revoked
                        ? Ui.Action("永久移除", async () =>
                        {
                            if (!NativeDialogs.Confirm("永久移除设备",
                                    "将从这台电脑上删除“" + captured.Name + "”的设备记录与授权，已撤销的历史不会影响其它设备；" +
                                    "审计记录会保留，方便以后查“这台手机是什么时候移除的”。\n\n手机需要重新配对才能再次访问。", "永久移除")) return;
                            await RunAsync(() => Runtime.Admin.RemoveDevice(captured.Id), "无法移除设备");
                            await RefreshAsync();
                        })
                        : Ui.Action("撤销", async () =>
                        {
                            if (!NativeDialogs.Confirm("撤销设备",
                                    "撤销后“" + captured.Name + "”需要重新配对才能访问，它当前的登录会立即失效，正在进行的上传会被取消，" +
                                    "它的扩展任务会被终止。", "撤销")) return;
                            // The whole revocation lives in AdminOperations: flag, timestamp, rotated token
                            // hash, rotated session epoch, cancelled uploads and the audit entry. The window
                            // used to set only the flag, so a cookie taken from the phone kept working.
                            await RunAsync(() => Runtime.Admin.RevokeDevice(captured.Id), "无法撤销设备");
                            await RefreshAsync();
                        }),
                    Ui.Action("永久移除…", async () =>
                    {
                        // Available for a live device too, and it is not a shortcut for "hide the row": the
                        // operation revokes first (token + session rotation, cancels uploads and extension
                        // tasks) and only then deletes the record.
                        if (!NativeDialogs.Confirm("永久移除设备",
                                "“" + captured.Name + "”" + (captured.Revoked ? "已被撤销，" : "当前仍然有效，会先被完整撤销，") +
                                "然后从这台电脑上删除它的设备记录与授权。审计记录保留，其它设备不受影响。", "永久移除")) return;
                        await RunAsync(() => Runtime.Admin.RemoveDevice(captured.Id), "无法移除设备");
                        await RefreshAsync();
                    })
                ];

                rows.Add(new AdaptiveTable.Row
                {
                    Cells =
                    [
                        new AdaptiveTable.Cell(device.Name),
                        new AdaptiveTable.Cell(device.Revoked ? "已撤销"
                            : presence switch
                            {
                                DevicePresence.Online => "在线",
                                DevicePresence.RecentlyActive => "最近活动",
                                _ => "离线"
                            }),
                        new AdaptiveTable.Cell(device.Level == DeviceAccessLevel.ReadWrite ? "读写" : "只读"),
                        new AdaptiveTable.Cell(DescribeCapabilities(device.Capabilities)),
                        new AdaptiveTable.Cell(Relative(now - device.LastSeenAt)),
                        new AdaptiveTable.Cell(device.LastSeenIp ?? "—", device.LastSeenIp),
                        new AdaptiveTable.Cell(device.UserAgent ?? "—", device.UserAgent)
                    ],
                    Actions = actions,
                    Extra =
                    [
                        ("设备 ID", device.Id),
                        ("配对时间", device.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
                        ("最近活动", device.LastSeenAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
                        ("最近地址", device.LastSeenIp ?? "—"),
                        ("设备信息", device.UserAgent ?? "—"),
                        ("文件写入", permissions.WriteFiles ? "允许" : "不允许（只读）"),
                        // 删除**不是**设备级能力：它由每个共享自己的「允许删除」控制。
                        // 这里明确写出来，避免把"读写设备"误读成"天然能删除"。
                        ("删除文件", "不随设备权限授予；由每个共享的「允许删除」单独控制"),
                        ("各共享实际能力", DescribeShareCapabilities(Runtime, device)),
                        ("扩展安装", permissions.InstallExtensions ? "允许" : "未授予"),
                        ("扩展执行", permissions.ExecuteExtensions ? "允许" : "未授予"),
                        ("撤销状态", device.Revoked ? "已于 " + device.RevokedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + " 撤销" : "有效")
                    ]
                });
            }

            var legend = new WrapPanel { Margin = new Thickness(0, 0, 0, Tokens.Space2) };
            legend.Children.Add(Ui.StatusBadge("在线", "success"));
            legend.Children.Add(Ui.StatusBadge("最近活动", "accent"));
            legend.Children.Add(Ui.StatusBadge("离线", "warning"));
            legend.Children.Add(Ui.StatusBadge("已撤销", "danger"));

            _body.Children.Add(Ui.Card("已配对设备", "状态永远是图标 + 文字，不只用颜色表示；长内容在「详情」里，不会被挤成竖排",
                legend,
                AdaptiveTable.Build(
                [
                    new AdaptiveTable.Column("名称", 150, 0),
                    new AdaptiveTable.Column("在线状态", 100, 0),
                    new AdaptiveTable.Column("权限", 70, 0),
                    new AdaptiveTable.Column("管理能力", 150, 1),
                    new AdaptiveTable.Column("最后活动", 120, 0),
                    new AdaptiveTable.Column("IP", 140, 1, Mono: true),
                    new AdaptiveTable.Column("设备信息", 160, 2)
                ], rows)));
        }
        catch (Exception ex)
        {
            _body.Children.Add(Ui.Notice("读取设备列表失败：" + ex.Message, InfoBarSeverity.Error));
        }

        return Task.CompletedTask;
    }

    /// <summary>Human wording for the capability set; "无" rather than an empty cell.</summary>
    internal static string DescribeCapabilities(DeviceCapability capabilities)
    {
        if (capabilities == DeviceCapability.None) return "无（仅文件权限）";
        List<string> names = [];
        if (capabilities.HasFlag(DeviceCapability.FileWrite)) names.Add("写入");
        if (capabilities.HasFlag(DeviceCapability.ExtensionInstall)) names.Add("装扩展");
        if (capabilities.HasFlag(DeviceCapability.ExtensionExecute)) names.Add("跑命令");
        if (capabilities.HasFlag(DeviceCapability.SystemView)) names.Add("看系统");
        return string.Join(" / ", names);
    }

    /// <summary>
    /// Per-share capabilities this device actually has right now, so the operator can see where delete
    /// comes from. "读写" is only a gate: what the device may do inside a share is that share's own
    /// capability set narrowed by the device level. Presenting the device level alone is what made
    /// "设备已授权" look like it should have produced a delete entry on the phone.
    /// </summary>
    internal static string DescribeShareCapabilities(LanDiskRuntime runtime, DeviceDefinition device)
    {
        var parts = new List<string>();
        foreach (ShareDefinition share in runtime.Shares.All().OrderBy(s => s.DisplayName, StringComparer.CurrentCulture))
        {
            ShareCapabilities effective = LanDiskRuntime.EffectiveCapabilities(share, device.Level);
            var names = new List<string>();
            if (effective.HasFlag(ShareCapabilities.Read)) names.Add("读取");
            if (effective.HasFlag(ShareCapabilities.Upload)) names.Add("上传");
            if (effective.HasFlag(ShareCapabilities.Rename)) names.Add("重命名");
            if (effective.HasFlag(ShareCapabilities.Delete)) names.Add("删除");
            if (!share.Enabled) names.Add("共享未启用");
            parts.Add(share.DisplayName + "：" + (names.Count == 0 ? "无" : string.Join("、", names)));
        }
        return parts.Count == 0 ? "尚未配置任何共享" : string.Join("；", parts);
    }

    /// <summary>A relative age, so "最后活动" is readable at a glance instead of a timestamp to decode.</summary>
    internal static string Relative(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age.TotalSeconds < 60) return $"{(int)age.TotalSeconds} 秒前";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes} 分钟前";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours} 小时前";
        return $"{(int)age.TotalDays} 天前";
    }

    /// <summary>
    /// Grants or removes the administrative capabilities of one device. Kept separate from the file-level
    /// read-only / read-write switch on purpose: "let this phone upload photos" and "let this phone run an
    /// approved command" are different decisions, and the dialog says so.
    /// </summary>
    private static async Task EditCapabilitiesAsync(DeviceDefinition device)
    {
        var window = new Window
        {
            Title = "管理能力 · " + device.Name,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            Background = Ui.Brush("Window")
        };

        var stack = new StackPanel { Margin = new Thickness(Tokens.Space5) };
        stack.Children.Add(new TextBlock
        {
            // 这里必须说清两件事，否则会误导：
            //   1) 「读写」只是**总闸门**，不放行任何具体动作；
            //   2) **删除权限不在设备上**，它由每个共享自己的「允许删除」控制。
            // 上一轮真机就是被这句话误导：Owner 把设备改成读写后，手机上仍然没有删除入口。
            Text = "文件权限（只读 / 读写）只是总闸门："
                 + "设备能在某个共享里做什么，由**该共享自身**授予的能力决定 —— "
                 + "读取 / 上传 / 重命名 / **删除** 都要在「文件与共享」页按共享逐个开启。"
                 + "把设备设为「读写」不会让它自动获得删除权限。下面这些能力是设备级的额外权限，默认全部关闭。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = Tokens.Body,
            Margin = new Thickness(0, 0, 0, Tokens.Space4),
            Foreground = Ui.Brush("TextSecondary")
        });

        var boxes = new List<(CheckBox Box, DeviceCapability Capability, string Label)>
        {
            (NewCheck("允许写入共享（即使设备仍是只读）", device.Capabilities.HasFlag(DeviceCapability.FileWrite)), DeviceCapability.FileWrite, "FileWrite"),
            (NewCheck("允许上传扩展程序（仍需本机批准）", device.Capabilities.HasFlag(DeviceCapability.ExtensionInstall)), DeviceCapability.ExtensionInstall, "ExtensionInstall"),
            (NewCheck("允许执行已批准的扩展命令", device.Capabilities.HasFlag(DeviceCapability.ExtensionExecute)), DeviceCapability.ExtensionExecute, "ExtensionExecute"),
            (NewCheck("允许查看系统运行信息", device.Capabilities.HasFlag(DeviceCapability.SystemView)), DeviceCapability.SystemView, "SystemView")
        };

        foreach ((CheckBox box, DeviceCapability _, string _) in boxes) stack.Children.Add(box);

        bool confirmed = false;
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, Tokens.Space5, 0, 0)
        };
        Button apply = Ui.Action("保存", () => { confirmed = true; window.Close(); return Task.CompletedTask; }, primary: true);
        buttons.Children.Add(apply);
        buttons.Children.Add(Ui.Action("取消", () => { window.Close(); return Task.CompletedTask; }));
        stack.Children.Add(buttons);

        window.Content = stack;
        window.ShowDialog();

        if (!confirmed) return;

        List<string> selected = [.. boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Label)];
        await RunAsync(() => Runtime.Admin.SetDeviceCapabilities(device.Id, selected), "无法修改管理能力");
    }

    private static CheckBox NewCheck(string text, bool isChecked) => new()
    {
        Content = text,
        IsChecked = isChecked,
        FontSize = Tokens.Body,
        Margin = new Thickness(0, 0, 0, Tokens.Space2),
        Foreground = Ui.Brush("TextPrimary")
    };

    private async Task ConfirmAsync(string id)
    {
        // Confirmation is a local decision: the operator in front of this window approves the request a
        // phone created. The decision and the device it produces live in AdminOperations, which is also
        // what POST /api/admin/pairing/{id}/confirm calls. This method used to call Pairing.Cancel — a
        // "confirm" button that cancelled the request and never created a device.
        await RunAsync(() => Runtime.Admin.ConfirmPairing(id), "无法确认配对");
        await RefreshAsync();
    }

    private async Task RejectAsync(string id)
    {
        await RunAsync(() => Runtime.Admin.RejectPairing(id), "无法拒绝配对");
        await RefreshAsync();
    }

    /// <summary>
    /// Runs a shared-layer operation and reports a refusal in place. The operation itself runs off the
    /// UI thread because it writes state files.
    /// </summary>
    internal static async Task RunAsync<T>(Func<OperationOutcome<T>> operation, string title)
    {
        OperationOutcome<T> outcome = await Task.Run(operation);
        if (!outcome.Ok) NativeDialogs.Info(title, outcome.Message ?? "操作未完成。");
    }

    private static string? Prompt(string title, string initial)
    {
        // A tiny purpose-built dialog: the alternative is a WinForms prompt, and the whole point of this
        // task is that the shell is WPF.
        var window = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = App.Instance.Shell,
            ResizeMode = ResizeMode.NoResize,
            Background = (Brush)Application.Current.Resources["LanDisk.Window"]
        };

        var box = new TextBox { Text = initial, FontSize = Ui.BodySize, Margin = new Thickness(0, 0, 0, 12) };
        var ok = new Button { Content = "确定", IsDefault = true, MinWidth = 88, Padding = new Thickness(0, 6, 0, 6) };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 88, Padding = new Thickness(0, 6, 0, 6), Margin = new Thickness(8, 0, 0, 0) };

        string? result = null;
        ok.Click += (_, _) => { result = box.Text; window.DialogResult = true; };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        window.Content = panel;

        return window.ShowDialog() == true ? result : null;
    }
}

/// <summary>连接 iPhone：把首次连接向导搬进原生窗口。</summary>
internal sealed class IphonePage : NativePage
{
    private readonly StackPanel _body = new();
    private readonly Image _bootstrapQr = new() { Width = 220, Height = 220, Stretch = Stretch.Uniform };
    private readonly Image _pairingQr = new() { Width = 220, Height = 220, Stretch = Stretch.Uniform };
    private System.Windows.Threading.DispatcherTimer? _watch;

    /// <summary>
    /// The pairing request this page's wizard produced. Completion is judged from *this* request, never
    /// from "the device list is non-empty": a machine that already has iPhone A must still show a real
    /// wizard while iPhone B is being added.
    /// </summary>
    private string? _pairingId;

    public override UIElement Build()
    {
        Root.Children.Add(Ui.Title("连接 iPhone"));
        Root.Children.Add(Ui.Body("电脑侧只需要做一次：让 iPhone 信任 LanDisk 的本地证书，然后配对。", secondary: true));
        Root.Children.Add(Ui.Row(Ui.Action("刷新", RefreshAsync)));
        Root.Children.Add(_body);
        return Root;
    }

    /// <summary>
    /// Watches while this page is on screen, and opens the wizard if it is not already open.
    ///
    /// <para>Opening this page *is* the user's request to connect a phone, and the certificate QR code
    /// only exists while the wizard runs — so starting it here is what makes "点击一次「连接 iPhone」"
    /// true. <see cref="AdminOperations.EnsureOnboardingStarted"/> never restarts a running wizard, so a
    /// page that is shown again (or a refresh) cannot change the bootstrap id the phone is already
    /// looking at.</para>
    /// </summary>
    public override void OnShown()
    {
        _watch ??= CreateWatch();
        _watch.Start();

        try
        {
            // If this page instance already finished its wizard, leave it alone: showing the completion
            // is the right answer, not silently opening a new session.
            if (Runtime.Admin.CompletionOf(_pairingId).Completed) return;
            Runtime.Admin.EnsureOnboardingStarted();
        }
        catch (Exception ex)
        {
            ShellStateLog.TryWriteLine(ResolveDataRoot(), "无法自动开启首次连接向导：" + ex.Message);
        }
    }

    private static string ResolveDataRoot() => App.ResolveDataRoot(Environment.GetCommandLineArgs());

    public override void OnHidden() => _watch?.Stop();

    private System.Windows.Threading.DispatcherTimer CreateWatch()
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.5)
        };
        timer.Tick += async (_, _) =>
        {
            // RefreshAsync rebuilds the body from the running state, so this is the same code path the
            // user's own "刷新" press takes — nothing is polled that the page would not show anyway.
            try { await RefreshAsync(); } catch { /* the page reports its own errors */ }
        };
        return timer;
    }

    public override async Task RefreshAsync()
    {
        _body.Children.Clear();

        OnboardingStatus status = Runtime.Onboarding.Status();

        // Completion first: if this wizard's request was confirmed, that is the answer, and the page must
        // not fall through to "start a new session" or to "a device exists somewhere, so we are done".
        AdminOperations.PairingCompletion completion = Runtime.Admin.CompletionOf(_pairingId);
        if (completion.Completed)
        {
            // The address to recommend comes from the pairing that just finished, not from the general
            // preference order: on a machine with a VPN/TUN up, the stable .local name is precisely the one
            // the phone could not reach, and telling the user to bookmark it would send them straight back
            // into the failure this wizard just worked around. The stable name is still shown, but as the
            // alternative it actually is.
            string recommended = completion.RecommendedOrigin
                                 ?? Runtime.Identity.HttpsOrigin(Runtime.Settings.HttpsPort);
            string stableOrigin = Runtime.Identity.HttpsOrigin(Runtime.Settings.HttpsPort);
            bool stableIsRecommended = string.Equals(recommended, stableOrigin, StringComparison.OrdinalIgnoreCase);

            var completionPanel = Ui.Panel("配对完成",
                Ui.Notice("配对完成，首次连接向导已自动关闭。", InfoBarSeverity.Success),
                Ui.KeyValues(
                    ("新设备", completion.DeviceName ?? "—"),
                    ("权限", completion.Level == nameof(DeviceAccessLevel.ReadWrite) ? "读写" : "只读"),
                    ("推荐访问地址", recommended),
                    ("稳定名称", stableIsRecommended ? "即推荐地址" : "备用：" + stableOrigin),
                    ("引导页", status.Active ? "仍在运行（可手动结束）" : "已自动关闭（8890 已释放）")),
                Ui.Body("手机以后直接访问上面的推荐地址即可，不需要再信任证书，也不需要重新配对。" +
                        (stableIsRecommended
                            ? ""
                            : "推荐地址是本机物理网卡地址：本次连接证明它可达，而稳定名称在当前网络（例如开着 VPN/TUN）下不可达。") +
                        "要再添加一台手机，重新打开本页即可。", secondary: true));
            _body.Children.Add(completionPanel);
            return;
        }

        _body.Children.Add(Ui.Body("第 1 步 · 让 iPhone 信任 LanDisk 的本地证书"));
        _body.Children.Add(Ui.Row(
            Ui.Action(status.Active ? "重新生成引导二维码" : "开始首次连接向导", async () =>
            {
                // A user asking for a new code means the old session is over: RestartOnboarding closes the
                // listener, cancels any unfinished pairing request and opens a fresh session with a new
                // bootstrap id. Calling Onboarding.Start() here would silently do nothing while a wizard is
                // already running, which is exactly the button-that-lies problem this replaces.
                OnboardingStatus started = Runtime.Admin.RestartOnboarding();
                if (started.LastError is not null) NativeDialogs.Info("无法开启向导", started.LastError);
                await RefreshAsync();
            }, primary: !status.Active),
            Ui.Action("结束向导", async () =>
            {
                Runtime.Onboarding.Stop("用户结束了首次连接向导");
                await RefreshAsync();
            })));

        if (status.Active)
        {
            string url = status.BootstrapUrl ?? "";
            _bootstrapQr.Source = NativeDialogs.Qr(url);
            _body.Children.Add(_bootstrapQr);
            _body.Children.Add(Ui.Mono(url));
            _body.Children.Add(Ui.Body("这个地址是明文 HTTP，只在向导开启期间、只在物理局域网地址上监听，" +
                                       "内容只有公开证书与安装说明，不含任何令牌。", secondary: true));
            _body.Children.Add(Ui.Body("iPhone 用相机扫描上面这个二维码 → 安装 LanDisk 安全证书 → " +
                                       "设置 → 通用 → 关于本机 → 证书信任设置 → 打开对 LanDisk Local CA 的完全信任。" +
                                       "苹果要求你本人确认这一步，LanDisk 不会也不能代做；" +
                                       "打开完全信任后引导页会自己检测到，不需要在手机上再点任何按钮。"));
        }
        else if (status.LastError is not null)
        {
            _body.Children.Add(Ui.Notice(status.LastError, InfoBarSeverity.Warning));
        }

        _body.Children.Add(Ui.Body("第 2 步 · 确认 HTTPS 已受信任"));
        _body.Children.Add(status.Trusted
            ? Ui.Notice("已检测到 iPhone 成功通过 HTTPS 访问，正在自动准备配对二维码。", InfoBarSeverity.Success)
            : Ui.Body("还在等待。手机上打开“完全信任”后，引导页会每 1–2 秒自动检测一次，这里不需要你点任何按钮。", secondary: true));

        _body.Children.Add(Ui.Body("第 3 步 · 扫描配对二维码"));

        // The address the phone actually reached us at, measured by its own HTTPS trust probe. The pairing
        // QR below is built from the same fact, so this line is what the user sees when the stable name is
        // not the address that worked — a VPN/TUN holding the default route is the usual reason. No token
        // is shown here; the raw link stays folded away below.
        string? verifiedHost = Runtime.Onboarding.VerifiedHttpsHost;
        if (!string.IsNullOrEmpty(verifiedHost))
        {
            _body.Children.Add(Ui.Body("已验证可达地址：" + verifiedHost, secondary: true));
        }

        if (!status.Trusted)
        {
            _body.Children.Add(Ui.Body("HTTPS 信任确认后，这里会自动出现配对二维码。一次性配对令牌只通过 HTTPS 传递。", secondary: true));
        }
        else
        {
            // Trusted: the request is created (or the live one reused) right here. "生成配对请求" used to
            // be a button the user had to find and press; refreshing must not pile up requests, which is
            // why AdminOperations.EnsurePairingRequest reuses an unexpired one.
            PairingRequest pending = Runtime.Admin.EnsurePairingRequest();
            _pairingId = pending.Id;
            string link = Runtime.Admin.PairingUrls(pending.Token).FirstOrDefault() ?? "";
            _pairingQr.Source = NativeDialogs.Qr(link);
            _body.Children.Add(_pairingQr);
            _body.Children.Add(Ui.KeyValues(
                ("短码", pending.ShortCode),
                ("设备", pending.DeviceName ?? "等待 iPhone 扫码"),
                ("剩余时间", Math.Max(0, (int)(pending.ExpiresAt - DateTimeOffset.Now).TotalSeconds) + " 秒")));
            _body.Children.Add(Ui.Row(
                Ui.Action("确认配对", async () => await ConfirmAsync(pending.Id), primary: true),
                Ui.Action("拒绝", async () => await RejectAsync(pending.Id))));

            // The raw link is deliberately folded away: it contains the one-time token.
            var details = new Expander { Header = "高级：显示原始配对链接", FontSize = Ui.SecondarySize };
            details.Content = new StackPanel
            {
                Children = { Ui.Mono(link), Ui.Body("该链接包含一次性配对令牌。只在排障时使用，不要截图或转发。", secondary: true) }
            };
            _body.Children.Add(details);
        }

        await Task.CompletedTask;
    }

    private async Task ConfirmAsync(string id)
    {
        // Same shared implementation as POST /api/admin/pairing/{id}/confirm and as the devices page.
        await DevicesPage.RunAsync(() => Runtime.Admin.ConfirmPairing(id), "无法确认配对");
        await RefreshAsync();
    }

    private async Task RejectAsync(string id)
    {
        await DevicesPage.RunAsync(() => Runtime.Admin.RejectPairing(id), "无法拒绝配对");
        await RefreshAsync();
    }
}

/// <summary>网络：物理网卡、被排除的网卡、虚拟网络、发现服务与监听地址，分块呈现。</summary>
internal sealed class NetworkPage : NativePage
{
    private readonly StackPanel _body = new();
    private bool _showExcluded;

    public override UIElement Build()
    {
        Root.Children.Add(Ui.PageHeader(
            "网络",
            "LanDisk 只使用物理 Ethernet/Wi-Fi。虚拟网卡、VPN 与隧道会被列出但永远不会用于手机访问；端口在服务启动时确定。",
            Ui.Action("刷新", RefreshAsync)));
        Root.Children.Add(_body);
        return Root;
    }

    /// <summary>
    /// Splits the adapter rows the network page draws into "usable for the phone" and "listed but excluded".
    ///
    /// <para><b>Why this is its own method.</b> The split used to be an inline
    /// <c>a.判定.StartsWith("使用")</c> — a decision taken from the <i>display string</i>. The presenter
    /// actually emits 「用于手机访问」, which begins with 「用」 and not 「使用」, so the accepted list was
    /// always empty: the page announced "没有可用物理网卡" while the very same page recommended
    /// <c>https://192.168.2.2:…</c>. Pulling it out makes the rule directly testable, and the test asserts
    /// the outcome is <i>unchanged when the label text is replaced wholesale</i> — the property the old code
    /// silently lacked.</para>
    /// </summary>
    internal static (List<AdapterRow> Accepted, List<AdapterRow> Excluded) SplitAdapters(IReadOnlyList<AdapterRow> adapters)
        => AdapterRows.Split(adapters);

    public override Task RefreshAsync()
    {
        _body.Children.Clear();

        LanDiskRuntime runtime = Runtime;
        bool vpn = runtime.VpnOrTunnelDetected(out string vpnDetails);
        List<AdapterRow> adapters = [.. Presenters.Adapters(runtime)];
        (List<AdapterRow> accepted, List<AdapterRow> excluded) = SplitAdapters(adapters);

        // 1) The state a user came for: is the phone able to reach this machine.
        var statusBadges = new WrapPanel { Margin = new Thickness(0, 0, 0, Tokens.Space2) };
        statusBadges.Children.Add(Ui.StatusBadge(runtime.Settings.LanEnabled ? "局域网访问已开启" : "局域网访问已关闭",
            runtime.Settings.LanEnabled ? "success" : "warning"));
        statusBadges.Children.Add(Ui.StatusBadge(runtime.Settings.HttpsEnabled ? "HTTPS 已启用" : "HTTPS 未启用",
            runtime.Settings.HttpsEnabled ? "success" : "danger"));
        statusBadges.Children.Add(Ui.StatusBadge(runtime.Mdns is { IsRunning: true } ? "发现服务运行中" : "发现服务未运行",
            runtime.Mdns is { IsRunning: true } ? "success" : "warning"));
        statusBadges.Children.Add(Ui.StatusBadge(vpn ? "存在 VPN/TUN" : "无 VPN/TUN", vpn ? "warning" : "success"));

        _body.Children.Add(Ui.Card("当前服务", null,
            statusBadges,
            Ui.KeyValues(
                ("推荐访问地址", RecommendedWithReason(runtime)),
                ("物理 HTTPS 地址", runtime.Admin.PhysicalHttpsOrigin() ?? "—"),
                ("稳定名称", runtime.Admin.StableHttpsOrigin() + (vpn ? "（VPN/TUN 可能阻断）" : "")),
                ("管理端（仅本机）", "http://127.0.0.1:" + runtime.Settings.HttpPort + "/admin（高级恢复入口，不会自动打开）"),
                ("监听地址", runtime.BoundUrls.Count == 0 ? "—" : string.Join("、", runtime.BoundUrls)))));

        // 2) Physical adapters: the ones that matter, as their own card with an adaptive table.
        _body.Children.Add(Ui.Card("物理网络",
            accepted.Count == 0 ? "没有可用于手机访问的物理网卡。" : "这些地址才是手机应当使用的地址",
            AdaptiveTable.Build(
            [
                new AdaptiveTable.Column("接口", 190, 0),
                new AdaptiveTable.Column("地址", 150, 0, Mono: true),
                new AdaptiveTable.Column("判定", 90, 0)
            ],
            [.. accepted.Select(a => new AdaptiveTable.Row
            {
                Cells =
                [
                    AdaptiveTable.Cell.Of(a.网卡),
                    AdaptiveTable.Cell.Of(a.地址),
                    AdaptiveTable.Cell.Of(a.判定)
                ],
                Extra = [("说明", a.说明)]
            })],
            Ui.EmptyState("没有可用物理网卡", "插上网线或连接 Wi-Fi 后点刷新；只有物理网卡地址会被发布给手机。"))));

        // 3) VPN/TUN, as a warning card rather than a banner over the whole page.
        if (vpn)
        {
            _body.Children.Add(Ui.Card("VPN / 隧道",
                "LanDisk 不会修改 VPN 或系统路由，只提示影响",
                Ui.Notice("检测到 VPN/TUN：" + vpnDetails +
                          "。手机可能无法通过稳定名称访问；请在 VPN 客户端中开启“允许局域网 / 绕过本地网络”，或直接使用上面的物理地址。",
                    InfoBarSeverity.Warning)));
        }

        // 4) Excluded adapters: collapsed by default, because they are the noise that made the old page
        // unreadable. The count is always visible so nothing is hidden silently.
        var excludedHeader = new StackPanel { Orientation = Orientation.Horizontal };
        excludedHeader.Children.Add(Ui.Action(
            _showExcluded ? "收起被排除的网卡" : $"查看被排除的网卡（{excluded.Count}）",
            () => { _showExcluded = !_showExcluded; return RefreshAsync(); }));

        var excludedContent = new StackPanel();
        if (_showExcluded)
        {
            excludedContent.Children.Add(AdaptiveTable.Build(
            [
                new AdaptiveTable.Column("接口", 200, 0),
                new AdaptiveTable.Column("地址", 150, 0, Mono: true),
                new AdaptiveTable.Column("判定", 110, 0)
            ],
            [.. excluded.Select(a => new AdaptiveTable.Row
            {
                Cells =
                [
                    AdaptiveTable.Cell.Of(a.网卡),
                    AdaptiveTable.Cell.Of(a.地址),
                    AdaptiveTable.Cell.Of(a.判定)
                ],
                Extra = [("说明", a.说明)]
            })]));
        }
        else
        {
            excludedContent.Children.Add(Ui.Body(
                "虚拟网卡、VPN 地址与自动专用地址（169.254.x）都会出现在这里；它们永远不会被发布给手机。", secondary: true));
        }

        _body.Children.Add(Ui.Card("虚拟 / 被排除的网络", null, excludedHeader, excludedContent));

        // 5) Advanced: the listener settings, with the restart requirement stated.
        _body.Children.Add(Ui.Card("高级",
            "端口在启动时确定，修改后需要重新启动服务才会生效",
            Ui.Row(
                Ui.Action(runtime.Settings.LanEnabled ? "关闭局域网访问" : "开启局域网访问", async () =>
                {
                    OperationOutcome<Core.Configuration.AppSettings> outcome = runtime.Admin.UpdateNetworkSettings(
                        lanEnabled: !runtime.Settings.LanEnabled,
                        httpsEnabled: true);
                    if (!outcome.Ok) NativeDialogs.Info("无法保存", outcome.Message ?? "操作未完成。");
                    await RefreshAsync();
                }),
                Ui.Action("重新启动服务", async () => await (App.Instance.Host?.RestartAsync() ?? Task.CompletedTask))),
            Ui.KeyValues(
                ("HTTP 端口（持久化值）", runtime.Settings.HttpPort.ToString()),
                ("HTTPS 端口（持久化值）", runtime.Settings.HttpsPort.ToString()),
                ("发现服务（mDNS 5353）", runtime.Mdns is { IsRunning: true } ? "运行中" : "未运行"),
                ("配置文件", Runtime.Paths.SettingsFile))));

        return Task.CompletedTask;
    }

    /// <summary>
    /// The recommended address with an honest qualifier: "本次已验证" when a probe actually measured it,
    /// "物理地址（VPN/TUN 下推荐）" when a VPN is up, "稳定名称" otherwise. An address is never labelled
    /// "已验证可达" unless the wizard really did verify it — a name chosen by default is not evidence.
    /// </summary>
    private static string RecommendedWithReason(LanDiskRuntime runtime)
    {
        string? origin = runtime.Admin.RecommendedHttpsOrigin();
        if (origin is null) return "—";
        return runtime.Admin.RecommendedHostKind switch
        {
            "verified" => origin + "（本次已验证）",
            "physical" => origin + "（物理地址" + (runtime.VpnOrTunnelDetected(out _) ? "，VPN/TUN 下推荐" : "") + "）",
            "stable" => origin + "（稳定名称）",
            _ => origin
        };
    }
}
