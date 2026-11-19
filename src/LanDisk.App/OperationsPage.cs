using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using LanDisk.Server.Services;

namespace LanDisk.App;

/// <summary>
/// 运营看板 — what the machine is doing right now.
///
/// <para><b>Not a debug page.</b> The self-check page answers "is anything broken" with a verdict per
/// subsystem; this one answers "what is going on" with numbers and a five-minute trace. They are separate
/// because mixing them produces exactly the page this replaces: a wall of diagnostics with no hierarchy,
/// where the one number that matters (is anyone uploading?) is the same size as the adapter list.</para>
///
/// <para><b>Refresh rate.</b> Two seconds while the page is visible, stopped the moment it is hidden. The
/// numbers are sampled on demand, so a dashboard nobody has open costs nothing at all.</para>
/// </summary>
internal sealed class OperationsPage : NativePage
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly StackPanel _tiles = new();
    private readonly StackPanel _details = new();
    private readonly Canvas _chart = new() { Height = 150, ClipToBounds = true };
    private readonly List<(double Up, double Down)> _history = [];

    public override UIElement Build()
    {
        Root.Children.Add(new TextBlock());
        Root.Children.Clear();

        var header = new StackPanel();
        var title = Ui.PageHeader("运营看板",
            "本机实时状态：负载、网络吞吐、存储、设备与正在进行的传输。每 2 秒采样一次，且只在本页打开时采样。");
        header.Children.Add(title);
        _summary = new TextBlock
        {
            FontSize = Tokens.Body,
            Margin = new Thickness(0, 0, 0, Tokens.Space4),
            Foreground = Ui.Brush("TextSecondary")
        };
        header.Children.Add(_summary);
        Root.Children.Add(header);

        Root.Children.Add(_tiles);
        Root.Children.Add(Ui.Card("最近 5 分钟网络速率", "每秒字节数；青色为上行，蓝色为下行，每 2 秒一个采样点", _chart));
        Root.Children.Add(_details);

        _timer.Tick += async (_, _) => await RefreshAsync();
        return Root;
    }

    private TextBlock _summary = new();

    public override void OnShown() => _timer.Start();

    public override void OnHidden() => _timer.Stop();

    public override Task RefreshAsync()
    {
        try
        {
            Render(Runtime.Metrics.Snapshot());
        }
        catch (Exception ex)
        {
            if (_tiles.Children.Count == 0)
            {
                _tiles.Children.Add(Ui.Notice("无法读取运行数据：" + ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error));
            }
        }

        return Task.CompletedTask;
    }

    private void Render(OperationsSnapshot snapshot)
    {
        _tiles.Children.Clear();
        _details.Children.Clear();
        _summary.Text = snapshot.Summary;

        // Row 1: the four numbers an operator looks at first.
        var row1 = NewRow();
        Add(row1, 0, Tile("LanDisk 版本", "v" + snapshot.System.Version,
            "已运行 " + Duration(snapshot.System.UptimeSeconds) + " · " + snapshot.System.MachineName));
        Add(row1, 1, Tile("在线设备", snapshot.Devices.Online + " 在线",
            snapshot.Devices.RecentlyActive + " 最近活动 · " + snapshot.Devices.Offline + " 离线 · " + snapshot.Devices.Total + " 已配对"));
        Add(row1, 2, Tile("存储可用", OperationsPage.Bytes(snapshot.Storage.FreeBytes),
            snapshot.Storage.EnabledShareCount + "/" + snapshot.Storage.ShareCount + " 个共享启用 · 容量 " + OperationsPage.Bytes(snapshot.Storage.TotalBytes)));
        Add(row1, 3, Tile("当前传输", snapshot.Transfers.ActiveCount + " 个进行中",
            "↑ " + snapshot.Transfers.ActiveUploads + " · ↓ " + snapshot.Transfers.ActiveDownloads));
        _tiles.Children.Add(row1);

        // Row 2: load, live rates, today's totals.
        var row2 = NewRow();
        Add(row2, 0, Tile("LanDisk 进程 CPU", snapshot.System.ProcessCpuPercent.ToString("0.0", CultureInfo.InvariantCulture) + " %",
            "内存 " + OperationsPage.Bytes(snapshot.System.WorkingSetBytes) + " · " + snapshot.System.ProcessorCount + " 核"));
        Add(row2, 1, Tile("整机 CPU", snapshot.System.SystemCpuPercent.ToString("0.0", CultureInfo.InvariantCulture) + " %",
            "系统总占用（kernel + user）"));
        Add(row2, 2, Tile("实时速率", "↑ " + OperationsPage.Bytes((long)snapshot.Network.UploadBytesPerSecond) + "/s",
            "↓ " + OperationsPage.Bytes((long)snapshot.Network.DownloadBytesPerSecond) + "/s"));
        Add(row2, 3, Tile("今日流量", "↑ " + OperationsPage.Bytes(snapshot.Transfers.TodayUploadBytes),
            "↓ " + OperationsPage.Bytes(snapshot.Transfers.TodayDownloadBytes) + " · 请求 " + snapshot.Requests.Total + " / 失败 " + snapshot.Requests.Failures));
        _tiles.Children.Add(row2);

        // Row 3: the facts that are a badge rather than a number.
        var badges = new WrapPanel { Margin = new Thickness(0, 0, 0, Tokens.Space4) };
        badges.Children.Add(Ui.StatusBadge(snapshot.Network.HttpsEnabled ? "HTTPS 已启用" : "HTTPS 未启用",
            snapshot.Network.HttpsEnabled ? "success" : "warning"));
        badges.Children.Add(Ui.StatusBadge(snapshot.Network.MdnsRunning ? "发现服务运行中" : "发现服务未运行",
            snapshot.Network.MdnsRunning ? "success" : "warning"));
        badges.Children.Add(Ui.StatusBadge(snapshot.Network.VpnOrTunnel ? "存在 VPN/TUN" : "无 VPN/TUN",
            snapshot.Network.VpnOrTunnel ? "warning" : "success"));
        badges.Children.Add(Ui.StatusBadge("本机 " + snapshot.Network.StableName, "accent"));
        badges.Children.Add(Ui.StatusBadge(
            "推荐地址 " + (string.IsNullOrWhiteSpace(snapshot.Network.RecommendedOrigin) ? "—" : snapshot.Network.RecommendedOrigin)
            + (snapshot.Network.RecommendedKind == "verified" ? "（已验证）" : snapshot.Network.RecommendedKind == "physical" ? "（物理）" : ""),
            "info"));
        _details.Children.Add(new Border
        {
            Background = Ui.Brush("Surface"),
            BorderBrush = Ui.Brush("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Tokens.RadiusLarge),
            Padding = new Thickness(Tokens.Space4, Tokens.Space3, Tokens.Space4, Tokens.Space3),
            Margin = new Thickness(0, 0, 0, Tokens.Space4),
            Child = badges
        });

        if (snapshot.Network.VpnOrTunnel && !string.IsNullOrWhiteSpace(snapshot.Network.VpnDetails))
        {
            _details.Children.Add(Ui.Notice(
                "检测到 VPN/TUN：" + snapshot.Network.VpnDetails +
                "。开启时手机可能无法通过稳定名称访问，请以推荐地址为准。",
                Wpf.Ui.Controls.InfoBarSeverity.Warning));
        }

        // Active transfers, as a table that can drop columns rather than deform.
        if (snapshot.Transfers.Active.Count > 0)
        {
            _details.Children.Add(Ui.Card("正在进行的传输", null,
                AdaptiveTable.Build(
                [
                    new AdaptiveTable.Column("共享", 130, 0),
                    new AdaptiveTable.Column("文件", 240, 0),
                    new AdaptiveTable.Column("设备", 130, 1),
                    new AdaptiveTable.Column("进度", 170, 0),
                    new AdaptiveTable.Column("方向", 70, 2)
                ],
                [.. snapshot.Transfers.Active.Select(t => new AdaptiveTable.Row
                {
                    Cells =
                    [
                        AdaptiveTable.Cell.Of(t.Share),
                        AdaptiveTable.Cell.Of(t.Path),
                        AdaptiveTable.Cell.Of(t.Device),
                        AdaptiveTable.Cell.Of(Bytes(t.Transferred) + " / " + Bytes(t.Total)),
                        AdaptiveTable.Cell.Of(t.Direction == "Upload" ? "上传" : "下载")
                    ],
                    Extra = [("开始时间", t.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")), ("传输 ID", t.Id)]
                })])));
        }

        // Warnings and errors, newest last, with the redacted detail available in full per row.
        _details.Children.Add(Ui.Card("最近的警告与错误",
            snapshot.Warnings.Count == 0 ? "没有失败的审计条目。" : snapshot.Warnings.Count + " 条最近的失败记录（完整历史见日志页）",
            snapshot.Warnings.Count == 0
                ? Ui.Body("当前没有需要注意的问题。", secondary: true)
                : AdaptiveTable.Build(
                [
                    new AdaptiveTable.Column("时间", 150, 0, Mono: true),
                    new AdaptiveTable.Column("类别", 90, 1),
                    new AdaptiveTable.Column("操作", 150, 0),
                    new AdaptiveTable.Column("摘要", 320, 0)
                ],
                [.. snapshot.Warnings.Select(w => new AdaptiveTable.Row
                {
                    Cells =
                    [
                        AdaptiveTable.Cell.Of(w.At.ToLocalTime().ToString("MM-dd HH:mm:ss")),
                        AdaptiveTable.Cell.Of(w.Category),
                        AdaptiveTable.Cell.Of(w.Action),
                        AdaptiveTable.Cell.Of(w.Detail ?? "—")
                    ],
                    Extra = [("完整摘要", w.Detail ?? "—"), ("成功", w.Success ? "是" : "否")]
                })])));

        AppendPoint(snapshot.Network.UploadBytesPerSecond, snapshot.Network.DownloadBytesPerSecond);
        DrawChart();
    }

    private void AppendPoint(double up, double down)
    {
        _history.Add((up, down));
        if (_history.Count > 150) _history.RemoveAt(0);   // five minutes at two seconds
    }

    private void DrawChart()
    {
        _chart.Children.Clear();
        double width = _chart.ActualWidth > 0 ? _chart.ActualWidth : 820;
        double height = _chart.Height;

        // The chart is a plain polyline of the samples this page has actually seen: no smoothing, no
        // interpolation, and a flat line really does mean nothing moved.
        double max = Math.Max(2048, _history.Count == 0 ? 0 : _history.Max(p => Math.Max(p.Up, p.Down)));

        _chart.Children.Add(new Line
        {
            X1 = 0,
            X2 = width,
            Y1 = height / 2,
            Y2 = height / 2,
            Stroke = Ui.Brush("Border"),
            StrokeThickness = 1,
            StrokeDashArray = [3, 4]
        });

        _chart.Children.Add(BuildLine(_history.Select(p => p.Down), max, width, height, Ui.Brush("AccentBlue")));
        _chart.Children.Add(BuildLine(_history.Select(p => p.Up), max, width, height, Ui.Brush("AccentCyan")));

        _chart.Children.Add(new TextBlock
        {
            Text = "峰值 " + max.ToString("N0", CultureInfo.InvariantCulture) + " B/s · 采样 " + _history.Count + " 点",
            FontSize = Tokens.Caption,
            Margin = new Thickness(Tokens.Space2, 2, 0, 0),
            Foreground = Ui.Brush("TextTertiary")
        });
    }

    private static Polyline BuildLine(IEnumerable<double> values, double max, double width, double height, Brush stroke)
    {
        var line = new Polyline { Stroke = stroke, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
        List<double> samples = [.. values];
        for (int i = 0; i < samples.Count; i++)
        {
            double x = samples.Count <= 1 ? 0 : width * i / (samples.Count - 1);
            double y = height - Math.Clamp(samples[i] / max, 0, 1) * (height - 14) - 7;
            line.Points.Add(new Point(x, y));
        }
        return line;
    }

    private static WrapPanel NewRow()
    {
        // Cards flow into as many columns as fit instead of being pinned to four: on a 1366×768 screen at
        // 200% (683 DIP) four fixed columns would each be ~150 DIP wide and the numbers would wrap. A
        // wrap panel with a floor of 210 DIP per card gives 1, 2, 3 or 4 columns by actual width.
        return new WrapPanel { Margin = new Thickness(0, 0, 0, Tokens.Space3) };
    }

    private static Border Tile(string label, string value, string detail)
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

        return new Border
        {
            Background = Ui.Brush("Surface"),
            BorderBrush = Ui.Brush("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Tokens.RadiusLarge),
            Padding = new Thickness(Tokens.Space4, Tokens.Space3, Tokens.Space3, Tokens.Space3),
            Margin = new Thickness(0, 0, Tokens.Space3, Tokens.Space3),
            Width = 210,
            Child = stack,
            ToolTip = detail
        };
    }

    private static void Add(Panel panel, int column, UIElement element)
    {
        panel.Children.Add(element);
    }

    internal static string Duration(double seconds)
    {
        TimeSpan span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays} 天 {span.Hours} 小时";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} 小时 {span.Minutes} 分";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes} 分 {span.Seconds} 秒";
        return $"{(int)span.TotalSeconds} 秒";
    }

    internal static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString(unit == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
