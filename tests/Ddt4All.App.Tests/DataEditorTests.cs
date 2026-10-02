using Avalonia.Headless.XUnit;
using Ddt4All.App.Services;
using Ddt4All.App.ViewModels;
using Ddt4All.Core.Ecu;
using Ddt4All.Core.Editing;
using Ddt4All.Core.Layout;
using Microsoft.Extensions.DependencyInjection;

namespace Ddt4All.App.Tests;

public class BitGridModelTests
{
    [Fact]
    public void Big_endian_items_cover_the_expected_cells()
    {
        var m = new BitGridModel([new("A", 2, 0, 8, false), new("B", 3, 4, 8, false)], 0, ["62", "F1"]);
        Assert.Equal(4, m.ByteCount);                 // B spans bits 4..11 of byte 3 -> byte 4
        Assert.Equal(0, m.Cells[8]);
        Assert.Equal(0, m.Cells[15]);
        Assert.Equal(BitGridModel.Free, m.Cells[16]);
        Assert.Equal(1, m.Cells[16 + 4]);
        Assert.Equal(1, m.Cells[24 + 3]);
        Assert.Equal(BitGridModel.Free, m.Cells[24 + 4]);
        Assert.Contains(new BitSpan(1, 2, 4, 4), m.Spans);
        Assert.Contains(new BitSpan(1, 3, 0, 4), m.Spans);
        Assert.False(m.HasOverlap);
        Assert.Equal("F1", m.ByteValues[1]);
    }

    [Fact]
    public void Overlaps_are_flagged_and_min_bytes_extends_the_grid()
    {
        var m = new BitGridModel([new("A", 1, 0, 16, false), new("B", 2, 0, 8, false)], 6, []);
        Assert.True(m.HasOverlap);
        Assert.Equal(BitGridModel.Overlap, m.Cells[8]);
        Assert.Equal(6, m.ByteCount);
    }

    [Fact]
    public void Little_endian_single_byte_field_ends_before_the_offset()
    {
        var m = new BitGridModel([new("L", 1, 2, 3, true)], 0, []);
        Assert.Equal(1, m.ByteCount);
        // 8 - 2 = 6: columns 3..5
        Assert.Equal([-1, -1, -1, 0, 0, 0, -1, -1], m.Cells);
    }
}

public class DataEditorTests
{
    private static EcuLayout SampleLayout()
    {
        var l = new EcuLayout();
        var cat = new ScreenCategory { Name = "Identification" };
        var s = new Screen { Name = "Vehicle", Width = 640, Height = 400, Color = new LayoutColor(0xDD, 0xEE, 0xFF) };
        s.Displays.Add(new ScreenDisplay { DataName = "VIN", RequestName = "Read VIN" });
        var b = new ScreenButton { Text = "Reset", UniqueName = "b1" };
        b.Send.Add(new SendCommand("Does not exist", 0));
        s.Buttons.Add(b);
        l.Screens.Add(s);
        cat.ScreenNames.Add("Vehicle");
        l.Categories.Add(cat);
        return l;
    }

    private static (TestApp App, DataEditorViewModel Vm, EcuContext Ctx, string Dir) Setup(bool layout = false)
    {
        var ctx = new EcuContext();
        var app = new TestApp(ecuContext: ctx);
        ctx.SetEcu(SampleEcu.Create(), layout ? SampleLayout() : null, null);
        var vm = app.Page<DataEditorViewModel>(PageId.DataEditor);
        vm.SaveDirectoryOverride = Path.Combine(app.Home, "ecus");
        return (app, vm, ctx, vm.SaveDirectory);
    }

    private static void SelectRequest(DataEditorViewModel vm, string n) => vm.SelectedRequestRow = vm.RequestRows.First(r => r.Name == n);
    private static void SelectData(DataEditorViewModel vm, string n) => vm.SelectedDataRow = vm.DataRows.First(r => r.Name == n);

    [AvaloniaFact]
    public void Edits_work_on_a_copy_and_undo_redo_restore_values()
    {
        var (app, vm, ctx, _) = Setup();
        using var _a = app;
        Assert.True(vm.HasEcu);
        Assert.NotSame(ctx.Ecu, vm.Working);
        Assert.False(vm.IsDirty);
        SelectRequest(vm, "Read VIN");
        vm.SelectedRequest!.SentBytes = "22F191";
        Assert.True(vm.IsDirty);
        Assert.Equal("22F191", vm.Working!.Requests["Read VIN"].SentBytes);
        Assert.Equal("22F190", ctx.Ecu!.Requests["Read VIN"].SentBytes);   // session untouched
        Assert.True(vm.CanUndo);
        vm.UndoCommand.Execute(null);
        Assert.Equal("22F190", vm.Working!.Requests["Read VIN"].SentBytes);
        Assert.Equal("22F190", vm.SelectedRequest!.SentBytes);
        Assert.True(vm.CanRedo);
        vm.RedoCommand.Execute(null);
        Assert.Equal("22F191", vm.Working!.Requests["Read VIN"].SentBytes);
        Assert.Equal("Read VIN", vm.SelectedRequestRow!.Name);
    }

    [AvaloniaFact]
    public void Rapid_edits_of_one_field_are_one_undo_step()
    {
        var (app, vm, _, _) = Setup();
        using var _a = app;
        SelectRequest(vm, "Read VIN");
        vm.SelectedRequest!.ReplyBytes = "6";
        vm.SelectedRequest!.ReplyBytes = "62";
        vm.SelectedRequest!.ReplyBytes = "62F1";
        vm.UndoCommand.Execute(null);
        Assert.Equal("62F190", vm.Working!.Requests["Read VIN"].ReplyBytes);
        Assert.False(vm.CanUndo);
    }

    [AvaloniaFact]
    public void Renaming_data_updates_requests_and_can_be_undone()
    {
        var (app, vm, _, _) = Setup();
        using var _a = app;
        SelectData(vm, "Coolant");
        vm.SelectedData!.Name = "CoolantTemp";
        Assert.True(vm.Working!.Data.Contains("CoolantTemp"));
        Assert.True(vm.Working.Requests["Read coolant temperature"].ReceiveItems.Contains("CoolantTemp"));
        Assert.Equal("CoolantTemp", vm.SelectedDataRow!.Name);
        Assert.Contains("CoolantTemp", vm.DataNames);
        // duplicate name is refused
        vm.SelectedData!.Name = "VIN";
        Assert.Equal("CoolantTemp", vm.SelectedData.Name);
        vm.UndoCommand.Execute(null);
        Assert.True(vm.Working!.Data.Contains("Coolant"));
        Assert.True(vm.Working.Requests["Read coolant temperature"].ReceiveItems.Contains("Coolant"));
    }

    [AvaloniaFact]
    public async Task Add_duplicate_delete_requests_and_data()
    {
        var (app, vm, _, _) = Setup();
        using var _a = app;
        int r0 = vm.Working!.Requests.Count, d0 = vm.Working.Data.Count;
        vm.AddRequestCommand.Execute(null);
        Assert.Equal(r0 + 1, vm.Working.Requests.Count);
        Assert.Equal("NewRequest", vm.SelectedRequestRow!.Name);
        vm.DuplicateRequestCommand.Execute(null);
        Assert.Equal("NewRequest_copy", vm.SelectedRequestRow!.Name);
        await vm.DeleteRequestCommand.ExecuteAsync(null);
        Assert.Equal(r0 + 1, vm.Working.Requests.Count);

        vm.AddDataCommand.Execute(null);
        Assert.Equal(d0 + 1, vm.Working.Data.Count);
        vm.DuplicateDataCommand.Execute(null);
        Assert.Equal(d0 + 2, vm.Working.Data.Count);

        // deleting used data cascades into the requests
        SelectData(vm, "VIN");
        await vm.DeleteDataCommand.ExecuteAsync(null);
        Assert.False(vm.Working.Data.Contains("VIN"));
        Assert.Empty(vm.Working.Requests["Read VIN"].ReceiveItems);
        vm.UndoCommand.Execute(null);
        Assert.True(vm.Working.Data.Contains("VIN"));
        Assert.Single(vm.Working.Requests["Read VIN"].ReceiveItems);
    }

    [AvaloniaFact]
    public void Item_editing_moves_bits_in_the_grid()
    {
        var (app, vm, _, _) = Setup();
        using var _a = app;
        SelectRequest(vm, "Read coolant temperature");
        var req = vm.SelectedRequest!;
        Assert.Equal(2, req.ReceiveItems.Count);
        Assert.Equal(5, req.ReceivedGrid.ByteCount);            // MinBytes 4 .. FanState at byte 5
        req.ReceiveItems[0].FirstByte = 2;
        Assert.Equal(1, req.Request.ReceiveItems["Coolant"].FirstByte - 1);
        Assert.Equal(0, req.ReceivedGrid.Cells[8]);
        req.ReceiveItems[0].Name = "EngineSpeed";                // 16 bit item now
        Assert.Equal(0, req.ReceivedGrid.Cells[8 + 15]);
        Assert.Equal(1, req.ReceivedGrid.Cells[8 * 4]);          // FanState sits at byte 5
        req.ReceiveItems[1].FirstByte = 3;
        Assert.True(req.ReceivedGrid.HasOverlap);
        req.AddReceivedCommand.Execute(null);
        Assert.Equal(3, req.ReceiveItems.Count);
        req.ReceiveItems[2].RemoveCommand.Execute(null);
        Assert.Equal(2, req.ReceiveItems.Count);
    }

    [AvaloniaFact]
    public void Data_properties_enumeration_and_preview()
    {
        var (app, vm, _, _) = Setup();
        using var _a = app;
        SelectData(vm, "FanMode");
        var d = vm.SelectedData!;
        Assert.Equal(3, d.Entries.Count);
        d.TestHex = "01";
        Assert.Equal("Forced on", d.TestResult);
        d.AddEntryCommand.Execute(null);
        Assert.Equal(4, vm.Working!.Data["FanMode"].Lists.Count);
        d.Entries[0].Text = "Auto";
        Assert.Equal("Auto", vm.Working.Data["FanMode"].Lists[0]);
        Assert.Equal(0, vm.Working.Data["FanMode"].Items["Auto"]);
        d.Entries[1].Value = 0;                                  // duplicate raw value
        Assert.True(d.Entries[1].Duplicate && d.Entries[0].Duplicate);
        d.Entries[1].Value = 1;
        Assert.False(d.Entries[1].Duplicate);

        SelectData(vm, "Coolant");
        d = vm.SelectedData!;
        d.TestHex = "7B";
        Assert.Equal("83 °C", d.TestResult);
        d.OffsetText = "-50";
        Assert.Equal("73 °C", d.TestResult);
        d.Bits = 12;
        Assert.Equal(2, vm.Working.Data["Coolant"].BytesCount);
        d.DivideByText = "0";
        Vm_revalidate(vm);
        Assert.Contains(vm.Issues, i => i.Message.Contains("divides by zero"));
    }

    private static void Vm_revalidate(DataEditorViewModel vm) => vm.Revalidate();

    [AvaloniaFact]
    public void Validation_reports_and_navigates()
    {
        var (app, vm, _, _) = Setup(layout: true);
        using var _a = app;
        SelectRequest(vm, "Read VIN");
        vm.SelectedRequest!.SentBytes = "22F";
        vm.Revalidate();
        Assert.True(vm.ErrorCount >= 1);
        Assert.Contains(vm.Issues, i => i.Kind == "screen" && i.Message.Contains("Does not exist"));
        var issue = vm.Issues.First(i => i.Kind == "request" && i.Message.Contains("not valid hex"));
        vm.SelectedIssue = issue;
        Assert.Equal(0, vm.TabIndex);
        Assert.Equal("Read VIN", vm.SelectedRequestRow!.Name);
        vm.SelectedIssue = vm.Issues.First(i => i.Kind == "screen");
        Assert.Equal(3, vm.TabIndex);
    }

    [AvaloniaFact]
    public void Ecu_properties_and_screens_are_editable_with_undo()
    {
        var (app, vm, _, _) = Setup(layout: true);
        using var _a = app;
        var p = vm.EcuProps!;
        Assert.True(p.IsCan);
        p.SendId = "7e2";
        Assert.Equal("7E2", vm.Working!.SendId);
        p.ProtocolIndex = 2;                                      // KWP2000
        Assert.Equal(EcuProtocol.Kwp2000, vm.Working.Protocol);
        Assert.True(p.IsKLine);
        p.ProjectsText = "A, B;C";
        Assert.Equal(["A", "B", "C"], vm.Working.Projects);
        p.AddIdentCommand.Execute(null);
        p.Idents[^1].Supplier = "BOSCH";
        Assert.Equal("BOSCH", vm.Working.AutoIdents[^1].Supplier);

        vm.TabIndex = 3;
        var s = vm.SelectedScreen!;
        Assert.Equal("Vehicle", s.Name);
        s.Width = 1024;
        s.ColorHex = "#102030";
        Assert.Equal(1024, vm.Layout!.Screens["Vehicle"].Width);
        Assert.Equal(new LayoutColor(0x10, 0x20, 0x30), vm.Layout.Screens["Vehicle"].Color);
        s.Name = "Vehicle info";
        Assert.True(vm.Layout.Screens.Contains("Vehicle info"));
        Assert.Equal("Vehicle info", vm.Layout.Categories[0].ScreenNames[0]);
        vm.UndoCommand.Execute(null);                              // name
        Assert.True(vm.Layout.Screens.Contains("Vehicle"));
        vm.UndoCommand.Execute(null);                              // colour
        Assert.Equal(new LayoutColor(0xDD, 0xEE, 0xFF), vm.Layout.Screens["Vehicle"].Color);
        vm.AddScreenCommand.Execute(null);
        Assert.Equal(2, vm.Screens.Count);
    }

    [AvaloniaFact]
    public void Save_exports_and_reloads()
    {
        var (app, vm, _, dir) = Setup(layout: true);
        using var _a = app;
        SelectRequest(vm, "Read VIN");
        vm.SelectedRequest!.ReplyBytes = "62F190AA";
        vm.Save_ForTest();
        var json = Path.Combine(dir, "EDC17C84_Engine.json");
        Assert.True(File.Exists(json));
        Assert.True(File.Exists(Path.Combine(dir, "EDC17C84_Engine.layout")));
        Assert.False(vm.IsDirty);
        var back = EcuFile.Load(json);
        Assert.Equal("62F190AA", back.Requests["Read VIN"].ReplyBytes);
        Assert.Equal(vm.Working!.Requests.Count, back.Requests.Count);
        Assert.Equal(json, vm.FilePath);
        var lay = EcuLayoutJson.Load(File.ReadAllBytes(Path.Combine(dir, "EDC17C84_Engine.layout")));
        Assert.True(lay.Screens.Contains("Vehicle"));

        var xml = vm.ExportXml(dir);
        var fromXml = EcuFile.Load(xml);
        Assert.Equal(EcuJsonDumper_Dump(vm.Working), EcuJsonDumper_Dump(fromXml));
    }

    private static string EcuJsonDumper_Dump(EcuFile f) => f.DumpJson();

    [AvaloniaFact]
    public void New_ecu_can_be_created_edited_and_saved()
    {
        var (app, vm, _, dir) = Setup();
        using var _a = app;
        vm.NewEcuCommand.Execute(null);
        TestApp.Pump();
        Assert.Equal("NEW_ECU", vm.Working!.EcuName);
        Assert.True(vm.IsDirty);
        vm.EcuProps!.EcuName = "My ECU";
        vm.Save_ForTest();
        Assert.True(File.Exists(Path.Combine(dir, "My_ECU.json")));
        Assert.Equal(0, vm.ErrorCount);
    }

    [AvaloniaFact]
    public void Use_in_session_hands_a_copy_to_the_session()
    {
        var (app, vm, ctx, _) = Setup();
        using var _a = app;
        SelectRequest(vm, "Read VIN");
        vm.SelectedRequest!.SentBytes = "22F1AA";
        vm.UseInSessionCommand.Execute(null);
        TestApp.Pump();
        Assert.Equal("22F1AA", ctx.Ecu!.Requests["Read VIN"].SentBytes);
        Assert.NotSame(ctx.Ecu, vm.Working);
        Assert.False(vm.SessionChanged);
    }

    [AvaloniaFact]
    public void External_ecu_change_keeps_unsaved_edits()
    {
        var (app, vm, ctx, _) = Setup();
        using var _a = app;
        SelectRequest(vm, "Read VIN");
        vm.SelectedRequest!.SentBytes = "22F1AA";
        ctx.SetEcu(EcuEditing.NewEcu("Other"), null, null);
        TestApp.Pump();
        Assert.True(vm.SessionChanged);
        Assert.Equal("EDC17C84 Engine", vm.Working!.EcuName);
        vm.ReloadFromSessionCommand.Execute(null);
        TestApp.Pump();
        Assert.Equal("Other", vm.Working!.EcuName);
        Assert.False(vm.IsDirty);
    }

    [AvaloniaFact]
    public async Task Every_tab_renders_and_screenshots()
    {
        var (app, vm, _, _) = Setup(layout: true);
        using var _a = app;
        SelectRequest(vm, "Read coolant temperature");
        for (int t = 0; t < 5; t++)
        {
            vm.TabIndex = t;
            await app.PumpAsync(80);
            Assert.NotNull(app.Window);
        }
    }

    [AvaloniaFact]
    public void Large_ecu_edits_stay_fast()
    {
        var (app, vm, ctx, _) = Setup();
        using var _a = app;
        var big = SampleEcu.Create();
        for (int i = 0; i < 3000; i++)
        {
            big.Data.Add(new EcuData { Name = "D" + i, BitsCount = 16, BytesCount = 2 });
            var r = new EcuRequest { Name = "R" + i, SentBytes = "22" + (0xF000 + i).ToString("X4"), MinBytes = 5 };
            r.ReceiveItems.Add(new DataItem("D" + i, 4));
            big.Requests.Add(r);
        }
        big.ResolveReferences();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        vm.Load(big);
        long load = sw.ElapsedMilliseconds;
        sw.Restart();
        SelectRequest(vm, "R1500");
        for (int i = 0; i < 20; i++) { vm.SelectedRequest!.MinBytes = 5 + i; }
        vm.SelectedRequest!.Name = "R1500x";
        vm.UndoCommand.Execute(null);
        vm.RequestSearch = "r15";
        Assert.True(load < 1500, $"load {load} ms");
        Assert.True(sw.ElapsedMilliseconds < 1500, $"edit {sw.ElapsedMilliseconds} ms");
    }
}

internal static class DataEditorTestExtensions
{
    /// <summary>The Save command is synchronous; this keeps tests independent of command plumbing.</summary>
    public static void Save_ForTest(this DataEditorViewModel vm) => vm.SaveCommand.Execute(null);
}
