using Ddt4All.App.Models;

namespace Ddt4All.App.Services.Fakes;

public sealed class FakeDiagnosticsService : IDiagnosticsService
{
    private List<DtcItem> _dtcs =
    [
        new("P0113", "Intake air temperature sensor circuit high", "Confirmed, present", true),
        new("P0299", "Turbocharger underboost", "Confirmed, present", true),
        new("U0100", "Lost communication with ECM/PCM", "Stored, intermittent", false),
        new("B1A22", "Driver airbag circuit resistance", "Confirmed, present", true),
        new("C0035", "Front left wheel speed sensor circuit", "Stored", false),
    ];

    public async Task<IReadOnlyList<DtcItem>> ReadDtcsAsync(string? ecu, CancellationToken ct = default)
    { await Task.Delay(350, ct); return _dtcs.ToArray(); }

    public async Task ClearDtcsAsync(string? ecu, CancellationToken ct = default)
    { await Task.Delay(350, ct); _dtcs = []; }
}
