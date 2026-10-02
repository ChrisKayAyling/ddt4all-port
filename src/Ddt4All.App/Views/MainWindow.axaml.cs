using Avalonia;
using Avalonia.Controls;
using Ddt4All.App.Services;

namespace Ddt4All.App.Views;

public partial class MainWindow : Window
{
    private ISettingsService? _settings;

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(ISettingsService settings) : this()
    {
        _settings = settings;
        var w = settings.Current.Window;
        Width = Math.Max(MinWidth, w.Width); Height = Math.Max(MinHeight, w.Height);
        if (w.X is { } x && w.Y is { } y)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
        }
        if (w.Maximized) WindowState = WindowState.Maximized;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        StartupTrace.Mark("window opened");
        // Fires after the first layout/render pass has been queued.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => StartupTrace.Mark("first frame (idle)"), Avalonia.Threading.DispatcherPriority.Background);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_settings is not null)
        {
            _settings.Update(s =>
            {
                s.Window.Maximized = WindowState == WindowState.Maximized;
                if (WindowState == WindowState.Normal)
                {
                    s.Window.Width = Width; s.Window.Height = Height;
                    s.Window.X = Position.X; s.Window.Y = Position.Y;
                }
            });
            _settings.SaveNow();
        }
        base.OnClosing(e);
    }
}
