using System.Windows;

namespace LanDisk.App;

/// <summary>
/// The design tokens the whole Windows UI is written against.
///
/// <para><b>Why constants instead of magic numbers.</b> A page that writes <c>new Thickness(0, 14, 0, 6)</c>
/// cannot be made consistent with the page next to it, and "the spacing looks wrong somewhere" is then
/// impossible to fix in one place. Every gap in the product is one of these seven values, every corner is
/// one of three radii, and every text size is one of seven steps — which is also what makes the layout
/// assertions in the UI evidence meaningful: a page cannot pass by inventing its own scale.</para>
///
/// <para>Colours are <i>not</i> here: they are semantic roles resolved from the active theme profile (see
/// <c>ThemeCatalog</c> and <c>Theme.xaml</c>), so the same page works under every theme without knowing what
/// any of them look like.</para>
/// </summary>
internal static class Tokens
{
    // Spacing: 4 / 8 / 12 / 16 / 20 / 24 / 32, named by intent so a reader can tell a card gap from a
    // section gap without counting pixels.
    public const double Space1 = 4;
    public const double Space2 = 8;
    public const double Space3 = 12;
    public const double Space4 = 16;
    public const double Space5 = 20;
    public const double Space6 = 24;
    public const double Space7 = 32;

    /// <summary>Outside padding of a page body. Tightened for the compact desktop density.</summary>
    public static Thickness PagePadding => new(Space5, Space4, Space5, Space5);

    /// <summary>Inside padding of a card or panel. Tightened: the Owner's complaint was cards eating the page.</summary>
    public static Thickness CardPadding => new(Space4, Space3, Space3, Space3);

    /// <summary>The gap between siblings inside a card.</summary>
    public static Thickness CardGap => new(0, 0, 0, Space1 + 2);

    public const double RadiusSmall = 4;
    public const double RadiusMedium = 8;
    public const double RadiusLarge = 12;

    // Typography. Nothing in the product is smaller than 12 DIP, which the layout gate enforces.
    // 12 是硬下限：界面证据里有一条门禁断言"任何文字都不得小于 12 DIP"，用来防止"为了塞下内容把字缩小"。
    public const double Caption = 12;
    public const double Body = 13.5;
    public const double BodyStrong = 13.5;
    public const double Subtitle = 15;
    public const double Title = 18;

    // Compact desktop density.
    //
    // The 1.7.1 numbers were chosen as if every page were a dashboard: a 24 DIP page title and a 30 DIP
    // headline metric made a 1920x1080 screen show four cards and nothing else, and values as short as
    // "HTTPS 已启用" were trimmed because the metric box could not hold them. The floor stays at 12 DIP —
    // shrinking text to fit is not a fix — so the reduction comes out of the headline sizes and the padding
    // instead. These are the Owner's requested ranges: page title 22–24, primary metric 22–26,
    // ordinary metric 18–22.
    public const double PageTitle = 22;

    /// <summary>A headline metric (the number a card exists to show).</summary>
    public const double MetricLarge = 24;

    /// <summary>An ordinary metric: secondary numbers inside a card or a table summary.</summary>
    public const double MetricSmall = 20;

    /// <summary>Windows' own font stack, in the order Windows itself prefers it.</summary>
    public const string FontStack = "Segoe UI Variable Text, Segoe UI Variable, Segoe UI, Microsoft YaHei UI";

    /// <summary>Monospace stack for addresses, paths, ids and timestamps.</summary>
    public const string MonoStack = "Cascadia Mono, Consolas, Courier New";

    /// <summary>Below this width the rail collapses its captions and tables drop their optional columns.</summary>
    public const double CompactWidth = 1280;

    /// <summary>Below this width tables keep only the essential columns and move the rest into details.</summary>
    public const double NarrowWidth = 1020;
}
