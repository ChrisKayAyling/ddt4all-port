using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Ddt4All.App.ViewModels.Screens;

namespace Ddt4All.App.Views.Screens;

/// <summary>
/// The live value box of a display tile: state dot, monospace value, unit and inline sparkline, all drawn in one pass.
/// A value update only invalidates this control's drawing (never layout), which is what keeps hundreds of changing tiles cheap.
/// </summary>
public sealed class ValueBox : Control
{
    public static readonly StyledProperty<FormDisplayRow?> RowProperty = AvaloniaProperty.Register<ValueBox, FormDisplayRow?>(nameof(Row));
    public static readonly StyledProperty<bool> CompactProperty = AvaloniaProperty.Register<ValueBox, bool>(nameof(Compact));
    public static readonly StyledProperty<string> LabelProperty = AvaloniaProperty.Register<ValueBox, string>(nameof(Label), "");

    public const int Points = 72;
    private FormDisplayRow? _hooked; private bool _attached;
    private IBrush? _fill, _fillErr, _border, _borderErr, _text, _muted, _live, _warn, _danger, _accent;
    private FontFamily _mono = FontFamily.Default;
    private FormattedText? _value, _unit, _label; private string? _valueKey, _unitKey, _labelKey; private bool _valueMono;

    static ValueBox() => AffectsRender<ValueBox>(RowProperty, CompactProperty, LabelProperty);
    public ValueBox() { ActualThemeVariantChanged += (_, _) => { if (_attached) LoadBrushes(); }; }

    public FormDisplayRow? Row { get => GetValue(RowProperty); set => SetValue(RowProperty, value); }
    public bool Compact { get => GetValue(CompactProperty); set => SetValue(CompactProperty, value); }
    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RowProperty) Rehook();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 120 : availableSize.Width, Compact ? 28 : 40);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); _attached = true; LoadBrushes(); Rehook(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { base.OnDetachedFromVisualTree(e); _attached = false; Rehook(); }

    private void Rehook()
    {
        var want = _attached ? Row : null;
        if (ReferenceEquals(want, _hooked)) return;
        if (_hooked is not null) { _hooked.PropertyChanged -= OnRowChanged; _hooked.Sampled -= OnSampled; }
        _hooked = want;
        if (_hooked is not null) { _hooked.PropertyChanged += OnRowChanged; _hooked.Sampled += OnSampled; }
        InvalidateVisual();
    }

    private void OnRowChanged(object? s, PropertyChangedEventArgs e) => InvalidateVisual();
    private void OnSampled() { if (Row is { IsNumeric: true } && !Compact) InvalidateVisual(); }

    private IBrush? Res(string key) => this.TryFindResource(key, ActualThemeVariant, out var v) ? v as IBrush : null;

    private void LoadBrushes()
    {
        _fill = Res("SurfaceAltBrush"); _fillErr = Res("DangerSoftBrush"); _border = Res("BorderSubtleBrush"); _borderErr = Res("DangerBrush");
        _text = Res("SystemControlForegroundBaseHighBrush") ?? Brushes.Black; _muted = Res("TextMutedBrush") ?? Brushes.Gray;
        _live = Res("SuccessBrush") ?? Brushes.Green; _warn = Res("WarningBrush") ?? Brushes.Orange; _danger = Res("DangerBrush") ?? Brushes.Red;
        _accent = Res("SystemControlHighlightAccentBrush") ?? Brushes.DodgerBlue;
        if (this.TryFindResource("MonoFont", ActualThemeVariant, out var f) && f is FontFamily ff) _mono = ff;
        _valueKey = _unitKey = _labelKey = null;
        InvalidateVisual();
    }

    private static FormattedText Make(string text, Typeface face, double size, IBrush? brush, double maxWidth)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush)
        {
            MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
        };
        if (maxWidth > 4) ft.MaxTextWidth = maxWidth;
        return ft;
    }

    /// <summary>Test hook: accumulate the time spent recording value boxes (the UI-thread part of a live update).</summary>
    internal static bool CollectStats; internal static long StatTicks, StatCount;

    public override void Render(DrawingContext ctx)
    {
        if (!CollectStats) { RenderCore(ctx); return; }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        RenderCore(ctx);
        StatTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; StatCount++;
    }

    private void RenderCore(DrawingContext ctx)
    {
        var row = Row;
        var r = new Rect(Bounds.Size);
        if (row is null || r.Width < 20 || r.Height < 10) return;
        bool err = row.IsError;
        ctx.DrawRectangle(err ? _fillErr : _fill, new Pen(err ? _borderErr : _border, 1), r.Deflate(0.5), 6, 6);

        double x = 10, cy = r.Height / 2;
        var dotBrush = row.State switch { ValueState.Live => _live, ValueState.Stale => _warn, ValueState.Error => _danger, _ => _muted };
        using (ctx.PushOpacity(row.State is ValueState.Live or ValueState.Stale or ValueState.Error ? 1 : 0.5))
            ctx.DrawEllipse(dotBrush, null, new Point(x + 3.5, cy), 3.5, 3.5);
        x += 15;

        if (Compact && Label.Length > 0)
        {
            if (_label is null || _labelKey != Label) { _label = Make(Label, new Typeface(FontFamily.Default), 12, _muted, 0); _labelKey = Label; }
            ctx.DrawText(_label, new Point(x, cy - _label.Height / 2));
            x += _label.Width + 8;
        }

        double sparkW = row.IsNumeric && !Compact ? 76 : 0;
        double avail = r.Width - x - 8 - (sparkW > 0 ? sparkW + 8 : 0);
        double size = Compact ? 14 : row.IsNumeric ? 17 : 14;
        if (row.ShowValue)
        {
            bool mono = row.IsNumeric;
            var key = row.ValueText + "\u0001" + (int)avail + "\u0001" + (int)row.State;
            if (_value is null || _valueKey != key)
            {
                var face = mono ? new Typeface(_mono, FontStyle.Normal, FontWeight.SemiBold) : new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Medium);
                _value = Make(row.ValueText, face, size, _text, Math.Max(10, avail)); _valueKey = key; _valueMono = mono;
            }
            using (ctx.PushOpacity(row.State == ValueState.Stale ? 0.5 : 1))
                ctx.DrawText(_value, new Point(x, cy - _value.Height / 2));
            if (row.UnitText.Length > 0 && x + _value.Width + 6 < x + avail)
            {
                if (_unit is null || _unitKey != row.UnitText) { _unit = Make(row.UnitText, new Typeface(FontFamily.Default), 12, _muted, 0); _unitKey = row.UnitText; }
                ctx.DrawText(_unit, new Point(x + _value.Width + 5, cy - _unit.Height / 2 + 1));
            }
        }
        else
        {
            var key = (row.State == ValueState.Waiting ? "…" : "–") + "\u0002";
            if (_value is null || _valueKey != key)
            {
                _value = Make(row.Placeholder, new Typeface(_mono), size, row.IsError ? _danger : _muted, 0); _valueKey = key;
            }
            ctx.DrawText(_value, new Point(x, cy - _value.Height / 2));
        }

        if (sparkW > 0) DrawSpark(ctx, row, new Rect(r.Width - 8 - sparkW, 7, sparkW, r.Height - 14));
    }

    private void DrawSpark(DrawingContext ctx, FormDisplayRow row, Rect r)
    {
        Span<double> v = stackalloc double[Points];
        int n = row.CopyRecent(v);
        if (n < 2) return;
        double min = double.MaxValue, max = double.MinValue;
        for (int i = 0; i < n; i++) { if (v[i] < min) min = v[i]; if (v[i] > max) max = v[i]; }
        double span = max - min;
        if (span < 1e-9) { min -= 0.5; max += 0.5; span = 1; }
        var g = new StreamGeometry();
        Point last = default;
        using (var c = g.Open())
        {
            for (int i = 0; i < n; i++)
            {
                var p = new Point(r.X + r.Width * i / (n - 1), r.Bottom - r.Height * (v[i] - min) / span);
                if (i == 0) c.BeginFigure(p, false); else c.LineTo(p);
                last = p;
            }
            c.EndFigure(false);
        }
        using (ctx.PushOpacity(row.IsStale ? 0.4 : 1))
        {
            ctx.DrawGeometry(null, new Pen(_accent, 1.4, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round), g);
            ctx.DrawEllipse(_accent, null, last, 2.2, 2.2);
        }
    }
}
