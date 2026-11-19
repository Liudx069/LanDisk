// WinForms is still enabled for the uninstall dialog, which puts System.Drawing's Brush/Color/FontFamily/
// Point into scope project-wide. These aliases pin this file to the WPF types it actually draws with.
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using System.Windows.Media;

namespace LanDisk.Setup.Visual;

/// <summary>
/// The installer's palette, resolved once as frozen brushes.
///
/// <para>Colours are the product's own dark surface, a step deeper than the app window so the installer reads
/// as a first-run surface rather than a dialog. Everything is a semantic role; nothing here is a one-off hex
/// picked at a call site, and nothing is loaded from a file — the whole visual is built from these values plus
/// geometry.</para>
/// </summary>
public static class SetupVisualTheme
{
    // Surface
    public static readonly Color Window = Hex("#0B0F14");
    public static readonly Color WindowElevated = Hex("#111823");
    public static readonly Color Frame = Hex("#141C26");
    public static readonly Color FrameEdge = Hex("#28323F");
    public static readonly Color Cavity = Hex("#0A0E13");
    public static readonly Color Rail = Hex("#1B2530");
    public static readonly Color RailHole = Hex("#0D131A");
    public static readonly Color Panel = Hex("#1A2330");
    public static readonly Color PanelEdge = Hex("#2C3846");
    public static readonly Color Bay = Hex("#222D3B");
    public static readonly Color Shadow = Color.FromArgb(90, 0, 0, 0);

    // Text
    public static readonly Color TextPrimary = Hex("#E8EDF4");
    public static readonly Color TextSecondary = Hex("#9FB0C2");
    public static readonly Color TextTertiary = Hex("#64748B");

    // Signal
    public static readonly Color Accent = Hex("#3BA9FF");
    public static readonly Color Success = Hex("#4ADE80");
    public static readonly Color Warning = Hex("#FBBF24");
    public static readonly Color Error = Hex("#F87171");
    public static readonly Color LedOff = Hex("#2A3542");

    public static readonly Brush WindowBrush = Frozen(Window);
    public static readonly Brush WindowElevatedBrush = Frozen(WindowElevated);
    public static readonly Brush FrameBrush = Frozen(Frame);
    public static readonly Brush FrameEdgeBrush = Frozen(FrameEdge);
    public static readonly Brush CavityBrush = Frozen(Cavity);
    public static readonly Brush RailBrush = Frozen(Rail);
    public static readonly Brush RailHoleBrush = Frozen(RailHole);
    public static readonly Brush PanelBrush = Frozen(Panel);
    public static readonly Brush PanelEdgeBrush = Frozen(PanelEdge);
    public static readonly Brush BayBrush = Frozen(Bay);
    public static readonly Brush ShadowBrush = Frozen(Shadow);

    public static readonly Brush TextPrimaryBrush = Frozen(TextPrimary);
    public static readonly Brush TextSecondaryBrush = Frozen(TextSecondary);
    public static readonly Brush TextTertiaryBrush = Frozen(TextTertiary);

    public static readonly Brush AccentBrush = Frozen(Accent);
    public static readonly Brush SuccessBrush = Frozen(Success);
    public static readonly Brush WarningBrush = Frozen(Warning);
    public static readonly Brush ErrorBrush = Frozen(Error);
    public static readonly Brush LedOffBrush = Frozen(LedOff);

    public static readonly Brush LedBlueBrush = Frozen(Hex("#5CC8FF"));
    public static readonly Brush LedGreenBrush = Frozen(Hex("#5BE38A"));

    /// <summary>Tiled perforation for vent panels: a small geometry, repeated. Vector, not a texture file.</summary>
    public static readonly Brush VentBrush = BuildVentBrush();

    /// <summary>Tiled port cavity for the switch face.</summary>
    public static readonly Brush PortBrush = Frozen(Hex("#2A3644"));
    public static readonly Brush PortEdgeBrush = Frozen(Hex("#3B4A5A"));

    public static readonly FontFamily FontStack = new("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");

    private static Brush BuildVentBrush()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(
            Frozen(Hex("#101822")),
            null,
            new RectangleGeometry(new Rect(0, 0, 6, 6))));
        group.Children.Add(new GeometryDrawing(
            Frozen(Hex("#1E2937")),
            null,
            new EllipseGeometry(new Point(3, 3), 1.7, 1.7)));
        group.Freeze();

        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 6, 6),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None
        };
        brush.Freeze();
        return brush;
    }

    private static Color Hex(string value) => (Color)ColorConverter.ConvertFromString(value);

    private static Brush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }
}
