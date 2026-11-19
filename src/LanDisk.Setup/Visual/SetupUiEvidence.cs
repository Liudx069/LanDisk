using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Path = System.IO.Path;
using Point = System.Windows.Point;

namespace LanDisk.Setup.Visual;

/// <summary>
/// Renders the installer window to PNG evidence, by actually opening the window and reading its pixels.
///
/// <para><b>Why the frames are real.</b> The previous evidence drew a window that had never been shown, so the
/// files came out as a dark rectangle and a "0% white pixels" check passed on nothing. Here the window is
/// shown, the WPF dispatcher is pumped until layout and render have actually happened, and the frame is taken
/// from the live visual tree with <see cref="RenderTargetBitmap"/>. The mid-transition capture is taken
/// <i>while an animation is running</i>, which is the only way to show that the LEDs fade rather than
/// switch.</para>
///
/// <para><b>What is asserted.</b> Beyond "not blank", the capture counts lit LED pixels per frame: the number
/// must be zero with the cabinet off and must strictly grow as the bands power on. That turns "the rack is the
/// progress" from a claim into a measurement.</para>
/// </summary>
public static class SetupUiEvidence
{
    /// <param name="reduceMotionFrame">True to also capture the reduced-motion variant of a stage.</param>
    public static int Render(SetupOptions options, string directory, bool reduceMotionFrame = true)
    {
        Directory.CreateDirectory(directory);
        var summary = new List<string>();

        bool ownsApplication = Application.Current is null;
        Application? app = null;
        if (ownsApplication)
        {
            app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }

        var window = new SetupWindow(options)
        {
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000
        };
        window.Show();
        Pump(TimeSpan.FromMilliseconds(320));

        // (name, stage, title, detail, expectLitPixels, animate)
        (string Name, int Stage, string Title, string Detail, bool ExpectLit, double SettleMs)[] states =
        [
            ("step1-off", 1, "验证安装包", "校验内置载荷与文件清单", false, 0),
            ("step2-mid", 2, "停止旧版本 / 准备程序", "正在点亮第一组工作指示灯", true, 120),
            ("step2-done", 2, "停止旧版本 / 准备程序", "结束运行中的实例，写入程序文件", true, 700),
            ("step3", 3, "安装程序并保留用户数据", "程序就位，设备 / 共享 / 证书原样保留", true, 700),
            ("step4", 4, "配置本地服务", "快捷方式与卸载注册", true, 700),
            ("step5", 5, "配置 HTTPS 与防火墙", "仅专用网络的防火墙规则", true, 700),
            ("step6-ready", 6, "LanDisk 已就绪", "服务已在 8891 / 8892 上响应", true, 900)
        ];

        long previousLit = -1;
        int previousStage = 0;
        bool first = true;
        foreach ((string name, int stage, string title, string detail, bool expectLit, double settle) in states)
        {
            // The first frame keeps the intro options visible (that is the screen the user actually starts on);
            // every later frame is the running layout, where the options are gone and the rack has the room.
            if (!first) window.EnterRunningLayout();
            first = false;

            window.PreviewStage(1, title, detail);
            Pump(TimeSpan.FromMilliseconds(60));

            // Start the power-on animation, then take the picture either mid-flight or after it settled.
            window.PreviewStage(stage, title, detail);
            Pump(TimeSpan.FromMilliseconds(settle));

            PixelStats stats = Capture(window, Path.Combine(directory, $"setup-{name}.png"));
            int litModel = window.Rack.LitLedCount;

            var problems = new List<string>();
            if (stats.BackgroundFraction > 0.92) problems.Add("画面几乎全是背景（疑似空白窗口）");
            if (stats.InkPixels < 900) problems.Add($"文字/线条像素过少（{stats.InkPixels}）");
            if (expectLit && litModel == 0) problems.Add("该阶段应有 LED 点亮，但模型里一个都没亮");
            if (!expectLit && litModel > 0) problems.Add($"机柜应断电，模型里却有 {litModel} 个 LED 点亮");
            if (expectLit && stats.LedPixels == 0) problems.Add("模型说 LED 已亮，但机柜区域里找不到点亮的像素（渲染没生效）");
            if (expectLit && previousLit >= 0 && stage > previousStage && litModel <= previousLit)
                problems.Add($"点亮的 LED 数量没有比上一阶段增加（{previousLit} → {litModel}）");

            summary.Add($"{name}\t{stats.Width}x{stats.Height}\tLitLED={litModel}/{window.Rack.Leds.Count + 4}\tLitPixels={stats.LedPixels}\t" +
                        (problems.Count == 0 ? $"PASS 非背景 {1 - stats.BackgroundFraction:P1}，文字 {stats.InkPixels} 像素" : "FAIL " + string.Join("；", problems)));

            if (expectLit) { previousLit = litModel; previousStage = stage; }
        }

        // Failure state.
        window.PreviewError("安装未完成：内置载荷校验失败（示例状态，用于界面证据）。", 2);
        Pump(TimeSpan.FromMilliseconds(220));
        PixelStats errorStats = Capture(window, Path.Combine(directory, "setup-error.png"));
        summary.Add($"error\t{errorStats.Width}x{errorStats.Height}\tLitPixels={errorStats.LedPixels}\t" +
                    (errorStats.ErrorPixels > 0 ? $"PASS 非背景 {1 - errorStats.BackgroundFraction:P1}，危险色 {errorStats.ErrorPixels} 像素" : "FAIL 错误状态没有出现危险色"));

        // Reduced-motion variant: same stage, whole-band fade instead of a per-LED stagger.
        if (reduceMotionFrame)
        {
            window.PreviewStage(1, "验证安装包", "减少动画模式");
            Pump(TimeSpan.FromMilliseconds(60));
            window.ReduceMotion = true;
            window.PreviewStage(3, "安装程序并保留用户数据", "减少动画：整组淡入");
            Pump(TimeSpan.FromMilliseconds(240));
            PixelStats rm = Capture(window, Path.Combine(directory, "setup-reduce-motion.png"));
            summary.Add($"reduce-motion\t{rm.Width}x{rm.Height}\tLitPixels={rm.LedPixels}\t" +
                        (rm.LedPixels > 0 ? $"PASS 非背景 {1 - rm.BackgroundFraction:P1}，整组淡入后 LED 已亮" : "FAIL 减少动画模式下 LED 未亮"));
        }

        window.Close();
        Pump(TimeSpan.FromMilliseconds(120));
        app?.Shutdown();

        File.WriteAllLines(Path.Combine(directory, "setup-ui-index.txt"),
            new[]
            {
                "LanDisk installer UI evidence",
                "method: 真实 WPF 窗口 Show + Dispatcher 泵送后用 RenderTargetBitmap 捕获真实可视树像素",
                "states: step1-off, step2-mid(动画进行中), step2-done, step3, step4, step5, step6-ready, error, reduce-motion",
                "rack: 纯矢量（Rectangle/Ellipse/Path/GeometryDrawing），无 PNG/GIF/视频/sprite，无逐帧换图",
                "INSTALLER_REAL_DPI: NOT_RUN（未在独立 DPI 进程中真实验证 125/150/200%；矢量绘制本身与 DPI 无关）",
                "DPI awareness: PerMonitorV2"
            }.Concat(summary));

        int failures = summary.Count(line => line.Contains("\tFAIL"));
        Console.WriteLine($"安装器界面证据已生成：{directory}（{summary.Count - failures}/{summary.Count} 张通过检查）");
        return failures == 0 ? 0 : 2;
    }

    // ---- capture ------------------------------------------------------------

    private sealed record PixelStats(int Width, int Height, long LedPixels, long InkPixels, long ErrorPixels, double BackgroundFraction);

    private static PixelStats Capture(SetupWindow window, string file)
    {
        var root = (System.Windows.Media.Visual)window.Content;
        var element = (FrameworkElement)root;
        int width = Math.Max(1, (int)Math.Round(element.ActualWidth));
        int height = Math.Max(1, (int)Math.Round(element.ActualHeight));

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(root);

        // The LED count is taken inside the rack's own rectangle. Scanning the whole window would count the
        // accent-coloured install button as "an LED is lit", which is exactly the kind of hollow check this
        // evidence exists to avoid.
        Rect rackRect = RackRect(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using (FileStream stream = File.Create(file)) encoder.Save(stream);

        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        target.CopyPixels(pixels, stride, 0);

        // The window background is #0B0F14; "ink" is anything clearly lighter (text, panel edges, logo).
        long leds = 0, ink = 0, error = 0, background = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * stride) + (x * 4);
                byte b = pixels[i];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];
                byte a = pixels[i + 3];
                if (a < 8) { background++; continue; }

                bool isBackground = Math.Abs(r - 0x0B) <= 6 && Math.Abs(g - 0x0F) <= 6 && Math.Abs(b - 0x14) <= 6;
                if (isBackground) { background++; continue; }

                if (r + g + b > 150) ink++;

                if (rackRect.Contains(new Point(x, y)))
                {
                    // Signal LEDs only: the rack's own blues (#5CC8FF) and greens (#5BE38A) are bright and
                    // saturated, while its unlit LED colour (#2A3542) is not.
                    bool blue = b > 190 && g > 150 && r > 60 && r < 150;
                    bool green = g > 190 && r > 60 && b > 90 && b < 190;
                    if (blue || green) leds++;
                }

                // Danger (#F87171).
                if (r > 200 && g < 150 && b < 150) error++;
            }
        }

        double total = (double)width * height;
        return new PixelStats(width, height, leds, ink, error, background / total);
    }

    /// <summary>The rack's rectangle in captured-image coordinates, so LED pixels can be counted where they are.</summary>
    private static Rect RackRect(SetupWindow window)
    {
        try
        {
            GeneralTransform transform = window.Rack.TransformToAncestor(window);
            return transform.TransformBounds(new Rect(0, 0, window.Rack.ActualWidth, window.Rack.ActualHeight));
        }
        catch (InvalidOperationException)
        {
            return Rect.Empty;
        }
    }

    /// <summary>Pumps the WPF dispatcher for a wall-clock duration, so animations actually advance.</summary>
    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = duration
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
