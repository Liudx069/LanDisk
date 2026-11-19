namespace LanDisk.Setup;

/// <summary>What the installer was asked to do, parsed from the command line. Pure and testable.</summary>
public sealed class SetupOptions
{
    public bool Silent { get; set; }

    /// <summary>When set, the installer renders its own states to this directory and exits (UI evidence).</summary>
    public string? RenderUiDir { get; set; }
    public bool Uninstall { get; set; }
    public bool RemoveUserData { get; set; }

    public string InstallDir { get; set; } = SetupLayout.DefaultInstallDir;
    public string DataDir { get; set; } = SetupLayout.DefaultDataDir;

    public bool DesktopShortcut { get; set; } = true;
    public bool StartMenuShortcut { get; set; } = true;
    public bool LaunchAfterInstall { get; set; } = true;

    /// <summary>Skip the firewall step entirely. Used by automated tests that must not touch the host firewall.</summary>
    public bool NoFirewall { get; set; }

    /// <summary>Do not wait for the running LanDisk to exit; fail instead. Used by acceptance tests.</summary>
    public bool NoForceStop { get; set; }

    /// <summary>Elevated helper modes. They do exactly one thing and accept no other arguments.</summary>
    public bool ElevatedFirewallAdd { get; set; }
    public bool ElevatedFirewallRemove { get; set; }

    /// <summary>Print the firewall commands and exit; how the rules are audited without touching a firewall.</summary>
    public bool PrintFirewallPlan { get; set; }

    /// <summary>
    /// When set, the firewall plan is also written here. A windowed executable has no stdout to
    /// capture, so an automated audit reads the file instead of trying to pipe a GUI process.
    /// </summary>
    public string PlanOutPath { get; set; } = string.Empty;

    /// <summary>Do not launch or open anything at the end.</summary>
    public bool NoLaunch { get; set; }

    /// <summary>
    /// Skip shortcut creation and the uninstall registration. Only automated acceptance runs use this:
    /// they install into a scratch directory and must not disturb the real Start Menu, desktop or
    /// "已安装的应用" entry of the machine running the test.
    /// </summary>
    public bool NoRegister { get; set; }

    public static SetupOptions Parse(string[] args)
    {
        var options = new SetupOptions();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--silent":
                case "/silent":
                    options.Silent = true;
                    break;

                case "--uninstall":
                    options.Uninstall = true;
                    break;

                case "--remove-data":
                    options.RemoveUserData = true;
                    break;

                case "--no-desktop-shortcut":
                    options.DesktopShortcut = false;
                    break;

                case "--no-start-menu-shortcut":
                    options.StartMenuShortcut = false;
                    break;

                case "--no-launch":
                    options.NoLaunch = true;
                    options.LaunchAfterInstall = false;
                    break;

                case "--no-register":
                    options.NoRegister = true;
                    break;

                case "--no-firewall":
                    options.NoFirewall = true;
                    break;

                case "--no-force-stop":
                    options.NoForceStop = true;
                    break;

                case "--print-firewall-plan":
                    options.PrintFirewallPlan = true;
                    break;

                case "--elevated-firewall-add":
                    options.ElevatedFirewallAdd = true;
                    break;

                case "--elevated-firewall-remove":
                    options.ElevatedFirewallRemove = true;
                    break;

                case "--install-dir":
                    if (i + 1 < args.Length) options.InstallDir = args[++i];
                    break;

                case "--data-dir":
                    if (i + 1 < args.Length) options.DataDir = args[++i];
                    break;

                case "--plan-out":
                    if (i + 1 < args.Length) options.PlanOutPath = args[++i];
                    break;

                // Renders the installer's own states to PNGs at several DPI scales and exits. Used by the
                // installer UI evidence, so the screenshots do not depend on the desktop's DPI or on the
                // window being the one on top.
                case "--render-ui":
                    if (i + 1 < args.Length) options.RenderUiDir = args[++i];
                    break;
            }
        }

        options.InstallDir = Path.GetFullPath(options.InstallDir);
        options.DataDir = Path.GetFullPath(options.DataDir);
        return options;
    }

    /// <summary>
    /// Explains why the requested combination is not acceptable. The elevated helper in particular
    /// must never be reachable with an arbitrary path, because it runs with administrator rights.
    /// </summary>
    public string? Validate()
    {
        if (ElevatedFirewallAdd || ElevatedFirewallRemove)
        {
            if (!Path.GetFullPath(InstallDir).Equals(Path.GetFullPath(SetupLayout.DefaultInstallDir), StringComparison.OrdinalIgnoreCase))
                return "提权助手只接受默认安装目录，拒绝其它路径。";

            if (ElevatedFirewallAdd && ElevatedFirewallRemove)
                return "提权助手一次只能执行一个动作。";
        }

        if (RemoveUserData && !Uninstall)
            return "--remove-data 只能与 --uninstall 一起使用。";

        if (string.IsNullOrWhiteSpace(InstallDir) || !Path.IsPathFullyQualified(InstallDir))
            return "安装目录必须是绝对路径。";

        if (string.IsNullOrWhiteSpace(DataDir) || !Path.IsPathFullyQualified(DataDir))
            return "数据目录必须是绝对路径。";

        return null;
    }
}
