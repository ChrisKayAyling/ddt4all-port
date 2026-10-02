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

public sealed class ScreensViewModel : PlaceholderPageViewModel
{
    public override PageId Id => PageId.Screens;
    public override string Title => Loc.T("Screens");
    public override string Description => Loc.T("Interactive ECU parameter screens: live values, input widgets and command buttons.");
    public override IReadOnlyList<string> Features => ["Screen tree and categories", "Live display widgets with auto refresh", "Input and button widgets", "Zoom, screen recorder, UI edit mode"];
    public override string DependsOn => "Ddt4All.Core (screens/layout model, codec) + Ddt4All.Comms";
    public override Avalonia.Media.Geometry Icon => Icons.Screens;
}

public sealed class RequestsViewModel : PlaceholderPageViewModel
{
    public override PageId Id => PageId.Requests;
    public override string Title => Loc.T("Requests");
    public override string Description => Loc.T("Browse the ECU's requests and parameters, edit values and send named requests.");
    public override IReadOnlyList<string> Features => ["Request and data item tree", "Parameter decoding with units", "Manual request with parameter inputs", "Send in expert mode only for write requests"];
    public override string DependsOn => "Ddt4All.Core (ECU model, bit codec)";
    public override Avalonia.Media.Geometry Icon => Icons.List;
}

public sealed class DataEditorViewModel : PlaceholderPageViewModel
{
    public override PageId Id => PageId.DataEditor;
    public override string Title => Loc.T("Data Editor");
    public override string Description => Loc.T("Edit ECU definitions: requests, data items, buttons and ECU parameters (expert).");
    public override IReadOnlyList<string> Features => ["Request table and request editor", "Data item editor with bit viewer", "Button editor", "Save current ECU / create new ECU"];
    public override string DependsOn => "Ddt4All.Core (ECU model + writers)";
    public override Avalonia.Media.Geometry Icon => Icons.Edit;
}

public sealed class PluginsViewModel : PlaceholderPageViewModel
{
    public override PageId Id => PageId.Plugins;
    public override string Title => Loc.T("Plugins");
    public override string Description => Loc.T("Vehicle-specific procedures contributed as plugins.");
    public override IReadOnlyList<string> Features => ["Discover plugins in the plugins folder", "Run and monitor procedures", "Sandboxed execution with expert-mode gating"];
    public override string DependsOn => "Plugin host (to be designed)";
    public override Avalonia.Media.Geometry Icon => Icons.Plugin;
}
