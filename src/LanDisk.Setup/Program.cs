using System.Drawing;
using System.Windows.Forms;

namespace LanDisk.Setup;

/// <summary>
/// Entry point. Five modes, and the two dangerous ones are reachable only with the exact fixed
/// arguments the installer itself passes to them.
/// </summary>
internal static class Program
{
    private const string InstanceMutexName = @"Local\LanDisk-Setup";

    [STAThread]
    private static int Main(string[] args)
    {
        SetupOptions options = SetupOptions.Parse(args);

        if (options.PrintFirewallPlan)
        {
            // Auditable without administrator rights and without touching a firewall: the acceptance
            // script and any reviewer can read exactly what the elevated step would run.
            IReadOnlyList<FirewallRule> rules = FirewallPlan.Rules(options.InstallDir);
            IReadOnlyList<string> problems = FirewallPlan.Violations(options.InstallDir, rules);

            var report = new System.Text.StringBuilder();
            report.AppendLine("# LanDisk firewall plan");
            report.AppendLine(FirewallPlan.Describe(rules));
            foreach (FirewallRule rule in rules)
            {
                report.AppendLine("netsh " + string.Join(' ', FirewallPlan.AddArguments(rule)));
            }
            report.AppendLine(problems.Count == 0 ? "INVARIANTS: PASS" : "INVARIANTS: FAIL");
            foreach (string problem in problems) report.AppendLine("  " + problem);

            string text = report.ToString();
            if (!string.IsNullOrWhiteSpace(options.PlanOutPath))
            {
                try
                {
                    File.WriteAllText(options.PlanOutPath, text, new System.Text.UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("写入防火墙计划失败：" + ex.Message);
                    return 6;
                }
            }

            try
            {
                Console.WriteLine(text);
            }
            catch (IOException)
            {
                // A windowed process started without a console has nowhere to write; the file above is
                // the machine-readable path and is what automated runs use.
            }

            return problems.Count == 0 ? 0 : 1;
        }

        // ---- elevated helper: exactly two actions, no UI, no arguments of its own ----------------
        if (options.ElevatedFirewallAdd || options.ElevatedFirewallRemove)
        {
            string? validation = options.Validate();
            if (validation is not null)
            {
                Console.Error.WriteLine("拒绝执行：" + validation);
                return 3;
            }

            if (!FirewallHelper.IsElevated)
            {
                Console.Error.WriteLine("该模式必须以管理员身份运行。");
                return 4;
            }

            int applied = options.ElevatedFirewallAdd
                ? FirewallHelper.AddRules(options.InstallDir, Console.WriteLine)
                : FirewallHelper.RemoveRules(options.InstallDir, Console.WriteLine);
            return applied > 0 ? 0 : 5;
        }

        string? other = options.Validate();
        if (other is not null)
        {
            Report(options, "LanDisk", other, isError: true);
            return 2;
        }

        // One installer at a time: two of them replacing the same files is a way to produce a broken
        // installation that looks installed.
        using var mutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            // In silent mode this must not open a dialog: there is nobody to click it, and an
            // installer that waits forever on a message box is worse than one that says "already
            // running" and exits. The acceptance script drives exactly this path.
            Report(options, "LanDisk", "安装程序已经在运行。", isError: false);
            return 0;
        }

        ApplicationConfiguration.Initialize();

        try
        {
            return options.Uninstall ? RunUninstall(options) : RunInstall(options);
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { }
        }
    }

    /// <summary>
    /// Says something to the person running the installer — unless it is running unattended, in which
    /// case it must never block on a dialog nobody can dismiss.
    /// </summary>
    private static void Report(SetupOptions options, string title, string message, bool isError)
    {
        if (!options.Silent)
        {
            MessageBox.Show(message, title, MessageBoxButtons.OK,
                isError ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            return;
        }

        try
        {
            if (isError) Console.Error.WriteLine(message);
            else Console.WriteLine(message);
        }
        catch (IOException)
        {
            // A windowed process started without a console has nowhere to write; the exit code is the
            // signal in that case.
        }
    }

    private static int RunInstall(SetupOptions options)
    {
        if (options.Silent)
        {
            var engine = new SetupEngine(options, Console.WriteLine);
            SetupOutcome outcome = engine.InstallAsync().GetAwaiter().GetResult();
            foreach (string line in outcome.Details) Console.WriteLine(line);
            Console.WriteLine(outcome.Summary);
            return outcome.Ok ? 0 : 1;
        }

        if (options.RenderUiDir is { Length: > 0 } renderDir)
        {
            return RenderInstallerUi(options, renderDir);
        }

        // WPF visual surface: one dark window whose server rack powers on as the install proceeds. The engine,
        // signing, manifest, migration, firewall and rollback are unchanged; only the visual layer is new.
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        var window = new SetupWindow(options);
        app.Run(window);
        return 0;
    }

    /// <summary>
    /// Renders the installer's own states to PNG files, by actually showing the window and capturing it.
    ///
    /// <para><b>What was wrong.</b> The first version drew a hidden form with <c>DrawToBitmap</c> without ever
    /// showing it or pumping the message loop, so the child controls never realised and the files came out as
    /// a dark rectangle with only the title bar — "0% white pixels" was technically true and entirely hollow.
    /// The capture now shows the window off-screen, pumps messages until layout settles, and then reads the
    /// real pixels, asserting that the picture contains the surface, the accent, the status colours and the
    /// stage text before it is accepted.</para>
    ///
    /// <para><b>DPI honesty.</b> This captures the window at its real 100% size. It does <i>not</i> pretend
    /// that enlarging a bitmap equals verifying a 125/150/200% DPI layout: that is marked
    /// <c>INSTALLER_REAL_DPI = NOT_RUN</c> in the report. The installer's own form is DPI-aware
    /// (<c>AutoScaleMode=Dpi</c> + PerMonitorV2), and the node-strip layout math is tested separately.</para>
    /// </summary>
    private static int RenderInstallerUi(SetupOptions options, string directory)
        => LanDisk.Setup.Visual.SetupUiEvidence.Render(options, directory);

    private static bool IsClose(Color a, Color b)
    {
        return Math.Abs(a.R - b.R) <= 12 && Math.Abs(a.G - b.G) <= 12 && Math.Abs(a.B - b.B) <= 12;
    }

    private static int RunUninstall(SetupOptions options)
    {
        if (options.Silent)
        {
            var engine = new SetupEngine(options, Console.WriteLine);
            SetupOutcome outcome = engine.UninstallAsync().GetAwaiter().GetResult();
            foreach (string line in outcome.Details) Console.WriteLine(line);
            Console.WriteLine(outcome.Summary);
            return outcome.Ok ? 0 : 1;
        }

        using var form = new UninstallForm(options);
        Application.Run(form);
        return 0;
    }
}
