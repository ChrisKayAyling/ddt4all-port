using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Ddt4All.App.Models;
using Ddt4All.App.Services;
using Ddt4All.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Ddt4All.App.Tests;

public class VehicleTests
{
    [Theory]
    [InlineData("FB", "Renault", "Megane IV")]
    [InlineData("xFB", "Renault", "Megane IV")]          // 'x' platform prefix used by definitions
    [InlineData("XFB", "Renault", "Megane IV")]
    [InlineData("HFEPh2", "Renault", "Kadjar Ph2")]      // exact code with phase suffix
    [InlineData("PZ1A", "Nissan", "Leaf")]
    public void Project_codes_resolve_to_manufacturer_and_model(string code, string maker, string model)
    {
        var v = VehicleCatalog.Resolve(code);
        Assert.NotNull(v);
        Assert.Equal(maker, v!.Manufacturer);
        Assert.Equal(model, v.Model);
    }

    [Fact]
    public void Unknown_and_special_codes_never_hide_an_ecu()
    {
        Assert.Null(VehicleCatalog.Resolve(""));
        Assert.Null(VehicleCatalog.Resolve("nonsense-code"));
        var other = VehicleCatalog.ResolveOrOther("nonsense-code");
        Assert.Equal(VehicleCatalog.OtherManufacturer, other.Manufacturer);
        Assert.Equal("Renault Samsung", VehicleCatalog.Parse("X", "Rsm Koleos").Manufacturer);
        Assert.Equal(VehicleCatalog.OtherManufacturer, VehicleCatalog.Parse("X", "Ddt_Training Foo").Manufacturer);
    }

    [Fact]
    public void Filter_by_manufacturer_and_vehicle()
    {
        EcuEntry E(string id, string maker, string model, string code) => new()
        {
            Id = id, Name = id, Address = "00", Protocol = "CAN", Project = "x" + code, Manufacturer = maker, Model = model,
            VehicleCode = code, ProjectName = maker + " " + model, SearchKey = (id + maker + model).ToLowerInvariant(),
        };
        var cat = new EcuCatalog([E("a", "Renault", "Megane IV", "FB"), E("b", "Renault", "Clio V", "BJA"), E("c", "Nissan", "Leaf", "PZ1A")], [], ["CAN"]);
        Assert.Equal(["Nissan", "Renault"], cat.Manufacturers);
        Assert.Equal(2, cat.Vehicles.Count(v => v.Manufacturer == "Renault"));
        Assert.Equal(2, EcuBrowserViewModel.Filter(cat, "", "", "", "none", default, "Renault", "").Count);
        Assert.Equal(1, EcuBrowserViewModel.Filter(cat, "", "", "", "none", default, "Renault", "FB").Count);
        Assert.Equal(1, EcuBrowserViewModel.Filter(cat, "leaf", "", "", "none", default).Count);   // vehicle names are searchable
    }

    /// <summary>Runs against the user's real ecu.zip when present (path from DDT4ALL_REAL_ZIP or /data0/ecu.zip); skipped otherwise.</summary>
    [AvaloniaFact]
    public async Task Real_database_manufacturer_model_cascade()
    {
        var zip = Environment.GetEnvironmentVariable("DDT4ALL_REAL_ZIP") ?? "/data0/ecu.zip";
        if (!File.Exists(zip)) return;
        var home = Path.Combine(Path.GetTempPath(), "ddt4all-veh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            using var sp = AppHost.Create(new AppHostOptions { SettingsPath = Path.Combine(home, "settings.json"), FileLogging = false });
            var catalog = sp.GetRequiredService<IEcuCatalogService>();
            Assert.True(await catalog.SetDatabasePathAsync(zip));
            var data = await catalog.LoadAsync();
            var withVehicle = data.Entries.Select(e => e.Id).Distinct().Count();
            var resolved = data.Entries.Where(e => e.VehicleCode.Length > 0).Select(e => e.Id).Distinct().Count();
            Assert.True(resolved >= withVehicle * 0.95, $"{resolved}/{withVehicle} ECUs have a vehicle");
            Assert.Contains("Renault", data.Manufacturers);
            Assert.Contains("Nissan", data.Manufacturers);

            var vm = new EcuBrowserViewModel(catalog, new Nav(), sp.GetRequiredService<INotificationService>(), sp.GetRequiredService<SessionState>(),
                sp.GetRequiredService<ISettingsService>());
            vm.OnNavigatedTo();
            for (int i = 0; i < 200 && vm.Rows.Count == 0; i++) { await Task.Delay(50); TestApp.Pump(); }
            var all = vm.ModelFilters.Count;
            vm.SelectedManufacturer = vm.ManufacturerFilters.First(m => m.Id == "Nissan");
            Assert.True(vm.ModelFilters.Count < all && vm.ModelFilters.Count > 1);               // models cascade from the manufacturer
            Assert.Contains(vm.ModelFilters, m => m.Title == "Leaf");
            vm.SelectedModel = vm.ModelFilters.First(m => m.Title == "Leaf");
            for (int i = 0; i < 100; i++) { await Task.Delay(30); TestApp.Pump(); if (vm.ResultSummary.Contains(" of ")) break; }
            Assert.All(vm.Rows.OfType<EcuEntry>(), e => Assert.Equal("Nissan", e.Manufacturer));
            Assert.NotEmpty(vm.Rows.OfType<EcuEntry>());

            // screenshot for visual review: Renault / Megane IV, first ECU selected
            vm.SelectedManufacturer = vm.ManufacturerFilters.First(m => m.Id == "Renault");
            vm.SelectedModel = vm.ModelFilters.First(m => m.Title == "Megane IV");
            for (int i = 0; i < 100; i++) { await Task.Delay(30); TestApp.Pump(); }
            var view = new Ddt4All.App.Views.EcuBrowserView { DataContext = vm };
            var window = new Avalonia.Controls.Window { Content = view, Width = 1180, Height = 720 };
            window.Show();
            vm.SelectedRow = vm.Rows.OfType<EcuEntry>().First();
            for (int i = 0; i < 20; i++) { await Task.Delay(20); TestApp.Pump(); }
            var dir = TestApp.FindScreenshotDir(); Directory.CreateDirectory(dir);
            window.CaptureRenderedFrame()!.Save(Path.Combine(dir, "28-ecu-browser-vehicles-real.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            window.Close();
        }
        finally { try { Directory.Delete(home, true); } catch { } }
    }

    private sealed class Nav : INavigationService { public void NavigateTo(PageId page) { } }
}
