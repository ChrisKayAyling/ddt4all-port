using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ddt4All.App.Localization;
using Ddt4All.App.Services;

namespace Ddt4All.App.ViewModels;

public sealed partial class NavItem : ObservableObject
{
    private readonly string _titleKey;
    public NavItem(PageId id, string titleKey, Geometry icon, Action<PageId> navigate)
    {
        ActivateCommand = new RelayCommand(() => navigate(id));
        Id = id; _titleKey = titleKey; Icon = icon;
        Loc.Instance.LanguageChanged += () => OnPropertyChanged(nameof(Title));
    }
    public PageId Id { get; }
    public IRelayCommand ActivateCommand { get; }
    public Geometry Icon { get; }
    public string Title => Loc.T(_titleKey);
    [ObservableProperty] private bool _isActive;
}
