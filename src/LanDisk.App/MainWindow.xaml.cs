using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LanDisk.Server.Services;
// WPF-UI ships controls with the same simple names as WPF itself (Button, TextBlock, ...). Importing
// the whole namespace would make every one of those ambiguous, so only the types actually used are
// brought in by name.
using FluentWindow = Wpf.Ui.Controls.FluentWindow;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;
using InfoBar = Wpf.Ui.Controls.InfoBar;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;
using Card = Wpf.Ui.Controls.Card;

namespace LanDisk.App;

/// <summary>
/// The one window the product has.
///
/// <para>The left rail lists the management areas, the right side shows the selected one, and the
/// status strip at the bottom always says what the service is doing — a user should never have to
/// guess whether the thing is running. Nothing in the normal flow opens a browser; the single exception
/// is the explicitly labelled recovery page under Settings → Advanced.</para>
/// </summary>
public partial class MainWindow : FluentWindow
{
    private readonly List<NavEntry> _entries = [];
    private NativePage? _current;
    private NavEntry? _selected;
    private bool _quitting;

    /// <summary>Set by the capture mode so an unknown or mis-selected page is a failure, not a silent fall-through.</summary>
    internal bool StrictNavigation { get; set; }

    /// <summary>The page that is currently shown, or null before the first selection.</summary>
    internal NativePage? Current => _current;

    internal MainWindow()
    {
        InitializeComponent();

                try { Tray.Icon = AppIcon.CreateImageSource(32); }
        catch (Exception ex) { ShellStateLog.TryWriteLine(ResolveDataRoot(), "托盘图标加载失败：" + ex.Message); }

        ContentHost.Background = (Brush)FindResource("LanDisk.Window");

        BuildNavigation();
        WireTray();

        SizeChanged += (_, _) => ApplyRailMode();
        ApplyRailMode();

        ContentRendered += async (_, _) =>
        {
            // --page lets another process (the installer) open a specific area of the product without going
            // near a browser. An unknown name lands on the overview in the product, but in capture mode it
            // is a hard failure (the capture must never stay on a page and still write a PASS).
            NavEntry? requested = string.IsNullOrWhiteSpace(App.Instance.Options.Page)
                ? null
                : _entries.FirstOrDefault(e => string.Equals(e.Title, App.Instance.Options.Page, StringComparison.OrdinalIgnoreCase));
            if (requested is null && !string.IsNullOrWhiteSpace(App.Instance.Options.Page) && StrictNavigation)
                throw new InvalidOperationException("找不到页面：" + App.Instance.Options.Page);

            // First-run only, and only for a machine that has never been used.
            //
            // The installer used to end with its own "LanDisk 已安装" window offering 连接 iPhone / 打开 LanDisk /
            // 完成 — a second application that disappeared as soon as it was used and taught the user nothing about
            // the product. The tutorial belongs here, in the product, and it must appear exactly once: a fresh
            // install with no paired device opens the phone page, while an upgrade that already has devices opens
            // the overview and never shows it.
            if (requested is null && IsFirstRun)
            {
                NavEntry? phone = _entries.FirstOrDefault(e => e.Page.Title == "连接 iPhone");
                if (phone is not null)
                {
                    await SelectAsync(phone);
                    return;
                }
            }

            await SelectAsync(requested ?? _entries[0]);
        };
        Closing += OnClosingAsync;
    }

    private static string ResolveDataRoot() => App.ResolveDataRoot(Environment.GetCommandLineArgs());

    internal LanDiskRuntime? Runtime => App.Instance.Host?.Runtime;

    /// <summary>
    /// True when this installation has never had a device paired and no share has been created. Used only to
    /// choose the first screen; it is not a permission or a state the product stores anywhere.
    /// </summary>
    internal bool IsFirstRun
    {
        get
        {
            LanDiskRuntime? runtime = Runtime;
            if (runtime is null) return false;
            return Core.Model.FirstRunPolicy.IsFirstRun(runtime.Devices.All().Count, runtime.Shares.All().Count(s => s.Enabled));
        }
    }
    /// <summary>The host the current page is drawn into; the UI capture checks it, not the window chrome.</summary>
    internal FrameworkElement PageHost => ContentHost;

    /// <summary>
    /// The management areas, grouped so the rail reads as a small number of intentions rather than a list of
    /// fifteen equal items. The pages themselves come from <see cref="PageCatalog"/> — the same list the UI
    /// capture reads — so the rail and the evidence cannot disagree about which pages exist.
    /// </summary>
    private void BuildNavigation()
    {
        foreach ((string group, IReadOnlyList<PageDescriptor> pages) in PageCatalog.Groups)
        {
            var caption = new TextBlock
            {
                Text = group,
                // 12 DIP 是产品下限（界面证据里有断言），分组标题也不例外。
                FontSize = Tokens.Caption,
                Margin = new Thickness(12, 14, 12, 4),
                Foreground = (Brush)FindResource("LanDisk.TextTertiary")
            };
            _groupCaptions.Add(caption);
            NavList.Children.Add(caption);

            foreach (PageDescriptor page in pages) Add(page);
        }
    }

    private readonly List<TextBlock> _groupCaptions = [];
    private readonly List<(TextBlock Label, StackPanel Row)> _navTexts = [];

    /// <summary>Adds one rail item from the catalogue.</summary>
    private void Add(PageDescriptor page)
    {
        var label = new TextBlock { Text = page.Title, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new SymbolIcon(page.Icon) { FontSize = 16, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center },
                label
            }
        };

        var button = new Button
        {
            Content = row,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 1, 0, 1),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand
        };

        var entry = new NavEntry(page, button);
        button.Click += async (_, _) => await SelectAsync(entry);
        _entries.Add(entry);
        NavList.Children.Add(button);
        _navTexts.Add((label, row));
    }

    /// <summary>
    /// Below a width threshold the rail collapses to an icon-only strip: group captions disappear and the
    /// item labels hide, leaving the symbols. This is what lets the window be usable on a 1366×768 screen at
    /// 200% (683×384 DIP) instead of insisting on a 1100 DIP rail that cannot fit there.
    /// </summary>
    private void ApplyRailMode()
    {
        bool narrow = ActualWidth < 900;
        foreach (TextBlock caption in _groupCaptions) caption.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        foreach ((TextBlock label, StackPanel row) in _navTexts)
        {
            label.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            row.Margin = narrow ? new Thickness(0) : new Thickness(0);
        }

        // Icon-only rail: shrink the column and let the buttons centre their symbols.
        if (RailColumn is not null) RailColumn.Width = new GridLength(narrow ? 56 : 228);
    }

    private void WireTray()
    {
        TrayOpen.Click += (_, _) => BringToFront();
        TrayIphone.Click += async (_, _) =>
        {
            BringToFront();
            await SelectAsync(_entries.First(e => e.Title == "连接 iPhone"));
        };
        TrayRestart.Click += async (_, _) =>
        {
            if (App.Instance.Host is null) return;
            await App.Instance.Host.RestartAsync();
            await RefreshCurrentAsync();
        };
        TrayExit.Click += async (_, _) =>
        {
            _quitting = true;
            await App.Instance.ExitAsync();
        };
    }

    /// <summary>
    /// Repaints the window chrome after a theme change. Page bodies follow on their own because they hold
    /// the very brush instances the palette mutates; the window's own surfaces are resolved here.
    /// </summary>
    internal void OnThemeChanged()
    {
        ContentHost.Background = (Brush)FindResource("LanDisk.Window");
        Background = (Brush)FindResource("LanDisk.Window");
        StatusText.Foreground = (Brush)FindResource("LanDisk.TextPrimary");
        OnHostStatusChanged(App.Instance.Host?.Status ?? new ServerStatus());
        _current?.RefreshAsync();
    }

    /// <summary>Closing the window minimises to the tray; quitting is an explicit menu action.</summary>
    private async void OnClosingAsync(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_quitting) return;

        e.Cancel = true;
        HideToTray();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Marks this as a real quit so the "close means minimise to tray" rule stops cancelling the
    /// shutdown. Used by the tray menu and by App when the host was stopped from outside (the
    /// updater has to see this process exit before it can replace the program files).
    /// </summary>
    internal void PrepareForExit() => _quitting = true;

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
        ShellStateLog.TryWriteLine(ResolveDataRoot(), "窗口已最小化到托盘，LanDisk 仍在后台运行。");
    }

    internal void BringToFront()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
    }

    private async Task SelectAsync(NavEntry entry)
    {
        foreach (NavEntry other in _entries)
        {
            other.Button.Background = ReferenceEquals(other, entry)
                ? (Brush)FindResource("LanDisk.Elevated")
                : Brushes.Transparent;
        }

        _current?.OnHidden();
        _current = entry.Create();
        _selected = entry;
        ContentHost.Background = (Brush)FindResource("LanDisk.Window");
        ContentHost.Children.Clear();
        ContentHost.Children.Add(_current.Build());
        _current.OnShown();
        await _current.RefreshAsync();
    }

    internal async Task RefreshCurrentAsync()
    {
        if (_current is not null) await _current.RefreshAsync();
    }

    /// <summary>
    /// Navigates to a page by its exact title.
    ///
    /// <para>Returns the page that is now showing, or null when the title does not exist. In strict mode
    /// (the UI capture) an unknown title is a hard failure rather than a silent no-op — the capture must
    /// never "stay on the previous page and still write a PASS row".</para>
    /// </summary>
    internal async Task<NativePage?> NavigateToAsync(string title)
    {
        NavEntry? entry = _entries.FirstOrDefault(e => e.Title == title);
        if (entry is null)
        {
            if (StrictNavigation) throw new InvalidOperationException("找不到页面：" + title);
            return null;
        }

        await SelectAsync(entry);
        return _current;
    }

    /// <summary>The title of the page currently shown (for the capture's page-identity check).</summary>
    internal string? CurrentTitle => _selected?.Title;

    /// <summary>The type of the page currently shown.</summary>
    internal Type? CurrentPageType => _current?.GetType();

    internal void OnHostStatusChanged(ServerStatus status)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnHostStatusChanged(status));
            return;
        }

        (string text, Brush brush) = status.State switch
        {
            ServerState.Running => ($"服务运行中 · {status.Version} · {string.Join("、", status.BoundUrls.Take(2))}",
                (Brush)FindResource("LanDisk.Success")),
            ServerState.Starting => ("正在启动服务…", (Brush)FindResource("LanDisk.Warning")),
            ServerState.Stopping => ("正在停止服务…", (Brush)FindResource("LanDisk.Warning")),
            ServerState.PortConflict => ($"端口被占用：{status.ConflictedPort}（LanDisk 不会自动更换端口）",
                (Brush)FindResource("LanDisk.Error")),
            ServerState.Failed => ("服务启动失败：" + (status.Error ?? "未知原因"), (Brush)FindResource("LanDisk.Error")),
            ServerState.StopFailed => ("服务未能正常停止，请查看日志", (Brush)FindResource("LanDisk.Error")),
            _ => ("服务未运行", (Brush)FindResource("LanDisk.TextSecondary"))
        };

        StatusText.Text = text;
        StatusDot.Fill = brush;
    }

    /// <summary>Shown when the service could not start at all, so the window is never silently empty.</summary>
    internal void ShowStartupFailure(ServerStatus status)
    {
        ContentHost.Children.Clear();
        var panel = new StackPanel();
        panel.Children.Add(Ui.Title(status.State == ServerState.PortConflict ? "端口被占用" : "服务启动失败"));
        panel.Children.Add(Ui.Notice(status.Error ?? "未知原因",
            status.State == ServerState.PortConflict ? InfoBarSeverity.Warning : InfoBarSeverity.Error));
        panel.Children.Add(Ui.Body("LanDisk 不会自动更换端口，也不会强制结束占用端口的程序。" +
                                   "请关闭占用该端口的程序后，用“重新启动服务”重试。", secondary: true));
        panel.Children.Add(Ui.Row(Ui.Action("重新启动服务", async () =>
        {
            if (App.Instance.Host is null) return;
            ServerStatus restarted = await App.Instance.Host.RestartAsync();
            OnHostStatusChanged(restarted);
            if (restarted.IsRunning) await SelectAsync(_entries[0]);
            else ShowStartupFailure(restarted);
        }, primary: true)));
        ContentHost.Children.Add(panel);
    }

    private sealed record NavEntry(PageDescriptor Page, Button Button)
    {
        public string Title => Page.Title;
        public NativePage Create() => Page.Create();
    }
}
