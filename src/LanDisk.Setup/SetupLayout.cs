namespace LanDisk.Setup;

/// <summary>
/// Every path and name the installer owns, in one place.
///
/// <para>The two roots are fixed on purpose. Programs live under <c>%LOCALAPPDATA%\Programs\LanDisk</c>
/// so that replacing them never needs elevation and an in-place <c>.lupd</c> update never asks for it
/// either; user data stays in <c>%LOCALAPPDATA%\LanDisk</c>, exactly where 1.3.0 kept it, so a repair
/// install takes over an existing installation instead of creating a second one.</para>
/// </summary>
public static class SetupLayout
{
    public const string ProductName = "LanDisk";
    public const string Publisher = "LanDisk";

    public const string ShellExeName = "LanDisk.exe";
    public const string ServerExeName = "LanDisk.Server.exe";
    public const string UpdaterExeName = "LanDisk.Updater.exe";
    public const string SetupExeName = "LanDisk-Setup.exe";
    public const string InstallManifestName = "install-manifest.json";
    public const string BuildInfoName = "build-info.json";

    /// <summary>Registered in HKCU so the product appears in "已安装的应用" for this user only.</summary>
    public const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LanDisk";

    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValueName = "LanDisk";

    /// <summary>Every firewall rule this product creates is named with this prefix — nothing else is touched.</summary>
    public const string FirewallRulePrefix = "LanDisk";

    /// <summary>Fixed ports. The installer never invents a different one.</summary>
    public const int AdminPort = 8891;
    public const int HttpsPort = 8892;
    public const int BootstrapPort = 8890;

    public const string PayloadResourceName = "LanDisk.Payload.zip";

    /// <summary>Files the installer manages itself and therefore never lists as installed program files.</summary>
    public static readonly string[] InstallerOwnedFiles = [InstallManifestName, SetupExeName];

    public static string DefaultInstallDir
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", ProductName);

    public static string DefaultDataDir
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName);

    public static string StartMenuDir
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ProductName);

    public static string DesktopDir
        => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>Old-style extraction folders a user may have created by hand; reported, never deleted.</summary>
    public static IEnumerable<string> KnownManualLayouts()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "LanDisk-win-x64");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "LanDisk-win-x64");
    }

    public static string ShellPathFor(string installDir) => Path.Combine(installDir, ShellExeName);

    public static string SetupCopyPathFor(string installDir) => Path.Combine(installDir, SetupExeName);

    public static string ManifestPathFor(string installDir) => Path.Combine(installDir, InstallManifestName);

    public static string BuildInfoPathFor(string installDir) => Path.Combine(installDir, BuildInfoName);

    public static string AdminUrlFileFor(string dataDir) => Path.Combine(dataDir, "admin-url.txt");

    /// <summary>True when a path is inside the given root (case-insensitive, separator-safe).</summary>
    public static bool IsInside(string candidate, string root)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(root)) return false;

        string left = Path.GetFullPath(candidate).TrimEnd('\\', '/');
        string right = Path.GetFullPath(root).TrimEnd('\\', '/');
        if (left.Equals(right, StringComparison.OrdinalIgnoreCase)) return true;

        return left.StartsWith(right + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
