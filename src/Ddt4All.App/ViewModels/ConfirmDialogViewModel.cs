using CommunityToolkit.Mvvm.ComponentModel;

namespace Ddt4All.App.ViewModels;

public sealed partial class ConfirmDialogViewModel : ObservableObject
{
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    public IReadOnlyList<string> Bullets { get; init; } = [];
    public bool HasBullets => Bullets.Count > 0;
    public bool RequireAcknowledgement { get; init; }
    public string AcknowledgementText { get; init; } = "";
    public string ConfirmText { get; init; } = "OK";
    public string CancelText { get; init; } = "Cancel";
    public bool IsDanger { get; init; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanConfirm))] private bool _acknowledged;
    public bool CanConfirm => !RequireAcknowledgement || Acknowledged;
}
