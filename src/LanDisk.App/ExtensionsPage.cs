using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LanDisk.Core.Extensions;
using LanDisk.Server.Services;

namespace LanDisk.App;

/// <summary>
/// 扩展程序：已安装 / 待批准 / 已禁用。
///
/// <para>上传只是把包放进隔离区，它永远不会自动运行。首次安装必须由本机操作员在这里明确批准，批准会固定
/// 当前包的 SHA256；包内容一旦变化就回到待批准。第一版的入口只能是包内的 Windows PE（.exe），没有
/// interpreter、没有任意命令行。</para>
/// </summary>
internal sealed class ExtensionsPage : NativePage
{
    private readonly StackPanel _body = new();
    private int _tab;

    public override UIElement Build()
    {
        Root.Children.Add(Ui.PageHeader(
            "扩展程序",
            "扩展由手机的扩展安装能力上传，但必须在这台电脑上批准后才能运行；运行发生在独立的受限进程里，主程序从不加载扩展代码。",
            Ui.Action("刷新", RefreshAsync)));
        Root.Children.Add(_body);
        return Root;
    }

    public override Task RefreshAsync()
    {
        _body.Children.Clear();
        LanDiskRuntime runtime = Runtime;
        IReadOnlyList<ExtensionPackageRecord> packages = runtime.Extensions.Packages();

        var pending = packages.Where(p => p.State == ExtensionState.PendingApproval).ToList();
        var approved = packages.Where(p => p.State == ExtensionState.Approved).ToList();
        var disabled = packages.Where(p => p.State == ExtensionState.Disabled).ToList();

        var badges = new WrapPanel { Margin = new Thickness(0, 0, 0, Tokens.Space3) };
        badges.Children.Add(Ui.StatusBadge(pending.Count + " 待批准", pending.Count > 0 ? "warning" : "success"));
        badges.Children.Add(Ui.StatusBadge(approved.Count + " 已安装", "info"));
        badges.Children.Add(Ui.StatusBadge(disabled.Count + " 已禁用", "info"));
        _body.Children.Add(badges);

        var tabs = new WrapPanel { Margin = new Thickness(0, 0, 0, Tokens.Space3) };
        // The active tab used to be marked with a "•" prefix, which *changed the button's width*: switching
        // tabs made the whole row jump sideways. Selection is shown by the primary style instead, so the
        // label never moves.
        tabs.Children.Add(Ui.Action("已安装", () => { _tab = 0; return RefreshAsync(); }, primary: _tab == 0));
        tabs.Children.Add(Ui.Action("待批准（" + pending.Count + "）", () => { _tab = 1; return RefreshAsync(); }, primary: _tab == 1));
        tabs.Children.Add(Ui.Action("已禁用", () => { _tab = 2; return RefreshAsync(); }, primary: _tab == 2));
        tabs.Children.Add(Ui.Action("安装扩展包…", async () =>
        {
            string? file = NativeDialogs.PickUpdatePackage();
            if (file is null) return;
            if (!file.EndsWith(".ldx", StringComparison.OrdinalIgnoreCase))
            {
                NativeDialogs.Info("不是扩展包", "请选择 .ldx 文件。");
                return;
            }

            OperationOutcome<ExtensionPackageRecord> uploaded = runtime.Extensions.Upload(File.ReadAllBytes(file), Path.GetFileName(file));
            if (!uploaded.Ok) NativeDialogs.Info("扩展包被拒绝", uploaded.Message ?? "无法安装。");
            else NativeDialogs.Info("已加入待批准", "「" + uploaded.Value!.Name + "」已隔离。请在「待批准」中检查后再批准，批准会固定当前包的 SHA256。");
            await RefreshAsync();
        }));
        _body.Children.Add(tabs);

        List<ExtensionPackageRecord> shown = _tab switch
        {
            1 => pending,
            2 => disabled,
            _ => approved
        };

        if (shown.Count == 0)
        {
            _body.Children.Add(Ui.EmptyState(
                _tab == 1 ? "没有待批准的扩展" : _tab == 2 ? "没有被禁用的扩展" : "还没有已安装的扩展",
                _tab == 1
                    ? "手机在获得「扩展安装」能力后可以上传 .ldx 包，上传后会出现在这里等待本机批准。"
                    : "扩展必须在本机批准后才能运行；运行发生在独立的受限进程里。"));
            return Task.CompletedTask;
        }

        foreach (ExtensionPackageRecord package in shown)
        {
            ExtensionPackageRecord captured = package;
            var content = new StackPanel();
            content.Children.Add(Ui.KeyValues(
                ("名称", package.Name),
                ("版本", package.Version),
                ("发布者", package.Publisher),
                ("包 SHA256", package.PackageSha256),
                ("大小", package.PackageBytes + " 字节"),
                ("入口文件", Path.GetFileName(package.Entrypoint)),
                ("超时", package.TimeoutSeconds + " 秒"),
                ("状态", Describe(package.State) + (package.ApprovedAt is { } at ? "（" + at.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + "）" : ""))));

            if (package.Commands.Count > 0)
            {
                content.Children.Add(Ui.Caption("声明的命令"));
                content.Children.Add(AdaptiveTable.Build(
                [
                    new AdaptiveTable.Column("命令 ID", 160, 0, Mono: true),
                    new AdaptiveTable.Column("名称", 160, 0),
                    new AdaptiveTable.Column("说明", 260, 1),
                    new AdaptiveTable.Column("参数 schema", 200, 1)
                ],
                [.. package.Commands.Select(c => new AdaptiveTable.Row
                {
                    Cells =
                    [
                        AdaptiveTable.Cell.Of(c.Id),
                        AdaptiveTable.Cell.Of(c.Name),
                        AdaptiveTable.Cell.Of(c.Description),
                        AdaptiveTable.Cell.Of(c.ArgumentsSchema is null ? "（无参数）" : c.ArgumentsSchema.ToJsonString())
                    ],
                    Extra = [("完整 schema", c.ArgumentsSchema?.ToJsonString() ?? "（无参数）")]
                })]));
            }

            var actions = new List<UIElement>();
            if (package.State == ExtensionState.PendingApproval)
            {
                actions.Add(Ui.Action("批准此包", async () =>
                {
                    if (!NativeDialogs.Confirm("批准扩展",
                            "批准「" + captured.Name + "」后，拥有扩展执行能力的手机就可以运行它声明的命令。\n\n" +
                            "批准的凭据是当前包的 SHA256：\n" + captured.PackageSha256 + "\n\n包内容一旦变化需要重新批准。", "批准"))
                    {
                        return;
                    }

                    OperationOutcome<ExtensionPackageRecord> approvedRecord = runtime.Extensions.Approve(captured.Id, captured.PackageSha256);
                    if (!approvedRecord.Ok) NativeDialogs.Info("无法批准", approvedRecord.Message ?? "操作未完成。");
                    await RefreshAsync();
                }, primary: true));
                actions.Add(Ui.Action("拒绝并删除", async () =>
                {
                    if (!NativeDialogs.Confirm("删除扩展包", "将删除隔离区里的「" + captured.Name + "」，之后需要重新上传。", "删除")) return;
                    runtime.Extensions.SetEnabled(captured.Id, false);
                    await RefreshAsync();
                }));
            }
            else if (package.State == ExtensionState.Approved)
            {
                actions.Add(Ui.Action("禁用", async () =>
                {
                    runtime.Extensions.SetEnabled(captured.Id, false);
                    await RefreshAsync();
                }));
            }
            else
            {
                actions.Add(Ui.Action("重新启用", async () =>
                {
                    OperationOutcome<ExtensionPackageRecord> enabled = runtime.Extensions.SetEnabled(captured.Id, true);
                    if (!enabled.Ok) NativeDialogs.Info("无法启用", enabled.Message ?? "操作未完成。");
                    await RefreshAsync();
                }));
            }

            if (actions.Count > 0) content.Children.Add(Ui.Row([.. actions]));
            _body.Children.Add(Ui.Card(package.Name, package.Id, content));
        }

        _body.Children.Add(Ui.Body(
            "扩展运行在独立的 LanDisk.ExtensionHost.exe 中：受限令牌（禁用可去除的特权与 Administrators 组）、" +
            "Low Integrity、独立工作目录、Job Object 限制（进程数 / 内存 / 时间），超时或取消会终止整个进程树。" +
            "无法建立受限令牌时宿主会拒绝运行，而不会退化为高权限执行。", secondary: true));

        return Task.CompletedTask;
    }

    private static string Describe(ExtensionState state) => state switch
    {
        ExtensionState.Approved => "已批准",
        ExtensionState.Disabled => "已禁用",
        _ => "待批准（隔离中）"
    };
}

/// <summary>
/// 任务与命令：扩展命令的执行记录与日志。
///
/// <para>状态为 Queued / Running / Succeeded / Failed / Cancelled / TimedOut；运行中的任务可以取消，
/// 取消会终止宿主并连带结束扩展进程树。stdout / stderr 有长度上限，并且显示前会做脱敏。</para>
/// </summary>
internal sealed class TasksPage : NativePage
{
    private readonly StackPanel _body = new();
    private string? _selectedId;
    private readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    public override UIElement Build()
    {
        Root.Children.Add(Ui.PageHeader(
            "任务与命令",
            "扩展命令的执行记录：来源设备、扩展、命令、状态、时长、退出码与日志。运行中的任务可以取消。",
            Ui.Action("刷新", RefreshAsync)));
        Root.Children.Add(_body);
        _timer.Tick += async (_, _) => await RefreshAsync();
        return Root;
    }

    public override void OnShown() => _timer.Start();

    public override void OnHidden() => _timer.Stop();

    public override Task RefreshAsync()
    {
        _body.Children.Clear();
        IReadOnlyList<ExtensionTaskRecord> tasks = Runtime.Extensions.Tasks();

        int running = tasks.Count(t => t.State is ExtensionTaskState.Running or ExtensionTaskState.Queued);
        var badges = new WrapPanel { Margin = new Thickness(0, 0, 0, Tokens.Space3) };
        badges.Children.Add(Ui.StatusBadge(tasks.Count + " 条记录", "info"));
        badges.Children.Add(Ui.StatusBadge(running + " 个进行中", running > 0 ? "accent" : "info"));
        badges.Children.Add(Ui.StatusBadge(tasks.Count(t => t.State == ExtensionTaskState.Failed) + " 个失败",
            tasks.Any(t => t.State == ExtensionTaskState.Failed) ? "warning" : "success"));
        _body.Children.Add(badges);

        if (tasks.Count == 0)
        {
            _body.Children.Add(Ui.EmptyState("还没有执行过扩展命令",
                "手机在获得「扩展执行」能力后可以运行已批准扩展声明的命令，执行记录会出现在这里。"));
            return Task.CompletedTask;
        }

        var rows = tasks.Select(task => new AdaptiveTable.Row
        {
            Cells =
            [
                AdaptiveTable.Cell.Of(task.Id[..8]),
                AdaptiveTable.Cell.Of(task.SourceDeviceName),
                AdaptiveTable.Cell.Of(task.ExtensionName),
                AdaptiveTable.Cell.Of(task.CommandId),
                AdaptiveTable.Cell.Of(Describe(task.State)),
                AdaptiveTable.Cell.Of(task.StartedAt.ToLocalTime().ToString("MM-dd HH:mm:ss")),
                AdaptiveTable.Cell.Of(Duration(task)),
                AdaptiveTable.Cell.Of(task.ExitCode.ToString())
            ],
            Actions =
            [
                Ui.Action("详情", () => { _selectedId = task.Id; return RefreshAsync(); }),
                Ui.Action("取消", async () =>
                {
                    OperationOutcome<bool> cancelled = Runtime.Extensions.Cancel(task.Id);
                    if (!cancelled.Ok) NativeDialogs.Info("无法取消", cancelled.Message ?? "任务不在运行中。");
                    await RefreshAsync();
                })
            ],
            Extra =
            [
                ("任务 ID", task.Id),
                ("参数摘要", task.ArgumentsSummary ?? "（无参数）"),
                ("状态", Describe(task.State)),
                ("错误", task.Error ?? "—"),
                ("完整日志", task.Log ?? "（无输出）")
            ]
        }).ToList();

        _body.Children.Add(Ui.Card("执行记录", null,
            AdaptiveTable.Build(
            [
                new AdaptiveTable.Column("任务", 90, 1, Mono: true),
                new AdaptiveTable.Column("来源设备", 150, 0),
                new AdaptiveTable.Column("扩展", 150, 0),
                new AdaptiveTable.Column("命令", 120, 1, Mono: true),
                new AdaptiveTable.Column("状态", 110, 0),
                new AdaptiveTable.Column("开始时间", 140, 0, Mono: true),
                new AdaptiveTable.Column("时长", 90, 1),
                new AdaptiveTable.Column("Exit Code", 90, 1)
            ], rows)));

        ExtensionTaskRecord? selected = _selectedId is null ? null : tasks.FirstOrDefault(t => t.Id == _selectedId);
        if (selected is not null)
        {
            _body.Children.Add(Ui.Card("任务详情 · " + selected.Id[..8],
                selected.ExtensionName + " · " + selected.CommandId + " · " + Describe(selected.State),
                Ui.KeyValues(
                    ("来源设备", selected.SourceDeviceName),
                    ("参数摘要", selected.ArgumentsSummary ?? "（无参数）"),
                    ("开始", selected.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
                    ("结束", selected.FinishedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—"),
                    ("退出码", selected.ExitCode.ToString()),
                    ("错误", selected.Error ?? "—")),
                Ui.Caption("输出（已脱敏，长度有上限）"),
                new TextBox
                {
                    Text = Redact(selected.Log) ?? "（无输出）",
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.NoWrap,
                    FontFamily = new System.Windows.Media.FontFamily(Tokens.MonoStack),
                    FontSize = Tokens.Caption,
                    Height = 180,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Background = Ui.Brush("Control"),
                    Foreground = Ui.Brush("TextPrimary"),
                    BorderBrush = Ui.Brush("Border")
                }));
        }

        return Task.CompletedTask;
    }

    private static string Describe(ExtensionTaskState state) => state switch
    {
        ExtensionTaskState.Queued => "Queued（排队）",
        ExtensionTaskState.Running => "Running（运行中）",
        ExtensionTaskState.Succeeded => "Succeeded（成功）",
        ExtensionTaskState.Failed => "Failed（失败）",
        ExtensionTaskState.Cancelled => "Cancelled（已取消）",
        _ => "TimedOut（超时）"
    };

    private static string Duration(ExtensionTaskRecord task)
        => task.FinishedAt is { } finished
            ? Math.Max(0, (int)(finished - task.StartedAt).TotalMilliseconds) + " ms"
            : "—";

    /// <summary>Masks anything that looks like a credential before a log line is shown or exported.</summary>
    internal static string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return System.Text.RegularExpressions.Regex.Replace(
            text, @"(?i)(password|passwd|pwd|token|secret|keymaterial|psk)\s*[=:]\s*\S+", "$1=***");
    }
}
