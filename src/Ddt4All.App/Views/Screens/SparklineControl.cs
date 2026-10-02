using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Ddt4All.App.Views.Screens;

/// <summary>Minimal line graph of the most recent samples (auto-scaled, min/max labels).</summary>
public sealed class SparklineControl : Control
{
    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<SparklineControl, IBrush?>(nameof(Stroke), Brushes.DodgerBlue);
    public static readonly StyledProperty<IBrush?> TextBrushProperty = AvaloniaProperty.Register<SparklineControl, IBrush?>(nameof(TextBrush), Brushes.Gray);
    public static readonly StyledProperty<IBrush?> GridBrushProperty = AvaloniaProperty.Register<SparklineControl, IBrush?>(nameof(GridBrush), Brushes.LightGray);

    private double[] _v = Array.Empty<double>();
    private int _n;

    static SparklineControl() => AffectsRender<SparklineControl>(StrokeProperty, TextBrushProperty, GridBrushProperty);

    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }

    public int Count => _n;

    public void SetData(IReadOnlyList<double> values)
    {
        if (_v.Length < values.Count) _v = new double[Math.Max(values.Count, 64)];
        for (int i = 0; i < values.Count; i++) _v[i] = values[i];
        _n = values.Count;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var r = new Rect(Bounds.Size);
        if (r.Width < 10 || r.Height < 10) return;
        var pad = new Rect(48, 6, Math.Max(1, r.Width - 54), Math.Max(1, r.Height - 12));
        var grid = new Pen(GridBrush, 1);
        ctx.DrawLine(grid, new Point(pad.Left, pad.Top), new Point(pad.Right, pad.Top));
        ctx.DrawLine(grid, new Point(pad.Left, pad.Center.Y), new Point(pad.Right, pad.Center.Y));
        ctx.DrawLine(grid, new Point(pad.Left, pad.Bottom), new Point(pad.Right, pad.Bottom));
        if (_n < 2) return;

        double min = double.MaxValue, max = double.MinValue;
        for (int i = 0; i < _n; i++) { if (_v[i] < min) min = _v[i]; if (_v[i] > max) max = _v[i]; }
        double span = max - min;
        if (span < 1e-9) { min -= 0.5; max += 0.5; span = 1; }

        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            for (int i = 0; i < _n; i++)
            {
                var p = new Point(pad.Left + pad.Width * i / (_n - 1), pad.Bottom - pad.Height * (_v[i] - min) / span);
                if (i == 0) c.BeginFigure(p, false); else c.LineTo(p);
            }
            c.EndFigure(false);
        }
        ctx.DrawGeometry(null, new Pen(Stroke, 1.6, lineJoin: PenLineJoin.Round), g);

        var face = new Typeface(FontFamily.Default);
        void Label(double val, double y)
        {
            var ft = new FormattedText(val.ToString("0.#", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 10, TextBrush)
            { MaxTextWidth = 44, MaxTextHeight = 14, TextAlignment = TextAlignment.Right };
            ctx.DrawText(ft, new Point(0, y - ft.Height / 2));
        }
        Label(max, pad.Top + 4); Label(min, pad.Bottom - 4);
    }
}
