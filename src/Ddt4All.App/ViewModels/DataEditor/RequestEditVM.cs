using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Ddt4All.Core.Codec;
using Ddt4All.Core.Ecu;
using Ddt4All.Core.Editing;

namespace Ddt4All.App.ViewModels;

/// <summary>Editable view of one <see cref="DataItem"/> placement inside a request.</summary>
public sealed partial class ItemEditVM : EditableVM
{
    private readonly RequestEditVM _owner;
    private readonly bool _sent;
    public ItemEditVM(IEditHost host, RequestEditVM owner, DataItem item, bool sent) : base(host) { _owner = owner; Item = item; _sent = sent; }
    public DataItem Item { get; }
    public IReadOnlyList<string> DataNames => Host.DataNames;
    public int ColorIndex { get; set; }
    public Avalonia.Media.IBrush Swatch => Ddt4All.App.Views.BitGridPalette.Brush(ColorIndex);

    private NamedCollection<DataItem> Items => _sent ? _owner.Request.SendItems : _owner.Request.ReceiveItems;

    public string Name
    {
        get => Item.Name;
        set
        {
            if (string.IsNullOrEmpty(value) || value == Item.Name) return;
            if (Items.Contains(value)) { Host.Warn(Loc("'{0}' is already used in this request.", value)); OnPropertyChanged(); return; }
            int idx = Items.IndexOf(Item.Name);
            Host.BeginEdit("item-name:" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this));
            Items.Remove(Item.Name);
            Item.Name = value;
            Items.Insert(Math.Max(idx, 0), Item);
            Host.Ecu.ResolveReferences();
            OnPropertyChanged(); OnPropertyChanged(nameof(Bits)); OnPropertyChanged(nameof(Missing));
            _owner.Refresh();
            Host.Modified();
        }
    }


    private static string Loc(string fmt, params object[] a) => Ddt4All.App.Localization.Loc.F(fmt, a);

    public int FirstByte { get => Item.FirstByte; set { if (Edit(Item.FirstByte, value, v => Item.FirstByte = Math.Max(0, v))) _owner.Refresh(); } }
    public int BitOffset { get => Item.BitOffset; set { if (Edit(Item.BitOffset, value, v => Item.BitOffset = Math.Max(0, v))) _owner.Refresh(); } }
    public int EndianIndex { get => (int)Item.Endian; set { if (Edit((int)Item.Endian, value, v => Item.Endian = (ByteOrder)Math.Clamp(v, 0, 2))) _owner.Refresh(); } }
    public bool Ref { get => Item.Ref; set => Edit(Item.Ref, value, v => Item.Ref = v); }
    public int Bits => Host.Ecu.Data.TryGetValue(Item.Name, out var d) ? d.BitsCount : 0;
    public bool Missing => !Host.Ecu.Data.Contains(Item.Name);
    public string Summary => Missing ? "?" : $"{Bits} bit";

    [RelayCommand] private void Remove() => _owner.RemoveItem(this, _sent);
}

/// <summary>Editable view of one request, including both item tables and their bit grids.</summary>
public sealed partial class RequestEditVM : EditableVM
{
    public RequestEditVM(IEditHost host, EcuRequest request) : base(host)
    {
        Request = request;
        foreach (var i in request.SendItems) SendItems.Add(new ItemEditVM(host, this, i, true));
        foreach (var i in request.ReceiveItems) ReceiveItems.Add(new ItemEditVM(host, this, i, false));
        Refresh();
    }

    public EcuRequest Request { get; }
    public ObservableCollection<ItemEditVM> SendItems { get; } = new();
    public ObservableCollection<ItemEditVM> ReceiveItems { get; } = new();

    public string Name
    {
        get => Request.Name;
        set
        {
            if (value == Request.Name) return;
            if (string.IsNullOrWhiteSpace(value) || Host.Ecu.Requests.Contains(value)) { Host.Warn(Ddt4All.App.Localization.Loc.T("Request names must be unique and not empty.")); OnPropertyChanged(); return; }
            var old = Request.Name;
            Structural(() => Host.Ecu.RenameRequest(old, value), request: value);
        }
    }

    public string SentBytes { get => Request.SentBytes; set { if (Edit(Request.SentBytes, (value ?? "").Trim(), v => Request.SentBytes = v, alsoNotify: [nameof(SentError)])) Refresh(); } }
    public string ReplyBytes { get => Request.ReplyBytes; set => Edit(Request.ReplyBytes, (value ?? "").Trim(), v => Request.ReplyBytes = v, alsoNotify: [nameof(ReplyError)]); }
    public int MinBytes { get => Request.MinBytes; set { if (Edit(Request.MinBytes, Math.Max(0, value), v => Request.MinBytes = v)) Refresh(); } }
    public int ShiftBytesCount { get => Request.ShiftBytesCount; set => Edit(Request.ShiftBytesCount, value, v => Request.ShiftBytesCount = v); }
    public bool ManualSend { get => Request.ManualSend; set => Edit(Request.ManualSend, value, v => Request.ManualSend = v); }

    private bool Deny(SessionAccess a) => (Request.Denied & a) != 0;
    private void SetDeny(SessionAccess a, bool on, string prop) =>
        Edit(Deny(a), on, v => Request.Denied = v ? Request.Denied | a : Request.Denied & ~a, prop);
    public bool DenyNoSds { get => Deny(SessionAccess.NoSds); set => SetDeny(SessionAccess.NoSds, value, nameof(DenyNoSds)); }
    public bool DenyPlant { get => Deny(SessionAccess.Plant); set => SetDeny(SessionAccess.Plant, value, nameof(DenyPlant)); }
    public bool DenyAfterSales { get => Deny(SessionAccess.AfterSales); set => SetDeny(SessionAccess.AfterSales, value, nameof(DenyAfterSales)); }
    public bool DenyEngineering { get => Deny(SessionAccess.Engineering); set => SetDeny(SessionAccess.Engineering, value, nameof(DenyEngineering)); }
    public bool DenySupplier { get => Deny(SessionAccess.Supplier); set => SetDeny(SessionAccess.Supplier, value, nameof(DenySupplier)); }

    public string? SentError => Request.SentBytes.Length > 0 && HexUtil.Parse(Request.SentBytes) == null ? Ddt4All.App.Localization.Loc.T("Not valid hex (even number of hex digits required).") : null;
    public string? ReplyError => Request.ReplyBytes.Length > 0 && HexUtil.Parse(Request.ReplyBytes) == null ? Ddt4All.App.Localization.Loc.T("Not valid hex (even number of hex digits required).") : null;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private BitGridModel _sentGrid = BitGridModel.Empty;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private BitGridModel _receivedGrid = BitGridModel.Empty;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private int _selectedSent = -1;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private int _selectedReceived = -1;

    public static readonly string[] EndianChoices = ["Default", "Little", "Big"];

    /// <summary>Rebuilds the bit grids after any change that moves bits.</summary>
    public bool HasSent => SendItems.Count > 0;
    public bool HasReceived => ReceiveItems.Count > 0;

    public void Refresh()
    {
        OnPropertyChanged(nameof(HasSent)); OnPropertyChanged(nameof(HasReceived));
        var tpl = HexUtil.Parse(Request.SentBytes) ?? Array.Empty<byte>();
        SentGrid = Build(Request.SendItems, SendItems, tpl, tpl.Length);
        ReceivedGrid = Build(Request.ReceiveItems, ReceiveItems, HexUtil.Parse(Request.ReplyBytes) ?? Array.Empty<byte>(), Request.MinBytes);
    }

    private BitGridModel Build(NamedCollection<DataItem> items, ObservableCollection<ItemEditVM> vms, byte[] bytes, int minBytes)
    {
        var order = Host.Ecu.Endianness;
        var list = new List<BitPlacement>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (i < vms.Count) vms[i].ColorIndex = i;
            int bits = Host.Ecu.Data.TryGetValue(it.Name, out var d) ? d.BitsCount : 0;
            list.Add(new BitPlacement(it.Name, it.FirstByte, it.BitOffset, bits, it.IsLittleEndian(order)));
        }

        var vals = bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)).ToArray();
        return new BitGridModel(list, minBytes, vals);
    }

    [RelayCommand] private void AddSent() => AddItem(true);
    [RelayCommand] private void AddReceived() => AddItem(false);

    private void AddItem(bool sent)
    {
        if (Host.DataNames.Count == 0) { Host.Warn(Ddt4All.App.Localization.Loc.T("Create a data definition first.")); return; }
        var coll = sent ? Request.SendItems : Request.ReceiveItems;
        var vms = sent ? SendItems : ReceiveItems;
        string name = Host.DataNames.FirstOrDefault(n => !coll.Contains(n)) ?? "";
        if (name.Length == 0) { Host.Warn(Ddt4All.App.Localization.Loc.T("Every data definition is already used in this request.")); return; }
        // next free byte after the last item
        int next = 1;
        foreach (var it in coll) if (Host.Ecu.Data.TryGetValue(it.Name, out var d)) next = Math.Max(next, it.FirstByte + Math.Max(1, (it.BitOffset + d.BitsCount + 7) / 8));
        Host.BeginEdit("add-item:" + Guid.NewGuid());
        var item = new DataItem(name, next);
        coll.Add(item);
        Host.Ecu.ResolveReferences();
        vms.Add(new ItemEditVM(Host, this, item, sent));
        Refresh();
        Host.Modified();
    }

    public void RemoveItem(ItemEditVM vm, bool sent)
    {
        Host.BeginEdit("remove-item:" + Guid.NewGuid());
        (sent ? Request.SendItems : Request.ReceiveItems).Remove(vm.Item.Name);
        (sent ? SendItems : ReceiveItems).Remove(vm);
        Refresh();
        Host.Modified();
    }
}
