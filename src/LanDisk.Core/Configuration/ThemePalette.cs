namespace LanDisk.Core.Configuration;

/// <summary>The two appearances the product ships. "Follow the system" resolves to one of them at runtime.</summary>
public enum ShellThemeKind
{
    Dark,
    Light
}

/// <summary>One palette entry: the same role expressed for both appearances.</summary>
public sealed record PaletteEntry(string Role, string DarkHex, string LightHex);

/// <summary>
/// The product palette, as data.
///
/// <para><b>Why it lives in Core.</b> The window needs <c>System.Windows.Media.Color</c>, which is WPF, so
/// the palette itself used to be unreachable from a test — and an unchecked theme is how a "浅色" button
/// ends up switching only half the colours. Keeping the values as hex strings means the palette can be
/// asserted on directly: every role present in both appearances, the two appearances actually different,
/// and every text colour readable on the surface it is drawn on.</para>
/// </summary>
public static class ThemePalette
{
    /// <summary>Dark is the product's own Fluent Obsidian palette; light is the same hues at usable contrast.</summary>
    public static IReadOnlyList<PaletteEntry> Entries { get; } =
    [
        new("Window",        "#FF0B0F14", "#FFF4F7FB"),
        new("Navigation",    "#FF0E141C", "#FFE9EFF6"),
        new("Surface",       "#FF111821", "#FFFFFFFF"),
        new("Secondary",     "#FF16202B", "#FFE8EEF5"),
        new("Elevated",      "#FF182432", "#FFF8FAFC"),
        new("Overlay",       "#FF1B2735", "#FFFFFFFF"),
        new("TextPrimary",   "#FFF5F7FA", "#FF17202A"),
        new("TextSecondary", "#FFAAB6C5", "#FF5B6878"),
        new("TextTertiary",  "#FF8894A3", "#FF5F6B7A"),
        new("TextDisabled",  "#FF5E6A78", "#FF9AA5B1"),
        new("Border",        "#FF233041", "#FFD3DCE6"),
        new("BorderStrong",  "#FF33455C", "#FFB4C0CE"),
        new("Control",       "#FF1B2634", "#FFFFFFFF"),
        new("ControlHover",  "#FF22303F", "#FFEDF2F8"),
        new("ControlPressed", "#FF17212D", "#FFE1E8F1"),
        new("Selection",     "#FF1E3A52", "#FFD6E7F7"),
        new("AccentCyan",    "#FF45C8FF", "#FF0A74C4"),
        new("AccentBlue",    "#FF5A8CFF", "#FF1D5FD8"),
        new("AccentHover",   "#FF69D4FF", "#FF0D86E0"),
        new("AccentPressed", "#FF2FB0E6", "#FF08619F"),
        new("Success",       "#FF47D18C", "#FF0F7A45"),
        new("Warning",       "#FFFFBE55", "#FF9A5B00"),
        new("Error",         "#FFFF6677", "#FFC01F2E"),
        new("Info",          "#FF6FB4FF", "#FF1D5FD8")
    ];

    /// <summary>
    /// Maps a <see cref="ThemeCatalog"/> profile role onto this palette's role names.
    ///
    /// <para>The palette predates the theme profiles and its names are baked into the XAML brush
    /// dictionary, so this is the one translation table between "what a theme profile calls a surface" and
    /// "what the window has always called it". Keeping it explicit means a new profile role that nobody
    /// mapped shows up as a missing brush in the theme tests rather than as a silently dark panel.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> ProfileRoleMap { get; } = new Dictionary<string, string>
    {
        ["window"] = "Window",
        ["navigation"] = "Navigation",
        ["surface"] = "Surface",
        ["surfaceSecondary"] = "Secondary",
        ["surfaceElevated"] = "Elevated",
        ["overlay"] = "Overlay",
        ["textPrimary"] = "TextPrimary",
        ["textSecondary"] = "TextSecondary",
        ["textTertiary"] = "TextTertiary",
        ["textDisabled"] = "TextDisabled",
        ["border"] = "Border",
        ["borderStrong"] = "BorderStrong",
        ["control"] = "Control",
        ["controlHover"] = "ControlHover",
        ["controlPressed"] = "ControlPressed",
        ["selection"] = "Selection",
        ["accent"] = "AccentCyan",
        ["accentHover"] = "AccentHover",
        ["accentPressed"] = "AccentPressed",
        ["success"] = "Success",
        ["warning"] = "Warning",
        ["danger"] = "Error",
        ["info"] = "Info"
    };

    /// <summary>The resource keys the XAML dictionary defines, derived from the roles above.</summary>
    public static string ColorKey(string role) => "LanDisk." + role + "Color";

    public static string BrushKey(string role) => "LanDisk." + role;

    public static string Hex(string role, ShellThemeKind kind)
    {
        PaletteEntry entry = Entries.FirstOrDefault(e => e.Role == role)
                             ?? throw new KeyNotFoundException("调色板里没有 " + role);
        return kind == ShellThemeKind.Dark ? entry.DarkHex : entry.LightHex;
    }

    public static (byte R, byte G, byte B) Rgb(string hex)
    {
        string value = hex.TrimStart('#');
        if (value.Length == 8) value = value[2..];   // AARRGGBB -> RRGGBB
        if (value.Length != 6) throw new FormatException("颜色值应为 #RRGGBB 或 #AARRGGBB：" + hex);
        return (Convert.ToByte(value[0..2], 16), Convert.ToByte(value[2..4], 16), Convert.ToByte(value[4..6], 16));
    }

    /// <summary>WCAG relative-luminance contrast ratio between two hex colours.</summary>
    public static double Contrast(string firstHex, string secondHex)
    {
        static double Luminance(string hex)
        {
            (byte r, byte g, byte b) = Rgb(hex);
            static double Channel(byte value)
            {
                double v = value / 255.0;
                return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
        }

        double a = Luminance(firstHex);
        double b2 = Luminance(secondHex);
        (double lighter, double darker) = a >= b2 ? (a, b2) : (b2, a);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>
    /// The worst contrast between any foreground role and the surface it is normally drawn on. This is the
    /// number that catches "white text on a white panel" before a screenshot does.
    /// </summary>
    public static (double Worst, string Where) WorstForegroundContrast(ShellThemeKind kind)
    {
        string surface = Hex("Surface", kind);
        string window = Hex("Window", kind);
        string elevated = Hex("Elevated", kind);

        var pairs = new (string Foreground, string Background, string Where)[]
        {
            ("TextPrimary", surface, "TextPrimary/Surface"),
            ("TextSecondary", surface, "TextSecondary/Surface"),
            ("TextPrimary", window, "TextPrimary/Window"),
            ("TextSecondary", window, "TextSecondary/Window"),
            ("TextPrimary", elevated, "TextPrimary/Elevated"),
            ("TextSecondary", elevated, "TextSecondary/Elevated"),
            ("Success", surface, "Success/Surface"),
            ("Warning", surface, "Warning/Surface"),
            ("Error", surface, "Error/Surface")
        };

        double worst = double.MaxValue;
        string where = string.Empty;
        foreach ((string foreground, string background, string label) in pairs)
        {
            double ratio = Contrast(Hex(foreground, kind), background);
            if (ratio < worst) { worst = ratio; where = label; }
        }

        return (worst, where);
    }
}
