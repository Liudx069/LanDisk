using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace LanDisk.App;

/// <summary>
/// The application icon, drawn at runtime.
///
/// Drawing it instead of shipping a binary keeps the repository free of opaque assets and means the
/// tray, the window and the taskbar all use the same mark without a build step to generate an .ico.
/// </summary>
internal static class AppIcon
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>
    /// The same mark as a WPF image source, which is what the tray control takes. The GDI icon is
    /// converted rather than drawn twice, so the tray and the taskbar cannot drift apart.
    /// </summary>
    public static System.Windows.Media.ImageSource CreateImageSource(int size = 32)
    {
        using Icon icon = Create(size);
        return System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle,
            System.Windows.Int32Rect.Empty,
            System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
    }

    public static Icon Create(int size = 32)
    {
        using var bitmap = new Bitmap(size, size);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(Color.Transparent);

            var bounds = new Rectangle(0, 0, size - 1, size - 1);
            using var path = RoundedRectangle(bounds, Math.Max(2, size / 5));
            using var body = new LinearGradientBrush(bounds, Color.FromArgb(59, 130, 246), Color.FromArgb(29, 78, 216), 55f);
            graphics.FillPath(body, path);

            using var accent = new SolidBrush(Color.FromArgb(147, 197, 253));
            float barWidth = size * 0.10f;
            graphics.FillRectangle(accent, size * 0.56f, size * 0.30f, barWidth, size * 0.40f);
            graphics.FillRectangle(accent, size * 0.72f, size * 0.30f, barWidth, size * 0.40f);
            graphics.FillRectangle(accent, size * 0.56f, size * 0.30f, size * 0.26f, size * 0.08f);

            using var font = new Font("Segoe UI", size * 0.46f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var text = new SolidBrush(Color.White);
            var format = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center
            };
            graphics.DrawString("L", font, text, new RectangleF(size * 0.10f, 0, size * 0.5f, size), format);
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using Icon temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        int diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
