using System.Collections.Specialized;
using Avalonia.Controls;
using Ddt4All.App.ViewModels.Plugins;

namespace Ddt4All.App.Views.Plugins;

public partial class PluginsView : UserControl
{
    public PluginsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is PluginsViewModel vm)
                vm.Log.CollectionChanged += OnLogChanged;
        };
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && this.FindControl<ListBox>("LogList") is { } list && list.ItemCount > 0)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => list.ScrollIntoView(list.ItemCount - 1), Avalonia.Threading.DispatcherPriority.Background);
    }
}
