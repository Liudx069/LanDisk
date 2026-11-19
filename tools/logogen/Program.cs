using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using IoPath = System.IO.Path;

namespace LanDisk.LogoGen;

/// <summary>
/// Converts the LanDisk wordmark into real vector artwork, once, at build time.
///
/// <para><b>Why a tool and not a hand-written path.</b> The brand asset must be genuine vector with no embedded
/// bitmap, and the installer must not rasterise anything at runtime. Letterforms drawn by hand would be an
/// approximation of the brand; glyph outlines taken from the actual font are the exact shapes, expressed as
/// Bézier paths. So the outlines are extracted here and written out twice: as
/// <c>Assets/Brand/LanDiskLogo.svg</c> (pure &lt;path&gt; elements) and as a generated C# file holding the same
/// outlines in WPF path mini-language, which the installer consumes as frozen <c>Geometry</c>.</para>
///
/// <para>The tagline needs a second font (Microsoft YaHei UI) because the Latin face has no CJK glyphs. Both
/// outlines are placed in one coordinate system with the composed mark's own origin, so the layout is
/// resolution independent.</para>
/// </summary>
internal static class Program
{
    private const string Wordmark = "LanDisk";
    private const string Tagline = "让数据更自由";
    private const double EmSize = 100.0;

    private static int Main(string[] args)
    {
        string repo = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        string svgPath = IoPath.Combine(repo, "Assets", "Brand", "LanDiskLogo.svg");
        string csPath = IoPath.Combine(repo, "src", "LanDisk.Setup", "Visual", "LanDiskLogoGeometry.cs");

        GlyphTypeface latin = LoadFace(["Segoe UI Semibold", "Segoe UI", "Arial"], boldWeight: true);
        GlyphTypeface cjk = LoadFace(["Microsoft YaHei UI", "Microsoft YaHei", "SimHei"], boldWeight: false);

        List<Shape> shapes = [];
        shapes.AddRange(Outlines(latin, Wordmark, EmSize, group: 0));
        shapes.AddRange(Outlines(cjk, Tagline, EmSize * 0.42, group: 1));

        if (shapes.Count == 0) throw new InvalidOperationException("没有取到任何字形轮廓");

        // Compose: wordmark on top, tagline underneath, both centred in the composed box.
        Rect wordBounds = Union(shapes.Where(s => s.Group == 0));
        Rect tagBounds = Union(shapes.Where(s => s.Group == 1));

        const double gap = EmSize * 0.34;
        double width = Math.Max(wordBounds.Width, tagBounds.Width);
        double height = wordBounds.Height + gap + tagBounds.Height;

        foreach (Shape shape in shapes)
        {
            if (shape.Group == 0)
            {
                shape.LayoutDx = (width - wordBounds.Width) / 2 - wordBounds.X;
                shape.LayoutDy = -wordBounds.Y;
            }
            else
            {
                shape.LayoutDx = (width - tagBounds.Width) / 2 - tagBounds.X;
                shape.LayoutDy = wordBounds.Height + gap - tagBounds.Y;
            }
        }

        Directory.CreateDirectory(IoPath.GetDirectoryName(svgPath)!);
        Directory.CreateDirectory(IoPath.GetDirectoryName(csPath)!);
        File.WriteAllText(svgPath, BuildSvg(shapes, width, height), new UTF8Encoding(false));
        File.WriteAllText(csPath, BuildCSharp(shapes, width, height), new UTF8Encoding(false));

        Console.WriteLine($"SVG  : {svgPath} ({new FileInfo(svgPath).Length} bytes)");
        Console.WriteLine($"XAML : {csPath} ({new FileInfo(csPath).Length} bytes)");
        Console.WriteLine($"字形 : wordmark {shapes.Count(s => s.Group == 0)} 个轮廓，tagline {shapes.Count(s => s.Group == 1)} 个轮廓");
        Console.WriteLine($"尺寸 : {N(width)} x {N(height)}（无单位矢量坐标）");
        return 0;
    }

    // ---- model --------------------------------------------------------------

    private sealed class Shape(int group, PathGeometry geometry)
    {
        public int Group { get; } = group;
        public PathGeometry Geometry { get; } = geometry;
        /// <summary>Advance offset from the pen: where this glyph sits inside the word.</summary>
        public double PenDx { get; set; }

        /// <summary>Composition offset: where the composed mark places this glyph.</summary>
        public double LayoutDx { get; set; }

        public double LayoutDy { get; set; }
        /// <summary>Bounds in the composed space: the outline bounds shifted by this glyph's pen offset.</summary>
        public Rect PlacedBounds
        {
            get
            {
                Rect b = Geometry.Bounds;
                return new Rect(b.X + PenDx + LayoutDx, b.Y + LayoutDy, b.Width, b.Height);
            }
        }
    }

    private static Rect Union(IEnumerable<Shape> shapes)
        => shapes.Aggregate(Rect.Empty, (acc, s) => Rect.Union(acc, s.PlacedBounds));

    // ---- font handling ------------------------------------------------------

    private static GlyphTypeface LoadFace(string[] candidates, bool boldWeight)
    {
        foreach (string name in candidates)
        {
            var family = new FontFamily(name);
            foreach (Typeface typeface in family.GetTypefaces())
            {
                if (!typeface.TryGetGlyphTypeface(out GlyphTypeface? glyph)) continue;
                if (boldWeight && typeface.Weight < FontWeights.SemiBold) continue;
                if (!boldWeight && typeface.Weight > FontWeights.Medium) continue;
                return glyph;
            }
        }

        throw new InvalidOperationException("找不到可用字体：" + string.Join(" / ", candidates));
    }

    private static List<Shape> Outlines(GlyphTypeface face, string text, double emSize, int group)
    {
        List<Shape> shapes = [];
        double pen = 0;

        foreach (char ch in text)
        {
            if (!face.CharacterToGlyphMap.TryGetValue(ch, out ushort glyphIndex))
                throw new InvalidOperationException($"字体缺少字形：{ch}");

            // The real outline, curves intact — not a flattened approximation.
            Geometry glyph = face.GetGlyphOutline(glyphIndex, emSize, emSize);
            if (glyph is PathGeometry geometry)
            {
                PathGeometry clone = geometry.Clone();
                clone.Freeze();
                shapes.Add(new Shape(group, clone) { PenDx = pen });
            }

            pen += face.AdvanceWidths[glyphIndex] * emSize;
        }

        // The reference mark has the tagline letters tracked wider than the default fit.
        if (group == 1)
        {
            double extra = emSize * 0.26;
            for (int i = 0; i < shapes.Count; i++) shapes[i].PenDx += extra * i;
        }

        return shapes;
    }

    // ---- SVG ----------------------------------------------------------------

    private static string BuildSvg(List<Shape> shapes, double width, double height)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        sb.AppendLine("<!-- LanDisk brand mark: pure Bezier outlines generated by tools/logogen from real -->");
        sb.AppendLine("<!-- font glyphs. It contains no embedded raster data and references nothing external. -->");
        sb.AppendLine($"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {N(width)} {N(height)}" width="{N(width)}" height="{N(height)}" role="img" aria-label="LanDisk 让数据更自由">""");
        sb.AppendLine("  <title>LanDisk 让数据更自由</title>");
        sb.AppendLine("  <g fill=\"#FFFFFF\" fill-rule=\"nonzero\">");
        for (int i = 0; i < shapes.Count; i++)
        {
            string opacity = shapes[i].Group == 0 ? "1" : "0.82";
            sb.AppendLine($"""    <path id="shape{i}" fill-opacity="{opacity}" d="{PathData(shapes[i])}"/>""");
        }
        sb.AppendLine("  </g>");
        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static string N(double value) => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

    private static string PathData(Shape shape)
    {
        var sb = new StringBuilder();
        Point Shift(Point p) => new(p.X + shape.PenDx + shape.LayoutDx, p.Y + shape.LayoutDy);

        foreach (PathFigure figure in shape.Geometry.Figures)
        {
            Point start = Shift(figure.StartPoint);
            sb.Append('M').Append(N(start.X)).Append(' ').Append(N(start.Y));
            foreach (PathSegment segment in figure.Segments)
            {
                switch (segment)
                {
                    case LineSegment line:
                        Append(sb, 'L', Shift(line.Point));
                        break;
                    case PolyLineSegment poly:
                        foreach (Point p in poly.Points) Append(sb, 'L', Shift(p));
                        break;
                    case BezierSegment bezier:
                        sb.Append('C');
                        AppendPoint(sb, Shift(bezier.Point1));
                        AppendPoint(sb, Shift(bezier.Point2));
                        AppendPoint(sb, Shift(bezier.Point3));
                        break;
                    case PolyBezierSegment multi:
                        for (int i = 0; i + 2 < multi.Points.Count; i += 3)
                        {
                            sb.Append('C');
                            AppendPoint(sb, Shift(multi.Points[i]));
                            AppendPoint(sb, Shift(multi.Points[i + 1]));
                            AppendPoint(sb, Shift(multi.Points[i + 2]));
                        }
                        break;
                    case QuadraticBezierSegment quad:
                        sb.Append('Q');
                        AppendPoint(sb, Shift(quad.Point1));
                        AppendPoint(sb, Shift(quad.Point2));
                        break;
                    case PolyQuadraticBezierSegment multiQuad:
                        for (int i = 0; i + 1 < multiQuad.Points.Count; i += 2)
                        {
                            sb.Append('Q');
                            AppendPoint(sb, Shift(multiQuad.Points[i]));
                            AppendPoint(sb, Shift(multiQuad.Points[i + 1]));
                        }
                        break;
                    case ArcSegment arc:
                        Point end = Shift(arc.Point);
                        sb.Append('A').Append(N(arc.Size.Width)).Append(' ').Append(N(arc.Size.Height))
                          .Append(' ').Append(N(arc.RotationAngle)).Append(' ').Append(arc.IsLargeArc ? '1' : '0')
                          .Append(' ').Append(arc.SweepDirection == SweepDirection.Clockwise ? '1' : '0')
                          .Append(' ').Append(N(end.X)).Append(' ').Append(N(end.Y));
                        break;
                }
            }
            if (figure.IsClosed) sb.Append('Z');
        }
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, char command, Point p)
        => sb.Append(command).Append(N(p.X)).Append(' ').Append(N(p.Y));

    private static void AppendPoint(StringBuilder sb, Point p)
        => sb.Append(N(p.X)).Append(' ').Append(N(p.Y)).Append(' ');

    // ---- C# (WPF path mini-language) ---------------------------------------

    private static string BuildCSharp(List<Shape> shapes, double width, double height)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("// LanDisk 品牌标识的字形轮廓，由 tools/logogen 在 build 时从真实字体轮廓生成。");
        sb.AppendLine("// 这里是纯矢量路径（WPF PathGeometry 迷你语言）：不含位图、不含 base64、不引用 SVG 文件；");
        sb.AppendLine("// 安装器把这些字符串解析成冻结的 Geometry 直接渲染，运行时不做任何 rasterize。");
        sb.AppendLine("// 要改品牌形状请改 tools/logogen 并重新生成，不要手改本文件。");
        sb.AppendLine();
        sb.AppendLine("using System.Windows.Media;");
        sb.AppendLine();
        sb.AppendLine("namespace LanDisk.Setup.Visual;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>The LanDisk brand mark as vector geometry.</summary>");
        sb.AppendLine("public static class LanDiskLogoGeometry");
        sb.AppendLine("{");
        sb.AppendLine($"    /// <summary>Design-space size of the composed mark ({N(width)} x {N(height)} units).</summary>");
        sb.AppendLine($"    public const double DesignWidth = {N(width)};");
        sb.AppendLine($"    public const double DesignHeight = {N(height)};");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Wordmark outlines (LanDisk).</summary>");
        sb.AppendLine("    public static readonly string[] Wordmark =");
        sb.AppendLine("    [");
        foreach (Shape shape in shapes.Where(s => s.Group == 0))
            sb.AppendLine($"        \"{PathData(shape)}\",");
        sb.AppendLine("    ];");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Tagline outlines (让数据更自由).</summary>");
        sb.AppendLine("    public static readonly string[] Tagline =");
        sb.AppendLine("    [");
        foreach (Shape shape in shapes.Where(s => s.Group == 1))
            sb.AppendLine($"        \"{PathData(shape)}\",");
        sb.AppendLine("    ];");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Parses one outline into a frozen geometry.</summary>");
        sb.AppendLine("    public static Geometry Parse(string pathData)");
        sb.AppendLine("    {");
        sb.AppendLine("        var geometry = Geometry.Parse(pathData);");
        sb.AppendLine("        geometry.Freeze();");
        sb.AppendLine("        return geometry;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
