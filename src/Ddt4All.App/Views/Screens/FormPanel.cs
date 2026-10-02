using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Templates;
using Avalonia.Media;
using Avalonia.Metadata;
using Ddt4All.App.ViewModels.Screens;

namespace Ddt4All.App.Views.Screens;

/// <summary>Picks the DataTemplate by row type (first match wins).</summary>
public sealed class FormTemplateSelector : IDataTemplate
{
    [Content] public List<DataTemplate> Templates { get; } = new();
    public bool Match(object? data) => data is FormRow;
    public Control? Build(object? param)
    {
        foreach (var t in Templates) if (t.Match(param)) return t.Build(param);
        return null;
    }
}

/// <summary>Result of laying the rows out for one width: a rectangle per row plus the card (section) rectangles.</summary>
public sealed class FormLayout
{
    public Rect[] Rects = Array.Empty<Rect>();
    public List<(Rect Rect, int First, int Count)> Cards = new();
    public double Height;
    public int Columns;
    public double Width;

    public const double Margin = 2, Gap = 12, CardPad = 12, TileGap = 10, RowGap = 8, HeadingGap = 2;

    public static FormLayout Compute(IReadOnlyList<FormRow> rows, double width, double minColumn)
    {
        var res = new FormLayout { Rects = new Rect[rows.Count], Width = width };
        double w = Math.Max(120, width - 2 * Margin);
        int cols = Math.Clamp((int)Math.Floor((w + Gap) / (minColumn + Gap)), 1, 6);
        res.Columns = cols;
        double colW = (w - Gap * (cols - 1)) / cols;
        var colH = new double[cols];

        int sectionCount = 0;
        for (int q = 0; q < rows.Count; q++) if (q == 0 || rows[q].Section != rows[q - 1].Section) sectionCount++;
        int i = 0;
        while (i < rows.Count)
        {
            int j = i; int sec = rows[i].Section;
            while (j < rows.Count && rows[j].Section == sec) j++;
            int span = sectionCount == 1 ? cols : SpanFor(rows, i, j, cols);
            double cardW = span * colW + Gap * (span - 1);
            double inner = cardW - 2 * CardPad;
            int tc = span;                      // tile columns inside the card
            double tileW = (inner - TileGap * (tc - 1)) / tc;

            // flow inside the card (card-local coordinates, shifted to page coordinates below)
            double y = CardPad; int col = 0; double rowMax = 0; var rowStart = i; var local = new Rect[j - i];
            void FlushRow(int from, int to, double h)
            {
                for (int k = from; k < to; k++) local[k - i] = new Rect(local[k - i].X, local[k - i].Y, local[k - i].Width, h);
            }
            int firstInRow = i;
            for (int k = i; k < j; k++)
            {
                var r = rows[k];
                if (r.Kind == FormRowKind.Heading)
                {
                    double h = r.GetHeight(inner);
                    local[k - i] = new Rect(CardPad, y, inner, h);
                    y += h + HeadingGap; firstInRow = k + 1; continue;
                }
                if (r.FullWidth)
                {
                    if (col > 0) { FlushRow(firstInRow, k, rowMax); y += rowMax + RowGap; col = 0; rowMax = 0; }
                    double h = r.GetHeight(inner);
                    local[k - i] = new Rect(CardPad, y, inner, h);
                    y += h + RowGap; firstInRow = k + 1; continue;
                }
                double th = r.GetHeight(tileW);
                local[k - i] = new Rect(CardPad + col * (tileW + TileGap), y, tileW, th);
                rowMax = Math.Max(rowMax, th);
                col++;
                if (col >= tc) { FlushRow(firstInRow, k + 1, rowMax); y += rowMax + RowGap; col = 0; rowMax = 0; firstInRow = k + 1; }
            }
            if (col > 0) { FlushRow(firstInRow, j, rowMax); y += rowMax + RowGap; }
            double cardH = y - RowGap + CardPad;

            // masonry: lowest run of `span` columns
            int bestCol = 0; double bestY = double.MaxValue;
            for (int c = 0; c + span <= cols; c++)
            {
                double top = 0; for (int q = c; q < c + span; q++) top = Math.Max(top, colH[q]);
                if (top < bestY - 0.5) { bestY = top; bestCol = c; }
            }
            double x0 = Margin + bestCol * (colW + Gap);
            for (int k = i; k < j; k++) res.Rects[k] = new Rect(local[k - i].X + x0, local[k - i].Y + bestY, local[k - i].Width, local[k - i].Height);
            res.Cards.Add((new Rect(x0, bestY, cardW, cardH), i, j - i));
            for (int q = bestCol; q < bestCol + span; q++) colH[q] = bestY + cardH + Gap;
            i = j;
        }
        res.Height = (colH.Length == 0 ? 0 : colH.Max()) - (rows.Count > 0 ? Gap : 0) + Margin;
        if (rows.Count == 0) res.Height = 0;
        return res;
    }

    private static int SpanFor(IReadOnlyList<FormRow> rows, int from, int to, int cols)
    {
        if (cols == 1) return 1;
        int tiles = 0; bool longNote = false;
        for (int k = from; k < to; k++)
        {
            var r = rows[k];
            if (r.Kind is FormRowKind.Display or FormRowKind.Input) tiles++;
            else if (r is FormButtonsRow br) tiles += (br.Buttons.Count + 1) / 2;
            else if (r is FormNoteRow n && n.Text.Length > 90) longNote = true;
        }
        int span = tiles <= 5 ? 1 : tiles <= 14 ? 2 : cols;
        if (longNote) span = Math.Max(span, 2);
        return Math.Min(span, cols);
    }
}

/// <summary>
/// Virtualising "cards of tiles" panel. Sections are cards laid out masonry-style over an adaptive column count; only rows
/// near the viewport have controls (recycled per row kind). Heights are computed, not measured, so 8000 rows cost one O(n) pass.
/// </summary>
public sealed class FormPanel : Panel
{
    public static readonly StyledProperty<IReadOnlyList<FormRow>?> RowsProperty = AvaloniaProperty.Register<FormPanel, IReadOnlyList<FormRow>?>(nameof(Rows));
    public static readonly StyledProperty<double> MinColumnWidthProperty = AvaloniaProperty.Register<FormPanel, double>(nameof(MinColumnWidth), 300);
    public static readonly StyledProperty<IBrush?> CardBrushProperty = AvaloniaProperty.Register<FormPanel, IBrush?>(nameof(CardBrush));
    public static readonly StyledProperty<IBrush?> CardBorderBrushProperty = AvaloniaProperty.Register<FormPanel, IBrush?>(nameof(CardBorderBrush));
    public static readonly DirectProperty<FormPanel, IDataTemplate?> ItemTemplateProperty =
        AvaloniaProperty.RegisterDirect<FormPanel, IDataTemplate?>(nameof(ItemTemplate), o => o.ItemTemplate, (o, v) => o.ItemTemplate = v);

    private IDataTemplate? _template;
    private FormLayout _layout = new();
    private double _layoutWidth = -1;
    private IReadOnlyList<FormRow>? _layoutRows;
    private Rect _viewport = new(0, 0, 0, 900);
    private Rect _window;
    private readonly Dictionary<int, Control> _realized = new();
    private readonly Dictionary<string, Stack<Control>> _pool = new();
    private readonly List<int> _tmp = new();

    static FormPanel()
    {
        AffectsMeasure<FormPanel>(RowsProperty, MinColumnWidthProperty);
        CardBrushProperty.Changed.AddClassHandler<FormPanel>((p, _) => p._cards.InvalidateVisual());
        CardBorderBrushProperty.Changed.AddClassHandler<FormPanel>((p, _) => p._cards.InvalidateVisual());
    }

    private readonly CardsLayer _cards;
    public FormPanel() { EffectiveViewportChanged += OnViewport; _cards = new CardsLayer(this) { IsHitTestVisible = false }; Children.Add(_cards); }

    public IReadOnlyList<FormRow>? Rows { get => GetValue(RowsProperty); set => SetValue(RowsProperty, value); }
    public double MinColumnWidth { get => GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }
    public IBrush? CardBrush { get => GetValue(CardBrushProperty); set => SetValue(CardBrushProperty, value); }
    public IBrush? CardBorderBrush { get => GetValue(CardBorderBrushProperty); set => SetValue(CardBorderBrushProperty, value); }
    public IDataTemplate? ItemTemplate { get => _template; set { if (SetAndRaise(ItemTemplateProperty, ref _template, value)) { ClearRealized(); InvalidateMeasure(); } } }

    /// <summary>Rows that currently have a control (tests / diagnostics).</summary>
    public int RealizedCount => _realized.Count;
    public FormLayout Layout => _layout;
    public IEnumerable<Control> RealizedControls => _realized.Values;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RowsProperty) { ClearRealized(); _layoutWidth = -1; }
    }

    private void OnViewport(object? sender, EffectiveViewportChangedEventArgs e)
    {
        _viewport = e.EffectiveViewport;
        if (_viewport.Height <= 0) return;
        if (_window.Height == 0 || _viewport.Top < _window.Top || _viewport.Bottom > _window.Bottom) { InvalidateMeasure(); }
        _cards.InvalidateVisual();
    }

    private void ClearRealized()
    {
        foreach (var (idx, c) in _realized) { Children.Remove(c); Recycle(c); }
        _realized.Clear(); _window = default;
    }

    private void Recycle(Control c)
    {
        if (c.DataContext is not FormRow r) return;
        if (!_pool.TryGetValue(r.PoolKey, out var st)) _pool[r.PoolKey] = st = new Stack<Control>();
        if (st.Count < 400) { c.DataContext = null; st.Push(c); }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0 ? 800 : availableSize.Width;
        var rows = Rows;
        if (rows is null || rows.Count == 0 || _template is null)
        {
            ClearRealized(); _layout = new FormLayout(); _layoutRows = rows;
            return new Size(width, 0);
        }
        if (!ReferenceEquals(rows, _layoutRows) || Math.Abs(width - _layoutWidth) > 0.5)
        {
            _layout = FormLayout.Compute(rows, width, MinColumnWidth);
            _layoutRows = rows; _layoutWidth = width;
            ClearRealized();
        }
        Realize(rows);
        _cards.Measure(new Size(width, _layout.Height));
        _cards.InvalidateVisual();
        return new Size(width, _layout.Height);
    }

    private void Realize(IReadOnlyList<FormRow> rows)
    {
        double vh = _viewport.Height > 0 ? _viewport.Height : 900;
        double buffer = Math.Max(200, vh * 0.35);
        double top = _viewport.Top - buffer, bottom = _viewport.Bottom + buffer;
        _window = new Rect(0, top, Math.Max(1, _layout.Width), bottom - top);
        var rects = _layout.Rects;

        // drop what left the window
        _tmp.Clear();
        foreach (var (idx, c) in _realized)
            if (idx >= rects.Length || rects[idx].Bottom < top || rects[idx].Top > bottom || !ReferenceEquals(c.DataContext, rows[idx])) _tmp.Add(idx);
        foreach (var idx in _tmp) { var c = _realized[idx]; _realized.Remove(idx); Children.Remove(c); Recycle(c); }

        for (int i = 0; i < rects.Length; i++)
        {
            var r = rects[i];
            if (r.Bottom < top || r.Top > bottom) continue;
            if (!_realized.TryGetValue(i, out var ctl))
            {
                var row = rows[i];
                if (_pool.TryGetValue(row.PoolKey, out var st) && st.Count > 0) { ctl = st.Pop(); ctl.DataContext = row; }
                else
                {
                    ctl = _template!.Build(row);
                    if (ctl is null) continue;
                    ctl.DataContext = row;
                }
                _realized[i] = ctl; Children.Add(ctl);
            }
            ctl.Measure(new Size(r.Width, r.Height));
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var rects = _layout.Rects;
        _cards.Arrange(new Rect(0, 0, finalSize.Width, Math.Max(finalSize.Height, _layout.Height)));
        foreach (var (idx, c) in _realized) if (idx < rects.Length) c.Arrange(rects[idx]);
        return finalSize;
    }

    /// <summary>Paints the section cards (always child 0, below the tiles).</summary>
    private sealed class CardsLayer(FormPanel owner) : Control
    {
        public override void Render(DrawingContext context)
        {
            var fill = owner.CardBrush; var pen = owner.CardBorderBrush is { } b ? new Pen(b, 1) : null;
            if (fill is null && pen is null) return;
            double top = owner._viewport.Top - 300, bottom = owner._viewport.Bottom + 300;
            foreach (var (rect, _, _) in owner._layout.Cards)
            {
                if (rect.Bottom < top || rect.Top > bottom) continue;
                context.DrawRectangle(fill, pen, rect.Deflate(0.5), 10, 10);
            }
        }
    }
}
