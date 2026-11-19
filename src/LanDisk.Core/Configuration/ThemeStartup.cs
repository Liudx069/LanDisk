namespace LanDisk.Core.Configuration;

/// <summary>
/// Resolves the persisted appearance into the profile that should be in force at startup.
///
/// <para><b>Why this is a pure function in Core.</b> The order of precedence — a saved custom profile, then a
/// named profile, then a migrated 1.6 <c>Theme</c> value, then "follow system" — is the part that can silently
/// regress (1.7.0 shipped a version that read only the legacy field, so a named theme was forgotten on the
/// next start). Keeping the decision here means it is tested against the real <see cref="ShellPreferences"/>
/// file without needing a WPF application, and the window only has to <i>apply</i> the answer.</para>
/// </summary>
public static class ThemeStartup
{
    /// <summary>The id a legacy <c>Theme</c> value migrates to.</summary>
    public static string MigrateLegacy(string? legacyTheme) => legacyTheme switch
    {
        "dark" => "obsidian",
        "light" => "arctic",
        _ => "follow-system"
    };

    /// <summary>The profile to start in, honouring the documented precedence.</summary>
    public static ThemeProfile Resolve(ShellPreferences preferences, bool systemPrefersDark)
    {
        // 1) A custom profile saved by this build: its exact roles.
        if (string.Equals(preferences.ThemeProfile, "custom", StringComparison.OrdinalIgnoreCase)
            && preferences.CustomTheme is { } custom
            && custom.Roles.Count > 0)
        {
            return custom.WithId("custom", custom.Name);
        }

        // 2) A known named profile.
        if (!string.IsNullOrWhiteSpace(preferences.ThemeProfile)
            && ThemeCatalog.Find(preferences.ThemeProfile) is { } named)
        {
            return ThemeCatalog.Resolve(named.Id, systemPrefersDark);
        }

        // 3) A legacy 1.6.x shell.json (only the old Theme field is present).
        if (preferences.HasKnownTheme)
        {
            return ThemeCatalog.Resolve(MigrateLegacy(preferences.Theme), systemPrefersDark);
        }

        // 4) Anything unrecognised: follow the system.
        return ThemeCatalog.Resolve("follow-system", systemPrefersDark);
    }

    /// <summary>
    /// The profile the phones should use, given the stored mobile theme. Kept next to the Windows resolution so
    /// both follow the same "custom, then named, then follow the device" shape.
    /// </summary>
    public static ThemeProfile ResolveMobile(string? profileId, ThemeProfile? custom, bool devicePrefersDark)
    {
        if (string.Equals(profileId, "custom", StringComparison.OrdinalIgnoreCase)
            && custom is { } stored
            && stored.Roles.Count > 0)
        {
            return stored.WithId("custom", stored.Name);
        }

        return ThemeCatalog.Resolve(profileId, devicePrefersDark, ThemeSurface.Mobile);
    }
}
