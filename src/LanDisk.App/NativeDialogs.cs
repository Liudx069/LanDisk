using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace LanDisk.App;

/// <summary>
/// The Windows dialogs the native UI uses.
///
/// <para>Folder picking is <see cref="OpenFolderDialog"/> and file picking is
/// <see cref="OpenFileDialog"/> — both are the dialogs Windows itself shows, so a user sees the same
/// places, the same favourites and the same keyboard behaviour as everywhere else. Nothing here
/// browses the file system itself: a picked path is handed to the same validation the share creation
/// has always used.</para>
/// </summary>
internal static class NativeDialogs
{
    public static string? PickFolder(string title, string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };

        if (!string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    /// <summary>Only the signed update packages are offered, so the wrong file cannot be picked by accident.</summary>
    public static string? PickUpdatePackage(string? initialDirectory = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 LanDisk 更新包",
            Filter = "LanDisk 更新包 (*.lupd)|*.lupd|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (!string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public static bool Confirm(string title, string message, string confirmText = "确定")
        => MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK
           && confirmText.Length > 0;

    public static void Info(string title, string message)
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>
    /// Opens a folder in Explorer — and only a folder.
    ///
    /// <para><b>Why this is a helper rather than two inline calls.</b> Handing a string to the shell
    /// (<c>UseShellExecute = true</c>) is also how a URL reaches the default browser, so the product's rule is
    /// that the shell is used from exactly one place for a URL (the labelled recovery page) and from this one
    /// place for folders. Anything that even looks like a URL is refused here, which means a future edit
    /// cannot turn "open the log folder" into a second, unlabelled way to a browser.</para>
    /// </summary>
    public static void OpenFolder(string path, Action<string>? report = null)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains("://", StringComparison.Ordinal) || !Directory.Exists(path))
        {
            string message = "这个位置不是本机目录，未打开：" + (path ?? "（空）");
            if (report is null) Info("无法打开", message); else report(message);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            if (report is null) Info("无法打开", ex.Message); else report("打开目录失败：" + ex.Message);
        }
    }

    /// <summary>
    /// <summary>
    /// Runs a local executable with one explicit elevation, for an operation the user asked for by pressing a
    /// button (currently only "repair the firewall rules").
    ///
    /// <para><b>Why it lives here.</b> The shell has exactly one <c>UseShellExecute</c> call site, and this is
    /// the second thing that legitimately needs one. Putting it in the caller would mean the invariant
    /// "nothing else in the shell hands a string to the shell" stops being checkable, so elevation goes
    /// through the same documented helper and is refused for anything URL-shaped, exactly like folder
    /// opening: it requires a real file path and rejects "://", so it cannot become a way to a browser.</para>
    /// </summary>
    public static bool RunElevated(string executablePath, IReadOnlyList<string> arguments, out string message)
    {
        message = string.Empty;
        if (string.IsNullOrWhiteSpace(executablePath)
            || executablePath.Contains("://", StringComparison.Ordinal)
            || !File.Exists(executablePath))
        {
            message = "要启动的程序不在本机：" + (executablePath ?? "（空）");
            return false;
        }

        try
        {
            var info = new System.Diagnostics.ProcessStartInfo(executablePath)
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            foreach (string argument in arguments) info.ArgumentList.Add(argument);

            using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(info);
            if (process is null)
            {
                message = "无法启动提权进程。";
                return false;
            }

            process.WaitForExit();
            if (process.ExitCode == 0) return true;
            message = "操作未完成（退出码 " + process.ExitCode + "）。";
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            message = "已取消提权：没有任何更改。";
            return false;
        }
        catch (Exception ex)
        {
            message = "启动失败：" + ex.Message;
            return false;
        }
    }

    /// Renders a QR code as an image. The code is produced locally from the pairing link; nothing is
    /// sent anywhere to draw it, and the link itself is only shown to the user when they ask for the
    /// raw value.
    /// </summary>
    public static BitmapImage? Qr(string text, int pixelsPerModule = 10)
    {
        try
        {
            using var generator = new QRCoder.QRCodeGenerator();
            QRCoder.QRCodeData data = generator.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
            byte[] png = new QRCoder.PngByteQRCode(data).GetGraphic(pixelsPerModule);

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(png);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}
