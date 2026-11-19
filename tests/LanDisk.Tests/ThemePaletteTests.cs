using LanDisk.Core.Configuration;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>
/// Task 09, fix 05: the appearance is complete in both modes.
///
/// <para><b>The defect.</b> "浅色" switched only the WPF-UI theme and left the product's own brushes at
/// their dark values, so the frame went light and the panels stayed dark — a half-implemented theme. The
/// palette is now data in Core (the WPF manager only turns it into brushes), which is what makes it
/// assertable: every role present in both appearances, the two appearances actually different, and every
/// foreground readable on the surface it is drawn on.</para>
/// </summary>
public sealed class ThemePaletteTests
{
    /// <summary>The roles the window and the pages resolve by name. Missing one is a NullReferenceException at runtime.</summary>
    private static readonly string[] RequiredRoles =
    [
        "Window", "Surface", "Secondary", "Elevated",
        "TextPrimary", "TextSecondary",
        "AccentCyan", "AccentBlue", "Success", "Warning", "Error"
    ];

    [Fact]
    public void Every_role_the_pages_resolve_exists_in_both_appearances()
    {
        foreach (string role in RequiredRoles)
        {
            Assert.Contains(ThemePalette.Entries, e => e.Role == role);
            // Throws if the role is missing, so this also proves Rgb parses both values.
            _ = ThemePalette.Rgb(ThemePalette.Hex(role, ShellThemeKind.Dark));
            _ = ThemePalette.Rgb(ThemePalette.Hex(role, ShellThemeKind.Light));
        }

        TestBase.Record("FIX-09-05",
            $"调色板覆盖全部 {RequiredRoles.Length} 个 LanDisk.* 角色，且深浅两套都有可解析的颜色值");
    }

    [Fact]
    public void The_two_appearances_are_actually_different()
    {
        foreach (string role in RequiredRoles)
        {
            Assert.NotEqual(ThemePalette.Hex(role, ShellThemeKind.Dark), ThemePalette.Hex(role, ShellThemeKind.Light));
        }

        // The direction has to be the right way round: light mode is lighter than dark mode where it
        // matters. A palette that swapped the two would still pass "they differ".
        Assert.True(ThemePalette.Contrast(ThemePalette.Hex("Window", ShellThemeKind.Light), "#FF000000")
                    > ThemePalette.Contrast(ThemePalette.Hex("Window", ShellThemeKind.Dark), "#FF000000"));
        Assert.True(ThemePalette.Contrast(ThemePalette.Hex("Surface", ShellThemeKind.Light), "#FF000000")
                    > ThemePalette.Contrast(ThemePalette.Hex("Surface", ShellThemeKind.Dark), "#FF000000"));

        TestBase.Record("FIX-09-05b", "深色与浅色两套值逐项不同，且浅色的 Window/Surface 确实比深色更亮（不是把两套写反）");
    }

    [Fact]
    public void No_foreground_is_drawn_in_the_colour_of_its_background()
    {
        foreach (ShellThemeKind kind in new[] { ShellThemeKind.Dark, ShellThemeKind.Light })
        {
            (double worst, string where) = ThemePalette.WorstForegroundContrast(kind);
            // 3.0 is the floor for "visible at all"; white-on-white and black-on-black sit near 1.0.
            Assert.True(worst >= 3.0, $"{kind}：最低对比度 {worst:0.00}（{where}）低于 3.0");
        }

        TestBase.Record("FIX-09-05c",
            $"两套主题里每种前景色对它所处的表面都达到可见对比度（深色最低 {ThemePalette.WorstForegroundContrast(ShellThemeKind.Dark).Worst:0.0}，浅色最低 {ThemePalette.WorstForegroundContrast(ShellThemeKind.Light).Worst:0.0}）");
    }

    [Fact]
    public void The_light_palette_matches_the_values_the_fix_prescribed()
    {
        // The task named these; pinning them keeps a later "tidy up" from quietly changing the design.
        Assert.Equal("#FFF4F7FB", ThemePalette.Hex("Window", ShellThemeKind.Light));
        Assert.Equal("#FFFFFFFF", ThemePalette.Hex("Surface", ShellThemeKind.Light));
        Assert.Equal("#FFE8EEF5", ThemePalette.Hex("Secondary", ShellThemeKind.Light));
        Assert.Equal("#FFF8FAFC", ThemePalette.Hex("Elevated", ShellThemeKind.Light));
        Assert.Equal("#FF17202A", ThemePalette.Hex("TextPrimary", ShellThemeKind.Light));
        Assert.Equal("#FF5B6878", ThemePalette.Hex("TextSecondary", ShellThemeKind.Light));
    }

    [Fact]
    public void The_chosen_appearance_survives_a_restart()
    {
        // Persistence is the part a user notices: pick light, close, reopen — the choice is still there.
        string dataRoot = Path.Combine(TestPaths.TempRoot, "theme-prefs-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dataRoot);
        try
        {
            ShellPreferences fresh = ShellPreferences.Load(dataRoot);
            Assert.True(fresh.HasKnownTheme);
            Assert.Equal("system", fresh.Theme);   // the default is "follow Windows"

            fresh.Theme = "light";
            fresh.Save(dataRoot);

            Assert.Equal("light", ShellPreferences.Load(dataRoot).Theme);
            Assert.True(ShellPreferences.Load(dataRoot).HasKnownTheme);

            // A file from a newer build with an unknown value must not be trusted blindly.
            File.WriteAllText(ShellPreferences.PathFor(dataRoot), "{\"theme\":\"solarized\"}");
            Assert.False(ShellPreferences.Load(dataRoot).HasKnownTheme);

            TestBase.Record("FIX-09-05d",
                "外观选择写入 shell.json 并可回读（light 能存能取；未知取值被判为不可信并回落到默认）");
        }
        finally
        {
            try { Directory.Delete(dataRoot, true); } catch { }
        }
    }

    [Fact]
    public void The_preference_file_is_part_of_the_managed_update_state()
    {
        // shell.json was already in the rollback scope; the theme lives in it, so an upgrade and a
        // rollback both preserve the user's choice without any extra work.
        Assert.Contains(ShellPreferences.FileName, LanDisk.Update.UpdateBackupScope.StateFiles);
    }
}
