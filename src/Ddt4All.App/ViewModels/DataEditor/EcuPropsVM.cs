using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ddt4All.App.Localization;
using Ddt4All.Core.Codec;
using Ddt4All.Core.Ecu;

namespace Ddt4All.App.ViewModels;

public sealed partial class AutoIdentVM : EditableVM
{
    private readonly EcuPropsVM _owner;
    private AutoIdent _v;
    public AutoIdentVM(IEditHost host, EcuPropsVM owner, AutoIdent v) : base(host) { _owner = owner; _v = v; }
    public AutoIdent Value => _v;
    private void Set(AutoIdent n, string prop) { if (Edit(_v, n, x => { int i = Host.Ecu.AutoIdents.IndexOf(_v); _v = x; if (i >= 0) Host.Ecu.AutoIdents[i] = x; }, prop)) { } }
    public string DiagVersion { get => _v.DiagVersion; set => Set(_v with { DiagVersion = value ?? "" }, nameof(DiagVersion)); }
    public string Supplier { get => _v.Supplier; set => Set(_v with { Supplier = value ?? "" }, nameof(Supplier)); }
    public string Soft { get => _v.Soft; set => Set(_v with { Soft = value ?? "" }, nameof(Soft)); }
    public string Version { get => _v.Version; set => Set(_v with { Version = value ?? "" }, nameof(Version)); }
    [RelayCommand] private void Remove() => _owner.RemoveIdent(this);
}

/// <summary>ECU level properties: identity, protocol, CAN ids, K-line key words, byte order, projects, auto identification.</summary>
public sealed partial class EcuPropsVM : EditableVM
{
    public static readonly string[] Protocols = ["", "CAN", "KWP2000", "ISO8", "ISO", "DOIP"];
    public static readonly string[] Endians = ["Default (big)", "Little", "Big"];

    public EcuPropsVM(IEditHost host) : base(host)
    {
        foreach (var a in host.Ecu.AutoIdents) Idents.Add(new AutoIdentVM(host, this, a));
    }

    private Ddt4All.Core.Ecu.EcuFile E => Host.Ecu;
    public ObservableCollection<AutoIdentVM> Idents { get; } = new();

    public string EcuName { get => E.EcuName; set => Edit(E.EcuName, value ?? "", v => E.EcuName = v); }
    public int ProtocolIndex
    {
        get { int i = Array.IndexOf(Protocols, E.Protocol.ToWire()); return i < 0 ? 0 : i; }
        set => Edit(ProtocolIndex, value, v => E.Protocol = EcuProtocolNames.Parse(Protocols[Math.Clamp(v, 0, Protocols.Length - 1)]),
            alsoNotify: [nameof(IsCan), nameof(IsKLine), nameof(IsKwp)]);
    }
    public bool IsCan => E.Protocol == EcuProtocol.Can;
    public bool IsKwp => E.Protocol == EcuProtocol.Kwp2000;
    public bool IsKLine => E.Protocol is EcuProtocol.Kwp2000 or EcuProtocol.Iso8 or EcuProtocol.Iso;
    public string SendId { get => E.SendId; set => Edit(E.SendId, Hex(value), v => E.SendId = v, alsoNotify: [nameof(SendIdError)]); }
    public string RecvId { get => E.RecvId; set => Edit(E.RecvId, Hex(value), v => E.RecvId = v, alsoNotify: [nameof(RecvIdError)]); }
    public int BaudRate { get => E.BaudRate; set => Edit(E.BaudRate, Math.Max(0, value), v => E.BaudRate = v); }
    public bool FastInit { get => E.FastInit; set => Edit(E.FastInit, value, v => E.FastInit = v); }
    public string Kw1 { get => E.Kw1; set => Edit(E.Kw1, Hex(value), v => E.Kw1 = v); }
    public string Kw2 { get => E.Kw2; set => Edit(E.Kw2, Hex(value), v => E.Kw2 = v); }
    public string FuncName { get => E.FuncName; set => Edit(E.FuncName, value ?? "", v => E.FuncName = v); }
    public string FuncAddr { get => E.FuncAddr; set => Edit(E.FuncAddr, Hex(value), v => E.FuncAddr = v); }
    public int EndianIndex { get => (int)E.Endianness; set => Edit((int)E.Endianness, value, v => E.Endianness = (ByteOrder)Math.Clamp(v, 0, 2)); }
    public string ProjectsText
    {
        get => string.Join(", ", E.Projects);
        set => Edit(ProjectsText, value ?? "", v => { E.Projects.Clear(); E.Projects.AddRange(v.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)); });
    }

    private static string Hex(string? v) => (v ?? "").Trim().ToUpperInvariant();
    private static bool IsHex(string s) => s.Length > 0 && s.All(c => HexUtil.HexValue(c) >= 0);
    public string? SendIdError => E.Protocol == EcuProtocol.Can && !IsHex(E.SendId) ? Loc.T("Hexadecimal CAN id required.") : null;
    public string? RecvIdError => E.Protocol == EcuProtocol.Can && !IsHex(E.RecvId) ? Loc.T("Hexadecimal CAN id required.") : null;
    public string DevicesText => Loc.F("{0} fault code devices (not editable here)", E.Devices.Count);

    [RelayCommand]
    private void AddIdent()
    {
        Host.BeginEdit("ident-add:" + Guid.NewGuid());
        var a = new AutoIdent("", "", "", "");
        E.AutoIdents.Add(a);
        Idents.Add(new AutoIdentVM(Host, this, a));
        Host.Modified();
    }

    public void RemoveIdent(AutoIdentVM v)
    {
        Host.BeginEdit("ident-remove:" + Guid.NewGuid());
        int i = Idents.IndexOf(v);
        if (i >= 0) { E.AutoIdents.RemoveAt(i); Idents.RemoveAt(i); }
        Host.Modified();
    }
}
