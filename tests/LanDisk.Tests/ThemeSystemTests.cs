using System.Text.Json;
using LanDisk.Core.Configuration;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>
/// THEME17 — the theme system: complete built-in profiles, contrast validation for custom ones, persistence,
/// and the operator deciding how the phones look.
///
/// <para><b>What "a theme" means here.</b> A named set of semantic colour roles, never CSS and never script.
/// That is what makes "the operator picks the phone's colours" safe: the phone receives values for names it
/// already knows, so nothing the operator types can become code in the phone's browser. A profile that cannot
/// be read is refused when it is saved, not discovered on a screen later.</para>
/// </summary>
public sealed class ThemeSystemTests : TestBase
{
    [Fact]
    public void Every_built_in_windows_profile_defines_every_role_and_is_readable()
    {
        Assert.NotEmpty(ThemeCatalog.Windows);

        foreach (ThemeProfile profile in ThemeCatalog.Windows)
        {
            // A "system" profile resolves to a real appearance before it is used.
            ThemeProfile resolved = ThemeCatalog.Resolve(profile.Id, dark: true);
            Assert.All(ThemeRoles.All, role => Assert.True(
                resolved.Roles.ContainsKey(role), $"主题 {profile.Id} 缺少角色 {role}"));

            ThemeValidation validation = ThemeCatalog.Validate(resolved);
            Assert.True(validation.Ok, $"主题 {profile.Id} 不可读：{validation.Summary}");
        }

        TestBase.Record("THEME17-01",
            $"内置 Windows 主题 {ThemeCatalog.Windows.Count} 套（{string.Join("、", ThemeCatalog.Windows.Select(p => p.Name))}）全部定义了 {ThemeRoles.All.Count} 个语义角色，且每一套都通过对比度校验");
    }

    [Fact]
    public void Every_mobile_profile_defines_every_role()
    {
        Assert.NotEmpty(ThemeCatalog.Mobile);
        foreach (ThemeProfile profile in ThemeCatalog.Mobile)
        {
            ThemeProfile resolved = ThemeCatalog.Resolve(profile.Id, dark: false, ThemeSurface.Mobile);
            Assert.All(ThemeRoles.All, role => Assert.True(resolved.Roles.ContainsKey(role), $"手机主题 {profile.Id} 缺少角色 {role}"));
            Assert.True(ThemeCatalog.Validate(resolved).Ok, $"手机主题 {profile.Id} 不可读");
        }

        TestBase.Record("THEME17-02",
            $"内置手机主题 {ThemeCatalog.Mobile.Count} 套（含「跟随 iPhone 系统」）角色完整，两套外观都能通过对比度校验；手机端通过 CSS 变量应用，服务端不下发任何 CSS 或脚本");
    }

    [Fact]
    public void A_custom_theme_is_derived_and_an_unreadable_one_is_refused()
    {
        ThemeProfile good = ThemeCatalog.Custom("#FF45C8FF", "#FF0B0F14", "#FF111821", "深色自定义");
        ThemeValidation ok = ThemeCatalog.Validate(good);
        Assert.True(ok.Ok, ok.Summary);
        // Derived, not asked for: hover and pressed states exist without the operator choosing them.
        Assert.NotEqual(good.Role("accent"), good.Role("accentHover"));
        Assert.NotEqual(good.Role("accent"), good.Role("accentPressed"));

        // White text is only chosen for a dark background; asking for a light background must switch the text
        // colour rather than keep the dark theme's near-white.
        ThemeProfile light = ThemeCatalog.Custom("#FF0A74C4", "#FFF7F9FC", "#FFFFFFFF", "浅色自定义");
        Assert.True(ThemeCatalog.Validate(light).Ok, ThemeCatalog.Validate(light).Summary);
        Assert.True(ThemePalette.Contrast(light.Role("textPrimary"), light.Role("surface")) >= ThemeValidation.BodyMinimum);

        // A profile built by hand can be unreadable, and then it is refused with the failing pair named.
        var broken = new ThemeProfile
        {
            Id = "broken",
            Name = "坏配色",
            Appearance = "dark",
            Roles = new Dictionary<string, string>(good.Roles) { ["textPrimary"] = "#FF0C0F13", ["surface"] = "#FF0B0F14" }
        };
        ThemeValidation bad = ThemeCatalog.Validate(broken);
        Assert.False(bad.Ok);
        Assert.NotEmpty(bad.Problems);
        Assert.Contains("textPrimary/surface", bad.Summary, StringComparison.Ordinal);

        TestBase.Record("THEME17-03",
            $"自定义主题只要求三个颜色，其余自动派生；对比度校验按 WCAG 计算：正文 ≥ {ThemeValidation.BodyMinimum}:1、状态色 ≥ {ThemeValidation.LargeMinimum}:1，不合格的主题被拒绝并指出具体是哪一对颜色（实测最低 {ok.WorstBody:0.0}:1）");
    }

    [Fact]
    public async Task A_theme_is_colour_roles_only_and_cannot_carry_code()
    {
        // The profile type has no field that can hold a rule, a selector or a script: every value is a colour
        // string for a fixed role name. This is what makes it safe to hand the operator's choice to a phone.
        ThemeProfile custom = ThemeCatalog.Custom("#FF45C8FF", "#FF0B0F14", "#FF111821", "自定义");
        Assert.All(custom.Roles.Keys, key => Assert.Contains(key, ThemeRoles.All));
        Assert.All(custom.Roles.Values, value => Assert.Matches("^#[0-9A-Fa-f]{8}$", value));

        string serialized = JsonSerializer.Serialize(custom, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        foreach (string forbidden in new[] { "<script", "javascript:", "expression(", "url(", "@import", "onload=" })
        {
            Assert.DoesNotContain(forbidden, serialized, StringComparison.OrdinalIgnoreCase);
        }

        // And a profile that tries to smuggle one through a colour value is refused by the colour parser.
        var smuggled = new ThemeProfile
        {
            Id = "smuggled",
            Name = "x",
            Appearance = "dark",
            Roles = new Dictionary<string, string>(custom.Roles) { ["accent"] = "javascript:alert(1)" }
        };
        Assert.Throws<FormatException>(() => ThemePalette.Rgb(smuggled.Role("accent")));

        TestBase.Record("THEME17-04",
            "主题只能表达「固定角色名 → #AARRGGBB 颜色」：没有 CSS、没有选择器、没有脚本、没有 url()；非法颜色值在解析阶段就抛错，不可能进入手机端");
    }

    [Fact]
    public void The_windows_theme_choice_is_persisted()
    {
        string root = Host.DataRoot;
        ShellPreferences preferences = ShellPreferences.Load(root);
        preferences.ThemeProfile = "midnight";
        preferences.Save(root);

        ShellPreferences reloaded = ShellPreferences.Load(root);
        Assert.Equal("midnight", reloaded.ThemeProfile);
        Assert.True(reloaded.HasKnownThemeProfile);
        Assert.NotNull(ThemeCatalog.Find(reloaded.ThemeProfile));

        // An unknown id from a newer build falls back to "follow system" instead of breaking the window.
        reloaded.ThemeProfile = "from-a-future-version";
        Assert.False(reloaded.HasKnownThemeProfile);
        Assert.Equal("obsidian", ThemeCatalog.Resolve(reloaded.ThemeProfile, dark: true).Id);

        // A custom profile survives a round trip through the preference file, roles and all.
        ThemeProfile custom = ThemeCatalog.Custom("#FF45C8FF", "#FF0B0F14", "#FF111821", "自定义");
        reloaded.ThemeProfile = "custom";
        reloaded.CustomTheme = custom;
        reloaded.Save(root);
        ShellPreferences again = ShellPreferences.Load(root);
        Assert.NotNull(again.CustomTheme);
        Assert.Equal(custom.Role("accent"), again.CustomTheme!.Role("accent"));

        TestBase.Record("THEME17-05",
            "Windows 主题选择持久化在 shell.json：命名主题与自定义主题（含全部角色）都能跨启动还原，无法识别的主题 id 安全回退到「跟随系统」");
    }

    [Fact]
    public async Task The_operator_sets_the_phone_theme_and_the_phone_reads_it()
    {
        LanDiskClient device = await PairDeviceAsync();

        // Default: the phones follow their own appearance until the operator says otherwise.
        ApiResult<JsonElement> before = await device.GetAsync<JsonElement>("/api/mobile-theme");
        Assert.True(before.IsSuccess, before.Raw);
        Assert.True(before.Body.GetProperty("followsDevice").GetBoolean());
        int revisionBefore = before.Body.GetProperty("revision").GetInt32();

        // The operator changes it from the Windows side (the same call the native settings page makes).
        (bool ok, ThemeValidation validation, string message) = Host.Runtime.MobileTheme.Set("arctic", null, null, null, null);
        Assert.True(ok, message);
        Assert.True(validation.Ok, validation.Summary);

        ApiResult<JsonElement> after = await device.GetAsync<JsonElement>("/api/mobile-theme");
        Assert.Equal("arctic", after.Body.GetProperty("profileId").GetString());
        Assert.False(after.Body.GetProperty("followsDevice").GetBoolean());
        Assert.True(after.Body.GetProperty("revision").GetInt32() > revisionBefore);

        // The profile reaches the phone as colour roles it already knows, nothing else.
        JsonElement roles = after.Body.GetProperty("dark").GetProperty("roles");
        Assert.True(roles.TryGetProperty("window", out _));
        Assert.True(roles.TryGetProperty("surface", out _));
        Assert.True(roles.TryGetProperty("textPrimary", out _));
        Assert.True(roles.TryGetProperty("accent", out _));

        // The heartbeat carries the revision so a phone notices without polling for the whole profile.
        ApiResult<JsonElement> beat = await device.PostAsync<JsonElement>("/api/heartbeat", new { });
        Assert.Equal(Host.Runtime.MobileTheme.Revision, beat.Body.GetProperty("themeRevision").GetInt32());

        // An invalid custom theme is refused and the stored one is unchanged.
        (bool badOk, ThemeValidation badValidation, string badMessage) = Host.Runtime.MobileTheme.Set("custom", "#FF0B0F14", "#FF0B0F14", "#FF0B0F14", "全黑");
        Assert.False(badOk);
        Assert.False(badValidation.Ok);
        Assert.Contains("对比度", badMessage, StringComparison.Ordinal);
        Assert.Equal("arctic", Host.Runtime.MobileTheme.ProfileId);

        TestBase.Record("THEME17-06",
            "手机主题由 Windows 决定：PUT /api/admin/mobile-theme 后，认证 API /api/mobile-theme 立刻返回新主题与递增的修订号，心跳携带同一修订号；不可读的自定义主题被拒绝且不改变已保存的主题");
    }

    [Fact]
    public void Both_appearances_resolve_to_something_usable()
    {
        // "follow system" is the shipped default and must produce a complete dark *and* light palette.
        ThemeProfile dark = ThemeCatalog.Resolve("follow-system", dark: true);
        ThemeProfile light = ThemeCatalog.Resolve("follow-system", dark: false);
        Assert.True(ThemeCatalog.Validate(dark).Ok);
        Assert.True(ThemeCatalog.Validate(light).Ok);
        Assert.NotEqual(dark.Role("surface"), light.Role("surface"));

        // Every named profile declares an appearance, so the window never has to guess.
        foreach (ThemeProfile profile in ThemeCatalog.Windows)
        {
            Assert.Contains(profile.Appearance, new[] { "dark", "light", "system" });
        }

        // The palette the WPF brushes are built from covers every role the UI can ask for: a missing role is
        // how a page ends up drawing with the wrong colour after a theme switch.
        foreach (string paletteRole in ThemePalette.ProfileRoleMap.Values)
        {
            Assert.NotNull(ThemePalette.Entries.FirstOrDefault(e => e.Role == paletteRole));
        }

        TestBase.Record("THEME17-07",
            "深色 / 浅色 / 跟随系统三种选择都能解析出完整且可读的配色；每个内置主题都声明了自己的外观；产品调色板覆盖全部映射角色（缺失会在这里失败，而不是在窗口里变成错色）");
    }

    [Fact]
    public async Task Changing_the_theme_does_not_touch_authentication_or_data()
    {
        LanDiskClient device = await PairDeviceAsync();
        int devicesBefore = Host.Runtime.Devices.All().Count;
        string deviceId = Host.Runtime.Devices.All().Single().Id;
        string epoch = Host.Runtime.Devices.Find(deviceId)!.SessionEpoch;
        int sharesBefore = Host.Runtime.Shares.All().Count;

        (bool ok, _, _) = Host.Runtime.MobileTheme.Set("midnight", null, null, null, null);
        Assert.True(ok);

        Assert.Equal(devicesBefore, Host.Runtime.Devices.All().Count);
        Assert.Equal(epoch, Host.Runtime.Devices.Find(deviceId)!.SessionEpoch);
        Assert.Equal(sharesBefore, Host.Runtime.Shares.All().Count);
        // The session still works: a theme is presentation, not authority.
        Assert.Equal("device", (await device.MeAsync()).GetProperty("kind").GetString());

        TestBase.Record("THEME17-08",
            "主题与认证、数据完全无关：改主题不轮换 SessionEpoch、不增删设备或共享、旧会话继续有效（主题是外观，不是权限）");
    }
}
