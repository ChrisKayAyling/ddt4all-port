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
        canvas.Scene = _vm.Scene;
    }

    private void Detach()
    {
        if (_vm is null) return;
        _vm.SceneChanged -= OnScene; _vm.ValuesChanged -= OnValues; _vm.HistoryChanged -= OnHistory; _vm.InputStaged -= OnStaged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); _vm?.OnShown(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { base.OnDetachedFromVisualTree(e); _vm?.OnHidden(); }

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
