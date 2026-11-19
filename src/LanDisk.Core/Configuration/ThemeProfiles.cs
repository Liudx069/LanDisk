namespace LanDisk.Core.Configuration;

/// <summary>Which surface a profile is meant for. The same named theme can look different on a phone.</summary>
public enum ThemeSurface
{
    Windows,
    Mobile
}

/// <summary>
/// A complete appearance, as data: every semantic role the UI draws with, resolved to a hex colour.
///
/// <para><b>Why roles and not colours in page code.</b> A page that writes <c>#FF111821</c> directly cannot
/// be re-themed, cannot be validated for contrast, and cannot be rendered on the phone. Pages name a role
/// ("this is a card surface", "this is secondary text"); the profile decides what that means, and the same
/// profile is what the iPhone receives as CSS variables.</para>
///
/// <para><b>No arbitrary CSS or JS.</b> A profile is twenty-odd colour strings with fixed names. There is
/// no way to express a rule, a selector or a script in it, which is what keeps "the operator can set the
/// phone's look" from becoming "the operator can inject anything into the phone".</para>
/// </summary>
public sealed class ThemeProfile
{
    public string Id { get; init; } = "obsidian";
    public string Name { get; init; } = "LanDisk Obsidian";

    /// <summary><c>dark</c>, <c>light</c>, or <c>system</c> to follow the device.</summary>
    public string Appearance { get; init; } = "dark";

    /// <summary>Role name → #AARRGGBB. Roles are fixed by <see cref="ThemeRoles"/>.</summary>
    public IReadOnlyDictionary<string, string> Roles { get; init; } = ThemeRoles.Empty;

    public bool IsSystem => string.Equals(Appearance, "system", StringComparison.OrdinalIgnoreCase);
    public bool IsLight => string.Equals(Appearance, "light", StringComparison.OrdinalIgnoreCase);

    public string Role(string name) => Roles.TryGetValue(name, out string? value)
        ? value
        : throw new KeyNotFoundException($"主题 {Id} 缺少角色 {name}");

    public ThemeProfile WithId(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Appearance = Appearance,
        Roles = Roles
    };
}

/// <summary>The role vocabulary. Adding a role means adding it here, to every built-in profile, and to the
/// XAML brush dictionary — there is no way to draw with an unregistered role.</summary>
public static class ThemeRoles
{
    public static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    /// <summary>Roles a profile must define. Order is the order they are written to the phone.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        "window", "navigation", "surface", "surfaceSecondary", "surfaceElevated", "overlay",
        "textPrimary", "textSecondary", "textTertiary", "textDisabled",
        "border", "borderStrong",
        "accent", "accentHover", "accentPressed",
        "control", "controlHover", "controlPressed", "selection",
        "success", "warning", "danger", "info"
    ];

    /// <summary>The roles that carry text and therefore have to pass contrast against their backdrop.</summary>
    public static IReadOnlyList<(string Foreground, string Background, string Where)> ContrastPairs { get; } =
    [
        ("textPrimary", "surface", "textPrimary/surface"),
        ("textPrimary", "window", "textPrimary/window"),
        ("textPrimary", "surfaceElevated", "textPrimary/surfaceElevated"),
        ("textPrimary", "navigation", "textPrimary/navigation"),
        ("textPrimary", "overlay", "textPrimary/overlay"),
        ("textSecondary", "surface", "textSecondary/surface"),
        ("textSecondary", "window", "textSecondary/window"),
        ("textSecondary", "surfaceElevated", "textSecondary/surfaceElevated"),
        ("textSecondary", "navigation", "textSecondary/navigation"),
        ("textTertiary", "surface", "textTertiary/surface"),
        ("accent", "surface", "accent/surface"),
        ("accent", "window", "accent/window"),
        ("success", "surface", "success/surface"),
        ("warning", "surface", "warning/surface"),
        ("danger", "surface", "danger/surface"),
        ("info", "surface", "info/surface")
    ];
}

/// <summary>The result of checking a profile for readability. Used both when saving a custom theme and by
/// the automated theme gates.</summary>
public sealed record ThemeValidation(bool Ok, double WorstBody, string WorstBodyWhere, IReadOnlyList<string> Problems)
{
    public const double BodyMinimum = 4.5;
    public const double LargeMinimum = 3.0;

    public string Summary => Ok
        ? $"对比度通过：最低正文对比度 {WorstBody:0.0}:1（门槛 {BodyMinimum}）"
        : string.Join("；", Problems);
}

/// <summary>
/// The built-in themes plus the derivation and validation rules for custom ones.
///
/// <para><b>Why derivation exists.</b> An operator choosing "accent + background + surface" must not have to
/// invent hover, pressed, border and disabled colours — and if they did, the product would be full of
/// unreadable combinations. Everything not supplied is derived from what was, with a contrast check at the
/// end that refuses a combination that cannot be read.</para>
/// </summary>
public static class ThemeCatalog
{
    private static readonly string[] DarkCore =
    [
        "window", "navigation", "surface", "surfaceSecondary", "surfaceElevated", "overlay",
        "textPrimary", "textSecondary", "textTertiary", "textDisabled",
        "border", "borderStrong", "control", "controlHover", "controlPressed", "selection"
    ];

    private static readonly Dictionary<string, string> Obsidian = new()
    {
        ["window"] = "#FF0B0F14",
        ["navigation"] = "#FF0E141C",
        ["surface"] = "#FF111821",
        ["surfaceSecondary"] = "#FF16202B",
        ["surfaceElevated"] = "#FF182432",
        ["overlay"] = "#FF1B2735",
        ["textPrimary"] = "#FFF5F7FA",
        ["textSecondary"] = "#FFAAB6C5",
        ["textTertiary"] = "#FF8894A3",
        ["textDisabled"] = "#FF5E6A78",
        ["border"] = "#FF233041",
        ["borderStrong"] = "#FF33455C",
        ["accent"] = "#FF45C8FF",
        ["accentHover"] = "#FF69D4FF",
        ["accentPressed"] = "#FF2FB0E6",
        ["control"] = "#FF1B2634",
        ["controlHover"] = "#FF22303F",
        ["controlPressed"] = "#FF17212D",
        ["selection"] = "#FF1E3A52",
        ["success"] = "#FF47D18C",
        ["warning"] = "#FFFFBE55",
        ["danger"] = "#FFFF6677",
        ["info"] = "#FF6FB4FF"
    };

    private static readonly Dictionary<string, string> Arctic = new()
    {
        ["window"] = "#FFF4F7FB",
        ["navigation"] = "#FFE9EFF6",
        ["surface"] = "#FFFFFFFF",
        ["surfaceSecondary"] = "#FFE8EEF5",
        ["surfaceElevated"] = "#FFF8FAFC",
        ["overlay"] = "#FFFFFFFF",
        ["textPrimary"] = "#FF17202A",
        ["textSecondary"] = "#FF5B6878",
        ["textTertiary"] = "#FF5F6B7A",
        ["textDisabled"] = "#FF9AA5B1",
        ["border"] = "#FFD3DCE6",
        ["borderStrong"] = "#FFB4C0CE",
        ["accent"] = "#FF0A74C4",
        ["accentHover"] = "#FF0D86E0",
        ["accentPressed"] = "#FF08619F",
        ["control"] = "#FFFFFFFF",
        ["controlHover"] = "#FFEDF2F8",
        ["controlPressed"] = "#FFE1E8F1",
        ["selection"] = "#FFD6E7F7",
        ["success"] = "#FF0F7A45",
        ["warning"] = "#FF9A5B00",
        ["danger"] = "#FFC01F2E",
        ["info"] = "#FF1D5FD8"
    };

    private static readonly Dictionary<string, string> Graphite = new()
    {
        ["window"] = "#FF14161A",
        ["navigation"] = "#FF181B20",
        ["surface"] = "#FF1C1F25",
        ["surfaceSecondary"] = "#FF22262D",
        ["surfaceElevated"] = "#FF262A32",
        ["overlay"] = "#FF2A2F38",
        ["textPrimary"] = "#FFF2F3F5",
        ["textSecondary"] = "#FFB3B8BF",
        ["textTertiary"] = "#FF9198A1",
        ["textDisabled"] = "#FF6A7078",
        ["border"] = "#FF30353D",
        ["borderStrong"] = "#FF454C56",
        ["accent"] = "#FF8AB4FF",
        ["accentHover"] = "#FFA3C4FF",
        ["accentPressed"] = "#FF6F9BEA",
        ["control"] = "#FF262A32",
        ["controlHover"] = "#FF2E333C",
        ["controlPressed"] = "#FF20242B",
        ["selection"] = "#FF35404F",
        ["success"] = "#FF6BD6A0",
        ["warning"] = "#FFFFC978",
        ["danger"] = "#FFFF8A94",
        ["info"] = "#FF9CC4FF"
    };

    private static readonly Dictionary<string, string> Midnight = new()
    {
        ["window"] = "#FF070B18",
        ["navigation"] = "#FF0A1022",
        ["surface"] = "#FF0D1428",
        ["surfaceSecondary"] = "#FF121B36",
        ["surfaceElevated"] = "#FF152043",
        ["overlay"] = "#FF1A2750",
        ["textPrimary"] = "#FFF0F4FF",
        ["textSecondary"] = "#FFA9B6DA",
        ["textTertiary"] = "#FF8894BC",
        ["textDisabled"] = "#FF5D6788",
        ["border"] = "#FF1F2C52",
        ["borderStrong"] = "#FF334379",
        ["accent"] = "#FF7C9CFF",
        ["accentHover"] = "#FF9BB2FF",
        ["accentPressed"] = "#FF6285F0",
        ["control"] = "#FF16203D",
        ["controlHover"] = "#FF1D2A4E",
        ["controlPressed"] = "#FF121A31",
        ["selection"] = "#FF26386B",
        ["success"] = "#FF57D9A3",
        ["warning"] = "#FFFFCB6B",
        ["danger"] = "#FFFF7E93",
        ["info"] = "#FF8FB4FF"
    };

    /// <summary>Windows profiles: an explicit appearance plus the two adaptations of the base palette.</summary>
    public static IReadOnlyList<ThemeProfile> Windows { get; } =
    [
        Make("follow-system", "跟随系统", "system", Obsidian, Arctic),
        Make("obsidian", "LanDisk Obsidian", "dark", Obsidian),
        Make("graphite", "Graphite", "dark", Graphite),
        Make("midnight", "Midnight", "dark", Midnight),
        Make("arctic", "Arctic", "light", Arctic)
    ];

    /// <summary>Mobile profiles: the same names, plus an explicit "follow the iPhone" choice.</summary>
    public static IReadOnlyList<ThemeProfile> Mobile { get; } =
    [
        Make("follow-iphone-system", "跟随 iPhone 系统", "system", Obsidian, Arctic),
        Make("obsidian", "LanDisk Obsidian", "dark", Obsidian),
        Make("graphite", "Graphite", "dark", Graphite),
        Make("midnight", "Midnight", "dark", Midnight),
        Make("arctic", "Arctic", "light", Arctic)
    ];

    public static ThemeProfile? Find(string? id, ThemeSurface surface = ThemeSurface.Windows)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        IReadOnlyList<ThemeProfile> source = surface == ThemeSurface.Mobile ? Mobile : Windows;
        return source.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Resolves a profile id for the appearance actually in force. A "system" profile is the one place the
    /// two halves of a theme are joined: the device decides light or dark, the profile decides what that
    /// looks like.
    /// </summary>
    public static ThemeProfile Resolve(string? id, bool dark, ThemeSurface surface = ThemeSurface.Windows)
    {
        ThemeProfile? profile = Find(id, surface);
        if (profile is null || profile.IsSystem)
        {
            return surface == ThemeSurface.Mobile
                ? Make(dark ? "obsidian" : "arctic", dark ? "LanDisk Obsidian" : "Arctic", dark ? "dark" : "light", dark ? Obsidian : Arctic)
                : Make(dark ? "obsidian" : "arctic", dark ? "LanDisk Obsidian" : "Arctic", dark ? "dark" : "light", dark ? Obsidian : Arctic);
        }

        return profile;
    }

    /// <summary>Default profile id for a surface, used for a fresh installation.</summary>
    public static string DefaultId(ThemeSurface surface)
        => surface == ThemeSurface.Mobile ? "follow-iphone-system" : "follow-system";

    /// <summary>
    /// Builds a custom profile from the minimum an operator should have to choose, deriving the rest.
    ///
    /// <para>Accent plus a background and a surface is enough: hover and pressed states are the accent moved
    /// towards white and black, borders are the surface lifted towards the text colour, and the text colours
    /// are chosen for the background's own luminance. The result is then validated like any other profile, so
    /// an unreadable combination is refused rather than stored.</para>
    /// </summary>
    public static ThemeProfile Custom(string? accent, string? background, string? surface, string? name = null)
    {
        string bg = Normalize(background, "#FF0B0F14");
        string sf = Normalize(surface, Lift(bg, 0.06));
        string ac = Normalize(accent, "#FF45C8FF");
        bool light = RelativeLuminance(bg) > 0.35;

        var roles = new Dictionary<string, string>
        {
            ["window"] = bg,
            ["navigation"] = Mix(bg, sf, 0.5),
            ["surface"] = sf,
            ["surfaceSecondary"] = Mix(sf, bg, 0.35),
            ["surfaceElevated"] = Lift(sf, 0.05),
            ["overlay"] = Lift(sf, 0.08),
            ["textPrimary"] = light ? "#FF11161C" : "#FFF5F7FA",
            ["textSecondary"] = light ? "#FF45505E" : "#FFB6C2D0",
            ["textTertiary"] = light ? "#FF5C6875" : "#FF97A3B1",
            ["textDisabled"] = light ? "#FF8C97A3" : "#FF6B7683",
            ["border"] = Mix(sf, light ? "#FF11161C" : "#FFF5F7FA", 0.18),
            ["borderStrong"] = Mix(sf, light ? "#FF11161C" : "#FFF5F7FA", 0.32),
            ["accent"] = ac,
            ["accentHover"] = Shift(ac, light ? -0.12 : 0.14),
            ["accentPressed"] = Shift(ac, light ? -0.24 : -0.14),
            ["control"] = Mix(sf, bg, 0.25),
            ["controlHover"] = Mix(sf, ac, 0.12),
            ["controlPressed"] = Mix(sf, bg, 0.4),
            ["selection"] = Mix(sf, ac, 0.3),
            ["success"] = light ? "#FF0F7A45" : "#FF47D18C",
            ["warning"] = light ? "#FF9A5B00" : "#FFFFBE55",
            ["danger"] = light ? "#FFC01F2E" : "#FFFF6677",
            ["info"] = light ? "#FF1D5FD8" : "#FF6FB4FF"
        };

        return new ThemeProfile
        {
            Id = "custom",
            Name = string.IsNullOrWhiteSpace(name) ? "自定义" : name!.Trim(),
            Appearance = light ? "light" : "dark",
            Roles = roles
        };
    }

    /// <summary>Checks every text role against the surfaces it is drawn on. A profile that fails is not
    /// stored, and the operator is told which pair and by how much.</summary>
    public static ThemeValidation Validate(ThemeProfile profile)
    {
        var problems = new List<string>();
        var missing = ThemeRoles.All.Where(role => !profile.Roles.ContainsKey(role)).ToList();
        foreach (string role in missing) problems.Add("缺少角色 " + role);

        double worst = double.MaxValue;
        string worstWhere = string.Empty;
        if (missing.Count == 0)
        {
            foreach ((string foreground, string background, string where) in ThemeRoles.ContrastPairs)
            {
                double ratio = ThemePalette.Contrast(profile.Role(foreground), profile.Role(background));
                if (ratio < worst) { worst = ratio; worstWhere = where; }

                // Body text is held to 4.5:1. The status colours are allowed the large-text threshold
                // because they are used for badges and bars, never for paragraphs.
                double minimum = foreground is "success" or "warning" or "danger" or "info" or "accent"
                    ? ThemeValidation.LargeMinimum
                    : ThemeValidation.BodyMinimum;
                if (ratio < minimum)
                {
                    problems.Add($"{where} 对比度 {ratio:0.0}:1 低于 {minimum}:1");
                }
            }
        }

        if (worst == double.MaxValue) worst = 0;
        return new ThemeValidation(problems.Count == 0, worst, worstWhere, problems);
    }

    // ---- colour helpers (kept here so a custom theme cannot introduce a colour library dependency) ----

    private static ThemeProfile Make(string id, string name, string appearance, params Dictionary<string, string>[] sources)
    {
        var roles = new Dictionary<string, string>();
        foreach (Dictionary<string, string> source in sources)
        {
            foreach ((string key, string value) in source) roles[key] = value;
        }
        foreach (string role in ThemeRoles.All)
        {
            if (!roles.ContainsKey(role)) throw new InvalidOperationException($"内置主题 {id} 缺少角色 {role}");
        }
        return new ThemeProfile { Id = id, Name = name, Appearance = appearance, Roles = roles };
    }

    private static string Normalize(string? hex, string fallback)
        => string.IsNullOrWhiteSpace(hex) ? fallback : hex.Trim();

    private static (double R, double G, double B) ToRgb(string hex)
    {
        (byte r, byte g, byte b) = ThemePalette.Rgb(hex);
        return (r / 255.0, g / 255.0, b / 255.0);
    }

    private static string ToHex(double r, double g, double b)
        => $"#FF{(byte)Math.Clamp(Math.Round(r * 255), 0, 255):X2}{(byte)Math.Clamp(Math.Round(g * 255), 0, 255):X2}{(byte)Math.Clamp(Math.Round(b * 255), 0, 255):X2}";

    private static string Mix(string first, string second, double amount)
    {
        (double r1, double g1, double b1) = ToRgb(first);
        (double r2, double g2, double b2) = ToRgb(second);
        return ToHex(r1 + (r2 - r1) * amount, g1 + (g2 - g1) * amount, b1 + (b2 - b1) * amount);
    }

    private static string Lift(string hex, double amount) => Mix(hex, "#FFFFFFFF", amount);

    private static string Shift(string hex, double amount)
        => amount >= 0 ? Mix(hex, "#FFFFFFFF", amount) : Mix(hex, "#FF000000", -amount);

    private static double RelativeLuminance(string hex)
    {
        (double r, double g, double b) = ToRgb(hex);
        static double Channel(double value) => value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
    }
}
