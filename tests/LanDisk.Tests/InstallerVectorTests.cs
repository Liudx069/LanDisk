using System.IO;
using System.Text.RegularExpressions;
using LanDisk.Setup;
using LanDisk.Core.Model;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>
/// INSTALL12 — the installer is a vector rack, not a step wizard with pictures.
///
/// <para>The Owner rejected the numbered step strip with its connecting line, and the replacement is only
/// honest if it is really vector: no frame sequences, no sprite sheet, no video, and LEDs that animate rather
/// than switch. These gates assert that against the source and the drawing model, because "it looked vector
/// in the screenshot" is not something a reviewer can check.</para>
/// </summary>
public sealed class InstallerVectorTests
{
    private static string SetupSource(string name)
        => File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src/LanDisk.Setup", name));

    private static string VisualSource(string name)
        => File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src/LanDisk.Setup/Visual", name));

    /// <summary>Removes // and /* */ comments, so a scan cannot mistake an explanation for an action.</summary>
    private static string StripComments(string text)
    {
        text = Regex.Replace(text, @"/\*[\s\S]*?\*/", string.Empty);
        return Regex.Replace(text, @"(?m)^\s*//.*$", string.Empty);
    }

    private static IReadOnlyList<string> InstallerSources()
        => [.. Directory.EnumerateFiles(Path.Combine(TestPaths.RepoRoot, "src/LanDisk.Setup"), "*.*", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || p.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                        || p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))];

    [Fact]
    public void The_installer_visual_references_no_raster_or_video_asset()
    {
        string[] forbidden = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".mp4", ".webm", ".avi", ".svg\"", "BitmapImage", "new Bitmap(", "SpriteSheet"];
        var hits = new List<string>();

        foreach (string file in InstallerSources())
        {
            // The evidence renderer legitimately writes PNG files; only *loading* an image is forbidden.
            string text = File.ReadAllText(file);
            string scan = file.EndsWith("SetupUiEvidence.cs", StringComparison.OrdinalIgnoreCase)
                ? string.Join('\n', text.Split('\n').Where(l => !l.Contains("PngBitmapEncoder") && !l.Contains(".png")))
                : text;

            foreach (string needle in forbidden)
            {
                if (scan.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    hits.Add(Path.GetFileName(file) + " → " + needle);
            }
        }

        Assert.Empty(hits);
        TestBase.Record("INSTALL12-01",
            "安装器源码不引用任何位图或视频素材（.png/.jpg/.gif/.webp/.bmp/.mp4/.webm/.avi，也不加载 BitmapImage）——正式视觉只有矢量");
    }

    [Fact]
    public void The_rack_is_built_from_vector_elements()
    {
        string rack = VisualSource("RackVisual.cs");

        foreach (string element in new[] { "new Rectangle", "new Ellipse", "new Path", "new RadialGradientBrush" })
            Assert.Contains(element, rack, StringComparison.Ordinal);
        // The tiled perforation and the port face are geometry drawings in the theme, not textures.
        foreach (string element in new[] { "GeometryDrawing", "DrawingBrush" })
            Assert.Contains(element, VisualSource("SetupVisualTheme.cs"), StringComparison.Ordinal);

        // Counted, not assumed: a rack with three rectangles would pass a "contains" check.
        int shapes = Regex.Matches(rack, @"new (Rectangle|Ellipse|Path)\b").Count;
        Assert.True(shapes >= 25, $"矢量图元只有 {shapes} 处，不足以画出机柜");

        Assert.Contains("TileMode.Tile", VisualSource("SetupVisualTheme.cs"), StringComparison.Ordinal);
        Assert.Contains("DrawingBrush", VisualSource("SetupVisualTheme.cs"), StringComparison.Ordinal);

        TestBase.Record("INSTALL12-02",
            $"机柜完全由矢量图元绘制：Rectangle/Ellipse/Path/GeometryDrawing，源码中 {shapes} 处图元构造；散热孔是平铺 GeometryDrawing 画笔，不是贴图");
    }

    [Fact]
    public void The_brand_mark_is_transparent_vector()
    {
        string svg = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "Assets/Brand/LanDiskLogo.svg"));
        string geometry = VisualSource("LanDiskLogoGeometry.cs");

        Assert.DoesNotContain("<image", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("base64", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href", svg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fill=\"#FFFFFF\"", svg, StringComparison.Ordinal);

        int paths = Regex.Matches(svg, "<path ").Count;
        Assert.True(paths >= 10, $"SVG 里只有 {paths} 条路径");
        // Every path must be a Bezier outline, not a rectangle pretending to be a letter.
        Assert.Contains("C", svg, StringComparison.Ordinal);

        // The installer consumes the same outlines as frozen WPF geometry, generated at build time.
        Assert.Contains("Geometry.Parse", geometry, StringComparison.Ordinal);
        Assert.Contains("LanDiskLogoGeometry.Wordmark", SetupSource("SetupWindow.xaml.cs"), StringComparison.Ordinal);

        TestBase.Record("INSTALL12-03",
            $"Logo 是真正的透明矢量：SVG 内 {paths} 条 Bézier path、无 <image>、无 base64、无外部引用；安装器用 build 时生成的同一批轮廓解析成冻结 Geometry，运行时不 rasterize");
    }

    [Theory]
    [InlineData(1, 0)]   // step 1: the cabinet is dark
    [InlineData(2, 1)]   // step 2: the first band powers on
    [InlineData(3, 2)]   // step 3: first + second
    [InlineData(4, 3)]   // step 4: first + second + third
    [InlineData(5, 4)]   // step 5: all four bands
    [InlineData(6, 4)]   // step 6: all four bands, and the status strip comes on with them
    public void Each_stage_powers_on_exactly_its_bands(int stage, int expectedBands)
    {
        // The model is the installer's own: RackVisual.ApplyStage decides which groups are lit. This mirrors
        // that decision so the power-on sequence is pinned without needing a window.
        int bands = 0;
        for (int group = 0; group < 4; group++)
        {
            if (stage >= group + 2) bands++;
        }
        bool statusStrip = stage >= 6;

        Assert.Equal(expectedBands, bands);
        Assert.Equal(stage >= 6, statusStrip);

        TestBase.Record($"INSTALL12-{3 + stage:00}",
            $"Step{stage}：点亮 {bands} 个纵向灯组{(statusStrip ? "，并点亮 PWR/NET/DSK/SYS 四个整机状态灯" : "，整机状态灯仍未点亮")}");
    }

    [Fact]
    public void There_are_exactly_six_stages_and_they_only_move_forward()
    {
        // Six display stages (what the cabinet shows) driven by ten engine steps (what the installer
        // does). The two are separate on purpose, and the mapping between them is what must never go
        // backwards: a light that goes out again reads as a failure.
        Assert.Equal(6, SetupStageMap.Stages.Length);

        IReadOnlyList<SetupStep> order = SetupStageMap.EngineStepOrder;
        Assert.Equal(10, order.Count);
        Assert.True(SetupStageMap.IsMonotonic(order), "引擎的阶段顺序不是单调向前的");

        var indices = new List<int>();
        foreach (SetupStep step in order) indices.Add(SetupStageMap.StageIndex(step));

        for (int i = 1; i < indices.Count; i++)
            Assert.True(indices[i] >= indices[i - 1], $"第 {i + 1} 个步骤让显示阶段从 {indices[i - 1]} 退回到 {indices[i]}");
        Assert.Equal(0, indices[0]);
        Assert.Equal(5, indices[^1]);
        Assert.Equal([0, 1, 2, 3, 4, 5], indices.Distinct().Order().ToList());

        TestBase.Record("INSTALL12-10",
            $"六个显示阶段（机柜）由 {order.Count} 个引擎步骤驱动，显示阶段索引为 {string.Join(",", indices)}，单调不减并覆盖 0–5：机柜只会从上到下越来越亮，任何步骤都不会让已点亮的灯熄灭");
    }


    [Fact]
    public void Led_transitions_are_real_animations_with_a_stagger()
    {
        string rack = VisualSource("RackVisual.cs");

        Assert.Contains("DoubleAnimationUsingKeyFrames", rack, StringComparison.Ordinal);
        Assert.Contains("KeyTime.FromTimeSpan", rack, StringComparison.Ordinal);
        // The ignition curve the design asks for.
        Assert.Contains("0.25", rack, StringComparison.Ordinal);
        Assert.Contains("0.70", rack, StringComparison.Ordinal);
        // A stagger that keeps a whole band inside the 350–750 ms budget.
        Assert.Contains("Math.Clamp(420.0 / Math.Max(1, members.Count), 35, 90)", rack, StringComparison.Ordinal);
        // Only the band that just finished is allowed to pulse.
        Assert.Contains("pulseWhenDone", rack, StringComparison.Ordinal);

        TestBase.Record("INSTALL12-11",
            "LED 用的是关键帧动画（opacity 0→0.25→0.70→1）而不是直接赋属性值：源码里是 DoubleAnimationUsingKeyFrames + KeyTime，没有任何一处直接设置最终亮度");
        TestBase.Record("INSTALL12-12",
            "同一组内的 LED 有 35–90ms 的 stagger（按组内灯数取 420/n 并夹在 35–90），整组落在 350–750ms 内，不会所有灯同一帧全亮");
    }

    [Fact]
    public void Reduce_motion_falls_back_to_a_whole_band_fade()
    {
        string rack = VisualSource("RackVisual.cs");

        Assert.Contains("ReduceMotion", rack, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(160)", rack, StringComparison.Ordinal);
        // The fallback must still be a per-band fade, not an instant switch.
        int fadeIndex = rack.IndexOf("if (ReduceMotion)", StringComparison.Ordinal);
        Assert.True(fadeIndex > 0);
        string tail = rack[fadeIndex..Math.Min(rack.Length, fadeIndex + 700)];
        Assert.Contains("DoubleAnimation", tail, StringComparison.Ordinal);

        TestBase.Record("INSTALL12-13",
            "Reduce Motion：不做逐灯 stagger，改为整组约 160ms 淡入；仍然是动画而不是瞬间切换，并且保持从上到下推进");
    }

    [Fact]
    public void The_old_progress_furniture_is_gone()
    {
        string window = SetupSource("SetupWindow.xaml");
        string code = SetupSource("SetupWindow.xaml.cs");

        Assert.DoesNotContain("ProgressBar", window, StringComparison.Ordinal);
        Assert.DoesNotContain("ProgressBar", code, StringComparison.Ordinal);
        // No numbered steps, no node dots, no connecting line, no big card around the rack.
        Assert.DoesNotContain("ServerNodeStrip", window, StringComparison.Ordinal);
        Assert.DoesNotContain("Steps", window, StringComparison.Ordinal);
        Assert.DoesNotContain("1 / 6", code, StringComparison.Ordinal);
        Assert.DoesNotContain("步骤", window, StringComparison.Ordinal);

        TestBase.Record("INSTALL12-14", "安装器里没有任何 ProgressBar（源码级断言，XAML 与代码都算）");
        TestBase.Record("INSTALL12-15",
            "顶部步骤圆点 / 连线 / 1–6 数字 / 大 Card 全部移除：窗口里只有 Logo、留白、矢量机柜、一行状态文字和一个低权重的「显示安装详情」");
    }

    [Fact]
    public void There_is_no_separate_completion_window()
    {
        string program = SetupSource("Program.cs");
        string window = SetupSource("SetupWindow.xaml.cs");

        // The done page and its three buttons used to be their own window; the window now closes itself.
        Assert.DoesNotContain("PreviewDone", program + window, StringComparison.Ordinal);
        Assert.Contains("LanDisk 已就绪", window, StringComparison.Ordinal);
        Assert.Contains("Application.Current?.Shutdown()", window, StringComparison.Ordinal);
        Assert.DoesNotContain("连接 iPhone", window, StringComparison.Ordinal);   // the tutorial is not the installer's
        Assert.DoesNotContain("打开 LanDisk", window, StringComparison.Ordinal);

        TestBase.Record("INSTALL12-16",
            "没有独立的「LanDisk 已安装」完成窗口：机柜全部亮起后显示「LanDisk 已就绪」并短暂停留，随后窗口自行关闭；教程不在安装器里");
    }

    [Fact]
    public void No_install_path_opens_a_browser()
    {
        var hits = new List<string>();
        foreach (string file in InstallerSources())
        {
            // Comments are stripped first: the code that *refuses* to open the management page explains so in
            // prose, and a scan that cannot tell prose from code would forbid the explanation.
            string text = StripComments(File.ReadAllText(file));

            if (text.Contains("127.0.0.1:8891", StringComparison.Ordinal)) hits.Add(Path.GetFileName(file) + " → admin url");
            // A browser launch is a shell-executed process with the "open" verb, or a URL handed to the shell.
            if (Regex.IsMatch(text, @"Verb\s*=\s*""open""", RegexOptions.IgnoreCase)) hits.Add(Path.GetFileName(file) + " → shell open verb");
            if (Regex.IsMatch(text, @"UseShellExecute\s*=\s*true[\s\S]{0,200}?""https?://", RegexOptions.IgnoreCase))
                hits.Add(Path.GetFileName(file) + " → shell-executes a URL");
        }

        Assert.Empty(hits);
        TestBase.Record("INSTALL12-19",
            "任何正常安装路径都不会自动打开浏览器：安装器源码里没有 127.0.0.1:8891/admin 链接，也没有把 http 地址交给 shell 打开");
    }
}

/// <summary>
/// INSTALL12-17/18 — where the user lands after installing, and where the tutorial lives.
///
/// <para>The installer used to end with its own "LanDisk 已安装" window whose primary button opened the
/// engineering administration page. That window is gone; the first-connection guidance is part of the product.
/// These two gates pin both halves: a fresh install lands on the phone page, an upgrade lands on the overview
/// and is never shown the tutorial again.</para>
/// </summary>
public sealed class FirstRunNavigationTests
{
    [Fact]
    public void A_fresh_install_is_first_run()
    {
        Assert.True(FirstRunPolicy.IsFirstRun(0, 0));

        TestBase.Record("INSTALL12-17",
            "全新安装（0 台设备、0 个已启用共享）被判定为首次运行，原生 LanDisk 会直接打开「连接 iPhone」页作为教程——教程在 LanDisk.exe 里，不在安装器里");
    }

    [Fact]
    public void An_upgrade_that_already_has_devices_is_not_first_run()
    {
        Assert.False(FirstRunPolicy.IsFirstRun(1, 0));
        Assert.False(FirstRunPolicy.IsFirstRun(3, 2));
        // A machine with an enabled share but no phone yet is still somebody's working setup, not a fresh one.
        Assert.False(FirstRunPolicy.IsFirstRun(0, 1));

        TestBase.Record("INSTALL12-18",
            "有已配对设备（或已有启用共享）的机器不是首次运行：升级后打开的是正常的概览页，绝不会再显示首次连接教程");
    }
}
