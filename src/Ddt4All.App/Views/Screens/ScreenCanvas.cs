using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

namespace Ddt4All.App.Views.Screens;

public sealed class InputCommittedEventArgs(SceneItem item, string text) : EventArgs
{
    public SceneItem Item { get; } = item;
    public string Text { get; } = text;
}

/// <summary>
/// Renders a whole DDT screen in one pass (no per-widget controls). Values are pushed with
/// <see cref="SetValue"/> and only the changed layouts are rebuilt; the visible area is culled so zooming
/// into a 300-widget screen stays cheap. At most one real editor control exists (the input being edited).
/// </summary>
public sealed class ScreenCanvas : Control
{
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<ScreenCanvas, double>(nameof(Zoom), 1.0, coerce: (_, v) => Math.Clamp(v, 0.25, 4.0));

    public static readonly StyledProperty<bool> ButtonsEnabledProperty =
        AvaloniaProperty.Register<ScreenCanvas, bool>(nameof(ButtonsEnabled), true);

    private ScreenScene? _scene;
    private int _hover = -1, _selected = -1, _pressed = -1;
    private Control? _editor;
    private SceneItem? _editing;
    private Rect _viewport = default;
    private int _lastRenderedItems;

    static ScreenCanvas()
    {
        AffectsMeasure<ScreenCanvas>(ZoomProperty);
        AffectsArrange<ScreenCanvas>(ZoomProperty);
        AffectsRender<ScreenCanvas>(ZoomProperty, ButtonsEnabledProperty);
    }

    public ScreenCanvas() { Focusable = true; ClipToBounds = true; }

    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public bool ButtonsEnabled { get => GetValue(ButtonsEnabledProperty); set => SetValue(ButtonsEnabledProperty, value); }

    public ScreenScene? Scene
    {
        get => _scene;
        set
        {
            CancelEdit();
            _scene = value; _hover = _selected = _pressed = -1;
            InvalidateMeasure(); InvalidateVisual();
        }
    }

    /// <summary>Visible part in canvas (zoomed) coordinates; lets the renderer skip off-screen items.</summary>
    public Rect Viewport { get => _viewport; set { if (_viewport != value) { _viewport = value; InvalidateVisual(); } } }

    public int SelectedSlot => _selected >= 0 && _scene is not null ? _scene.Items[_selected].Slot : -1;
    public int LastRenderedItems => _lastRenderedItems;

    public event EventHandler<SceneItem>? ButtonInvoked;
    public event EventHandler<SceneItem>? DisplaySelected;
    public event EventHandler<InputCommittedEventArgs>? InputCommitted;

    public void SelectSlot(int slot)
    {
        int idx = -1;
        if (_scene is not null && slot >= 0)
            for (int i = 0; i < _scene.Items.Length; i++) if (_scene.Items[i].Kind == SceneKind.Display && _scene.Items[i].Slot == slot) { idx = i; break; }
        if (idx != _selected) { _selected = idx; InvalidateVisual(); }
    }

    /// <summary>Update one display's text. Cheap; call in batches then <see cref="Refresh"/> once.</summary>
    public void SetValue(int slot, string? text)
    {
        var it = _scene?.BySlot(slot);
        if (it is null) return;
        text ??= "";
        if (it.Value == text) return;
        it.Value = text; it.ValueText = null;
    }

    public void SetInputText(SceneItem it, string text, bool staged)
    {
        it.Value = text; it.ValueText = null; it.Staged = staged; InvalidateVisual();
    }

    public void Refresh() => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize)
    {
        _editor?.Measure(Size.Infinity);
        return _scene is null ? default : new Size(_scene.Width * Zoom, _scene.Height * Zoom);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_editor is not null && _editing is not null)
        {
            var r = _editing.ValueRect;
            double z = Zoom;
            _editor.Arrange(new Rect(r.X * z, r.Y * z, r.Width * z, r.Height * z));
        }
        return finalSize;
    }

    // ------------------------------------------------------------------ rendering

    private static readonly IBrush ButtonFill = new ImmutableSolidColorBrush(Color.FromRgb(0xE9, 0xEC, 0xF2));
    private static readonly IBrush ButtonHot = new ImmutableSolidColorBrush(Color.FromRgb(0xD9, 0xE2, 0xF5));
    private static readonly IBrush ButtonDown = new ImmutableSolidColorBrush(Color.FromRgb(0xC2, 0xD0, 0xEE));
    private static readonly IBrush ButtonText = new ImmutableSolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37));
    private static readonly IBrush InputFill = new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly IBrush StagedFill = new ImmutableSolidColorBrush(Color.FromRgb(0xFE, 0xF3, 0xC7));
    private static readonly Pen EdgePen = new(new ImmutableSolidColorBrush(Color.FromArgb(70, 0, 0, 0)), 1);
    private static readonly Pen ButtonPen = new(new ImmutableSolidColorBrush(Color.FromRgb(0x9A, 0xA5, 0xB8)), 1);
    private static readonly Pen InputPen = new(new ImmutableSolidColorBrush(Color.FromRgb(0x6B, 0x8E, 0xD6)), 1);

    private FormattedText Layout(string text, SceneItem it, IBrush fore, double maxW, double maxH, TextAlignment align = TextAlignment.Left)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, it.Face, it.FontSize, fore)
        {
            MaxTextWidth = Math.Max(1, maxW), MaxTextHeight = Math.Max(1, maxH), Trimming = TextTrimming.CharacterEllipsis, TextAlignment = align,
        };
        return ft;
    }

    public override void Render(DrawingContext ctx)
    {
        var scene = _scene;
        if (scene is null) return;
        double z = Zoom;
        var bounds = new Rect(0, 0, scene.Width * z, scene.Height * z);
        ctx.DrawRectangle(scene.Background, null, bounds);

        Rect vis = _viewport.Width > 0 ? _viewport.Inflate(8) : bounds;
        var visDesign = new Rect(vis.X / z, vis.Y / z, vis.Width / z, vis.Height / z);
        int drawn = 0;
        using var _ = ctx.PushTransform(Matrix.CreateScale(z, z));
        var items = scene.Items;
        for (int i = 0; i < items.Length; i++)
        {
            var it = items[i];
            if (!it.Bounds.Intersects(visDesign)) continue;
            drawn++;
            switch (it.Kind)
            {
                case SceneKind.Label: DrawLabel(ctx, it); break;
                case SceneKind.Display: DrawPair(ctx, it, false, i == _selected, i == _hover); break;
                case SceneKind.Input: DrawPair(ctx, it, true, false, i == _hover); break;
                case SceneKind.Button: DrawButton(ctx, it, i == _hover, i == _pressed); break;
            }
        }
        _lastRenderedItems = drawn;
    }

    private void DrawLabel(DrawingContext ctx, SceneItem it)
    {
        ctx.DrawRectangle(it.Back, null, it.Bounds);
        if (it.Caption.Length == 0) return;
        var align = it.Alignment switch { 1 => TextAlignment.Right, 2 => TextAlignment.Center, _ => TextAlignment.Left };
        var ft = it.CaptionText ??= Layout(it.Caption, it, it.Fore, it.Bounds.Width - 6, it.Bounds.Height, align);
        ctx.DrawText(ft, new Point(it.Bounds.X + 3, it.Bounds.Y + Math.Max(0, (it.Bounds.Height - ft.Height) / 2)));
    }

    private void DrawPair(DrawingContext ctx, SceneItem it, bool input, bool selected, bool hot)
    {
        var lr = it.LabelRect;
        var vr = it.ValueRect;
        ctx.DrawRectangle(it.Back, null, lr);
        var cap = it.CaptionText ??= Layout(it.Caption, it, it.Fore, lr.Width - 6, lr.Height);
        ctx.DrawText(cap, new Point(lr.X + 3, lr.Y + Math.Max(0, (lr.Height - cap.Height) / 2)));

        IBrush valueBack = input ? (it.Staged ? StagedFill : InputFill) : it.Back;
        IBrush valueFore = input ? ButtonText : it.Fore;
        ctx.DrawRectangle(valueBack, input ? InputPen : EdgePen, vr);
        if (!ReferenceEquals(_editing, it) && (it.Value.Length > 0 || input))
        {
            string shown = it.Value.Length > 0 ? it.Value : (input ? "No Value" : "");
            var ft = it.ValueText ??= Layout(shown, it, it.Value.Length > 0 ? valueFore : EdgePen.Brush!, vr.Width - 6, vr.Height);
            ctx.DrawText(ft, new Point(vr.X + 3, vr.Y + Math.Max(0, (vr.Height - ft.Height) / 2)));
        }

        if (selected) ctx.DrawRectangle(null, new Pen(Brushes.DodgerBlue, 2), it.Bounds.Deflate(1));
        else if (hot && !input) ctx.DrawRectangle(null, new Pen(Brushes.DodgerBlue, 1), it.Bounds);
    }

    private void DrawButton(DrawingContext ctx, SceneItem it, bool hot, bool down)
    {
        IBrush fill = down ? ButtonDown : hot ? ButtonHot : ButtonFill;
        DrawingContext.PushedState? op = ButtonsEnabled ? null : ctx.PushOpacity(0.55);
        ctx.DrawRectangle(fill, ButtonPen, it.Bounds, 4, 4);
        var ft = it.CaptionText ??= Layout(it.Caption, it, ButtonText, it.Bounds.Width - 8, it.Bounds.Height, TextAlignment.Center);
        ctx.DrawText(ft, new Point(it.Bounds.X + 4, it.Bounds.Y + Math.Max(0, (it.Bounds.Height - ft.Height) / 2)));
        op?.Dispose();
    }

    // ------------------------------------------------------------------ interaction

    private int HitTest(Point p)
    {
        if (_scene is null) return -1;
        double z = Zoom;
        var d = new Point(p.X / z, p.Y / z);
        for (int i = _scene.Items.Length - 1; i >= 0; i--)
        {
            var it = _scene.Items[i];
            if (it.Kind != SceneKind.Label && it.Bounds.Contains(d)) return i;
        }
        return -1;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        int h = HitTest(e.GetPosition(this));
        if (h == _hover) return;
        _hover = h;
        ToolTip.SetTip(this, h >= 0 && _scene!.Items[h].Tooltip.Length > 0 ? _scene.Items[h].Tooltip : null);
        Cursor = h >= 0 && _scene!.Items[h].Kind is SceneKind.Button or SceneKind.Input ? new Cursor(StandardCursorType.Hand) : null;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover != -1) { _hover = -1; InvalidateVisual(); }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        int h = HitTest(e.GetPosition(this));
        if (h >= 0 && _scene!.Items[h].Kind == SceneKind.Button) { _pressed = h; InvalidateVisual(); }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        int pressed = _pressed; _pressed = -1;
        var p = e.GetPosition(this);
        int h = HitTest(p);
        if (_editor is not null && (h < 0 || !ReferenceEquals(_scene!.Items[h], _editing))) CommitEdit();
        if (h < 0) { InvalidateVisual(); return; }
        var it = _scene!.Items[h];
        switch (it.Kind)
        {
            case SceneKind.Button when pressed == h:
                ButtonInvoked?.Invoke(this, it);
                break;
            case SceneKind.Display:
                _selected = h; DisplaySelected?.Invoke(this, it); break;
            case SceneKind.Input:
                if (it.ValueRect.Contains(new Point(p.X / Zoom, p.Y / Zoom))) BeginEdit(it);
                break;
        }
        InvalidateVisual();
    }

    /// <summary>Starts in-place editing of an input item (TextBox, or ComboBox for list data).</summary>
    public void BeginEdit(SceneItem it)
    {
        CommitEdit();
        _editing = it;
        Control ed;
        if (it.Choices is { Length: > 0 } ch)
        {
            var cb = new ComboBox { ItemsSource = ch, FontSize = it.FontSize * Zoom, MinHeight = 0, Padding = new Thickness(4, 0), VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch };
            if (Array.IndexOf(ch, it.Value) is var ix and >= 0) cb.SelectedIndex = ix;
            cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is string s && ReferenceEquals(_editing, it) && s != it.Value) { Commit(it, s); } };
            cb.DropDownClosed += (_, _) => EndEdit();
            ed = cb;
            Dispatcher.UIThread.Post(() => { cb.Focus(); }, DispatcherPriority.Loaded);
        }
        else
        {
            var tb = new TextBox { Text = it.Value, FontSize = it.FontSize * Zoom, MinHeight = 0, MinWidth = 0, Padding = new Thickness(3, 0), VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center, BorderThickness = new Thickness(1) };
            tb.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { CommitEdit(); e.Handled = true; }
                else if (e.Key == Key.Escape) { CancelEdit(); e.Handled = true; }
            };
            tb.LostFocus += (_, _) => CommitEdit();
            ed = tb;
            Dispatcher.UIThread.Post(() => { tb.Focus(); tb.SelectAll(); }, DispatcherPriority.Loaded);
        }
        _editor = ed;
        ((Avalonia.Controls.ISetLogicalParent)ed).SetParent(this);
        VisualChildren.Add(ed); LogicalChildren.Add(ed);
        InvalidateMeasure(); InvalidateArrange(); InvalidateVisual();
    }

    private void Commit(SceneItem it, string text)
    {
        InputCommitted?.Invoke(this, new InputCommittedEventArgs(it, text));
        EndEdit();
    }

    public void CommitEdit()
    {
        if (_editor is null || _editing is null) return;
        var it = _editing;
        string? text = _editor switch { TextBox tb => tb.Text, ComboBox { SelectedItem: string s } => s, _ => null };
        if (text is not null && text != it.Value) { Commit(it, text); return; }
        EndEdit();
    }

    public void CancelEdit() => EndEdit();

    private void EndEdit()
    {
        if (_editor is null) return;
        var ed = _editor; _editor = null; _editing = null;
        VisualChildren.Remove(ed); LogicalChildren.Remove(ed);
        ((Avalonia.Controls.ISetLogicalParent)ed).SetParent(null);
        InvalidateVisual();
    }
}
