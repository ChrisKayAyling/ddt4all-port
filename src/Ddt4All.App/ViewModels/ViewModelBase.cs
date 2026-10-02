using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Ddt4All.App.Localization;
using Ddt4All.App.Services;

namespace Ddt4All.App.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    protected ViewModelBase()
    {
        // Long-lived (singleton / cached page) view models only. Refreshes every computed Loc.T(...) property.
        Loc.Instance.LanguageChanged += () => Dispatcher.UIThread.Post(() => OnPropertyChanged(string.Empty));
    }
}

public abstract class PageViewModel : ViewModelBase
{
    public abstract PageId Id { get; }
    public abstract string Title { get; }
    /// <summary>Called every time the page becomes the visible page.</summary>
    public virtual void OnNavigatedTo() { }
}

/// <summary>Combo-box item whose label follows the active language.</summary>
public sealed class ChoiceItem : ObservableObject
{
    private readonly string _titleKey;
    public ChoiceItem(string id, string titleKey)
    {
        Id = id; _titleKey = titleKey;
        Loc.Instance.LanguageChanged += () => OnPropertyChanged(nameof(Title));
    }
    public string Id { get; }
    public string Title => Loc.T(_titleKey);
    public override string ToString() => Title;
}
