using Avalonia.Controls;
using Ddt4All.App.ViewModels;

namespace Ddt4All.App.Views;

public partial class EcuBrowserView : UserControl
{
    public EcuBrowserView() { InitializeComponent(); }
    public EcuBrowserViewModel? Vm => DataContext as EcuBrowserViewModel;
}
