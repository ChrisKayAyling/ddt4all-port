using Avalonia.Input.Platform;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ddt4All.App.Localization;
using Ddt4All.App.Services;
using Ddt4All.Core.Codec;
using Ddt4All.Core.Ecu;

namespace Ddt4All.App.ViewModels;

public enum ResponseStatus { None, Ok, Error, Pending }
public enum RequestFilter { All, Read, Write }

/// <summary>Requests page: searchable request list, typed parameter editors, guarded send, decoded response and history.</summary>
public sealed partial class RequestsViewModel : PageViewModel
{
    private readonly IEcuContext _ctx;
    private readonly IConnectionService _connection;
    private readonly SessionState _session;
    private readonly INotificationService _toasts;
    private readonly IDialogService _dialogs;
    private readonly IFilePickerService _picker;
    private RequestRow[] _all = Array.Empty<RequestRow>();
    private EcuFile? _loadedFor;
    private byte[]? _frame;

    public RequestsViewModel(IEcuContext ctx, IConnectionService connection, SessionState session, INotificationService toasts,
        IDialogService dialogs, IFilePickerService picker)
    {
        Response.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasResponseRows)); OnPropertyChanged(nameof(NoResponseRows)); };
        Params.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasParams));
        _ctx = ctx; _connection = connection; _session = session; _toasts = toasts; _dialogs = dialogs; _picker = picker;
        ctx.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IEcuContext.Ecu) or null) Dispatcher.UIThread.Post(Reload);
            else if (e.PropertyName == nameof(IEcuContext.Transport)) Dispatcher.UIThread.Post(RefreshSendState);
        };
        connection.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(RefreshSendState);
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionState.IsExpertMode)) Dispatcher.UIThread.Post(RefreshSendState);
        };
        Reload();
    }

    public override PageId Id => PageId.Requests;
    public override string Title => Loc.T("Requests");
    public override void OnNavigatedTo() => Reload();

    // ------------------------------------------------------------------ list
    public IReadOnlyList<RequestRow> Rows { get; private set; } = Array.Empty<RequestRow>();
    public bool HasEcu => _ctx.Ecu != null;
    public bool NoEcu => _ctx.Ecu == null;
    public string EcuTitle => _ctx.Ecu?.EcuName ?? "";
    public string CountText => _ctx.Ecu == null ? "" : Rows.Count == _all.Length ? Loc.F("{0} requests", _all.Length) : Loc.F("{0} of {1} requests", Rows.Count, _all.Length);

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private RequestFilter _filter;
    [ObservableProperty] private RequestRow? _selectedRow;

    public int FilterIndex { get => (int)Filter; set { if (value >= 0) Filter = (RequestFilter)value; } }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnFilterChanged(RequestFilter value)
    {
        OnPropertyChanged(nameof(FilterIndex));
        ApplyFilter();
    }

    private void Reload()
    {
        var ecu = _ctx.Ecu;
        if (ReferenceEquals(ecu, _loadedFor) && ecu != null && _all.Length == ecu.Requests.Count) { ApplyFilter(); return; }
        _loadedFor = ecu;
        _all = ecu == null ? Array.Empty<RequestRow>() : BuildRows(ecu);
        History.Clear();
        SelectedRow = null;
        ApplyFilter();
        OnPropertyChanged(nameof(HasEcu)); OnPropertyChanged(nameof(NoEcu)); OnPropertyChanged(nameof(EcuTitle));
        if (_all.Length > 0) SelectedRow = _all[0];
    }

    private RequestRow[] BuildRows(EcuFile ecu)
    {
        var rows = new RequestRow[ecu.Requests.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            var r = ecu.Requests[i];
            rows[i] = new RequestRow(r, IsWrite(r));
        }
        return rows;
    }

    private bool IsWrite(EcuRequest r)
    {
        var t = r.GetSentBytesTemplate();
        return t.IsEmpty || !_session.IsSafeRequest(t[0]);
    }

    private void ApplyFilter()
    {
        var q = SearchText.Trim().ToLowerInvariant();
        IEnumerable<RequestRow> e = _all;
        if (Filter == RequestFilter.Read) e = e.Where(r => !r.IsWrite);
        else if (Filter == RequestFilter.Write) e = e.Where(r => r.IsWrite);
        if (q.Length > 0)
        {
            var parts = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            e = e.Where(r => parts.All(p => r.Key.Contains(p, StringComparison.Ordinal)));
        }
        Rows = (q.Length == 0 && Filter == RequestFilter.All) ? _all : e.ToArray();
        OnPropertyChanged(nameof(Rows)); OnPropertyChanged(nameof(CountText));
        if (SelectedRow != null && !Rows.Contains(SelectedRow)) { /* keep showing the selection in the editor */ }
    }

    // ------------------------------------------------------------------ selected request
    public ObservableCollection<ParamEditorViewModel> Params { get; } = new();
    public ObservableCollection<ResponseRow> Response { get; } = new();
    public ObservableCollection<HistoryEntry> History { get; } = new();

    [ObservableProperty] private string _requestName = "";
    [ObservableProperty] private string _frameHex = "";
    [ObservableProperty] private string _templateHex = "";
    [ObservableProperty] private string _requestInfo = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasResponse))] private string _rawResponse = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private ResponseStatus _statusKind;
    [ObservableProperty] private bool _isSending;
    [ObservableProperty] private bool _isWriteRequest;
    [ObservableProperty] private HistoryEntry? _selectedHistory;
    [ObservableProperty] private string _sendBlockReason = "";
    public bool HasParams => Params.Count > 0;
    public bool HasResponse => RawResponse.Length > 0;
    public bool HasSample => SelectedRow?.Request.ReplyBytes.Length > 0;
    public bool HasHistory => History.Count > 0;

    public bool StatusIsOk => StatusKind == ResponseStatus.Ok;
    public bool StatusIsError => StatusKind == ResponseStatus.Error;
    public bool StatusIsPending => StatusKind == ResponseStatus.Pending;
    public bool HasResponseRows => Response.Count > 0;
    public bool NoResponseRows => Response.Count == 0;
    partial void OnStatusKindChanged(ResponseStatus value) { OnPropertyChanged(nameof(StatusIsOk)); OnPropertyChanged(nameof(StatusIsError)); OnPropertyChanged(nameof(StatusIsPending)); }

    partial void OnSelectedRowChanged(RequestRow? value) => LoadRequest(value);

    partial void OnSelectedHistoryChanged(HistoryEntry? value)
    {
        if (value?.Response == null || value.Request == null || SelectedRow?.Request != value.Request) return;
        ShowResponse(value.Request, value.Response);
    }

    private void LoadRequest(RequestRow? row)
    {
        Params.Clear();
        Response.Clear();
        RawResponse = ""; StatusText = ""; StatusKind = ResponseStatus.None;
        var ecu = _ctx.Ecu;
        if (row == null || ecu == null)
        {
            RequestName = ""; FrameHex = ""; TemplateHex = ""; RequestInfo = ""; _frame = null;
            OnPropertyChanged(nameof(HasParams)); OnPropertyChanged(nameof(HasSample));
            RefreshSendState();
            return;
        }

        var req = row.Request;
        RequestName = req.Name;
        TemplateHex = HexFormat.Spaced(req.GetSentBytesTemplate());
        IsWriteRequest = row.IsWrite;
        RequestInfo = BuildInfo(req);
        var tpl = req.GetSentBytesTemplate();
        foreach (var item in req.SendItems)
        {
            var data = item.Data ?? ecu.GetData(item.Name);
            if (data == null) continue;
            var initial = tpl.IsEmpty ? "" : req.DecodeValue(tpl, item) ?? "";
            Params.Add(new ParamEditorViewModel(item, data, initial, OnParamChanged) { DefaultText = initial });
        }

        foreach (var item in req.ReceiveItems) Response.Add(Placeholder(req, item));
        RebuildFrame();
        OnPropertyChanged(nameof(HasParams)); OnPropertyChanged(nameof(HasSample));
    }

    private static string BuildInfo(EcuRequest r)
    {
        var parts = new List<string>();
        if (r.MinBytes > 0) parts.Add(Loc.F("min reply {0} B", r.MinBytes));
        if (r.ManualSend) parts.Add(Loc.T("manual send"));
        if (r.Denied != SessionAccess.None) parts.Add(Loc.F("denied: {0}", r.Denied.ToString().ToLowerInvariant()));
        return string.Join(" · ", parts);
    }

    private ResponseRow Placeholder(EcuRequest req, DataItem item)
    {
        var d = item.Data ?? _ctx.Ecu?.GetData(item.Name);
        return new ResponseRow { Name = item.Name, Value = "—", Unit = d?.Unit ?? "", Position = Pos(item), IsMissing = true, Description = Desc(d) };
    }

    private static string Pos(DataItem i) => i.BitOffset != 0 ? $"{i.FirstByte}.{i.BitOffset}" : i.FirstByte.ToString();
    private static string? Desc(EcuData? d) => d == null || string.IsNullOrWhiteSpace(d.Description) ? null : d.Description;

    private void OnParamChanged() => RebuildFrame();

    private void RebuildFrame()
    {
        var req = SelectedRow?.Request;
        if (req == null) { _frame = null; FrameHex = ""; RefreshSendState(); return; }
        var frame = req.GetSentBytesTemplate().ToArray();
        bool ok = !(frame.Length == 0 && req.SentBytes.Length > 0);
        foreach (var p in Params)
        {
            string? err = null;
            if (p.Text.Length == 0 && p.Kind != ParamKind.Ascii) err = Loc.T("Value required");
            else if (!req.TrySetInput(frame, p.Name, p.Text, out err)) { }
            p.Error = err == null ? null : Trim(err);
            if (err != null) ok = false;
        }
        _frame = ok ? frame : null;
        FrameHex = HexFormat.Spaced(frame);
        RefreshSendState();
    }

    private static string Trim(string e)
    {
        int i = e.IndexOf(": ", StringComparison.Ordinal);
        return i > 0 && i < 40 ? e[(i + 2)..] : e;
    }

    // ------------------------------------------------------------------ sending
    public bool IsConnected => _ctx.Transport != null;
    public bool IsExpert => _session.IsExpertMode;

    public bool CanSend => ComputeBlock() == null && !IsSending;

    private string? ComputeBlock()
    {
        if (SelectedRow == null) return Loc.T("Select a request.");
        if (_frame == null) return Loc.T("Fix the invalid parameters first.");
        if (_ctx.Transport == null) return Loc.T("Connect to a vehicle to send requests.");
        if (SelectedRow.IsWrite && !_session.IsExpertMode) return Loc.T("Expert mode is required to send write requests.");
        return null;
    }

    private void RefreshSendState()
    {
        SendBlockReason = ComputeBlock() ?? "";
        OnPropertyChanged(nameof(CanSend)); OnPropertyChanged(nameof(IsConnected)); OnPropertyChanged(nameof(IsExpert));
        SendCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsSendingChanged(bool value) => RefreshSendState();

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var req = SelectedRow?.Request;
        var transport = _ctx.Transport;
        var frame = _frame;
        if (req == null || transport == null || frame == null || ComputeBlock() != null) return;
        if (SelectedRow!.IsWrite)
        {
            if (!await _dialogs.ConfirmAsync(Loc.T("Send write request"),
                    Loc.F("'{0}' may change the ECU configuration or state.\n\n{1}\n\nSend it now?", req.Name, HexFormat.Spaced(frame)),
                    Loc.T("Send"), danger: true)) return;
        }

        IsSending = true;
        StatusKind = ResponseStatus.Pending; StatusText = Loc.T("Waiting for reply...");
        var sent = HexFormat.Spaced(frame);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            if (_ctx is IEcuTransportProvider tp) transport = await tp.EnsureAddressedAsync(cts.Token);
            var reply = await transport.RequestAsync(frame, cts.Token);
            var resp = req.DecodeResponse(reply);
            ShowResponse(req, resp);
            AddHistory(req, sent, resp);
        }
        catch (OperationCanceledException)
        {
            Fail(req, sent, Loc.T("Timed out waiting for the ECU."));
        }
        catch (Exception ex)
        {
            Fail(req, sent, ex.Message);
        }
        finally { IsSending = false; }
    }

    private void Fail(EcuRequest req, string sent, string message)
    {
        StatusKind = ResponseStatus.Error; StatusText = message; RawResponse = "";
        History.Insert(0, new HistoryEntry { Time = DateTime.Now, RequestName = req.Name, Sent = sent, Received = "", Status = message, Ok = false, Request = req });
        TrimHistory();
        _toasts.Error(Loc.T("Request failed"), message);
    }

    private void AddHistory(EcuRequest req, string sent, EcuResponse resp)
    {
        History.Insert(0, new HistoryEntry
        {
            Time = DateTime.Now, RequestName = req.Name, Sent = sent, Received = HexFormat.Spaced(resp.Raw),
            Status = StatusText, Ok = resp.IsPositive, Request = req, Response = resp,
        });
        TrimHistory();
        OnPropertyChanged(nameof(HasHistory));
    }

    private void TrimHistory() { while (History.Count > 200) History.RemoveAt(History.Count - 1); OnPropertyChanged(nameof(HasHistory)); }

    private void ShowResponse(EcuRequest req, EcuResponse resp)
    {
        var ecu = _ctx.Ecu;
        RawResponse = HexFormat.Spaced(resp.Raw);
        Response.Clear();
        if (resp.IsNegative)
        {
            StatusKind = resp.IsResponsePending ? ResponseStatus.Pending : ResponseStatus.Error;
            StatusText = string.Format("{0}: 0x{1:X2} {2}", Loc.T("Negative response"), resp.NegativeResponseCode ?? 0, Loc.T(resp.NegativeResponseText ?? ""));
            foreach (var i in req.ReceiveItems) Response.Add(Placeholder(req, i));
            return;
        }

        if (resp.Raw.Length == 0)
        {
            StatusKind = ResponseStatus.Error; StatusText = Loc.T("Empty reply.");
            foreach (var i in req.ReceiveItems) Response.Add(Placeholder(req, i));
            return;
        }

        StatusKind = ResponseStatus.Ok;
        StatusText = Loc.F("Positive response, {0} bytes", resp.Raw.Length);
        var order = ecu?.Endianness ?? ByteOrder.Default;
        foreach (var v in resp.Values)
        {
            var d = v.Item.Data ?? ecu?.GetData(v.Item.Name);
            string raw = d?.GetHexValue(resp.Raw, v.Item, order) ?? "";
            Response.Add(new ResponseRow
            {
                Name = v.Name, Value = v.Text ?? Loc.T("(reply too short)"), Unit = v.Text != null && d is { Scaled: true } ? d.Unit : "",
                Raw = raw, Position = Pos(v.Item), IsMissing = v.Text == null, Description = Desc(d),
            });
        }
    }

    [RelayCommand]
    private void DecodeSample()
    {
        var req = SelectedRow?.Request;
        if (req == null) return;
        var bytes = HexUtil.Parse(req.ReplyBytes);
        if (bytes == null || bytes.Length == 0) return;
        // ReplyBytes is only the reply header; pad to the minimum length so every item can be decoded.
        if (bytes.Length < req.MinBytes) Array.Resize(ref bytes, req.MinBytes);
        var resp = req.DecodeResponse(bytes);
        ShowResponse(req, resp);
        StatusText = Loc.T("Decoded the simulated sample reply (not from the ECU).");
        StatusKind = ResponseStatus.Pending;
    }

    [RelayCommand]
    private void ResetParams()
    {
        foreach (var p in Params) p.Text = p.DefaultText;
    }

    [RelayCommand] private void ClearHistory() { History.Clear(); OnPropertyChanged(nameof(HasHistory)); }

    [RelayCommand]
    private async Task CopyFrameAsync()
    {
        var top = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime l ? l.MainWindow : null;
        if (top?.Clipboard != null && FrameHex.Length > 0) await top.Clipboard.SetTextAsync(FrameHex);
    }

    [RelayCommand]
    private async Task OpenEcuFileAsync()
    {
        var path = await _picker.PickFileAsync(Loc.T("Open ECU definition"), (Loc.T("ECU definitions"), ["*.xml", "*.json"]));
        if (path == null) return;
        try
        {
            var (ecu, layout) = await Task.Run(() => EcuContext.LoadFile(path));
            _ctx.SetEcu(ecu, layout, path);
        }
        catch (Exception ex) { _toasts.Error(Loc.T("Cannot open ECU file"), ex.Message); }
    }
}
