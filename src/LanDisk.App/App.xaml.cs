using System.IO;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Appearance;

namespace LanDisk.App;

/// <summary>
/// The Windows application.
///
/// <para>LanDisk.exe is a native WPF program, not a launcher that hands the user to a browser: the
/// window in front of the user is the management surface. The web page still exists and is still
/// reachable, but only through Advanced → Web management recovery, and it is labelled as what it is —
/// a fallback for when the native window cannot run.</para>
///
/// <para>Two rules are carried over unchanged from the WinForms shell because the rest of the product
/// depends on them: one instance per signed-in user (the fixed ports and the data root belong to one
/// process), and a second launch brings the existing window forward instead of starting a second
/// server.</para>
/// </summary>
public partial class App : Application
{
    /// <summary>Per-user mutex: two different Windows users may each run a copy, but not the same user twice.</summary>
    internal const string MutexName = @"Local\LanDisk.Shell.SingleInstance.v1";

    /// <summary>Set by a second launch to ask the already running window to come to the front.</summary>
    internal const string ActivationEventName = @"Local\LanDisk.Shell.Activate.v1";

    private Mutex? _mutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationRegistration;

    internal static App Instance => (App)Current;

    internal ShellOptions Options { get; private set; } = ShellOptions.Parse([]);

    internal ServerController? Host { get; private set; }

    internal MainWindow? Shell { get; private set; }

    internal ShellStateLog? StateLog { get; private set; }

    /// <summary>Surfaced by the window so a failed start is explained instead of silently blank.</summary>
    internal string? StartupError { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        Options = ShellOptions.Parse(e.Args);

        // Evidence mode is decided *before* the single-instance mutex, and deliberately so.
        //
        // The capture is an offline run that starts the server on its own scratch data root, photographs the
        // pages and exits. It used to run after the mutex check, which meant that whenever the Owner had
        // LanDisk open — the normal case on a real machine — the capture silently handed off to the running
        // instance and exited 0, producing no evidence at all while appearing to succeed. A run that must not
        // touch the user's session must not be gated by the user's session.
        if (UiCapture.Requested(e.Args))
        {
            base.OnStartup(e);
            DispatcherUnhandledException += (_, args) => ReportFatal(args.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, args) => ReportFatal(args.ExceptionObject as Exception);

            // Scratch data root + free ports: an evidence run must not collide with the product the user has
            // open, and must not be able to read or write their real devices, shares or certificates.
            string[] captureArgs = CaptureEnvironment.Prepare(e.Args, out string captureRoot);

            ApplyTheme();
            Host = new ServerController(captureArgs);
            StateLog = new ShellStateLog(captureRoot);
            Host.StatusChanged += (_, status) => StateLog?.Write(status);
            _ = CaptureAndExitAsync(captureArgs);
            return;
        }

        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            ActivateExistingWindow();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) => ReportFatal(args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => ReportFatal(args.ExceptionObject as Exception);

        ApplyTheme();

        Host = new ServerController(e.Args);
        StateLog = new ShellStateLog(ResolveDataRoot(e.Args));

        // The updater — and the management page's "退出 LanDisk" — stop the host from *outside* the
        // shell. When that happens the process must actually leave: the updater waits for LanDisk.exe
        // to exit before it can replace the binaries, and a tray icon whose server is dead is worse
        // than no tray icon. The WinForms shell subscribed to this event; the WPF rewrite dropped the
        // subscription, so the real-device upgrade produced "旧实例在 60 秒内没有退出，更新程序无法
        // 替换程序文件" and rolled back. Closing the window normally means "minimise to tray", so the
        // quit has to be flagged explicitly before shutdown.
        Host.ExitRequested += (_, _) => Dispatcher.BeginInvoke(() => _ = ExitFromHostAsync());

        Host.StatusChanged += (_, status) =>
        {
            StateLog?.Write(status);
            Shell?.OnHostStatusChanged(status);
        };

        Shell = new MainWindow();
        MainWindow = Shell;

        if (!Options.StartMinimized) Shell.Show();

        BeginActivationListener();
        _ = StartHostAsync();
    }

    private async Task CaptureAndExitAsync(string[] args)
    {
        // The capture's own verdict has to become the process exit code.
        //
        // It used to be thrown away and the process always exited 0, so a run with 335 failing combinations was
        // indistinguishable from a clean one to whatever launched it — and the release flow that called the
        // capture could not tell that the acceptance had failed. "The program finished" is not "the acceptance
        // passed"; the caller must be able to see the difference.
        int exitCode = 3;
        try
        {
            ServerStatus status = await Host!.StartAsync().ConfigureAwait(true);
            StateLog?.Write(status);
            exitCode = await UiCapture.RunAsync(args, status).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("UI 截图失败：" + ex.Message);
            exitCode = 3;
        }
        finally
        {
            // A capture run is a diagnostic: it has written its files and must not linger in the tray.
            Shutdown();
            Environment.Exit(exitCode);
        }
    }

    /// <summary>
    /// Applies the appearance the user last chose and returns it. The choice is stored in
    /// <c>shell.json</c>, which the update system treats as managed state, so it survives an upgrade,
    /// a rollback and a reinstall. A Windows 10 machine without Mica still gets a solid window.
    /// </summary>
    internal static Core.Configuration.ThemeProfile ApplyTheme()
        => ThemeManager.ApplyFromPreferences(ResolveDataRoot(Environment.GetCommandLineArgs()));

    private async Task StartHostAsync()
    {
        try
        {
            ServerStatus status = await Host!.StartAsync().ConfigureAwait(true);
            Shell?.OnHostStatusChanged(status);

            if (!status.IsRunning && !string.IsNullOrEmpty(status.Error))
            {
                StartupError = status.Error;
                Shell?.ShowStartupFailure(status);
            }
        }
        catch (Exception ex)
        {
            StartupError = ex.Message;
            ReportFatal(ex);
        }
    }

    private void BeginActivationListener()
    {
        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            (_, _) => Dispatcher.BeginInvoke(() => Shell?.BringToFront()),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    private static void ActivateExistingWindow()
    {
        try
        {
            using EventWaitHandle activation = EventWaitHandle.OpenExisting(ActivationEventName);
            activation.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The first instance is still starting up; nothing to activate yet.
        }
        catch (UnauthorizedAccessException)
        {
            // Different integrity level; the running instance simply stays as it is.
        }
    }

    internal static string ResolveDataRoot(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--data-dir" && i + 1 < args.Length) return Path.GetFullPath(args[i + 1]);
            if (args[i].StartsWith("--data-dir=", StringComparison.Ordinal))
                return Path.GetFullPath(args[i]["--data-dir=".Length..]);
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanDisk");
    }

    /// <summary>Quits the whole product: stop the server, then leave.</summary>
    internal async Task ExitAsync()
    {
        try
        {
            if (Host is not null) await Host.StopAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or TimeoutException)
        {
            // A stop that fails still has to let the process exit; the shell-state log records it.
        }

        Shutdown();
    }

    /// <summary>
    /// Leaves the process because the host stopped on somebody else's initiative (updater, or the
    /// management page). Always runs on the UI thread.
    /// </summary>
    private async Task ExitFromHostAsync()
    {
        Shell?.PrepareForExit();
        ShellStateLog.TryWriteLine(ResolveDataRoot(Environment.GetCommandLineArgs()), "服务由外部请求停止，外壳退出。");

        var timeout = Task.Delay(TimeSpan.FromSeconds(20));
        Task exit = ExitAsync();
        if (await Task.WhenAny(exit, timeout).ConfigureAwait(true) == timeout)
        {
            // The host is already gone; refusing to leave would strand the updater, which is waiting
            // for exactly this process to disappear. Hard exit is the correct last resort here.
            Environment.Exit(0);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Host?.Dispose(); } catch { }
        try { _activationRegistration?.Unregister(null); } catch { }
        try { _activationEvent?.Dispose(); } catch { }
        try { _mutex?.Dispose(); } catch { }

        base.OnExit(e);
    }

    private static void ReportFatal(Exception? exception)
    {
        string message = exception?.Message ?? "未知错误。";
        try
        {
            // The data root comes from the command line, not from a hardcoded path: an isolated run
            // (screenshots, runtime identity check, acceptance) must not write into the real
            // %LOCALAPPDATA%\LanDisk. The old hardcoded path did exactly that.
            string dataRoot = ResolveDataRoot(Environment.GetCommandLineArgs());
            Directory.CreateDirectory(dataRoot);
            File.AppendAllText(
                Path.Combine(dataRoot, "shell-crash.log"),
                $"{DateTimeOffset.Now:O}  {exception}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        try
        {
            MessageBox.Show(
                "LanDisk 遇到了一个未预期的错误，已记录到数据目录中的 shell-crash.log。" +
                Environment.NewLine + Environment.NewLine + message,
                "LanDisk",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (InvalidOperationException)
        {
            // No dispatcher left to show anything; the log above is the record.
        }
    }
}
