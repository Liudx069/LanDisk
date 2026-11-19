using Microsoft.Win32;

namespace LanDisk.Setup;

/// <summary>A shortcut the installer would create. Pure data, so the decision is testable.</summary>
public sealed record ShortcutPlan(string LinkPath, string TargetPath, string WorkingDirectory, string Description, string IconLocation);

/// <summary>
/// Everything the installer registers with Windows on the user's behalf: shortcuts, the entry in
/// "已安装的应用", and the startup value the product manages itself.
///
/// <para>All of it is per-user (HKCU and the user's own Start Menu), which is the same reason the
/// program files live under <c>%LOCALAPPDATA%</c>: an install that needs no administrator for its own
/// bookkeeping is an install that can be repaired and removed without one either.</para>
/// </summary>
public static class ShellIntegration
{
    public static IReadOnlyList<ShortcutPlan> ShortcutPlans(string installDir, bool desktop, bool startMenu, string dataDir)
    {
        installDir = Path.GetFullPath(installDir);
        string shell = SetupLayout.ShellPathFor(installDir);
        var plans = new List<ShortcutPlan>();

        if (startMenu)
        {
            plans.Add(new ShortcutPlan(
                Path.Combine(SetupLayout.StartMenuDir, "LanDisk.lnk"),
                shell, installDir, "把电脑硬盘安全地共享给同一局域网中的 iPhone", shell));
        }

        if (desktop)
        {
            plans.Add(new ShortcutPlan(
                Path.Combine(SetupLayout.DesktopDir, "LanDisk.lnk"),
                shell, installDir, "把电脑硬盘安全地共享给同一局域网中的 iPhone", shell));
        }

        _ = dataDir;
        return plans;
    }

    /// <summary>The registry values that make LanDisk appear in "已安装的应用".</summary>
    public static IReadOnlyDictionary<string, object> UninstallValues(string installDir, string dataDir, string version, long estimatedBytes)
    {
        installDir = Path.GetFullPath(installDir);
        string setupCopy = SetupLayout.SetupCopyPathFor(installDir);
        string shell = SetupLayout.ShellPathFor(installDir);

        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["DisplayName"] = "LanDisk",
            ["DisplayVersion"] = version,
            ["Publisher"] = SetupLayout.Publisher,
            ["InstallLocation"] = installDir,
            ["DisplayIcon"] = shell,
            // The uninstaller is the installer in uninstall mode; it is copied into the install
            // directory so the entry keeps working after the download is gone.
            ["UninstallString"] = $"\"{setupCopy}\" --uninstall",
            ["QuietUninstallString"] = $"\"{setupCopy}\" --uninstall --silent",
            ["ModifyPath"] = $"\"{setupCopy}\"",
            ["NoModify"] = 0,
            ["NoRepair"] = 0,
            ["EstimatedSize"] = (int)Math.Min(int.MaxValue, estimatedBytes / 1024),
            ["InstallDate"] = DateTime.Now.ToString("yyyyMMdd"),
            // Purely informational for the uninstaller, which is also what makes "keep my data" the
            // default an explicit, inspectable fact rather than an implicit one.
            ["LanDiskDataRoot"] = Path.GetFullPath(dataDir)
        };
    }

    public static void RegisterUninstall(IReadOnlyDictionary<string, object> values)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(SetupLayout.UninstallKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法写入卸载注册表项。");

        foreach ((string name, object value) in values) key.SetValue(name, value);
    }

    public static void UnregisterUninstall()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(SetupLayout.UninstallKeyPath, throwOnMissingSubKey: false); }
        catch { }
    }

    public static bool IsRegistered()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SetupLayout.UninstallKeyPath);
        return key is not null;
    }

    public static void RemoveAutostart()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SetupLayout.RunKeyPath, writable: true);
            key?.DeleteValue(SetupLayout.RunValueName, throwOnMissingValue: false);
        }
        catch { }
    }

    public static bool AutostartEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SetupLayout.RunKeyPath);
            return key?.GetValue(SetupLayout.RunValueName) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Creates one shortcut through the Windows shell. Thin on purpose: the decision is above.</summary>
    public static bool CreateShortcut(ShortcutPlan plan, Action<string>? log = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(plan.LinkPath)!);
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                log?.Invoke("系统没有提供 WScript.Shell，跳过快捷方式：" + plan.LinkPath);
                return false;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(plan.LinkPath);
            link.TargetPath = plan.TargetPath;
            link.WorkingDirectory = plan.WorkingDirectory;
            link.Description = plan.Description;
            link.IconLocation = plan.IconLocation;
            link.Save();

            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke("创建快捷方式失败：" + ex.Message);
            return false;
        }
    }

    public static void RemoveShortcuts(string installDir)
    {
        foreach (ShortcutPlan plan in ShortcutPlans(installDir, desktop: true, startMenu: true, dataDir: string.Empty))
        {
            try { if (File.Exists(plan.LinkPath)) File.Delete(plan.LinkPath); }
            catch { }
        }

        try
        {
            string dir = SetupLayout.StartMenuDir;
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch { }
    }

    /// <summary>Removes the uninstaller copy and the program directory's own files.</summary>
    public static void RemoveProgramFiles(string installDir, Action<string>? log = null)
    {
        string root = Path.GetFullPath(installDir);
        if (!Directory.Exists(root)) return;

        // Only ever inside the installation directory: the data root and, above all, the shares a
        // user pointed LanDisk at are somewhere else entirely and must not be reachable from here.
        if (SetupLayout.IsInside(root, SetupLayout.DefaultDataDir))
            throw new InvalidOperationException("安装目录与数据目录重叠，拒绝删除。");

        try { Directory.Delete(root, recursive: true); }
        catch (Exception ex) { log?.Invoke("删除程序文件失败：" + ex.Message); }
    }

    /// <summary>
    /// True when a directory looks like a LanDisk data root: it holds at least one of the files or
    /// directories LanDisk itself creates there. This is the guard that makes "delete my data" safe to
    /// offer — a folder full of the user's own documents simply is not what this deletes.
    /// </summary>
    public static bool LooksLikeDataRoot(string dataDir)
    {
        try
        {
            if (!Directory.Exists(dataDir)) return false;

            foreach (string file in new[] { "settings.json", "shares.json", "devices.json", "install.json", "preferences.json" })
            {
                if (File.Exists(Path.Combine(dataDir, file))) return true;
            }

            foreach (string directory in new[] { "tls", "secrets", "logs", "thumbs", "updates" })
            {
                if (Directory.Exists(Path.Combine(dataDir, directory))) return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes the data root after an explicit opt-in. Refuses anything that is not recognisably a
    /// LanDisk data directory, refuses a directory that contains the installation, and never follows a
    /// share target: shares point at folders elsewhere on disk and are not reachable from here at all.
    /// </summary>
    public static bool RemoveUserData(string dataDir, Action<string>? log = null)
    {
        string root = Path.GetFullPath(dataDir);

        if (!root.Equals(Path.GetFullPath(SetupLayout.DefaultDataDir), StringComparison.OrdinalIgnoreCase)
            && !LooksLikeDataRoot(root))
        {
            log?.Invoke("该目录不像是 LanDisk 的数据目录，拒绝删除：" + root);
            return false;
        }

        if (SetupLayout.IsInside(SetupLayout.DefaultInstallDir, root))
        {
            log?.Invoke("数据目录里包含安装目录，拒绝删除：" + root);
            return false;
        }

        if (!Directory.Exists(root)) return true;

        try
        {
            Directory.Delete(root, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke("删除用户数据失败：" + ex.Message);
            return false;
        }
    }
}
