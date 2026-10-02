using System.Text;
using Ddt4All.Comms;
using Ddt4All.Comms.Elm;
using Ddt4All.Comms.Serial;
using Ddt4All.Comms.Simulation;
using Ddt4All.Core.Database;
using Ddt4All.Core.Ecu;
using CoreProtocol = Ddt4All.Core.Ecu.EcuProtocol;

namespace Ddt4All.App.Services.Real;

/// <summary>
/// A virtual car for demo / simulation mode: a <see cref="SimulatedBus"/> with a software ELM327 in front of it. When an
/// ecu.zip is available the ECUs on the bus are derived from it (identification answers match the database autoidents,
/// the ECU's own requests are answered with plausible moving values), so scan, DTC read and screens all work without hardware.
/// </summary>
public sealed class SimulatedVehicle : IAsyncDisposable
{
    private readonly CancellationTokenSource _traffic = new();

    private SimulatedVehicle(SimulatedBus bus, SimulatedElm device, InMemorySerialLink host, IReadOnlyList<string> ecus)
    { Bus = bus; Device = device; Host = host; EcuNames = ecus; }

    public SimulatedBus Bus { get; }
    public SimulatedElm Device { get; }
    public InMemorySerialLink Host { get; }
    public IReadOnlyList<string> EcuNames { get; }

    /// <summary>Maximum number of ECUs taken from the database.</summary>
    public const int MaxDatabaseEcus = 10;

    public static SimulatedVehicle Create(EcuDatabase? db, CanAddressing addressing)
    {
        var bus = new SimulatedBus();
        var names = new List<string>();
        var usedIds = new HashSet<string>();

        if (db is not null) AddDatabaseEcus(bus, db, addressing, names, usedIds);
        if (names.Count == 0) AddDefaultEcus(bus, addressing, names, usedIds);

        var (host, device) = SimulatedElm.Create(bus, new SimulatedElmOptions { Version = "ELM327 v1.5", Voltage = 12.6 });
        var v = new SimulatedVehicle(bus, device, host, names);
        var frames = new List<(uint, byte[])>();
        uint[] ids = [0x0C6, 0x12E, 0x186, 0x1F6, 0x29A, 0x35C, 0x391, 0x3F7, 0x5DA, 0x5E8, 0x621, 0x7BB];
        for (int i = 0; i < ids.Length; i++)
            frames.Add((ids[i], new byte[] { (byte)ids[i], (byte)(i * 17), 0x10, 0x00, 0x7F, 0xFF, (byte)(i * 3), (byte)i }));
        _ = bus.GenerateTrafficAsync(frames, TimeSpan.FromMilliseconds(4), v._traffic.Token);
        return v;
    }

    private static void AddDatabaseEcus(SimulatedBus bus, EcuDatabase db, CanAddressing addressing, List<string> names, HashSet<string> usedIds)
    {
        var seen = new HashSet<string>();
        int kline = 0;
        foreach (var e in db.Entries)
        {
            if (names.Count >= MaxDatabaseEcus) break;
            if (e.AutoIdentCount == 0) continue;
            var ident = e.GetAutoIdent(0);
            switch (e.Protocol)
            {
                case CoreProtocol.Can:
                {
                    if (!seen.Add("C" + e.Address) || addressing.Lookup(e.Address) is not { } ids) continue;
                    EcuFile? file = null;
                    try { file = db.LoadEcu(e); } catch (Exception) { /* definition unreadable: identify only */ }
                    var script = BuildScript(file, ident, kwp: false);
                    AddCan(bus, e.EcuName, ids.Tx, ids.Rx, script, usedIds);
                    if (file is not null && Ddt4All.Comms.Hex.TryParse(file.SendId, out _) && file.SendId.Trim('0').Length > 0 && file.RecvId.Trim('0').Length > 0)
                        AddCan(bus, e.EcuName, file.SendId, file.RecvId, script, usedIds);
                    names.Add($"{e.EcuName} ({e.Address})");
                    break;
                }
                case CoreProtocol.Kwp2000 or CoreProtocol.Iso8 or CoreProtocol.Iso:
                {
                    if (kline >= 2 || !seen.Add("K" + e.Address) || !byte.TryParse(e.Address, System.Globalization.NumberStyles.HexNumber, null, out var a)) continue;
                    EcuFile? file = null;
                    try { file = db.LoadEcu(e); } catch (Exception) { }
                    bus.Add(new FakeKwpEcu(e.EcuName, a, BuildScript(file, ident, kwp: true)));
                    names.Add($"{e.EcuName} ({e.Address}, K-line)");
                    kline++;
                    break;
                }
            }
        }
    }

    private static void AddDefaultEcus(SimulatedBus bus, CanAddressing addressing, List<string> names, HashSet<string> usedIds)
    {
        (string Addr, string Name, string Sup, string Soft, string Ver)[] defaults =
        [
            ("26", "Demo UCH", "DEMO", "0001", "0100"),
            ("04", "Demo Cluster", "DEMO", "0002", "0100"),
            ("01", "Demo ABS", "DEMO", "0003", "0100"),
        ];
        foreach (var d in defaults)
        {
            var (tx, rx) = addressing.Lookup(d.Addr) ?? ("7E0", "7E8");
            if (!usedIds.Add(tx)) continue;
            AddCan(bus, d.Name, tx, rx, BuildScript(null, new AutoIdent("8", d.Sup, d.Soft, d.Ver), kwp: false), usedIds);
            names.Add($"{d.Name} ({d.Addr})");
        }
    }

    private static void AddCan(SimulatedBus bus, string name, string txHex, string rxHex, EcuScript script, HashSet<string> usedIds)
    {
        if (!uint.TryParse(txHex, System.Globalization.NumberStyles.HexNumber, null, out var tx) ||
            !uint.TryParse(rxHex, System.Globalization.NumberStyles.HexNumber, null, out var rx)) return;
        if (!usedIds.Add(txHex.ToUpperInvariant())) return;
        bus.Add(new FakeCanEcu(name, tx, rx, script, is29Bit: txHex.Length > 3));
    }

    // ----------------------------------------------------------------- ECU behaviour

    /// <summary>Diag version byte the Python scanner would turn back into the database value (decimal digits read as hex).</summary>
    internal static byte DiagByteFor(string dbDiag)
    {
        if (!int.TryParse(dbDiag, System.Globalization.NumberStyles.HexNumber, null, out var v)) return 0x08;
        var digits = v.ToString("X");
        return digits.All(char.IsAsciiDigit) && int.TryParse(digits, out var b) && b <= 255 ? (byte)b : (byte)(v & 0xFF);
    }

    internal static EcuScript BuildScript(EcuFile? file, AutoIdent ident, bool kwp)
    {
        var s = new EcuScript();
        var dtcs = new List<(byte[] Code, byte Status)>
        {
            ([0x01, 0x13, 0x00], 0x2F),   // P0113 active
            ([0x02, 0x99, 0x00], 0x2F),   // P0299 active
            ([0x40, 0x35, 0x00], 0x08),   // C0035 stored
            ([0xC1, 0x00, 0x00], 0x28),   // U0100 stored
        };
        var gate = new object();

        s.On("10", req => [new EcuReply([0x50, req.Length > 1 ? req[1] : (byte)1, 0x00, 0x32, 0x01, 0xF4])]);
        s.On("3E", "7E00");
        s.On("11", req => [new EcuReply([0x51, req.Length > 1 ? req[1] : (byte)1])]);

        byte diag = DiagByteFor(ident.DiagVersion ?? "");
        string supplier = ident.Supplier ?? "", soft = ident.Soft ?? "", version = ident.Version ?? "";
        s.On("22F1A0", _ => [new EcuReply([0x62, 0xF1, 0xA0, diag])]);
        s.On("22F18A", _ => [new EcuReply([0x62, 0xF1, 0x8A, .. Encoding.ASCII.GetBytes(supplier)])]);
        s.On("22F194", _ => [new EcuReply([0x62, 0xF1, 0x94, .. Encoding.ASCII.GetBytes(soft)])]);
        s.On("22F195", _ => [new EcuReply([0x62, 0xF1, 0x95, .. Encoding.ASCII.GetBytes(version)])]);
        s.On("22F190", _ => [new EcuReply([0x62, 0xF1, 0x90, .. Encoding.ASCII.GetBytes("VF1SIMULATED00001")])]);
        s.On("2180", _ =>
        {
            var r = new byte[26];
            r[0] = 0x61; r[1] = 0x80; r[7] = diag;
            var sup = Encoding.ASCII.GetBytes(supplier.PadRight(3)[..3]);
            Array.Copy(sup, 0, r, 8, 3);
            WriteHex(r, 16, soft, 2); WriteHex(r, 18, version, 2);
            return [new EcuReply(r)];
        });

        s.On("1902", _ =>
        {
            lock (gate)
            {
                var r = new List<byte> { 0x59, 0x02, 0xFF };
                foreach (var d in dtcs) { r.AddRange(d.Code); r.Add(d.Status); }
                return [new EcuReply(r.ToArray())];
            }
        });
        s.On("18", _ =>
        {
            lock (gate)
            {
                var r = new List<byte> { 0x58, (byte)dtcs.Count };
                foreach (var d in dtcs) { r.Add(d.Code[0]); r.Add(d.Code[1]); r.Add(d.Status); }
                return [new EcuReply(r.ToArray())];
            }
        });
        s.On("14", _ => { lock (gate) dtcs.Clear(); return [new EcuReply([0x54, 0xFF, 0x00])]; });

        if (file is not null)
        {
            int k = 0;
            foreach (var req in file.Requests)
            {
                var t = req.GetSentBytesTemplate().ToArray();
                if (t.Length == 0) continue;
                int idx = ++k;
                int prefixLen = Math.Min(t.Length, t[0] is 0x22 or 0x2E or 0x2F ? 3 : 2);
                if (t[0] is 0x10 or 0x3E or 0x14 or 0x19 or 0x11) continue;   // handled generically above
                var header = new byte[prefixLen]; Array.Copy(t, header, prefixLen);
                header[0] = (byte)(header[0] + 0x40);
                int total = Math.Max(req.MinBytes, prefixLen + 1);
                int n = 0;
                s.On(Convert.ToHexString(t, 0, prefixLen), _ =>
                {
                    n++;
                    var r = new byte[total];
                    Array.Copy(header, r, prefixLen);
                    // deterministic, slowly moving payload so live data / graphs look alive
                    for (int i = prefixLen; i < total; i++) r[i] = (byte)(0x20 + (idx * 13 + i * 7 + (n / 4)) % 90);
                    return [new EcuReply(r)];
                });
            }
        }
        return s;
    }

    private static void WriteHex(byte[] dest, int offset, string hex, int count)
    {
        hex = new string(hex.Where(Uri.IsHexDigit).ToArray());
        hex = hex.PadLeft(count * 2, '0');
        if (hex.Length > count * 2) hex = hex[^(count * 2)..];
        Convert.FromHexString(hex).CopyTo(dest, offset);
    }

    public async ValueTask DisposeAsync()
    {
        _traffic.Cancel();
        await Device.DisposeAsync().ConfigureAwait(false);
        _traffic.Dispose();
    }
}
