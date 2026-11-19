// WinForms is still enabled for the uninstall dialog, which puts System.Drawing's Brush/Color/Rectangle/
// Ellipse/Point/Size/FontFamily into scope project-wide. These aliases pin this file to the WPF vector types.
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Ellipse = System.Windows.Shapes.Ellipse;
using FontFamily = System.Windows.Media.FontFamily;
using FontWeights = System.Windows.FontWeights;
using Path = System.Windows.Shapes.Path;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Rectangle = System.Windows.Shapes.Rectangle;
using Size = System.Windows.Size;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace LanDisk.Setup.Visual;

/// <summary>
/// A server rack drawn entirely from vector primitives, where the LEDs are the installation progress.
///
/// <para><b>Why the rack is the progress bar.</b> The previous installer showed six numbered nodes on a
/// connecting line — the shape of an internal deployment tool — and the Owner rejected it. A rack that powers
/// on from the top down says the same thing without a single step number, and it says it in the product's own
/// language.</para>
///
/// <para><b>Real animation, not frame swapping.</b> This is one persistent visual tree. Every LED is its own
/// <see cref="Ellipse"/> with its own <see cref="DoubleAnimation"/> on <c>Opacity</c>; nothing is swapped,
/// hidden or re-created between stages. Within a group the LEDs are staggered, so the cabinet reads as powering
/// up layer by layer rather than flashing. Once a group has finished it holds a steady value — only the group
/// currently coming up pulses.</para>
///
/// <para><b>No raster assets.</b> Frame, rails, panels, drive bays, vents, ports, buttons and LEDs are
/// <see cref="Rectangle"/>, <see cref="Ellipse"/>, <see cref="Path"/> and <see cref="Border"/> elements, with
/// vent and port textures expressed as tiled <see cref="GeometryDrawing"/> brushes. There is no PNG, GIF, video
/// or sprite sheet anywhere in this file, and the drawing stays sharp at any DPI because it is all geometry.
/// </para>
/// </summary>
public sealed class RackVisual : Canvas
{
    // Design space. The whole rack is laid out in these units and then scaled uniformly, so the picture is
    // resolution independent and never reflows at a different DPI.
    public const double DesignWidth = 420.0;
    public const double DesignHeight = 700.0;

    private const double FrameX = 42;
    private const double FrameW = 336;
    private const double FrameTop = 16;
    private const double FrameBottom = 664;
    private const double UnitGap = 7;
    private const double UnitX = FrameX + 16;
    private const double UnitW = FrameW - 32;

    private readonly List<Led> _leds = [];
    private readonly List<Ellipse> _finalLeds = [];
    private double _unitY = FrameTop + 58;

    // What has already been applied. Without this, a repeated stage notification re-ran the rise animation
    // for bands that were already lit — and because a delayed animation shows the *base* value (0) until its
    // BeginTime, an already-finished band dropped to dark and lit up again.
    private readonly bool[] _lit = new bool[GroupCount];
    private bool _finalLit;

    /// <summary>True when the OS asks for reduced motion; groups then fade in whole instead of per LED.</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>
    /// How many times a band's body animation has actually been (re)started. Instrumentation for tests:
    /// it is what makes "a finished band is not replayed by a repeated stage notification" checkable,
    /// which counting lit LEDs alone cannot show.
    /// </summary>
    public int AnimationStarts(int group) => group >= 0 && group < GroupCount ? _groupStarts[group] : _decorationStarts;

    private readonly int[] _groupStarts = new int[GroupCount];
    private int _decorationStarts;

    /// <summary>
    /// The body-opacity schedule for one LED of a band: ignition, then optionally a single settle pulse.
    ///
    /// <para>Pure and separate from the WPF animation object so the shape of the animation can be asserted
    /// without a rendering host. Both parts are one schedule by construction — the settle pulse cannot
    /// replace the ignition, because it is not a second animation.</para>
    /// </summary>
    /// <returns>Time in milliseconds and the opacity at that time.</returns>
    public static IReadOnlyList<(double Ms, double Opacity)> RiseSchedule(double stagger, int index, int memberCount, bool pulseWhenDone)
    {
        double delay = index * stagger;
        var schedule = new List<(double, double)>
        {
            (delay + 45, 0.25),
            (delay + 110, 0.70),
            (delay + 210, 1.00)
        };

        if (pulseWhenDone)
        {
            double settle = memberCount * stagger + 260;
            schedule.Add((settle, 1.00));
            schedule.Add((settle + 180, 0.78));
            schedule.Add((settle + 360, 1.00));
        }

        return schedule;
    }

    public RackVisual()
    {
        Width = DesignWidth;
        Height = DesignHeight;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        ClipToBounds = true;

        BuildCabinet();
        BuildRackUnits();
        StatusStrip = BuildStatusStrip();
    }

    /// <summary>Every LED in the rack, in the order it powers on.</summary>
    public IReadOnlyList<Led> Leds => _leds;

    /// <summary>How many power-on groups the rack has: four vertical bands, then the status strip.</summary>
    public const int GroupCount = 4;

    /// <summary>
    /// How many LEDs the current stage has powered on. This is the model-level truth the evidence renderer
    /// asserts on: unlike counting coloured pixels, it cannot be confused by the accent-coloured buttons
    /// elsewhere in the window, and it is what makes "the rack powers on progressively" checkable.
    /// </summary>
    public int LitLedCount { get; private set; }

    /// <summary>
    /// Applies a stage. Stage 1 is "everything off"; stages 2–5 power on bands one to four; stage 6 powers the
    /// remaining status LEDs and leaves the rack in its ready state. Nothing ever powers back off, so the
    /// picture is monotonic — the same guarantee <c>SetupProgressState</c> gives in the model.
    /// </summary>
    public void ApplyStage(int stage, bool animate)
    {
        for (int group = 0; group < GroupCount; group++)
        {
            bool lit = stage >= group + 2;

            // Only a group that actually changes state is touched. A repeated notification for the same
            // stage, or a quick jump across stages, therefore leaves finished bands stable: they neither
            // re-darken nor replay their ignition.
            if (lit == _lit[group]) continue;
            _lit[group] = lit;

            SetGroup(group, lit ? 1.0 : 0.0, animate && lit, pulseWhenDone: animate && lit && stage == group + 2);
        }

        LitLedCount = _leds.Count(l => l.Group >= 0 && l.Group < GroupCount && stage >= l.Group + 2)
                      + (stage >= 6 ? _finalLeds.Count : 0);

        bool finalLit = stage >= 6;
        if (finalLit != _finalLit)
        {
            _finalLit = finalLit;
            SetFinal(finalLit, animate && finalLit);
        }

        if (stage <= 1)
        {
            for (int group = 0; group < GroupCount; group++)
            {
                if (_lit[group] || _leds.Any(l => l.Group == group && l.Body.Opacity != 0.0))
                {
                    _lit[group] = false;
                    SetGroup(group, 0.0, animate: false, pulseWhenDone: false);
                }
            }
            SetGroup(-1, 0, animate: false, pulseWhenDone: false);
        }
    }

    /// <summary>Error state: the rack holds whatever is lit and the status strip turns red.</summary>
    public void ApplyError(int stage)
    {
        ApplyStage(stage, animate: false);
        foreach (Ellipse led in _finalLeds)
        {
            led.Fill = SetupVisualTheme.ErrorBrush;
            led.Opacity = 1.0;
        }

        StatusStrip.Fill = SetupVisualTheme.ErrorBrush;
        StatusStrip.Opacity = 1.0;
    }

    private void SetGroup(int group, double target, bool animate, bool pulseWhenDone)
    {
        List<Led> members = [.. _leds.Where(l => l.Group == group)];
        if (members.Count == 0) return;

        if (!animate)
        {
            foreach (Led led in members)
            {
                led.Body.BeginAnimation(OpacityProperty, null);
                led.Body.Opacity = target;
                led.Glow.BeginAnimation(OpacityProperty, null);
                led.Glow.Opacity = target * 0.55;
            }
            return;
        }

        // Counted for both animated paths (staggered and reduced-motion): the point of the counter is to show
        // whether an already-finished band was animated again, and both paths animate.
        if (group >= 0 && group < GroupCount) _groupStarts[group]++;
        else _decorationStarts++;

        if (ReduceMotion)
        {
            // Reduced motion: no per-LED stagger. The whole band fades together over ~160 ms, so the
            // top-to-bottom progression is still legible without a cascade of individual animations.
            var fade = new DoubleAnimation(target, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var glow = new DoubleAnimation(target * 0.55, TimeSpan.FromMilliseconds(160));
            foreach (Led led in members)
            {
                led.Body.BeginAnimation(OpacityProperty, fade.Clone());
                led.Glow.BeginAnimation(OpacityProperty, glow.Clone());
            }
            return;
        }

        // Stagger: the whole band lands inside the 350–750 ms budget, each LED taking ~90 ms of its own.
        double stagger = Math.Clamp(420.0 / Math.Max(1, members.Count), 35, 90);
        for (int i = 0; i < members.Count; i++)
        {
            Led led = members[i];

            // One animation carries the ignition and, when this band is the one that just completed, the
            // settle pulse. It used to be two BeginAnimation calls on the same property; the second replaces
            // the first, so the band never rose — a delayed animation holds the base value (0) until its
            // BeginTime, and the band sat dark before popping to full.
            var rise = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(i * stagger) };
            foreach ((double ms, double opacity) in RiseSchedule(stagger, i, members.Count, pulseWhenDone))
            {
                TimeSpan at = TimeSpan.FromMilliseconds(ms);
                rise.KeyFrames.Add(opacity >= 1.0 && ms > i * stagger + 210
                    ? new LinearDoubleKeyFrame(opacity, KeyTime.FromTimeSpan(at))
                    : new EasingDoubleKeyFrame(opacity, KeyTime.FromTimeSpan(at))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    });
            }

            led.Body.BeginAnimation(OpacityProperty, rise);

            var glow = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(i * stagger) };
            glow.KeyFrames.Add(new LinearDoubleKeyFrame(0.45, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
            glow.KeyFrames.Add(new EasingDoubleKeyFrame(0.55, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260))));
            led.Glow.BeginAnimation(OpacityProperty, glow);
        }
    }

    private void SetFinal(bool lit, bool animate)
    {
        StatusStrip.Fill = lit ? SetupVisualTheme.AccentBrush : SetupVisualTheme.LedOffBrush;
        StatusStrip.Opacity = lit ? 1.0 : 0.25;

        double stagger = 110;
        for (int i = 0; i < _finalLeds.Count; i++)
        {
            Ellipse led = _finalLeds[i];
            if (!lit)
            {
                led.BeginAnimation(OpacityProperty, null);
                led.Opacity = 0.12;
                continue;
            }

            if (!animate || ReduceMotion)
            {
                led.BeginAnimation(OpacityProperty, null);
                led.Opacity = 1.0;
                continue;
            }

            var rise = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(i * stagger) };
            rise.KeyFrames.Add(new LinearDoubleKeyFrame(0.3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70))));
            rise.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
            led.BeginAnimation(OpacityProperty, rise);
        }
    }

    // ---- cabinet ------------------------------------------------------------

    private void BuildCabinet()
    {
        // Body shadow so the rack sits on the surface instead of floating as a flat rectangle.
        Children.Add(Shape(new Rectangle
        {
            Width = FrameW + 18,
            Height = FrameBottom - FrameTop + 26,
            RadiusX = 14,
            RadiusY = 14,
            Fill = SetupVisualTheme.ShadowBrush
        }, FrameX - 9, FrameTop + 8));

        // Outer shell.
        Children.Add(Shape(new Rectangle
        {
            Width = FrameW,
            Height = FrameBottom - FrameTop,
            RadiusX = 12,
            RadiusY = 12,
            Fill = SetupVisualTheme.FrameBrush,
            Stroke = SetupVisualTheme.FrameEdgeBrush,
            StrokeThickness = 1
        }, FrameX, FrameTop));

        // Inner opening (the darker cavity the units sit in).
        Children.Add(Shape(new Rectangle
        {
            Width = FrameW - 24,
            Height = FrameBottom - FrameTop - 40,
            RadiusX = 6,
            RadiusY = 6,
            Fill = SetupVisualTheme.CavityBrush
        }, FrameX + 12, FrameTop + 18));

        // Left / right mounting rails with the regular hole pattern of a real cabinet.
        for (int side = 0; side < 2; side++)
        {
            double x = side == 0 ? FrameX + 15 : FrameX + FrameW - 21;
            Children.Add(Shape(new Rectangle
            {
                Width = 6,
                Height = FrameBottom - FrameTop - 44,
                Fill = SetupVisualTheme.RailBrush
            }, x, FrameTop + 20));

            for (double y = FrameTop + 30; y < FrameBottom - 30; y += 16)
            {
                Children.Add(Shape(new Ellipse
                {
                    Width = 2.4,
                    Height = 2.4,
                    Fill = SetupVisualTheme.RailHoleBrush
                }, x + 1.8, y));
            }
        }

        // Feet.
        foreach (double x in new[] { FrameX + 34, FrameX + FrameW - 54 })
        {
            Children.Add(Shape(new Ellipse { Width = 20, Height = 10, Fill = SetupVisualTheme.FrameEdgeBrush }, x, FrameBottom - 6));
            Children.Add(Shape(new Rectangle { Width = 10, Height = 14, RadiusX = 3, RadiusY = 3, Fill = SetupVisualTheme.FrameBrush }, x + 5, FrameBottom - 2));
        }
    }

    private Path StatusStrip { get; }

    private Path BuildStatusStrip()
    {
        // A thin light bar on the right front post: the "system" indicator of the cabinet itself.
        Path strip = new Path
        {
            Data = Geometry.Parse("M0 0 H3.4 A1.7 1.7 0 0 1 3.4 3.4 H0 A1.7 1.7 0 0 1 0 0 Z"),
            Fill = SetupVisualTheme.LedOffBrush,
            Opacity = 0.25
        };
        Children.Add(Shape(strip, FrameX + FrameW - 9, FrameTop + 40, 3.4, 120));

        // Power / Network / Storage / System: the four machine-level indicators, lit at the ready stage.
        string[] labels = ["PWR", "NET", "DSK", "SYS"];
        for (int i = 0; i < labels.Length; i++)
        {
            double y = FrameTop + 178 + i * 17;
            var led = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = SetupVisualTheme.LedGreenBrush,
                Opacity = 0.12
            };
            Children.Add(Shape(led, FrameX + FrameW - 10.6, y));
            _finalLeds.Add(led);

            TextBlock text = Text(labels[i], 6.4, SetupVisualTheme.TextTertiaryBrush);
            Children.Add(Shape(text, FrameX + FrameW - 40, y - 2.2));
        }

        return strip;
    }

    // ---- rack units ---------------------------------------------------------

    private void BuildRackUnits()
    {
        // Band 1 — head unit + the first drive row.
        Unit(56, UnitKind.Head);
        Unit(52, UnitKind.Drives8, group: 0);

        // Band 2 — two more drive shelves.
        Unit(44, UnitKind.Drives12, group: 1);
        Unit(46, UnitKind.Drives12, group: 1);

        // Band 3 — compute nodes and a vent shelf.
        Unit(48, UnitKind.Nodes, group: 2);
        Unit(34, UnitKind.Vent, group: 2);

        // Band 4 — the network switch, a drive shelf and the deep vent block at the bottom.
        Unit(46, UnitKind.Switch, group: 3);
        Unit(50, UnitKind.Drives8, group: 3);
        Unit(96, UnitKind.Vent, group: 3);
    }

    private enum UnitKind { Head, Drives8, Drives12, Nodes, Vent, Switch }

    private void Unit(double height, UnitKind kind, int group = -1)
    {
        var panel = new Rectangle
        {
            Width = UnitW,
            Height = height,
            RadiusX = 4,
            RadiusY = 4,
            Fill = SetupVisualTheme.PanelBrush,
            Stroke = SetupVisualTheme.PanelEdgeBrush,
            StrokeThickness = 1
        };
        Children.Add(Shape(panel, UnitX, _unitY));

        switch (kind)
        {
            case UnitKind.Head:
                // Brand label + three status dashes on the left, product id on the right — the reference's
                // head unit, drawn rather than photographed.
                Children.Add(Shape(new Rectangle { Width = 30, Height = 2, RadiusX = 1, RadiusY = 1, Fill = SetupVisualTheme.FrameEdgeBrush }, UnitX + UnitW - 44, _unitY + height - 14));
                Children.Add(Shape(Text("LanDisk", 15, SetupVisualTheme.TextPrimaryBrush), UnitX + UnitW - 96, _unitY + 12));
                Children.Add(Shape(Text("LD-RACK-01", 6.6, SetupVisualTheme.TextTertiaryBrush), UnitX + UnitW - 62, _unitY + height - 22));
                for (int i = 0; i < 3; i++)
                {
                    var led = new Rectangle { Width = 16, Height = 5, RadiusX = 2.5, RadiusY = 2.5, Fill = SetupVisualTheme.LedBlueBrush, Opacity = 0 };
                    Children.Add(Shape(led, UnitX + 16 + i * 20, _unitY + 22));
                    _leds.Add(new Led(0, led, GlowOverlay(16, 5, SetupVisualTheme.LedBlueBrush, UnitX + 16 + i * 20, _unitY + 22, 22)));
                }
                for (int i = 0; i < 2; i++)
                {
                    Children.Add(Shape(new Ellipse { Width = 6, Height = 6, Fill = SetupVisualTheme.FrameEdgeBrush }, UnitX + 14, _unitY + 10 + i * 10));
                }
                break;

            case UnitKind.Drives8:
            case UnitKind.Drives12:
            {
                int count = kind == UnitKind.Drives8 ? 8 : 12;
                bool twoRows = kind == UnitKind.Drives12;
                int perRow = twoRows ? 6 : 8;
                for (int row = 0; row < (twoRows ? 2 : 1); row++)
                {
                    for (int col = 0; col < perRow; col++)
                    {
                        double bayW = (UnitW - 18) / perRow - 3;
                        double bayH = twoRows ? (height - 16) / 2 - 3 : height - 16;
                        double bx = UnitX + 9 + col * (bayW + 3);
                        double by = _unitY + 8 + row * (bayH + 3);

                        Children.Add(Shape(new Rectangle
                        {
                            Width = bayW,
                            Height = bayH,
                            RadiusX = 2,
                            RadiusY = 2,
                            Fill = SetupVisualTheme.BayBrush,
                            Stroke = SetupVisualTheme.PanelEdgeBrush,
                            StrokeThickness = 0.6
                        }, bx, by));

                        // Handle slot, then the two indicator LEDs every carrier has.
                        Children.Add(Shape(new Rectangle { Width = bayW * 0.6, Height = 1.4, Fill = SetupVisualTheme.FrameEdgeBrush }, bx + bayW * 0.2, by + bayH * 0.42));

                        int index = row * perRow + col;
                        double ly = by + bayH - 5.2;
                        var activity = new Ellipse { Width = 3.4, Height = 3.4, Fill = SetupVisualTheme.LedGreenBrush, Opacity = 0 };
                        Children.Add(Shape(activity, bx + 3, ly));
                        _leds.Add(new Led(group, activity, GlowOverlay(3.4, 3.4, SetupVisualTheme.LedGreenBrush, bx + 3, ly, 9)));

                        var status = new Ellipse { Width = 3.4, Height = 3.4, Fill = SetupVisualTheme.LedBlueBrush, Opacity = 0 };
                        Children.Add(Shape(status, bx + bayW - 7, ly));
                        _leds.Add(new Led(group, status, GlowOverlay(3.4, 3.4, SetupVisualTheme.LedBlueBrush, bx + bayW - 7, ly, 9)));

                        _ = index;
                    }
                }
                break;
            }

            case UnitKind.Nodes:
                for (int node = 0; node < 2; node++)
                {
                    double nh = (height - 12) / 2;
                    double ny = _unitY + 6 + node * (nh + 4);
                    Children.Add(Shape(new Rectangle { Width = UnitW - 18, Height = nh, RadiusX = 2, RadiusY = 2, Fill = SetupVisualTheme.BayBrush }, UnitX + 9, ny));

                    // Vent field on the left of each node, LEDs on the right.
                    Children.Add(Shape(new Rectangle
                    {
                        Width = (UnitW - 18) * 0.55,
                        Height = nh - 10,
                        Fill = SetupVisualTheme.VentBrush
                    }, UnitX + 14, ny + 5));

                    for (int i = 0; i < 4; i++)
                    {
                        double lx = UnitX + UnitW - 96 + i * 13;
                        var led = new Ellipse { Width = 4, Height = 4, Fill = SetupVisualTheme.LedGreenBrush, Opacity = 0 };
                        Children.Add(Shape(led, lx, ny + nh / 2 - 2));
                        _leds.Add(new Led(2, led, GlowOverlay(4, 4, SetupVisualTheme.LedGreenBrush, lx, ny + nh / 2 - 2, 10)));
                    }

                    Children.Add(Shape(new Ellipse
                    {
                        Width = 12,
                        Height = 12,
                        Stroke = SetupVisualTheme.FrameEdgeBrush,
                        StrokeThickness = 1.2,
                        Fill = Brushes.Transparent
                    }, UnitX + UnitW - 40, ny + nh / 2 - 6));
                }
                break;

            case UnitKind.Switch:
                // 8 RJ45 ports: a real switch face, drawn with geometry and tiled brushes.
                for (int i = 0; i < 8; i++)
                {
                    double px = UnitX + 12 + i * 17;
                    Children.Add(Shape(new Rectangle
                    {
                        Width = 13,
                        Height = height - 16,
                        RadiusX = 1.5,
                        RadiusY = 1.5,
                        Fill = SetupVisualTheme.PortBrush,
                        Stroke = SetupVisualTheme.PortEdgeBrush,
                        StrokeThickness = 0.7
                    }, px, _unitY + 8));
                    Children.Add(Shape(new Rectangle { Width = 9, Height = 3, Fill = SetupVisualTheme.CavityBrush }, px + 2, _unitY + 12));

                    var link = new Ellipse { Width = 3.6, Height = 3.6, Fill = SetupVisualTheme.LedGreenBrush, Opacity = 0 };
                    Children.Add(Shape(link, px + 4.6, _unitY + height - 12));
                    _leds.Add(new Led(3, link, GlowOverlay(3.6, 3.6, SetupVisualTheme.LedGreenBrush, px + 4.6, _unitY + height - 12, 9)));
                }
                for (int i = 0; i < 3; i++)
                {
                    var led = new Ellipse { Width = 4, Height = 4, Fill = SetupVisualTheme.LedBlueBrush, Opacity = 0 };
                    Children.Add(Shape(led, UnitX + UnitW - 58 + i * 11, _unitY + 12));
                    _leds.Add(new Led(3, led, GlowOverlay(4, 4, SetupVisualTheme.LedBlueBrush, UnitX + UnitW - 58 + i * 11, _unitY + 12, 10)));
                }
                Children.Add(Shape(new Rectangle { Width = 26, Height = 2, Fill = SetupVisualTheme.FrameEdgeBrush }, UnitX + UnitW - 34, _unitY + height - 16));
                break;

            case UnitKind.Vent:
                Children.Add(Shape(new Rectangle
                {
                    Width = UnitW - 20,
                    Height = height - 14,
                    RadiusX = 3,
                    RadiusY = 3,
                    Fill = SetupVisualTheme.VentBrush
                }, UnitX + 10, _unitY + 7));
                if (group >= 0)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        double lx = UnitX + UnitW - 34 + i * 9;
                        var led = new Ellipse { Width = 3.2, Height = 3.2, Fill = SetupVisualTheme.LedBlueBrush, Opacity = 0 };
                        Children.Add(Shape(led, lx, _unitY + height - 10));
                        _leds.Add(new Led(group, led, GlowOverlay(3.2, 3.2, SetupVisualTheme.LedBlueBrush, lx, _unitY + height - 10, 8)));
                    }
                }
                break;
        }

        _unitY += height + UnitGap;
    }

    /// <summary>The soft halo behind a lit LED: a radial gradient ellipse, still pure vector.</summary>
    private UIElement GlowOverlay(double w, double h, Brush colour, double x, double y, double size)
    {
        var halo = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = new RadialGradientBrush
            {
                GradientStops =
                [
                    new GradientStop(((SolidColorBrush)colour).Color, 0.0),
                    new GradientStop(Color.FromArgb(0, 0, 0, 0), 1.0)
                ]
            },
            Opacity = 0,
            IsHitTestVisible = false
        };
        Children.Add(Shape(halo, x - (size - w) / 2, y - (size - h) / 2));
        return halo;
    }

    // ---- helpers ------------------------------------------------------------

    private static UIElement Shape(UIElement element, double x, double y, double? width = null, double? height = null)
    {
        if (width is not null) element.SetValue(FrameworkElement.WidthProperty, width.Value);
        if (height is not null) element.SetValue(FrameworkElement.HeightProperty, height.Value);
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        return element;
    }

    private static TextBlock Text(string value, double size, Brush foreground) => new()
    {
        Text = value,
        FontSize = size,
        Foreground = foreground,
        FontFamily = SetupVisualTheme.FontStack,
        FontWeight = FontWeights.SemiBold
    };

    /// <summary>One indicator LED plus its halo. The body is what the stage animation drives.</summary>
    public sealed record Led(int Group, Shape Body, UIElement Glow);
}
