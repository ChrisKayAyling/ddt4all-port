using Ddt4All.App.Models;
using Ddt4All.Comms.Diagnostics;
using Ddt4All.Core.Database;
using Microsoft.Extensions.DependencyInjection;

namespace Ddt4All.App.Services.Real;

/// <summary>
/// Drives the whole real stack against the built-in simulator: open ecu.zip, connect, scan, open the found ECU, read / clear DTCs,
/// terminal requests, a request from the ECU definition and the CAN sniffer. Used by <c>--headless-check</c> and the App tests.
/// Throws on the first failed step; returns a human readable log otherwise.
/// </summary>
public static class EndToEndCheck
{
    /// <summary>Builds an ecu.zip from the ECU XML fixtures of tests/Ddt4All.Core.Tests (<see cref="EcuZipBuilder"/>).</summary>
    public static string BuildFixtureZip(string fixtureDir, string outputZip)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputZip))!);
        using var fs = File.Create(outputZip);
        using var zb = new EcuZipBuilder(fs);
        foreach (var n in new[] { "rich_ecu.xml", "dummy_ecu_can.xml", "dummy_ecu_can_ext.xml", "dummy_ecu_iso8.xml", "dummy_ecu_kwp.xml" })
        {
            var f = Path.Combine(fixtureDir, n);
            if (!File.Exists(f)) continue;
            using var xml = File.OpenRead(f);
            zb.AddXml(n, xml);
        }
        return outputZip;
    }

    /// <summary>Finds tests/Ddt4All.Core.Tests/TestData above the executable (source checkout) or via DDT4ALL_FIXTURES.</summary>
    public static string? FindFixtureDir()
    {
        if (Environment.GetEnvironmentVariable("DDT4ALL_FIXTURES") is { Length: > 0 } e && Directory.Exists(e)) return e;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var c = Path.Combine(d.FullName, "tests", "Ddt4All.Core.Tests", "TestData");
            if (Directory.Exists(c)) return c;
            var local = Path.Combine(d.FullName, "TestData");
            if (File.Exists(Path.Combine(local, "rich_ecu.xml"))) return local;
        }
        return null;
    }

    public static async Task<IReadOnlyList<string>> RunAsync(IServiceProvider sp, string zipPath, CancellationToken ct = default)
    {
        var log = new List<string>();
        void Step(string s) => log.Add(s);
        var catalog = sp.GetRequiredService<IEcuCatalogService>();
        var connection = sp.GetRequiredService<IConnectionService>();
        var session = sp.GetRequiredService<IEcuSession>();
        var diag = sp.GetRequiredService<IDiagnosticsService>();
        var scan = sp.GetRequiredService<IScanService>();
        var state = sp.GetRequiredService<SessionState>();

        // 1. database
        if (!await catalog.SetDatabasePathAsync(zipPath, ct).ConfigureAwait(false)) throw new InvalidOperationException("ecu.zip not opened: " + catalog.Error);
        var cat = await catalog.LoadAsync(ct).ConfigureAwait(false);
        if (cat.Entries.Count == 0) throw new InvalidOperationException("catalog is empty");
        Step($"catalog: {cat.Entries.Count} rows, {cat.Projects.Count} projects ({catalog.DatabasePath})");

        // 2. connect to the simulator
        var profile = DeviceProfile.All[0];
        await connection.ConnectAsync(new ConnectionRequest(TransportKind.Serial, null, 38400, null, 0, profile, true, false), ct).ConfigureAwait(false);
        if (connection.State != ConnectionState.Connected) throw new InvalidOperationException("not connected");
        Step($"connected: {connection.Adapter?.Name} / {connection.Adapter?.Firmware} / {connection.Adapter?.Voltage:0.0} V");

        // 3. scan + identify
        var scanProgress = new List<int>();
        var found = await scan.ScanAsync(new ScanRequest(), new Progress<Ddt4All.Comms.Scanning.ScanProgress>(p => scanProgress.Add(p.Completed)), null, ct).ConfigureAwait(false);
        if (found.Count == 0) throw new InvalidOperationException("scan found no ECU");
        foreach (var f in found) Step($"scan: {f.FunctionalAddress} {f.AddressName} -> {f.EcuName ?? "(no file)"} [{f.Match}] diag={f.DiagVersion} {f.Supplier}/{f.Soft}/{f.Version}");
        var hit = found.FirstOrDefault(f => f.HasDefinition) ?? throw new InvalidOperationException("no scanned ECU matched the database");

        // 4. open it into the shared session
        await session.OpenAsync(hit.Href!, hit.BusAddress, ct).ConfigureAwait(false);
        if (!session.IsLoaded || session.Ecu is null) throw new InvalidOperationException("ECU not loaded");
        Step($"session: {session.DisplayName}, {session.Ecu.Requests.Count} requests, {session.Layout?.Screens.Count ?? 0} screens, status bar '{state.CurrentEcu}'");

        // 5. DTCs (clear needs expert mode)
        var dtcs = await diag.ReadDtcsAsync(session.DisplayName, ct).ConfigureAwait(false);
        if (dtcs.Count == 0) throw new InvalidOperationException("no DTCs read from the simulated ECU");
        Step("dtc: " + string.Join(", ", dtcs.Select(d => d.Code)));
        state.IsExpertMode = false;
        try { await diag.ClearDtcsAsync(session.DisplayName, ct).ConfigureAwait(false); throw new InvalidOperationException("DTC clear was allowed outside expert mode"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Expert", StringComparison.OrdinalIgnoreCase)) { Step("dtc clear blocked outside expert mode: ok"); }
        state.IsExpertMode = true;
        await diag.ClearDtcsAsync(session.DisplayName, ct).ConfigureAwait(false);
        state.IsExpertMode = false;
        if ((await diag.ReadDtcsAsync(session.DisplayName, ct).ConfigureAwait(false)).Count != 0) throw new InvalidOperationException("DTCs not cleared");
        Step("dtc clear (expert): ok, list empty afterwards");

        // 6. terminal
        var at = await connection.SendRawAsync("ATRV", ct).ConfigureAwait(false);
        var rsp = await connection.SendRawAsync("22 F1 90", ct).ConfigureAwait(false);
        if (!rsp.StartsWith("62 F1 90", StringComparison.Ordinal)) throw new InvalidOperationException("unexpected terminal response: " + rsp);
        Step($"terminal: ATRV -> {at}; 22 F1 90 -> {rsp}");

        // 7. a request of the ECU definition, decoded
        var req = session.Ecu.Requests.FirstOrDefault(r => !r.ManualSend && r.GetSentBytesTemplate().Length > 0 && r.GetSentBytesTemplate()[0] is 0x21 or 0x22);
        if (req is not null)
        {
            var t = await session.EnsureAddressedAsync(ct).ConfigureAwait(false);
            var r = await req.SendAsync(t, null, ct).ConfigureAwait(false);
            Step($"request {req.Name}: {(r.IsPositive ? "positive" : "negative")}, {r.Values.Count} values" + (r.Values.Count > 0 ? $" (first: {r.Values[0].Name}={r.Values[0].Text})" : ""));
        }

        // 8. sniffer
        if (sp.GetRequiredService<ISnifferService>() is { } sniffer)
        {
            int frames = 0;
            sniffer.FramesReceived += b => Interlocked.Add(ref frames, b.Count);
            await sniffer.StartAsync(ct).ConfigureAwait(false);
            for (int i = 0; i < 40 && Volatile.Read(ref frames) == 0; i++) await Task.Delay(50, ct).ConfigureAwait(false);
            await sniffer.StopAsync().ConfigureAwait(false);
            if (frames == 0) throw new InvalidOperationException("sniffer saw no frames");
            Step($"sniffer: {frames} frames");
        }

        return log;
    }
}
