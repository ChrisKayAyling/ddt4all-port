using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Media;
using Ddt4All.App.ViewModels.Screens;

namespace Ddt4All.App.Views.Screens;

public static class Converters
{
    public static readonly IValueConverter BoldIfTrue = new FuncValueConverter<bool, FontWeight>(b => b ? FontWeight.SemiBold : FontWeight.Normal);
}

public partial class ScreensView : UserControl
{
    private ScreensViewModel? _vm;

    public ScreensView()
    {
        InitializeComponent();
        var scroller = this.FindControl<ScrollViewer>("Scroller")!;
        var canvas = this.FindControl<ScreenCanvas>("Canvas")!;
        scroller.ScrollChanged += (_, _) => canvas.Viewport = new Rect(scroller.Offset.X, scroller.Offset.Y, scroller.Viewport.Width, scroller.Viewport.Height);
        scroller.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || _vm is null) return;
            if (e.Delta.Y > 0) _vm.ZoomInCommand.Execute(null); else if (e.Delta.Y < 0) _vm.ZoomOutCommand.Execute(null);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        this.FindControl<Button>("FitButton")!.Click += (_, _) => _vm?.FitTo(scroller.Bounds.Width - 28, scroller.Bounds.Height - 28);

        // text editors of the form commit on Enter / focus loss (the handlers read the row from the DataContext, not from a field)
        var form = this.FindControl<FormPanel>("Form")!;
        form.AddHandler(LostFocusEvent, (_, e) => { if (e.Source is Control { DataContext: FormInputRow r }) r.CommitText(); });
        form.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.Source is TextBox { DataContext: FormInputRow r }) { r.CommitText(); e.Handled = true; }
        });

        // narrow windows: the screen list gives way to the form
        SizeChanged += (_, e) =>
        {
            var root = this.FindControl<Grid>("RootGrid")!;
            double w = e.NewSize.Width;
            root.ColumnDefinitions[0].Width = new GridLength(w < 640 ? 150 : w < 900 ? 200 : 272);
            root.Margin = w < 640 ? new Thickness(8, 10, 8, 8) : new Thickness(20, 18, 20, 16);
            // header tools drop below the title when there is no room beside it
            var tools = this.FindControl<WrapPanel>("HeaderTools")!;
            bool narrow = w < 1000;
            Grid.SetRow(tools, narrow ? 1 : 0); Grid.SetColumn(tools, narrow ? 0 : 1); Grid.SetColumnSpan(tools, narrow ? 2 : 1);
            tools.HorizontalAlignment = narrow ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Right;
            tools.Margin = narrow ? new Thickness(0, 8, 0, 0) : default;
        };

        canvas.ButtonInvoked += async (_, it) => { if (_vm is not null && it.Button is not null) await _vm.PressButtonAsync(it.Button); };
        canvas.DisplaySelected += (_, it) => { _vm?.SelectDisplay(it.Slot); };
        canvas.InputCommitted += (_, e) =>
        {
            if (_vm is null) return;
            if (_vm.StageInput(e.Item, e.Text)) { } // view update happens through InputStaged
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Detach();
        _vm = DataContext as ScreensViewModel;
        if (_vm is null) return;
        var canvas = this.FindControl<ScreenCanvas>("Canvas")!;
        var spark = this.FindControl<SparklineControl>("Spark")!;
        _vm.SceneChanged += OnScene;
        _vm.ValuesChanged += OnValues;
        _vm.HistoryChanged += OnHistory;
        _vm.InputStaged += OnStaged;
        _vm.PropertyChanged += OnVmChanged;
        canvas.Scene = _vm.Scene;
    }

    private void Detach()
    {
        if (_vm is null) return;
        _vm.SceneChanged -= OnScene; _vm.ValuesChanged -= OnValues; _vm.HistoryChanged -= OnHistory; _vm.InputStaged -= OnStaged; _vm.PropertyChanged -= OnVmChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); _vm?.OnShown(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { base.OnDetachedFromVisualTree(e); _vm?.OnHidden(); }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ScreensViewModel.IsClassicView) || _vm is null || !_vm.IsClassicView || _vm.Engine is not { } eng) return;
        // the canvas was idle while hidden: bring every value up to date
        var canvas = this.FindControl<ScreenCanvas>("Canvas")!;
        for (int i = 0; i < eng.SlotCount; i++) canvas.SetValue(i, eng.GetText(i));
        canvas.Refresh();
    }

    private void OnScene(ScreenScene? scene)
    {
        var canvas = this.FindControl<ScreenCanvas>("Canvas")!;
        canvas.Scene = scene;
        this.FindControl<SparklineControl>("Spark")!.SetData(Array.Empty<double>());
    }

    private void OnValues(IReadOnlyList<int> slots)
    {
        var canvas = this.FindControl<ScreenCanvas>("Canvas")!;
        var eng = _vm!.Engine;
        if (eng is null) return;
        for (int i = 0; i < slots.Count; i++) canvas.SetValue(slots[i], eng.GetText(slots[i]));
        canvas.Refresh();
    }

    private void OnHistory()
    {
        var spark = this.FindControl<SparklineControl>("Spark")!;
        spark.SetData(_vm!.SelectedHistory);
        this.FindControl<ScreenCanvas>("Canvas")!.SelectSlot(_vm.SelectedSlot);
    }

    private void OnStaged(SceneItem item, string text, bool staged) =>
        this.FindControl<ScreenCanvas>("Canvas")!.SetInputText(item, text, staged);
}
