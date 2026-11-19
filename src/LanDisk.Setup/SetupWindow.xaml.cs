using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LanDisk.Setup.Visual;
using Application = System.Windows.Application;
using Path = System.Windows.Shapes.Path;

namespace LanDisk.Setup;

/// <summary>
/// The installer's visual surface: a dark, chrome-less window whose only moving part is a vector server rack
/// that powers on from the top down.
///
/// <para><b>What is gone and why.</b> The numbered step strip, its connecting line and the progress bar are
/// removed — the Owner rejected the "industrial deployment tool" look they produced. So is the separate
/// "LanDisk 已安装" completion window: the install now ends by lighting the whole cabinet, holding a single
/// line of text, and closing. Anything that still needs saying (how to connect an iPhone) is said inside
/// LanDisk itself, which the installer may start but never replaces.</para>
///
/// <para><b>Nothing opens a browser.</b> The completion screen previously offered the administration page at
/// <c>127.0.0.1:8891/admin</c> as the natural next step. That page stays available only through the explicitly
/// labelled advanced section, and no install path opens it automatically.</para>
///
/// <para>The install logic itself is untouched: this class drives <see cref="SetupEngine"/> and renders its
/// progress, exactly as the previous console did. Signing, manifest, migration, firewall, UAC and rollback all
/// live in the engine.</para>
/// </summary>
public partial class SetupWindow : Window
{
    private readonly SetupOptions _options;
    private readonly List<string> _log = [];
    private readonly SetupProgressState _state = new();
    private bool _running;
    private bool _succeeded;
    private bool _detailOpen;

    public SetupWindow(SetupOptions options)
    {
        _options = options;
        InitializeComponent();

        BuildLogo();
        Rack.ReduceMotion = ReduceMotion;

        DetailText.Text = string.Empty;
        StateTitle.Text = "准备安装";
        StateDetail.Text = "安装到 " + (string.IsNullOrEmpty(options.InstallDir) ? "%LOCALAPPDATA%\\Programs\\LanDisk" : options.InstallDir) + "；用户数据、配对与证书保持不动。";
        Rack.ApplyStage(1, animate: false);

        Loaded += (_, _) => FitToWorkArea();
    }

    /// <summary>Honour the OS "reduce motion" setting; Program sets this before showing the window.</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>The stage currently shown, for the installer UI gate.</summary>
    public int CurrentStage => _state.Active + 1;

    /// <summary>True once the run finished without an error.</summary>
    public bool Succeeded => _succeeded;

    /// <summary>Runs the engine without an interactive click, used by the evidence renderer.</summary>
    public Task RunAsync() => ExecuteAsync();

    /// <summary>Applies a stage for the evidence renderer (no engine involved).</summary>
    public void PreviewStage(int stage, string title, string detail)
    {
        StateTitle.Text = title;
        StateDetail.Text = detail;
        Rack.ApplyStage(stage, animate: false);
    }

    /// <summary>Applies the failure state for the evidence renderer.</summary>
    public void PreviewError(string message, int stage)
    {
        StateTitle.Text = "安装未完成";
        StateDetail.Text = message;
        Rack.ApplyError(stage);
    }

    /// <summary>Switches the window into its "running" layout: options collapse, the state line takes over.</summary>
    public void EnterRunningLayout()
    {
        IntroPanel.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
    }

    public void SetDetail(string text)
    {
        _log.Add(text);
        DetailText.Text = string.Join(Environment.NewLine, _log);
        DetailScroll.ScrollToEnd();
    }

    // ---- brand --------------------------------------------------------------

    private void BuildLogo()
    {
        // The mark is real geometry: the wordmark outlines and the tagline outlines are drawn as paths, so it
        // stays sharp at any DPI and the installer carries no image asset for it.
        var group = new Canvas
        {
            Width = LanDiskLogoGeometry.DesignWidth,
            Height = LanDiskLogoGeometry.DesignHeight
        };

        foreach (string outline in LanDiskLogoGeometry.Wordmark)
        {
            group.Children.Add(new Path
            {
                Data = LanDiskLogoGeometry.Parse(outline),
                Fill = SetupVisualTheme.TextPrimaryBrush
            });
        }

        foreach (string outline in LanDiskLogoGeometry.Tagline)
        {
            group.Children.Add(new Path
            {
                Data = LanDiskLogoGeometry.Parse(outline),
                Fill = SetupVisualTheme.TextPrimaryBrush,
                Opacity = 0.82
            });
        }

        // The tagline is part of the mark itself, so there is no second copy under it: the brand says
        // "让数据更自由" exactly once.
        LogoBox.Child = group;
    }

    private void FitToWorkArea()
    {
        Rect area = SystemParameters.WorkArea;
        if (Height > area.Height - 40) Height = Math.Max(MinHeight, area.Height - 40);
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

    // ---- interaction --------------------------------------------------------

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (!_running) Close();
    }

    private void OnDetailToggle(object sender, RoutedEventArgs e)
    {
        _detailOpen = !_detailOpen;
        DetailSurface.Visibility = _detailOpen ? Visibility.Visible : Visibility.Collapsed;
        DetailToggle.Content = _detailOpen ? "隐藏安装详情" : "显示安装详情";
    }

    private async void OnInstallClick(object sender, RoutedEventArgs e) => await ExecuteAsync();

    private async Task ExecuteAsync()
    {
        if (_running) return;
        _running = true;
        InstallButton.IsEnabled = false;
        EnterRunningLayout();

        try
        {
            var engine = new SetupEngine(
                _options,
                line => Dispatcher.Invoke(() => SetDetail(line)),
                step => Dispatcher.Invoke(() => OnStep(step)));

            SetupOutcome outcome = await Task.Run(() => engine.InstallAsync());
            foreach (string line in outcome.Details) SetDetail(line);

            if (!outcome.Ok)
            {
                OnStepFailed();
                PreviewError(outcome.Summary, Math.Max(0, _state.ErrorStage));
                InstallButton.IsEnabled = true;
                _running = false;
                return;
            }

            _succeeded = true;
            _state.Complete(SetupStageMap.StageCount - 1);
            Rack.ApplyStage(6, animate: true);

            // "Ready" is held long enough to read and then the window leaves on its own: there is no separate
            // completion dialog, and the rack never keeps animating.
            StateTitle.Text = "LanDisk 已就绪";
            StateDetail.Text = outcome.Summary;
            await Task.Delay(ReduceMotion ? 700 : 1000);

            Application.Current?.Shutdown();
        }
        catch (Exception ex)
        {
            PreviewError(ex.Message, Math.Max(0, _state.Active));
            SetDetail(ex.ToString());
            _running = false;
            InstallButton.IsEnabled = true;
        }
    }

    private void OnStep(SetupStep step)
    {
        int stage = SetupStageMap.StageIndex(step);
        _state.SetActive(stage);
        _state.Complete(stage);
        (string _, string title, string detail) = SetupStageMap.Stages[stage];
        StateTitle.Text = title;
        StateDetail.Text = detail;
        Rack.ApplyStage(stage + 1, animate: true);
    }

    private void OnStepFailed()
    {
        _state.SetError(Math.Max(0, _state.Active));
        Rack.ApplyError(_state.ErrorStage);
        StateTitle.Text = "安装未完成";
    }
}
