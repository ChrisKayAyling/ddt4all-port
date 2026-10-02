using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ddt4All.App.Localization;
using Ddt4All.App.Models;
using Ddt4All.App.Services;
using Ddt4All.Plugins;

namespace Ddt4All.App.ViewModels.Plugins;

/// <summary>
/// Plugins page: searchable list by category, detail with warnings, explicit confirmation for anything that writes
/// (expert mode required), step by step progress log and cancel. All execution goes through <see cref="PluginRunner"/>,
/// which enforces the same gates again, so this VM is a convenience layer rather than the only safety net.
/// </summary>
public sealed partial class PluginsViewModel : PageViewModel, IPluginInteraction
{
    private const string AllCategories = "All categories";
    private readonly PluginRegistry _registry;
    private readonly PluginRunner _runner;
    private readonly IConnectionService _connection;
    private readonly SessionState _session;
    private readonly INotificationService _toasts;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<int>? _prompt;
    private string _lastKnownLanguage = Loc.Instance.Language;

    public PluginsViewModel(PluginRegistry registry, IEcuDefinitionProvider definitions, IConnectionService connection, SessionState session, INotificationService toasts)
    {
        _registry = registry; _runner = new PluginRunner(definitions); _connection = connection; _session = session; _toasts = toasts;
        Categories = new(new[] { new ChoiceItem("", AllCategories) }.Concat(registry.Categories.Select(c => new ChoiceItem(c, c))));
        _selectedCategory = Categories[0];
        connection.PropertyChanged += OnExternalChange;
        session.PropertyChanged += OnExternalChange;
        Loc.Instance.LanguageChanged += () => Dispatcher.UIThread.Post(Rebuild);
        Rebuild();
    }

    public override PageId Id => PageId.Plugins;
    public override string Title => Loc.T("Plugins");

    // ------------------------------------------------------------------ list

    public ObservableCollection<ChoiceItem> Categories { get; }
    public ObservableCollection<object> Rows { get; } = new();
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private ChoiceItem _selectedCategory;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection)), NotifyPropertyChangedFor(nameof(NoSelection))] private PluginRow? _selectedRow;
    [ObservableProperty] private string _resultSummary = "";
    [ObservableProperty] private bool _isEmpty;

    public bool HasSelection => SelectedRow is not null;
    public bool NoSelection => SelectedRow is null;

    partial void OnSearchTextChanged(string value) => Rebuild();
    partial void OnSelectedCategoryChanged(ChoiceItem value) => Rebuild();

    private void Rebuild()
    {
        var keepId = SelectedRow?.Id;
        string? category = string.IsNullOrEmpty(SelectedCategory?.Id) ? null : SelectedCategory!.Id;
        var found = _registry.Search(SearchText, category)
            .Select(p => new PluginRow(p))
            .OrderBy(r => r.Category, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Rows.Clear();
        foreach (var g in found.GroupBy(r => r.Category))
        {
            Rows.Add(new GroupHeaderRow(g.Key, g.Count()));
            foreach (var r in g) Rows.Add(r);
        }
        IsEmpty = found.Count == 0;
        ResultSummary = string.Format(Loc.T("{0} of {1} plugins"), found.Count, _registry.All.Count);
        var again = found.FirstOrDefault(r => r.Id == keepId);
        if (again is not null) SelectedRow = again;
        else if (!IsRunning) SelectedRow = null;
    }

    // ------------------------------------------------------------------ detail

    public ObservableCollection<PluginActionViewModel> Actions { get; } = new();
    public IPlugin? Plugin => SelectedRow?.Plugin;
    public string DetailName => SelectedRow?.Name ?? "";
    public string DetailVehicle => SelectedRow?.Vehicle ?? "";
    public string DetailCategory => SelectedRow?.Category ?? "";
    public string DetailDescription => Plugin is { } p ? Loc.T(p.Info.Description) : "";
    public bool IsExperimental => Plugin?.Info.Experimental == true;
    public bool NeedsHardware => Plugin?.Info.RequiresHardware == true;
    public IReadOnlyList<string> Warnings => Plugin?.Info.Warnings.Select(Loc.T).ToList() ?? [];
    public bool HasWarnings => Warnings.Count > 0;
    public IReadOnlyList<string> Instructions => Plugin?.Info.Instructions.Select(Loc.T).ToList() ?? [];
    public bool HasInstructions => Instructions.Count > 0;
    public string EcuDefinition => Plugin?.Info.EcuDefinition ?? "-";
    public string ProtocolText => Plugin?.Info.Protocol ?? "-";
    public bool HasEcu => Plugin?.Info.EcuDefinition is not null;

    partial void OnSelectedRowChanged(PluginRow? value)
    {
        if (IsRunning) return; // selection is locked while running (see list IsEnabled); defensive
        Actions.Clear();
        if (value is not null) foreach (var a in value.Plugin.Actions) Actions.Add(new PluginActionViewModel(a, this));
        ClearRun();
        OnPropertyChanged(string.Empty);
        RefreshActionState();
    }

    // ------------------------------------------------------------------ gating state

    public bool IsConnected => _connection.State == ConnectionState.Connected && _connection.AddressableTransport is not null;
    public bool IsExpert => _session.IsExpertMode;

    private void OnExternalChange(object? sender, PropertyChangedEventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        OnPropertyChanged(nameof(IsConnected)); OnPropertyChanged(nameof(IsExpert)); RefreshActionState();
    });

    internal void RefreshActionState()
    {
        foreach (var a in Actions) a.BlockReason = ComputeBlockReason(a);
    }

    private string? ComputeBlockReason(PluginActionViewModel a)
    {
        if (IsRunning) return Loc.T("A plugin is running");
        if (Plugin is { Info.RequiresHardware: true } && !IsConnected) return Loc.T("Connect to an adapter first");
        if (a.Writes && !_session.IsExpertMode) return Loc.T("Expert mode required");
        var bad = a.Parameters.FirstOrDefault(p => p.HasError);
        if (bad is not null) return bad.Error;
        return null;
    }

    // ------------------------------------------------------------------ run

    public ObservableCollection<PluginLogLine> Log { get; } = new();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle)), NotifyPropertyChangedFor(nameof(CanSelectPlugin))] private bool _isRunning;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasResult)), NotifyPropertyChangedFor(nameof(ResultIsSuccess)), NotifyPropertyChangedFor(nameof(ResultIsWarning)), NotifyPropertyChangedFor(nameof(ResultIsFailure)), NotifyPropertyChangedFor(nameof(HasRunPanel))] private PluginResult? _result;
    public bool IsIdle => !IsRunning;
    public bool CanSelectPlugin => !IsRunning;
    public bool HasResult => Result is not null;
    public bool ResultIsSuccess => Result is { Outcome: PluginOutcome.Success };
    public bool ResultIsWarning => Result is { Outcome: PluginOutcome.Warning or PluginOutcome.Cancelled };
    public bool ResultIsFailure => Result is { Outcome: PluginOutcome.Failed or PluginOutcome.Blocked };
    public string ResultText => Result is { } r ? Loc.T(r.Summary) : "";
    public IReadOnlyList<string> ResultValues => Result?.Values.Select(kv => $"{Loc.T(kv.Key)}: {kv.Value}").ToList() ?? [];
    public bool HasResultValues => ResultValues.Count > 0;
    public bool HasLog => Log.Count > 0;
    /// <summary>The docked panel under the details (confirmation, operator prompt, progress log, result).</summary>
    public bool HasRunPanel => IsConfirming || HasPrompt || HasLog || HasResult;

    partial void OnIsRunningChanged(bool value) => RefreshActionState();
    partial void OnResultChanged(PluginResult? value) { OnPropertyChanged(nameof(ResultText)); OnPropertyChanged(nameof(ResultValues)); OnPropertyChanged(nameof(HasResultValues)); }

    private void ClearRun()
    {
        Log.Clear(); Result = null; Progress = 0; IsIndeterminate = false; StatusText = ""; PendingAction = null; Acknowledged = false; { OnPropertyChanged(nameof(HasLog)); OnPropertyChanged(nameof(HasRunPanel)); }
    }

    // confirmation step ---------------------------------------------------
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsConfirming)), NotifyPropertyChangedFor(nameof(ConfirmTitle)), NotifyPropertyChangedFor(nameof(HasRunPanel))] private PluginActionViewModel? _pendingAction;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanConfirm))] private bool _acknowledged;
    public bool IsConfirming => PendingAction is not null;
    public bool CanConfirm => Acknowledged && IsExpert;
    public string ConfirmTitle => PendingAction is { } a ? string.Format(Loc.T("Confirm: {0}"), a.Title) : "";
    public string ConfirmTarget => Plugin?.Info.EcuDefinition is { } e ? string.Format(Loc.T("This will write to the ECU defined by {0}."), e) : Loc.T("This will write to the ECU.");

    /// <summary>Run button handler. Read-only actions start immediately; writing actions open the confirmation step first.</summary>
    internal async Task RequestRunAsync(PluginActionViewModel a)
    {
        if (IsRunning) return;
        RefreshActionState();
        if (a.BlockReason is not null) return;
        if (a.Writes)
        {
            Acknowledged = false; PendingAction = a; OnPropertyChanged(nameof(ConfirmTarget));
            return;
        }
        await ExecuteAsync(a, confirmed: false).ConfigureAwait(true);
    }

    [RelayCommand]
    private void CancelConfirmation() { PendingAction = null; Acknowledged = false; }

    [RelayCommand]
    private async Task ConfirmAndRunAsync()
    {
        var a = PendingAction;
        if (a is null || !CanConfirm) return;
        PendingAction = null; Acknowledged = false;
        await ExecuteAsync(a, confirmed: true).ConfigureAwait(true);
    }

    private async Task ExecuteAsync(PluginActionViewModel a, bool confirmed)
    {
        var plugin = Plugin!;
        ClearRun();
        IsRunning = true; IsIndeterminate = true; StatusText = Loc.T("Starting...");
        _cts = new CancellationTokenSource();
        var progress = new UiProgress(this);
        PluginResult result;
        try
        {
            result = await _runner.RunAsync(plugin, a.Id, new PluginRunRequest
            {
                Transport = _connection.AddressableTransport,
                ExpertMode = _session.IsExpertMode,
                Confirmed = confirmed,
                Parameters = a.Values,
                Interaction = this,
            }, progress, _cts.Token).ConfigureAwait(true);
        }
        finally { _cts.Dispose(); _cts = null; }

        progress.Flush();
        ClosePrompt();
        Result = result;
        IsIndeterminate = false;
        if (result.Outcome == PluginOutcome.Success) Progress = 1;
        StatusText = result.Outcome switch
        {
            PluginOutcome.Success => Loc.T("Done"), PluginOutcome.Cancelled => Loc.T("Cancelled"), PluginOutcome.Blocked => Loc.T("Blocked"),
            PluginOutcome.Warning => Loc.T("Finished with a warning"), _ => Loc.T("Failed"),
        };
        IsRunning = false;
        switch (result.Outcome)
        {
            case PluginOutcome.Success: _toasts.Success(a.Title, result.Summary); break;
            case PluginOutcome.Warning or PluginOutcome.Cancelled: _toasts.Warning(a.Title, result.Summary); break;
            default: _toasts.Error(a.Title, result.Summary); break;
        }
    }

    [RelayCommand]
    private void CancelRun()
    {
        if (_cts is null) return;
        StatusText = Loc.T("Cancelling after the current request...");
        _cts.Cancel();
        _prompt?.TrySetCanceled();
    }

    // progress marshalling -------------------------------------------------
    private sealed class UiProgress(PluginsViewModel vm) : IProgress<PluginEvent>
    {
        public void Report(PluginEvent e)
        {
            if (Dispatcher.UIThread.CheckAccess()) vm.Apply(e); else Dispatcher.UIThread.Post(() => vm.Apply(e));
        }
        /// <summary>Lets queued UI posts run before the final state is set.</summary>
        public void Flush() => Dispatcher.UIThread.RunJobs();
    }

    private void Apply(PluginEvent e)
    {
        var kind = e.Kind switch
        {
            PluginEventKind.Step => LogKind.Step, PluginEventKind.Tx => LogKind.Tx, PluginEventKind.Rx => LogKind.Rx, PluginEventKind.Warning => LogKind.Warning,
            PluginEventKind.Error => LogKind.Error, PluginEventKind.Result => LogKind.Result, _ => LogKind.Info,
        };
        string text = e.Kind switch
        {
            PluginEventKind.Step => e.TotalSteps > 0 ? $"{Loc.T("Step")} {e.Step}/{Math.Max(e.TotalSteps, e.Step)}: {Loc.T(e.Message)}" : $"{Loc.T("Step")} {e.Step}: {Loc.T(e.Message)}",
            PluginEventKind.Tx => "-> " + e.Message,
            PluginEventKind.Rx => "<- " + e.Message,
            _ => Loc.T(e.Message),
        };
        Log.Add(new PluginLogLine(kind, e.Time.ToLocalTime().ToString("HH:mm:ss.fff"), text));
        { OnPropertyChanged(nameof(HasLog)); OnPropertyChanged(nameof(HasRunPanel)); }
        if (e.Kind == PluginEventKind.Step)
        {
            StatusText = Loc.T(e.Message);
            if (e.TotalSteps > 0) { IsIndeterminate = false; Progress = Math.Min(0.97, (double)Math.Max(e.Step - 1, 0) / e.TotalSteps); }
        }
    }

    // operator prompts -------------------------------------------------------
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPrompt)), NotifyPropertyChangedFor(nameof(HasRunPanel))] private string? _promptTitle;
    [ObservableProperty] private string _promptMessage = "";
    public ObservableCollection<PromptChoice> PromptChoices { get; } = new();
    public bool HasPrompt => PromptTitle is not null;

    public async ValueTask<int> ChooseAsync(string title, string message, IReadOnlyList<string> choices, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _prompt = tcs;
            PromptMessage = Loc.T(message); PromptTitle = Loc.T(title);
            PromptChoices.Clear();
            for (int i = 0; i < choices.Count; i++) PromptChoices.Add(new PromptChoice(Loc.T(choices[i]), i, Pick));
        });
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        try { return await tcs.Task.ConfigureAwait(false); }
        finally { await Dispatcher.UIThread.InvokeAsync(ClosePrompt); }
    }

    private void Pick(int i) => _prompt?.TrySetResult(i);

    private void ClosePrompt() { _prompt = null; PromptTitle = null; PromptChoices.Clear(); }
}
