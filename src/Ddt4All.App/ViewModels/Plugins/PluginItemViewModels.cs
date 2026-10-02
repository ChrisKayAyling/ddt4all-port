using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ddt4All.App.Localization;
using Ddt4All.Plugins;

namespace Ddt4All.App.ViewModels.Plugins;

/// <summary>One plugin in the list (localised names fall back to the English text).</summary>
public sealed class PluginRow(IPlugin plugin)
{
    public IPlugin Plugin { get; } = plugin;
    public string Id => Plugin.Info.Id;
    public string Name => Loc.T(Plugin.Info.Name);
    public string Vehicle => Loc.T(Plugin.Info.Vehicle);
    public string Category => Loc.T(Plugin.Info.Category);
    public bool Writes => Plugin.Actions.Any(a => a.Writes);
    public bool IsExperimental => Plugin.Info.Experimental;
}

/// <summary>An editable input of an action.</summary>
public sealed partial class PluginParamViewModel : ObservableObject
{
    private readonly PluginParameter _p;
    public PluginParamViewModel(PluginParameter p) { _p = p; _value = p.DefaultValue; }

    public string Id => _p.Id;
    public string Label => Loc.T(_p.Label);
    public string Description => Loc.T(_p.Description);
    public bool IsChoice => _p.Kind == PluginParameterKind.Choice;
    public bool IsText => !IsChoice;
    public IReadOnlyList<string> Choices => _p.Choices;
    public bool IsMono => _p.Kind == PluginParameterKind.Hex || _p.Id is "vin" or "pin";
    public bool Optional => _p.MinLength == 0 && _p.Kind != PluginParameterKind.Choice;
    public string Placeholder => _p.MinLength == 0 ? Loc.T("optional") : "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Error)), NotifyPropertyChangedFor(nameof(HasError))] private string _value;
    public string? Error => _p.Validate(Value) is { } e ? Loc.T(e) : null;
    public bool HasError => Error is not null;
}

/// <summary>An action row with its parameters and Run button.</summary>
public sealed partial class PluginActionViewModel : ObservableObject
{
    private readonly PluginsViewModel _owner;
    public PluginActionViewModel(PluginAction action, PluginsViewModel owner)
    {
        Action = action; _owner = owner;
        Parameters = new(action.Parameters.Select(p => new PluginParamViewModel(p)));
        foreach (var p in Parameters) p.PropertyChanged += (_, _) => owner.RefreshActionState();
        RunCommand = new AsyncRelayCommand(() => _owner.RequestRunAsync(this), () => BlockReason is null);
    }

    public PluginAction Action { get; }
    public string Id => Action.Id;
    public string Title => Loc.T(Action.Title);
    public string Description => Loc.T(Action.Description);
    public bool Writes => Action.Writes;
    public bool IsReadOnly => !Action.Writes;
    public ObservableCollection<PluginParamViewModel> Parameters { get; }
    public bool HasParameters => Parameters.Count > 0;
    public IAsyncRelayCommand RunCommand { get; }

    /// <summary>Why Run is disabled right now (null = can run).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasBlockReason))] private string? _blockReason;
    public bool HasBlockReason => BlockReason is not null;
    partial void OnBlockReasonChanged(string? value) => RunCommand.NotifyCanExecuteChanged();

    public IReadOnlyDictionary<string, string> Values => Parameters.ToDictionary(p => p.Id, p => p.Value);
}

public enum LogKind { Step, Tx, Rx, Info, Warning, Error, Result }

public sealed class PluginLogLine(LogKind kind, string time, string text)
{
    public LogKind Kind { get; } = kind;
    public string Time { get; } = time;
    public string Text { get; } = text;
    public bool IsStep => Kind == LogKind.Step;
    public bool IsTx => Kind == LogKind.Tx;
    public bool IsRx => Kind == LogKind.Rx;
    public bool IsWarning => Kind == LogKind.Warning;
    public bool IsError => Kind == LogKind.Error;
    public bool IsResult => Kind == LogKind.Result;
}

/// <summary>A button of an operator prompt.</summary>
public sealed class PromptChoice(string text, int index, Action<int> pick)
{
    public string Text { get; } = text;
    public bool IsPrimary => Index == 0;
    public int Index { get; } = index;
    public IRelayCommand Command { get; } = new RelayCommand(() => pick(index));
}
