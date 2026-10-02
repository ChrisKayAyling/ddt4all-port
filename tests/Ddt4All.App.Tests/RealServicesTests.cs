using Avalonia.Headless.XUnit;
using Ddt4All.App.Models;
using Ddt4All.App.Services;
using Ddt4All.App.Services.Real;
using Ddt4All.App.ViewModels;
using Ddt4All.Core.Database;
using Microsoft.Extensions.DependencyInjection;

namespace Ddt4All.App.Tests;

/// <summary>Real Core/Comms services over the built-in simulator and an ecu.zip generated from the Core test fixtures.</summary>
public class RealServicesTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "ddt4all-real-" + Guid.NewGuid().ToString("N"));
    private readonly string _zip;
    private readonly string _settings;

    public RealServicesTests()
    {
        Directory.CreateDirectory(_home);
        _zip = EndToEndCheck.BuildFixtureZip(Path.Combine(AppContext.BaseDirectory, "TestData"), Path.Combine(_home, "ecu.zip"));
        _settings = Path.Combine(_home, "settings.json");
    }

    public void Dispose() { try { Directory.Delete(_home, true); } catch { } }

    private ServiceProvider Create() => AppHost.Create(new AppHostOptions { SettingsPath = _settings, FileLogging = false });

    [Fact]
    public async Task End_to_end_over_the_simulator()
    {
        using var sp = Create();
        var log = await EndToEndCheck.RunAsync(sp, _zip);
        Assert.Contains(log, l => l.StartsWith("scan:") && l.Contains("RICH_ECU"));
        Assert.Contains(log, l => l.StartsWith("dtc:") && l.Contains("P0113"));
        Assert.Contains(log, l => l.StartsWith("terminal:") && l.Contains("62 F1 90"));
        await sp.GetRequiredService<IConnectionService>().DisconnectAsync();
    }

    [Fact]
    public async Task Catalog_maps_database_and_reports_missing_file()
    {
        using var sp = Create();
        var catalog = sp.GetRequiredService<IEcuCatalogService>();
        Assert.False(catalog.IsAvailable);                              // nothing configured: graceful empty state
        Assert.Empty((await catalog.LoadAsync()).Entries);
        Assert.NotNull(catalog.Error);
        Assert.False(await catalog.SetDatabasePathAsync(Path.Combine(_home, "nope.zip")));
        Assert.True(await catalog.SetDatabasePathAsync(_zip));
        var cat = await catalog.LoadAsync();
        Assert.True(catalog.IsAvailable);
        Assert.Contains(cat.Entries, e => e.Name == "RICH_ECU");
        Assert.Contains("CAN", cat.Protocols);
        var ecu = await catalog.LoadEcuAsync("rich_ecu.json");
        Assert.Equal("RICH_ECU", ecu.EcuName);
        Assert.Equal(_zip, sp.GetRequiredService<ISettingsService>().Current.EcuZipPath);
    }

    [Fact]
    public async Task Diagnostics_clear_requires_expert_mode_and_session()
    {
        using var sp = Create();
        var diag = sp.GetRequiredService<IDiagnosticsService>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => diag.ReadDtcsAsync(null));   // no ECU / not connected
        await Assert.ThrowsAsync<InvalidOperationException>(() => diag.ClearDtcsAsync(null));
    }

    [Fact]
    public void Scan_replicates_decimal_diag_conversion()
    {
        // DB stores diag versions as hex ("10"); the scanner sends the byte and Python turns it into the DECIMAL text "10"
        Assert.Equal("10", EcuScanService.Dec(10));
        Assert.Equal("8", EcuScanService.Dec(8));
        Assert.Equal((byte)10, SimulatedVehicle.DiagByteFor("10"));
        Assert.Equal((byte)8, SimulatedVehicle.DiagByteFor("08"));
    }

    [AvaloniaFact]
    public async Task Scan_viewmodel_lists_found_ecu_and_opens_it()
    {
        using var sp = Create();
        var catalog = sp.GetRequiredService<IEcuCatalogService>();
        await catalog.SetDatabasePathAsync(_zip);
        var conn = sp.GetRequiredService<IConnectionService>();
        await conn.ConnectAsync(new ConnectionRequest(TransportKind.Serial, null, 38400, null, 0, DeviceProfile.All[0], true, false));
        var nav = new Nav();
        var vm = new ScanViewModel(sp.GetRequiredService<IScanService>(), catalog, conn, sp.GetRequiredService<IEcuSession>(),
            sp.GetRequiredService<IFilePickerService>(), sp.GetRequiredService<INotificationService>(), nav);
        await vm.RefreshAsync();
        Assert.True(vm.CanStart);
        await vm.StartScanCommand.ExecuteAsync(null);
        TestApp.Pump();
        Assert.NotEmpty(vm.Results);
        Assert.Equal(100, vm.Progress);
        var row = vm.Results.First(r => r.CanOpen);
        await row.OpenCommand.ExecuteAsync(null);
        Assert.True(sp.GetRequiredService<IEcuSession>().IsLoaded);
        Assert.Equal(PageId.Screens, nav.Last);
        Assert.StartsWith("RICH_ECU", sp.GetRequiredService<SessionState>().CurrentEcu);
        await conn.DisconnectAsync();
    }

    private sealed class Nav : INavigationService { public PageId? Last; public void NavigateTo(PageId page) => Last = page; }
}
