using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Ddt4All.App.Services;
using Ddt4All.App.ViewModels;
using Ddt4All.Core.Ecu;
using Microsoft.Extensions.DependencyInjection;

namespace Ddt4All.App.Tests;

public class RequestsTests
{
    private static (TestApp App, RequestsViewModel Vm, EcuContext Ctx, SampleEcu.StubTransport Stub) Setup(bool connected = true)
    {
        var ctx = new EcuContext();
        var app = new TestApp(ecuContext: ctx);
        var stub = new SampleEcu.StubTransport();
        if (connected) ctx.TransportOverride = stub;
        ctx.SetEcu(SampleEcu.Create(), null, null);
        var vm = app.Page<RequestsViewModel>(PageId.Requests);
        TestApp.Pump();
        return (app, vm, ctx, stub);
    }

    private static void Select(RequestsViewModel vm, string name) => vm.SelectedRow = vm.Rows.First(r => r.Name == name);

    [AvaloniaFact]
    public void Lists_requests_and_marks_write_services()
    {
        var (app, vm, _, _) = Setup();
        using var _a = app;
        Assert.Equal(8, vm.Rows.Count);
        Assert.True(vm.Rows.First(r => r.Name == "Set fan mode").IsWrite);
        Assert.True(vm.Rows.First(r => r.Name == "ECU reset").IsWrite);
        Assert.False(vm.Rows.First(r => r.Name == "Read VIN").IsWrite);
        Assert.False(vm.Rows.First(r => r.Name == "Start diagnostic session").IsWrite);
        vm.SearchText = "FAN";
        Assert.Equal(["Set fan mode"], vm.Rows.Select(r => r.Name));
        vm.SearchText = "22f4";
        Assert.Equal(2, vm.Rows.Count);
        vm.SearchText = "";
        vm.Filter = RequestFilter.Write;
        Assert.All(vm.Rows, r => Assert.True(r.IsWrite));
        vm.Filter = RequestFilter.Read;
        Assert.All(vm.Rows, r => Assert.False(r.IsWrite));
    }

    [AvaloniaFact]
    public void Typed_editors_build_the_frame()
    {
        var (app, vm, _, _) = Setup();
        using var _a = app;
        Select(vm, "Set fan mode");
        var p = Assert.Single(vm.Params);
        Assert.Equal(ParamKind.List, p.Kind);
        Assert.Equal(["Automatic", "Forced on", "Forced off"], p.Choices);
        Assert.Equal("2E F4 20 00", vm.FrameHex);
        p.Text = "Forced off";
        Assert.Equal("2E F4 20 02", vm.FrameHex);

        Select(vm, "Set idle offset");
        var o = Assert.Single(vm.Params);
        Assert.Equal(ParamKind.Scaled, o.Kind);
        Assert.Equal("%", o.Unit);
        o.Text = "12.5";
        Assert.Equal("2E F4 21 19", vm.FrameHex);   // 12.5 / 5 * 10 = 25
        o.Text = "abc";
        Assert.True(o.HasError);
        Assert.False(vm.CanSend);

        Select(vm, "Write calibration");
        Assert.Equal([ParamKind.Hex, ParamKind.Ascii], vm.Params.Select(x => x.Kind));
        vm.Params[0].Text = "BEEF";
        vm.Params[1].Text = "Z";
        Assert.Equal("2E F4 30 BE EF 5A", vm.FrameHex);
        vm.Params[0].Text = "XYZ";
        Assert.True(vm.Params[0].HasError);
        vm.ResetParamsCommand.Execute(null);
        Assert.False(vm.Params[0].HasError);
    }

    [AvaloniaFact]
    public async Task Write_requests_are_gated_by_expert_mode_and_confirmation()
    {
        var (app, vm, _, stub) = Setup();
        using var _a = app;
        Select(vm, "Set fan mode");
        Assert.False(vm.CanSend);
        Assert.Contains("Expert", vm.SendBlockReason);
        app.Main.Session.IsExpertMode = true;
        TestApp.Pump();
        Assert.True(vm.CanSend);

        app.Dialogs.Answer = false;
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Empty(stub.Sent);                       // declined

        app.Dialogs.Answer = true;
        vm.Params[0].Text = "Forced on";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal(new byte[] { 0x2E, 0xF4, 0x20, 0x01 }, Assert.Single(stub.Sent));
        Assert.Equal("6E F4 20", vm.RawResponse);
        Assert.True(vm.StatusIsOk);
        Assert.Single(vm.History);

        app.Main.Session.IsExpertMode = false;
        TestApp.Pump();
        Assert.False(vm.CanSend);
    }

    [AvaloniaFact]
    public async Task Safe_read_decodes_the_response_without_expert_mode()
    {
        var (app, vm, _, stub) = Setup();
        using var _a = app;
        Select(vm, "Read coolant temperature");
        Assert.True(vm.CanSend);
        Assert.Equal(["—", "—"], vm.Response.Select(r => r.Value));
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Response.Count);
        Assert.Equal("83", vm.Response[0].Value);       // 0x7B - 40
        Assert.Equal("°C", vm.Response[0].Unit);
        Assert.Equal("7b", vm.Response[0].Raw);
        Assert.Equal("High", vm.Response[1].Value);     // list text
        Assert.Equal("62 F4 05 7B 02", vm.RawResponse);

        Select(vm, "Read VIN");
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal("VF1RL000AB1234567", vm.Response[0].Value);

        Select(vm, "Read engine speed");
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal("750", vm.Response[0].Value);      // 0x0BB8 / 4
        Assert.Equal(3, vm.History.Count);
        Assert.Equal(3, stub.Sent.Count);
    }

    [AvaloniaFact]
    public async Task Negative_response_is_reported()
    {
        var (app, vm, _, _) = Setup();
        using var _a = app;
        Select(vm, "Start diagnostic session");
        await vm.SendCommand.ExecuteAsync(null);
        Assert.True(vm.StatusIsError);
        Assert.Contains("0x31", vm.StatusText);
        Assert.Equal("7F 10 31", vm.RawResponse);
        Assert.False(vm.History[0].Ok);
    }

    [AvaloniaFact]
    public void Offline_send_is_blocked_but_sample_decodes()
    {
        var (app, vm, _, _) = Setup(connected: false);
        using var _a = app;
        Select(vm, "Read VIN");
        Assert.False(vm.CanSend);
        Assert.Contains("Connect", vm.SendBlockReason);
        Assert.True(vm.HasSample);
        vm.DecodeSampleCommand.Execute(null);
        Assert.Single(vm.Response);
    }

    [AvaloniaFact]
    public void Switching_ecu_resets_the_page()
    {
        var (app, vm, ctx, _) = Setup();
        using var _a = app;
        Assert.True(vm.HasEcu);
        ctx.SetEcu(Ddt4All.Core.Editing.EcuEditing.NewEcu("Other"), null, null);
        TestApp.Pump();
        Assert.Single(vm.Rows);
        Assert.Equal("Other", vm.EcuTitle);
    }

    [AvaloniaFact]
    public void Filtering_five_thousand_requests_is_fast()
    {
        var (app, vm, ctx, _) = Setup();
        using var _a = app;
        var big = SampleEcu.Create();
        for (int i = 0; i < 5000; i++)
            big.Requests.Add(new EcuRequest { Name = "Request number " + i, SentBytes = "22" + (0xF000 + i).ToString("X4") });
        big.ResolveReferences();
        ctx.SetEcu(big, null, null);
        TestApp.Pump();
        Assert.Equal(5008, vm.Rows.Count);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) { vm.SearchText = "number 49"; vm.SearchText = ""; }
        Assert.True(sw.ElapsedMilliseconds < 800, $"{sw.ElapsedMilliseconds} ms");
        vm.SearchText = "number 4999";
        Assert.Equal(1, vm.Rows.Count);
    }
}
