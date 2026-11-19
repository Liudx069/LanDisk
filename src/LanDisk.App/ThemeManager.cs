using System.Windows;
using System.Windows.Media;
using LanDisk.Core.Configuration;
using Wpf.Ui.Appearance;

namespace LanDisk.App;

/// <summary>What the user picked. Stored by name in <c>shell.json</c>.</summary>
internal enum ShellTheme
{
    System,
    Light,
    Dark
}

/// <summary>
/// The application appearance, in one place.
///
/// <para><b>What was wrong.</b> The previous build switched only the WPF-UI theme and left the product's
/// own brushes (<c>LanDisk.Window</c>, <c>Surface</c>, <c>TextPrimary</c>, …) at their dark values, so
/// "浅色" produced a light frame with dark panels — a half-implemented theme. The two dictionaries are now
/// switched together.</para>
///
/// <para><b>How it updates live.</b> Pages read their brushes once, through
/// <c>Application.Current.Resources["LanDisk.X"]</c>, and keep the instance. Rather than rebuilding every
/// page on a theme change (which loses scroll position and can race with a refresh), the palette mutates
/// the colours of those same <see cref="SolidColorBrush"/> instances: WPF repaints them, and every control
/// that already holds one follows immediately.</para>
/// </summary>
internal static class ThemeManager
{
    private static ShellTheme _current = ShellTheme.System;

    public static ShellTheme Current => _current;

    /// <summary>The appearance actually in force, after resolving <see cref="ShellTheme.System"/>.</summary>
    public static bool IsDarkNow => _current switch
    {
        ShellTheme.Light => false,
        ShellTheme.Dark => true,
        _ => SystemPrefersDark()
    };

    private static bool SystemPrefersDark()
    {
        try { return ApplicationThemeManager.GetSystemTheme() != SystemTheme.Light; }
        catch { return true; }
    }

    public static ShellTheme Parse(string? name) => name switch
    {
        "light" => ShellTheme.Light,
        "dark" => ShellTheme.Dark,
        _ => ShellTheme.System
    };

    public static string ToStorageName(ShellTheme theme) => theme switch
    {
        ShellTheme.Light => "light",
        ShellTheme.Dark => "dark",
        _ => "system"
    };

    public static string Describe(ShellTheme theme) => theme switch
    {
        ShellTheme.Light => "浅色",
        ShellTheme.Dark => "深色",
        _ => "跟随系统"
    };

    /// <summary>
    /// Switches both dictionaries and repaints. Safe to call before the window exists; the WPF-UI theme
    /// manager applies to the application resources, so controls created afterwards pick it up.
    /// </summary>
    public static void Apply(ShellTheme theme)
    {
        _current = theme;
        bool dark = IsDarkNow;

        // 1) WPF-UI's own templates (control chrome, focus visuals, scrollbars).
        try { ApplicationThemeManager.Apply(dark ? ApplicationTheme.Dark : ApplicationTheme.Light); }
        catch { /* older Windows builds have no system theme to read; the product palette still applies */ }

        // 2) The product palette, on the instances the pages already hold.
        ApplyPalette(dark);

        // 3) The window chrome and any surface that resolved a brush at build time.
        if (Application.Current is { } app)
        {
            foreach (Window window in app.Windows)
            {
                if (window is MainWindow shell) shell.OnThemeChanged();
            }
        }
    }

    /// <summary>
    /// Applies a named theme profile (see <see cref="ThemeCatalog"/>), which is what the theme picker in
    /// settings chooses. A <c>system</c> profile resolves through the OS appearance, so the shipped default
    /// still follows Windows; the named profiles are what the operator picks instead.
    /// </summary>
    public static ThemeProfile ApplyProfile(string? profileId)
    {
        _current = ShellTheme.System;
        ThemeProfile profile = ThemeCatalog.Resolve(profileId, SystemPrefersDark());

        try { ApplicationThemeManager.Apply(profile.IsLight ? ApplicationTheme.Light : ApplicationTheme.Dark); }
        catch { }

        ApplyProfileRoles(profile);

        if (Application.Current is { } app)
        {
            foreach (Window window in app.Windows)
            {
                if (window is MainWindow shell) shell.OnThemeChanged();
            }
        }

        return profile;
    }

    /// <summary>The appearance a profile asks for, without applying it (used by the settings preview).</summary>
    public static bool IsDarkFor(ThemeProfile profile)
        => profile.IsSystem ? SystemPrefersDark() : !profile.IsLight;

    internal static void ApplyProfileRoles(ThemeProfile profile)
    {
        if (Application.Current is not { } app) return;

        foreach ((string role, string paletteRole) in ThemePalette.ProfileRoleMap)
        {
            if (!profile.Roles.TryGetValue(role, out string? hex)) continue;
            (byte r, byte g, byte b) = ThemePalette.Rgb(hex);
            Color wanted = Color.FromRgb(r, g, b);

            string colorKey = ThemePalette.ColorKey(paletteRole);
            if (app.Resources[colorKey] is Color) app.Resources[colorKey] = wanted;

            string brushKey = ThemePalette.BrushKey(paletteRole);
            if (app.Resources[brushKey] is SolidColorBrush brush)
            {
                if (brush.IsFrozen) app.Resources[brushKey] = new SolidColorBrush(wanted);
                else brush.Color = wanted;
            }
        }

        // The profile also carries values for roles that only exist as tokens (no legacy palette entry);
        // they are used by the web surface and by the phone, and are exposed here for completeness.
        app.Resources["LanDisk.ActiveProfile"] = profile;
    }

    /// <summary>The id of the profile currently in force (readable by the theme restart tests).</summary>
    public static string? ActiveProfileId
        => (Application.Current?.Resources["LanDisk.ActiveProfile"] as ThemeProfile)?.Id;

    /// <summary>
    /// Applies the persisted appearance and returns the resolved profile. Called once at startup, before the
    /// window exists, so the very first layout uses the stored theme rather than the default and a later
    /// switch.
    ///
    /// <para><b>Priority.</b> A 1.7 profile id wins; a legacy 1.6 <c>Theme</c> value is migrated; anything
    /// unrecognised falls back to "follow system". A saved custom profile is applied role-for-role.</para>
    /// </summary>
    public static ThemeProfile ApplyFromPreferences(string dataRoot)
    {
        ShellPreferences preferences = ShellPreferences.Load(dataRoot);

        // The precedence (custom → named → migrated legacy → follow system) lives in Core so it is tested
        // without a WPF application; this method only applies the answer.
        ThemeProfile resolved = ThemeStartup.Resolve(preferences, SystemPrefersDark());

        if (string.Equals(resolved.Id, "custom", StringComparison.OrdinalIgnoreCase)) return ApplyCustom(resolved);

        _current = ShellTheme.System;
        return ApplyProfile(resolved.Id);
    }

    /// <summary>Applies a custom profile (its own roles + the WPF-UI appearance it implies).</summary>
    public static ThemeProfile ApplyCustom(ThemeProfile custom)
    {
        try { ApplicationThemeManager.Apply(custom.IsLight ? ApplicationTheme.Light : ApplicationTheme.Dark); }
        catch { }

        ApplyProfileRoles(custom);

        if (Application.Current is { } app)
        {
            foreach (Window window in app.Windows)
            {
                if (window is MainWindow shell) shell.OnThemeChanged();
            }
        }

        return custom;
    }

    public static void Save(string dataRoot, ShellTheme theme)
    {
        ShellPreferences preferences = ShellPreferences.Load(dataRoot);
        preferences.Theme = ToStorageName(theme);
        preferences.Save(dataRoot);
    }

    private static void ApplyPalette(bool dark)
    {
        if (Application.Current is not { } app) return;

        ShellThemeKind kind = dark ? ShellThemeKind.Dark : ShellThemeKind.Light;

        foreach (PaletteEntry entry in ThemePalette.Entries)
        {
            (byte r, byte g, byte b) = ThemePalette.Rgb(ThemePalette.Hex(entry.Role, kind));
            Color wanted = Color.FromRgb(r, g, b);

            // The Color entries are what the XAML brushes were built from; update both so a page that
            // resolves either form agrees.
            string colorKey = ThemePalette.ColorKey(entry.Role);
            if (app.Resources[colorKey] is Color)
            {
                app.Resources[colorKey] = wanted;
            }

            string brushKey = ThemePalette.BrushKey(entry.Role);
            if (app.Resources[brushKey] is SolidColorBrush brush)
            {
                if (brush.IsFrozen) app.Resources[brushKey] = new SolidColorBrush(wanted);
                else brush.Color = wanted;
            }
        }
    }
}
