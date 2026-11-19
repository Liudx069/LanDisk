using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
// WPF-UI ships controls with the same simple names as WPF itself (Button, TextBlock, ...). Importing
// the whole namespace would make every one of those ambiguous, so only the types actually used are
// brought in by name.
using InfoBar = Wpf.Ui.Controls.InfoBar;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;
using Card = Wpf.Ui.Controls.Card;

namespace LanDisk.App;

/// <summary>
/// Small builders for the page bodies.
///
/// <para>The pages are written in C# rather than XAML on purpose: every one of them is a list, a
/// table or a form whose contents come from the running server, and building them in code keeps the
/// shape of the data and the shape of the screen in the same place instead of splitting it across two
/// files. It is still ordinary WPF — these are the same controls XAML would instantiate.</para>
///
/// <para>Typography follows the design system: body text never drops below 12 DIP, addresses and
/// timestamps are never wrapped (they get a tooltip and a copy button instead, because a column that
/// squeezes an IP address turns it into a vertical column of digits).</para>
/// </summary>
internal static class Ui
{
    public const double BodySize = 13.5;
    public const double SecondarySize = 12.5;
    public const double PageTitleSize = 24;
    public const double SectionSize = 17;

    /// <summary>
    /// Resolves a semantic brush from the active theme profile.
    ///
    /// <para>Pages never name a colour. They name a role — "this is secondary text", "this is a card border"
    /// — and the theme decides what that means, which is what lets five built-in themes and a custom one all
    /// work without a single page knowing about any of them. The fallback exists only so a missing role shows
    /// up as obviously wrong (magenta) in a screenshot instead of silently disappearing.</para>
    /// </summary>
    public static Brush Brush(string role)
    {
        if (Application.Current?.Resources["LanDisk." + role] is Brush brush) return brush;
        return Brushes.Magenta;
    }

    /// <summary>A short label above a value, used in metric tiles and detail panels.</summary>
    public static TextBlock Caption(string text) => new()
    {
        Text = text,
        FontSize = Tokens.Caption,
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
        // Centred by default: a caption used as a control label must not stretch to the height of the row it
        // sits in, which the layout gate reports as a "single-line text height anomaly".
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = Brush("TextTertiary")
    };

    /// <summary>A big number for a dashboard tile, with its unit in the caption rather than the value.</summary>
    /// <summary>
    /// A headline value. Tagged <see cref="PrimaryTag"/> because the UI capture asserts that *these* are never
    /// trimmed: a metric that has to be abbreviated with "…" is a layout failure, not a long value. Data-table
    /// cells are deliberately not tagged — a path or a user agent is allowed to trim into the row details.
    /// </summary>
    public static TextBlock Metric(string text) => new()
    {
        Text = text,
        FontSize = Tokens.MetricLarge,
        FontWeight = FontWeights.SemiBold,
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Foreground = Brush("TextPrimary"),
        Tag = PrimaryTag
    };

    /// <summary>Marks a value that must never be shown as an ellipsis.</summary>
    public const string PrimaryTag = "landisk-primary";

    /// <summary>A single key/value line: label left, selectable monospace value right.</summary>
    public static Grid KeyValue(string label, string? value) => KeyValues((label, value));

    /// <summary>
    /// The header every page starts with: what this page is, one sentence about it, and the actions that
    /// belong to the page as a whole. Keeping it uniform is what makes the fifteen pages feel like one
    /// product instead of fifteen tools.
    /// </summary>
    public static StackPanel PageHeader(string title, string description, params UIElement[] actions)
    {
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, Tokens.Space4) };
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = Tokens.PageTitle,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextPrimary"),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (!string.IsNullOrWhiteSpace(description))
        {
            header.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = Tokens.Body,
                Margin = new Thickness(0, Tokens.Space1, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextSecondary"),
                MaxWidth = 900,
                HorizontalAlignment = HorizontalAlignment.Left
            });
        }
        if (actions.Length > 0)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, Tokens.Space3, 0, 0)
            };
            foreach (UIElement action in actions) row.Children.Add(action);
            header.Children.Add(row);
        }
        return header;
    }

    /// <summary>
    /// A card: one titled surface. The title is a subtitle rather than a page title, and the card owns its
    /// padding and radius so every card in the product is the same shape.
    /// </summary>
    public static Border Card(string? title, string? subtitle = null, params UIElement[] content)
    {
        var stack = new StackPanel();
        if (!string.IsNullOrWhiteSpace(title))
        {
            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = Tokens.Subtitle,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("TextPrimary"),
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            stack.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = Tokens.Caption,
                Margin = new Thickness(0, 2, 0, Tokens.Space2),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextTertiary")
            });
        }
        else if (content.Length > 0)
        {
            stack.Children.Add(new Border { Height = Tokens.Space2 });
        }

        foreach (UIElement element in content) stack.Children.Add(element);

        return new Border
        {
            Background = Brush("Surface"),
            BorderBrush = Brush("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Tokens.RadiusLarge),
            Padding = Tokens.CardPadding,
            Margin = new Thickness(0, 0, 0, Tokens.Space4),
            Child = stack
        };
    }

    /// <summary>
    /// A status badge: an icon, a word, and a colour — in that order of importance. Status is never conveyed
    /// by colour alone, because a colour-blind operator and a greyscale screenshot both have to read it.
    /// </summary>
    public static Border StatusBadge(string text, string tone)
    {
        (string brush, string glyph) = tone switch
        {
            "success" => ("Success", "✓"),
            "warning" => ("Warning", "!"),
            "danger" => ("Error", "×"),
            "accent" => ("AccentCyan", "●"),
            _ => ("Info", "i")
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = glyph,
            FontSize = Tokens.Caption,
            FontWeight = FontWeights.Bold,
            Tag = "landisk-glyph",
            Margin = new Thickness(0, 0, Tokens.Space1 + 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush(brush)
        });
        row.Children.Add(new TextBlock
        {
            Text = text,
            Tag = PrimaryTag,
            FontSize = Tokens.Caption,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("TextSecondary")
        });

        return new Border
        {
            Background = Brush("Control"),
            BorderBrush = Brush("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Tokens.RadiusSmall + 4),
            Padding = new Thickness(Tokens.Space2, 2, Tokens.Space2 + 2, 2),
            Margin = new Thickness(0, 0, Tokens.Space2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = row
        };
    }

    /// <summary>The presence/state badge for a device or a share, mapped from the wire value.</summary>
    public static Border PresenceBadge(string wire)
    {
        (string text, string tone) = wire switch
        {
            "online" => ("在线", "success"),
            "recently-active" => ("最近活动", "accent"),
            "revoked" => ("已撤销", "danger"),
            _ => ("离线", "warning")
        };
        return StatusBadge(text, tone);
    }

    /// <summary>An empty state that says what would be here and what to do — never a bare blank panel.</summary>
    public static Border EmptyState(string title, string hint) => new()
    {
        Background = Brush("Surface"),
        BorderBrush = Brush("Border"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(Tokens.RadiusLarge),
        Padding = new Thickness(Tokens.Space6, Tokens.Space6, Tokens.Space6, Tokens.Space6),
        Margin = new Thickness(0, 0, 0, Tokens.Space4),
        Child = new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = Tokens.Subtitle,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = Brush("TextPrimary")
                },
                new TextBlock
                {
                    Text = hint,
                    FontSize = Tokens.Body,
                    Margin = new Thickness(0, Tokens.Space2, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    Foreground = Brush("TextSecondary")
                }
            }
        }
    };

    /// <summary>A long value with a copy button: the readable way to offer a path or an id without letting it
    /// stretch the layout.</summary>
    public static Grid CopyableValue(string value, double maxWidth = 420)
    {
        var grid = new Grid { MaxWidth = maxWidth, HorizontalAlignment = HorizontalAlignment.Left };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        TextBox box = Mono(value);
        box.MaxWidth = maxWidth - 60;
        Grid.SetColumn(box, 0);
        grid.Children.Add(box);

        var copy = new Button
        {
            Content = "复制",
            FontSize = Tokens.Caption,
            Padding = new Thickness(Tokens.Space2, 1, Tokens.Space2, 1),
            Margin = new Thickness(Tokens.Space2, 0, 0, 0),
            ToolTip = "复制到剪贴板"
        };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(value); copy.Content = "已复制"; }
            catch { copy.Content = "复制失败"; }
        };
        Grid.SetColumn(copy, 1);
        grid.Children.Add(copy);
        return grid;
    }

    public static TextBlock Title(string text) => new()
    {
        Text = text,
        FontSize = PageTitleSize,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 4),
        // Explicit: inheriting from the window chrome produced dark-on-dark titles under the dark
        // palette, which the screenshot evidence caught.
        Foreground = (Brush)Application.Current.Resources["LanDisk.TextPrimary"]
    };

    public static TextBlock Section(string text) => new()
    {
        Text = text,
        FontSize = SectionSize,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 16, 0, 8),
        Foreground = (Brush)Application.Current.Resources["LanDisk.TextPrimary"]
    };

    public static TextBlock Body(string text, bool secondary = false) => new()
    {
        Text = text,
        FontSize = secondary ? SecondarySize : BodySize,
        TextWrapping = TextWrapping.Wrap,
        Foreground = secondary ? (Brush)Application.Current.Resources["LanDisk.TextSecondary"]
                               : (Brush)Application.Current.Resources["LanDisk.TextPrimary"],
        Margin = new Thickness(0, 0, 0, secondary ? 2 : 6)
    };

    /// <summary>An address, a path or a timestamp: never wrapped, always selectable and copyable.</summary>
    public static TextBox Mono(string text) => new()
    {
        // Tagged: a mono value is a primary value, and a read-only TextBox reports clipping through
        // Extent/Viewport rather than DesiredSize, so the capture measures it differently.
        Tag = PrimaryTag,
        Text = text,
        IsReadOnly = true,
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
        FontSize = SecondarySize,
        TextWrapping = TextWrapping.NoWrap,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(0),
        Margin = new Thickness(0, 0, 0, 2),
        ToolTip = text,
        Foreground = (Brush)Application.Current.Resources["LanDisk.TextPrimary"]
    };

    /// <summary>
    /// A panel is an explicit Border with the design system's own surface colour rather than WPF-UI's
    /// Card. The Card control resolves its fill from the theme dictionary, and on this Windows 10 build
    /// that produced a light fill under the dark palette — unreadable body text, which the UI screenshot
    /// evidence caught. Owning the brush removes the dependency entirely.
    /// </summary>
    public static Border Panel(string title, params UIElement[] content)
    {
        var stack = new StackPanel();
        if (!string.IsNullOrEmpty(title)) stack.Children.Add(Section(title));
        foreach (UIElement element in content) stack.Children.Add(element);

        return Surface(stack);
    }

    public static Border Surface(UIElement content, Thickness? padding = null) => new()
    {
        Background = (Brush)Application.Current.Resources["LanDisk.Surface"],
        BorderBrush = (Brush)Application.Current.Resources["LanDisk.Secondary"],
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = padding ?? new Thickness(16),
        Margin = new Thickness(0, 0, 0, 12),
        Child = content
    };

    public static InfoBar Notice(string message, InfoBarSeverity severity = InfoBarSeverity.Informational) => new()
    {
        Message = message,
        Severity = severity,
        IsOpen = true,
        IsClosable = false,
        Margin = new Thickness(0, 0, 0, 12)
    };

    /// <summary>A key/value table. Values are selectable and never wrap.</summary>
    public static Grid KeyValues(params (string Key, string? Value)[] rows)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var key = new TextBlock
            {
                Text = rows[i].Key,
                FontSize = SecondarySize,
                Margin = new Thickness(0, 3, 12, 3),
                Foreground = (Brush)Application.Current.Resources["LanDisk.TextSecondary"]
            };
            Grid.SetRow(key, i);
            Grid.SetColumn(key, 0);
            grid.Children.Add(key);

            UIElement value = Mono(string.IsNullOrEmpty(rows[i].Value) ? "—" : rows[i].Value!);
            // A key/value value is a primary value: "HTTPS 已…" is exactly what the Owner saw where
            // "HTTPS 已启用" should have been, so the capture treats trimming here as a failure.
            if (value is TextBlock valueText) valueText.Tag = PrimaryTag;
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
        }

        return grid;
    }

    /// <summary>
    /// A table that degrades instead of squeezing. The last column takes the slack; every other column
    /// is sized to its content and clipped with an ellipsis plus a tooltip rather than wrapped, because
    /// a wrapped IP address or timestamp becomes a vertical column of digits — the exact defect the web
    /// page had.
    /// </summary>
    public static StackPanel TableRows(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var panel = new StackPanel();

        Grid header = NewRowGrid(headers.Count);
        for (int c = 0; c < headers.Count; c++)
        {
            var cell = new TextBlock
            {
                Text = headers[c],
                FontSize = SecondarySize,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 12, 6),
                Foreground = (Brush)Application.Current.Resources["LanDisk.TextSecondary"]
            };
            Grid.SetColumn(cell, c);
            header.Children.Add(cell);
        }
        panel.Children.Add(header);

        foreach (IReadOnlyList<string> row in rows)
        {
            Grid line = NewRowGrid(headers.Count);
            for (int c = 0; c < headers.Count; c++)
            {
                string text = c < row.Count ? row[c] : string.Empty;
                var cell = new TextBlock
                {
                    Text = text,
                    FontSize = BodySize,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 4, 12, 4),
                    ToolTip = text,
                    Foreground = (Brush)Application.Current.Resources["LanDisk.TextPrimary"]
                };
                Grid.SetColumn(cell, c);
                line.Children.Add(cell);
            }
            panel.Children.Add(line);
        }

        if (rows.Count == 0) panel.Children.Add(Body("（暂无数据）", secondary: true));

        return panel;
    }

    private static Grid NewRowGrid(int columns)
    {
        var grid = new Grid();
        for (int c = 0; c < columns; c++)
        {
            bool last = c == columns - 1;
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = last ? new GridLength(1, GridUnitType.Star) : GridLength.Auto,
                MinWidth = last ? 120 : 80
            });
        }
        return grid;
    }

    public static StackPanel Row(params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        foreach (UIElement child in children)
        {
            if (child is FrameworkElement element) element.Margin = new Thickness(0, 0, 8, 0);
            panel.Children.Add(child);
        }
        return panel;
    }

    public static Button Action(string text, Func<Task> onClick, bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            FontSize = BodySize,
            Padding = new Thickness(14, 7, 14, 7),
            MinHeight = 32,
            // A default gap so a hand-built toolbar cannot end up with buttons touching. Ui.Row sets
            // the same value for everything it lays out, so this never doubles up; it only rescues the
            // panels assembled inline — which is where the Owner saw controls "贴得太紧".
            Margin = new Thickness(0, 0, Tokens.Space2, 0)
        };
        if (primary) button.SetResourceReference(FrameworkElement.StyleProperty, "LanDisk.PrimaryButton");

        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await onClick(); }
            finally { button.IsEnabled = true; }
        };

        return button;
    }

    public static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(4, 0, 12, 0)
    };

    /// <summary>A coloured status dot plus a word — status is never conveyed by colour alone.</summary>
    public static StackPanel Status(bool ok, string textPositive, string textNegative)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 10,
            Height = 10,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = (Brush)Application.Current.Resources[ok ? "LanDisk.Success" : "LanDisk.Error"]
        });
        panel.Children.Add(new TextBlock
        {
            Text = ok ? textPositive : textNegative,
            FontSize = BodySize,
            VerticalAlignment = VerticalAlignment.Center
        });
        return panel;
    }
}
