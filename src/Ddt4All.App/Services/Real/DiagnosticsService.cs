using Ddt4All.App.Localization;
using Ddt4All.App.Models;
using Ddt4All.Comms;
using Ddt4All.Comms.Diagnostics;
using Ddt4All.Core.Ecu;
using Microsoft.Extensions.Logging;

namespace Ddt4All.App.Services.Real;

/// <summary>DTC read / clear on the current ECU session using the Comms diagnostic helpers (UDS first, KWP2000 fallback).</summary>
public sealed class DiagnosticsService : IDiagnosticsService
{
    private readonly IEcuSession _ecu;
    private readonly SessionState _state;
    private readonly ILogger<DiagnosticsService>? _log;

    public DiagnosticsService(IEcuSession ecu, SessionState state, ILogger<DiagnosticsService>? log = null)
    { _ecu = ecu; _state = state; _log = log; }

    public async Task<IReadOnlyList<DtcItem>> ReadDtcsAsync(string? ecu, CancellationToken ct = default)
    {
        var t = await _ecu.EnsureAddressedAsync(ct);
        IReadOnlyList<Dtc> list;
        bool kwp = _ecu.Ecu?.Protocol is Ddt4All.Core.Ecu.EcuProtocol.Kwp2000 or Ddt4All.Core.Ecu.EcuProtocol.Iso8;
        if (kwp) list = await t.ReadDtcsKwpAsync(ct: ct);
        else
        {
            try { list = await t.ReadDtcsUdsAsync(ct: ct); }
            catch (Exception ex) when (ex is NegativeResponseException or ProtocolException)
            {
                _log?.LogDebug("UDS DTC read refused ({Message}), trying KWP2000", ex.Message);
                list = await t.ReadDtcsKwpAsync(ct: ct);
            }
        }
        return list.Select(d => ToItem(d, _ecu.Ecu)).ToList();
    }

    public async Task ClearDtcsAsync(string? ecu, CancellationToken ct = default)
    {
        if (!_state.IsExpertMode) throw new InvalidOperationException(Loc.T("Expert mode required"));
        var t = await _ecu.EnsureAddressedAsync(ct);
        bool kwp = _ecu.Ecu?.Protocol is Ddt4All.Core.Ecu.EcuProtocol.Kwp2000 or Ddt4All.Core.Ecu.EcuProtocol.Iso8;
        if (kwp) { await t.ClearDtcsKwpAsync(ct: ct); return; }
        try { await t.ClearDtcsUdsAsync(ct: ct); }
        catch (Exception ex) when (ex is NegativeResponseException or ProtocolException)
        {
            _log?.LogDebug("UDS DTC clear refused ({Message}), trying KWP2000", ex.Message);
            await t.ClearDtcsKwpAsync(ct: ct);
        }
    }

    internal static DtcItem ToItem(Dtc d, EcuFile? file)
    {
        // ECU definitions list their DTCs as "devices": match on the 2 byte code
        uint two = d.Length == 3 ? d.Raw >> 8 : d.Raw;
        string description = "";
        if (file is not null)
            foreach (var dev in file.Devices)
                if (dev.Dtc == two) { description = dev.Name; break; }
        return new DtcItem(d.Text, description, StatusText(d.Status), (d.Status & 0x01) != 0);
    }

    /// <summary>ISO 14229 DTC status byte in words.</summary>
    internal static string StatusText(byte s)
    {
        var parts = new List<string>(4);
        if ((s & 0x08) != 0) parts.Add(Loc.T("Confirmed")); else if ((s & 0x04) != 0) parts.Add(Loc.T("Pending")); else parts.Add(Loc.T("Stored"));
        parts.Add((s & 0x01) != 0 ? Loc.T("present") : Loc.T("not present"));
        if ((s & 0x80) != 0) parts.Add(Loc.T("warning lamp"));
        return string.Join(", ", parts);
    }
}
