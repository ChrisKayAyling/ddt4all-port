using Avalonia.Input.Platform;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ddt4All.App.ViewModels;

namespace Ddt4All.App.Views;

public partial class LogView : UserControl
{
    public LogView() { InitializeComponent(); }
    public LogViewModel? Vm => DataContext as LogViewModel;

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (Vm is { } vm) { vm.Appended += OnAppended; OnAppended(); }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.Appended -= OnAppended;
        base.OnUnloaded(e);
    }

    private void OnAppended()
    {
        if (Vm is { AutoScroll: true, Entries.Count: > 0 } vm)
            Dispatcher.UIThread.Post(() => List.ScrollIntoView(vm.Entries[^1]), DispatcherPriority.Background);
    }

    private async void OnCopy(object? s, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb && Vm is { } vm) await cb.SetTextAsync(vm.ExportText());
    }

    private void OnOpenFolder(object? s, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Services.AppPaths.LogDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = Services.AppPaths.LogDir, UseShellExecute = true });
        }
        catch { /* no file manager available */ }
    }
}
