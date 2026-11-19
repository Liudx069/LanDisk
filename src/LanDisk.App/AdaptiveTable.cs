using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace LanDisk.App;

/// <summary>
/// A table that degrades instead of deforming.
///
/// <para><b>What it replaces.</b> The pages used a grid per row: a header grid, then one grid per row, each
/// sizing its own columns from its own content. That is why a long volume id pushed the status column off the
/// card, why headers stopped lining up with their data once one row had a longer value, and why a row with
/// four action buttons wrapped onto a second line. It also could not respond to anything: the only way to make
/// it fit was to make the window bigger.</para>
///
/// <para><b>How it behaves now.</b> One grid defines the columns, so the header and every row share the same
/// widths by construction. Columns carry a minimum width and a priority; when the available width cannot hold
/// them all, the lowest-priority columns are dropped — and their content is not lost, it moves into the row's
/// details panel, which the row expands in place. Actions live in one column: the primary action as a button,
/// the rest behind a "…" menu, so a row never wraps.</para>
///
/// <para>Everything is ordinary WPF: the panel is a <see cref="Grid"/> and the width decision runs on
/// <c>SizeChanged</c>, which is what makes it testable in the UI capture.</para>
/// </summary>
internal sealed class AdaptiveTable
{
    /// <param name="Header">Column caption.</param>
    /// <param name="MinWidth">Width below which this column is not worth drawing.</param>
    /// <param name="Priority">0 = always shown, 1 = dropped when narrow, 2 = dropped first.</param>
    /// <param name="Mono">Draw values in the monospace stack (addresses, paths, ids).</param>
    internal sealed record Column(string Header, double MinWidth, int Priority = 0, bool Mono = false);

    /// <param name="Text">The value as shown (may be shortened).</param>
    /// <param name="Full">The complete value, used for the tooltip and for the details panel.</param>
    internal sealed record Cell(string Text, string? Full = null, string? Label = null)
    {
        public static Cell Of(string? text) => new(string.IsNullOrWhiteSpace(text) ? "—" : text);
        public string Detail => Full ?? Text;
    }

    /// <summary>One row: cells aligned to the columns, optional action buttons, optional extra details.</summary>
    internal sealed class Row
    {
        public required IReadOnlyList<Cell> Cells { get; init; }
        public IReadOnlyList<UIElement> Actions { get; init; } = [];
        public IReadOnlyList<(string Label, string Value)> Extra { get; init; } = [];
        public UIElement? DetailContent { get; init; }
    }

    private readonly List<Column> _columns;
    private readonly List<Row> _rows;
    private readonly Grid _grid = new();
    private readonly List<ColumnDefinition> _definitions = [];
    private readonly List<List<UIElement>> _cellsByColumn = [];
    private readonly List<StackPanel> _detailsByRow = [];
    private readonly List<Button> _expanders = [];
    private readonly List<UIElement> _actionPanels = [];
    private double _lastWidth = -1;

    private AdaptiveTable(IReadOnlyList<Column> columns, IReadOnlyList<Row> rows)
    {
        _columns = [.. columns];
        _rows = [.. rows];
        Build();
        ApplyWidth(Tokens.NarrowWidth);   // a sane first pass; SizeChanged refines it
    }

    public UIElement Element => _grid;

    /// <summary>
    /// Builds the table. <paramref name="emptyState"/> is shown when there are no rows, because an empty table
    /// with a header and nothing under it reads as a bug.
    /// </summary>
    public static UIElement Build(IReadOnlyList<Column> columns, IReadOnlyList<Row> rows, UIElement? emptyState = null)
    {
        if (rows.Count == 0 && emptyState is not null)
        {
            return emptyState;
        }

        return new AdaptiveTable(columns, rows).Element;
    }

    private void Build()
    {
        _grid.ColumnDefinitions.Clear();
        _definitions.Clear();
        _cellsByColumn.Clear();

        for (int i = 0; i < _columns.Count; i++)
        {
            var definition = new ColumnDefinition { Width = new GridLength(_columns[i].MinWidth, GridUnitType.Pixel) };
            _grid.ColumnDefinitions.Add(definition);
            _definitions.Add(definition);
            _cellsByColumn.Add([]);
        }

        // One extra column for the actions, so they never wrap into the data.
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // header

        // ---- header
        for (int c = 0; c < _columns.Count; c++)
        {
            var header = new TextBlock
            {
                Text = _columns[c].Header,
                FontSize = Tokens.Caption,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, Tokens.Space3, Tokens.Space1),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Ui.Brush("TextTertiary"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(header, c);
            Grid.SetRow(header, 0);
            _grid.Children.Add(header);
            _cellsByColumn[c].Add(header);
        }

        // ---- rows
        for (int r = 0; r < _rows.Count; r++)
        {
            Row row = _rows[r];
            _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });          // data
            _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });          // details

            int dataRow = 1 + r * 2;
            int detailRow = dataRow + 1;

            var rowSurface = new Border
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = Ui.Brush("Border"),
                Padding = new Thickness(0, Tokens.Space2, 0, Tokens.Space2)
            };
            Grid.SetRow(rowSurface, dataRow);
            Grid.SetColumn(rowSurface, 0);
            Grid.SetColumnSpan(rowSurface, _columns.Count + 1);
            _grid.Children.Add(rowSurface);

            for (int c = 0; c < _columns.Count; c++)
            {
                Cell cell = c < row.Cells.Count ? row.Cells[c] : new Cell("—");
                bool mono = _columns[c].Mono;

                var value = new TextBlock
                {
                    Text = cell.Text,
                    FontSize = Tokens.Body,
                    FontFamily = mono ? new FontFamily(Tokens.MonoStack) : new FontFamily(Tokens.FontStack),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap,
                    Margin = new Thickness(0, 0, Tokens.Space3, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = c == 0 ? Ui.Brush("TextPrimary") : Ui.Brush("TextSecondary"),
                    ToolTip = cell.Detail
                };

                var host = new Grid();
                host.Children.Add(value);
                Grid.SetColumn(host, c);
                Grid.SetRow(host, dataRow);
                _grid.Children.Add(host);
                _cellsByColumn[c].Add(host);
            }

            // Actions: primary as a button, the rest behind "…". No wrapping, ever.
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (UIElement action in row.Actions.Take(1)) actions.Children.Add(action);
            if (row.Actions.Count > 1)
            {
                var more = new Button
                {
                    Content = "…",
                    Padding = new Thickness(Tokens.Space2, 2, Tokens.Space2, 2),
                    Margin = new Thickness(Tokens.Space2, 0, 0, 0),
                    MinWidth = 30,
                    ToolTip = "更多操作"
                };
                var menu = new ContextMenu();
                foreach (UIElement action in row.Actions.Skip(1))
                {
                    var item = new MenuItem { Header = action is Button b ? b.Content?.ToString() ?? "操作" : "操作" };
                    if (action is Button source)
                    {
                        item.Click += (_, _) => source.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    }
                    menu.Items.Add(item);
                }
                more.Click += (_, _) => { menu.PlacementTarget = more; menu.IsOpen = true; };
                actions.Children.Add(more);
            }

            var expander = new Button
            {
                Content = "详情",
                FontSize = Tokens.Caption,
                Padding = new Thickness(Tokens.Space2, 2, Tokens.Space2, 2),
                MinWidth = 0,
                Margin = new Thickness(Tokens.Space2, 0, 0, 0),
                Visibility = Visibility.Collapsed
            };
            int captured = r;
            expander.Click += (_, _) =>
            {
                StackPanel panel = _detailsByRow[captured];
                bool show = panel.Visibility != Visibility.Visible;
                panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                expander.Content = show ? "收起" : "详情";
            };
            _expanders.Add(expander);

            // The actions and the details toggle share one cell, so they are laid out as two columns of a
            // single host rather than being added to the same grid cell. Adding both to the same cell drew
            // them on top of each other — that is exactly the "详情 / 只读 / … 互相重叠" the Owner photographed.
            var actionHost = new Grid { VerticalAlignment = VerticalAlignment.Center };
            actionHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            actionHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(actions, 0);
            actionHost.Children.Add(actions);
            Grid.SetColumn(expander, 1);
            actionHost.Children.Add(expander);

            Grid.SetColumn(actionHost, _columns.Count);
            Grid.SetRow(actionHost, dataRow);
            _grid.Children.Add(actionHost);
            _actionPanels.Add(actionHost);

            // Details: everything the narrow layout had to drop, plus anything the row calls extra.
            var details = new StackPanel
            {
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 0, 0, Tokens.Space2)
            };
            foreach ((string label, string value) in row.Extra)
            {
                details.Children.Add(Ui.KeyValue(label, value));
            }
            if (row.DetailContent is not null) details.Children.Add(row.DetailContent);
            Grid.SetRow(details, detailRow);
            Grid.SetColumn(details, 0);
            Grid.SetColumnSpan(details, _columns.Count + 1);
            _grid.Children.Add(details);
            _detailsByRow.Add(details);
        }

        _grid.SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);
    }

    /// <summary>
    /// Decides which columns are drawn at this width. Dropped content is available in the row details, and
    /// the details toggle appears for exactly those rows that have something the drop would hide.
    /// </summary>
    internal void ApplyWidth(double available)
    {
        if (available <= 0 || Math.Abs(available - _lastWidth) < 1) return;
        _lastWidth = available;

        // Actions column keeps a fixed reservation; the rest shares what is left.
        // Room for the primary action, the "…" overflow and the details toggle, plus the 8 DIP gaps
        // between them. The three sit side by side in one cell and must never overlap.
        const double actionsReserve = 232;
        double budget = available - actionsReserve;

        var visible = new List<bool>();
        double used = 0;
        foreach (Column column in _columns)
        {
            visible.Add(true);
            used += column.MinWidth;
        }

        // Drop the least important columns until the columns fit, never dropping priority 0.
        for (int priority = 2; priority >= 1 && used > budget; priority--)
        {
            for (int c = _columns.Count - 1; c >= 0 && used > budget; c--)
            {
                if (!visible[c] || _columns[c].Priority != priority) continue;
                visible[c] = false;
                used -= _columns[c].MinWidth;
            }
        }

        for (int c = 0; c < _columns.Count; c++)
        {
            _definitions[c].Width = visible[c]
                ? new GridLength(Math.Max(_columns[c].MinWidth, (budget - used + _columns[c].MinWidth) / Math.Max(1, visible.Count(v => v))), GridUnitType.Pixel)
                : new GridLength(0);
            foreach (UIElement element in _cellsByColumn[c])
            {
                element.Visibility = visible[c] ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // A row only offers "详情" when something exists to show: dropped columns with real content, or extras.
        for (int r = 0; r < _rows.Count; r++)
        {
            bool droppedContent = false;
            for (int c = 0; c < _columns.Count; c++)
            {
                if (visible[c]) continue;
                if (c < _rows[r].Cells.Count && !string.IsNullOrWhiteSpace(_rows[r].Cells[c].Text)) droppedContent = true;
            }
            bool hasDetails = droppedContent || _rows[r].Extra.Count > 0 || _rows[r].DetailContent is not null;
            _expanders[r].Visibility = hasDetails ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
