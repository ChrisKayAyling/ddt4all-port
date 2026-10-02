using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Ddt4All.App.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog() { InitializeComponent(); }
    private void OnConfirm(object? s, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? s, RoutedEventArgs e) => Close(false);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(false); e.Handled = true; }
        base.OnKeyDown(e);
    }
}
