using Microsoft.Win32;

using System.IO;

namespace LanDisk.App;

/// <summary>
/// The optional "start with Windows" toggle.
///
/// It is off unless the user turns it on, it only ever writes the per-user Run key (which needs no
/// administrator rights and is trivially reversible), and it never touches scheduled tasks,
/// services or machine-wide settings.
/// </summary>
internal static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LanDisk";

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public static string? RegisteredCommand()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>Registers the current executable, quoting the path so spaces are safe.</summary>
    public static bool Enable(out string? error)
    {
        error = null;
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                error = "无法确定 LanDisk.exe 的路径。";
                return false;
            }

            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                                    ?? throw new IOException("无法打开注册表启动项。");
            key.SetValue(ValueName, $"\"{exe}\" --startup", RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool Disable(out string? error)
    {
        error = null;
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            error = ex.Message;
            return false;
        }
    }
}
