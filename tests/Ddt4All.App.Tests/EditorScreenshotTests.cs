using Avalonia.Headless.XUnit;
using Ddt4All.App.Services;
using Ddt4All.App.ViewModels;
using Ddt4All.Core.Layout;

namespace Ddt4All.App.Tests;

public class EditorScreenshotTests
{
    [AvaloniaFact]
    public async Task Requests_and_editor_pages()
    {
        var ctx = new EcuContext();
        using var app = new TestApp(ecuContext: ctx);
        app.Page<RequestsViewModel>(PageId.Requests);
        app.Screenshot("20-requests-empty");
        ctx.TransportOverride = new SampleEcu.StubTransport();
        ctx.SetEcu(SampleEcu.Create(), null, null);
        var r = app.Page<RequestsViewModel>(PageId.Requests);
        r.SelectedRow = r.Rows.First(x => x.Name == "Read coolant temperature");
        await r.SendCommand.ExecuteAsync(null);
        await app.PumpAsync(150);
        app.Screenshot("21-requests-read");
        app.Main.Session.IsExpertMode = true;
        r.SelectedRow = r.Rows.First(x => x.Name == "Set idle offset");
        r.Params[0].Text = "12.5";
        await app.PumpAsync(150);
        app.Screenshot("22-requests-write-expert");

        var layout = new EcuLayout();
        var cat = new ScreenCategory { Name = "Identification" };
        layout.Screens.Add(new Screen { Name = "Vehicle", Width = 640, Height = 400 });
        cat.ScreenNames.Add("Vehicle"); layout.Categories.Add(cat);
        ctx.SetEcu(SampleEcu.Create(), layout, null);
        var d = app.Page<DataEditorViewModel>(PageId.DataEditor);
        d.SelectedRequestRow = d.RequestRows.First(x => x.Name == "Read coolant temperature");
        await app.PumpAsync(200);
        app.Screenshot("23-editor-requests");
        d.TabIndex = 1;
        d.SelectedDataRow = d.DataRows.First(x => x.Name == "Coolant");
        await app.PumpAsync(200);
        app.Screenshot("24-editor-data");
        d.SelectedDataRow = d.DataRows.First(x => x.Name == "FanMode");
        await app.PumpAsync(200);
        app.Screenshot("25-editor-enum");
        d.TabIndex = 2; await app.PumpAsync(150); app.Screenshot("26-editor-ecu");
        d.SelectedRequestRow = d.RequestRows.First(x => x.Name == "Read VIN");
        d.SelectedRequest!.SentBytes = "22F";
        d.Revalidate();
        d.TabIndex = 4; await app.PumpAsync(150); app.Screenshot("27-editor-validation");
    }
}
