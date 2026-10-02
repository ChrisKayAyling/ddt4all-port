using Ddt4All.Comms;
using Ddt4All.Comms.Simulation;
using Ddt4All.Core.Database;
using Ddt4All.Core.Ecu;
using Ddt4All.Core.Layout;

namespace Ddt4All.App.Tests;

/// <summary>Access to the user's real ecu.zip (DDT4ALL_REAL_ZIP or /data0/ecu.zip). Tests that need it skip cleanly when it is absent.</summary>
internal static class RealZip
{
    public static string? Path
    {
        get
        {
            var p = Environment.GetEnvironmentVariable("DDT4ALL_REAL_ZIP");
            if (!string.IsNullOrEmpty(p)) return File.Exists(p) ? p : null;
            return File.Exists("/data0/ecu.zip") ? "/data0/ecu.zip" : null;
        }
    }

    public static bool Available => Path is not null;

    public static void RequireOrSkip()
    {
        if (!Available) Assert.Skip("real ecu.zip not available (set DDT4ALL_REAL_ZIP or provide /data0/ecu.zip)");
    }

    private static EcuDatabase? _db;
    private static readonly object Lock = new();

    public static EcuDatabase Db()
    {
        lock (Lock)
        {
            return _db ??= EcuDatabase.Open(Path!, new EcuDatabaseOptions { CacheDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ddt4all-tests-realzip-cache") });
        }
    }

    public static (EcuFile Ecu, EcuLayout Layout, Screen Screen) Load(string ecuName, string screenName)
    {
        var db = Db();
        foreach (var e in db.Entries)
        {
            if (!string.Equals(e.EcuName, ecuName, StringComparison.Ordinal)) continue;
            var layout = db.LoadLayout(e);
            if (!layout.Screens.TryGetValue(screenName, out var scr)) continue;
            return (db.LoadEcu(e), layout, scr);
        }
        throw new InvalidOperationException($"ECU/screen not found in the real zip: {ecuName} / {screenName}");
    }

    /// <summary>Simulated ECU answering every request used by the screen's displays with slowly moving synthetic bytes.</summary>
    public static async Task<SimulatedTransport> SimulatorAsync(EcuFile ecu, Screen screen)
    {
        var script = new EcuScript();
        int tick = 0;
        var done = new HashSet<string>();
        foreach (var d in screen.Displays)
        {
            var req = ecu.GetRequest(d.RequestName);
            if (req is null || !req.TryBuildRequest(null, out var payload, out _) || payload is null || payload.Length == 0) continue;
            if (!done.Add(Convert.ToHexString(payload))) continue;
            int len = Math.Max(req.MinBytes, payload.Length);
            foreach (var it in req.ReceiveItems)
            {
                int bits = it.Data?.BitsCount ?? 8;
                len = Math.Max(len, it.FirstByte - 1 + (it.BitOffset + bits + 7) / 8);
            }
            len = Math.Min(len, 400);
            var pl = payload;
            script.On(Convert.ToHexString(payload), _ =>
            {
                int k = Interlocked.Increment(ref tick);
                var b = new byte[len];
                for (int i = 0; i < len; i++)
                    b[i] = i < pl.Length ? pl[i] : (byte)(((i * 37 + 11) % 200) + (int)(25 * Math.Sin(k / 7.0 + i * 0.7)));
                b[0] = (byte)(pl[0] + 0x40);
                return [new EcuReply(b)];
            });
        }
        var t = new SimulatedTransport().AddCan("REAL", 0x700, script);
        await t.ConnectEcuAsync(new EcuAddress { Name = "REAL", TxId = 0x700 });
        return t;
    }
}
