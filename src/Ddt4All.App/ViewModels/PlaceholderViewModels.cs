using Ddt4All.App.Localization;
using Ddt4All.App.Services;

namespace Ddt4All.App.ViewModels;

/// <summary>Page whose engine lives in Core/Comms and is not wired yet. Describes planned scope.</summary>
public abstract class PlaceholderPageViewModel : PageViewModel
{
    public abstract string Description { get; }
    public abstract IReadOnlyList<string> Features { get; }
    public abstract string DependsOn { get; }
    public abstract Avalonia.Media.Geometry Icon { get; }
    public IReadOnlyList<string> LocalizedFeatures => Features.Select(Loc.T).ToArray();
}

// ScreensViewModel now lives in ViewModels/Screens/ScreensViewModel.cs
