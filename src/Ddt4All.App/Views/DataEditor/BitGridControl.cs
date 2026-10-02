using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Ddt4All.App.ViewModels;

namespace Ddt4All.App.Views;

public static class BitGridPalette
{
    private static readonly Color[] Colors =
    [
        Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0x10, 0xB9, 0x81), Color.FromRgb(0xF5, 0x9E, 0x0B), Color.FromRgb(0xA8, 0x55, 0xF7),
        Color.FromRgb(0xEC, 0x48, 0x99), Color.FromRgb(0x14, 0xB8, 0xA6), Color.FromRgb(0xEF, 0x44, 0x44), Color.FromRgb(0x84, 0xCC, 0x16),
    ];
    public static Color At(int i) => Colors[((i % Colors.Length) + Colors.Length) % Colors.Length];
    public static IBrush Brush(int i) => new SolidColorBrush(At(i));
}

/// <summary>Bit/byte layout viewer: one row per frame byte, eight bit cells (bit 7 .. bit 0) coloured per data item.</summary>
public sealed class BitGridControl : Control
{
    public static readonly StyledProperty<BitGridModel?> ModelProperty = AvaloniaProperty.Register<BitGridControl, BitGridModel?>(nameof(Model));
    public static readonly StyledProperty<int> SelectedItemProperty = AvaloniaProperty.Register<BitGridControl, int>(nameof(SelectedItem), -1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public const double LabelWidth = 92, CellWidth = 34, CellHeight = 26, HeaderHeight = 20;

    static BitGridControl()
    {
        AffectsRender<BitGridControl>(ModelProperty, SelectedItemProperty);
        AffectsMeasure<BitGridControl>(ModelProperty);
    }

    public BitGridModel? Model { get => GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public int SelectedItem { get => GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        int rows = Model?.ByteCount ?? 0;
        return new Size(LabelWidth + 8 * CellWidth + 2, HeaderHeight + Math.Max(1, rows) * CellHeight + 2);
    }

    private IBrush Res(string key, IBrush fallback) => this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : fallback;

    private static FormattedText Text(string s, double size, IBrush brush, bool bold = false) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal), size, brush);

    public override void Render(DrawingContext ctx)
    {
        var m = Model;
        var muted = Res("TextMutedBrush", Brushes.Gray);
        var fg = Res("SystemControlForegroundBaseHighBrush", Brushes.Black);
        var line = new Pen(Res("BorderSubtleBrush", Brushes.LightGray), 1);
        var empty = Res("SurfaceAltBrush", Brushes.WhiteSmoke);
        var danger = Res("DangerBrush", Brushes.Red);

        for (int c = 0; c < 8; c++)
        {
            var t = Text((7 - c).ToString(CultureInfo.InvariantCulture), 10, muted);
            ctx.DrawText(t, new Point(LabelWidth + c * CellWidth + (CellWidth - t.Width) / 2, 3));
        }

        if (m == null || m.ByteCount == 0)
        {
            var t = Text("—", 12, muted);
            ctx.DrawText(t, new Point(LabelWidth + 4, HeaderHeight + 4));
            return;
        }

        for (int b = 0; b < m.ByteCount; b++)
        {
            double y = HeaderHeight + b * CellHeight;
            var lab = Text("Byte " + (b + 1).ToString(CultureInfo.InvariantCulture), 11, muted);
            ctx.DrawText(lab, new Point(2, y + (CellHeight - lab.Height) / 2));
            var val = m.ByteValues[b];
            if (val.Length > 0)
            {
                var vt = Text(val, 11, fg, true);
                ctx.DrawText(vt, new Point(LabelWidth - 8 - vt.Width, y + (CellHeight - vt.Height) / 2));
            }

            for (int c = 0; c < 8; c++)
            {
                int item = m.Cells[b * 8 + c];
                IBrush fill = item == BitGridModel.Free ? empty : item == BitGridModel.Overlap ? danger : new SolidColorBrush(BitGridPalette.At(item), item == SelectedItem ? 0.85 : 0.5);
                var r = new Rect(LabelWidth + c * CellWidth, y, CellWidth, CellHeight);
                ctx.DrawRectangle(fill, line, r);
            }
        }

        foreach (var s in m.Spans)
        {
            double x = LabelWidth + s.Col * CellWidth, y = HeaderHeight + s.Byte * CellHeight, w = s.Count * CellWidth;
            if (s.Item == SelectedItem)
                ctx.DrawRectangle(null, new Pen(fg, 2), new Rect(x + 1, y + 1, w - 2, CellHeight - 2));
            var t = Text(m.Names[s.Item], 11, fg, true);
            t.MaxTextWidth = Math.Max(1, w - 6);
            t.MaxTextHeight = CellHeight;
            t.Trimming = TextTrimming.CharacterEllipsis;
            ctx.DrawText(t, new Point(x + 4, y + (CellHeight - t.Height) / 2));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var m = Model;
        if (m == null) return;
        var p = e.GetPosition(this);
        int c = (int)((p.X - LabelWidth) / CellWidth), b = (int)((p.Y - HeaderHeight) / CellHeight);
        if (c < 0 || c > 7 || b < 0 || b >= m.ByteCount) return;
        int item = m.Cells[b * 8 + c];
        if (item >= 0) SelectedItem = item;
    }
}
