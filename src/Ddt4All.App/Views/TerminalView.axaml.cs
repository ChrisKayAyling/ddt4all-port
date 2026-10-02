using Avalonia.Input.Platform;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ddt4All.App.ViewModels;

namespace Ddt4All.App.Views;

public partial class TerminalView : UserControl
{
    public TerminalView() { InitializeComponent(); }
    public TerminalViewModel? Vm => DataContext as TerminalViewModel;

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (Vm is { } vm) { vm.LineAdded += OnLine; ScrollToEnd(); }
        InputBox.Focus();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.LineAdded -= OnLine;
        base.OnUnloaded(e);
    }

    private void OnLine() => Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Background);

    private void ScrollToEnd()
    {
        if (Vm is { AutoScroll: true, Lines.Count: > 0 } vm) Log.ScrollIntoView(vm.Lines[^1]);
    }

    private async void OnCopy(object? s, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb && Vm is { } vm) await cb.SetTextAsync(vm.ExportText());
    }
}
