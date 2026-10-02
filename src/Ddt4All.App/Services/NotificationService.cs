using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Ddt4All.App.Services;

public enum NotificationLevel { Info, Success, Warning, Error }

public sealed partial class Toast : ObservableObject
{
    public Toast(NotificationLevel level, string title, string? message, Action<Toast> dismiss)
    {
        Level = level; Title = title; Message = message;
        DismissCommand = new RelayCommand(() => dismiss(this));
    }
    public NotificationLevel Level { get; }
    public string Title { get; }
    public string? Message { get; }
    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public bool IsInfo => Level == NotificationLevel.Info;
    public bool IsSuccess => Level == NotificationLevel.Success;
    public bool IsWarning => Level == NotificationLevel.Warning;
    public bool IsError => Level == NotificationLevel.Error;
    public IRelayCommand DismissCommand { get; }
}

public interface INotificationService
{
    ObservableCollection<Toast> Toasts { get; }
    void Show(NotificationLevel level, string title, string? message = null, TimeSpan? timeout = null);
    void Info(string title, string? message = null);
    void Success(string title, string? message = null);
    void Warning(string title, string? message = null);
    void Error(string title, string? message = null);
}

public sealed class NotificationService : INotificationService
{
    public ObservableCollection<Toast> Toasts { get; } = new();
    private const int MaxVisible = 4;

    public void Show(NotificationLevel level, string title, string? message = null, TimeSpan? timeout = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Show(level, title, message, timeout));
            return;
        }
        var toast = new Toast(level, title, message, t => Toasts.Remove(t));
        Toasts.Add(toast);
        while (Toasts.Count > MaxVisible) Toasts.RemoveAt(0);

        var life = timeout ?? level switch { NotificationLevel.Error => TimeSpan.FromSeconds(9), NotificationLevel.Warning => TimeSpan.FromSeconds(6), _ => TimeSpan.FromSeconds(4) };
        var timer = new DispatcherTimer { Interval = life };
        timer.Tick += (_, _) => { timer.Stop(); Toasts.Remove(toast); };
        timer.Start();
    }

    public void Info(string title, string? message = null) => Show(NotificationLevel.Info, title, message);
    public void Success(string title, string? message = null) => Show(NotificationLevel.Success, title, message);
    public void Warning(string title, string? message = null) => Show(NotificationLevel.Warning, title, message);
    public void Error(string title, string? message = null) => Show(NotificationLevel.Error, title, message);
}
