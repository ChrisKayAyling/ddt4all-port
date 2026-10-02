using System.Text.Json;
using Ddt4All.Core.Ecu;
using Ddt4All.Core.Layout;
using Ddt4All.Core.Loading;

namespace Ddt4All.Core.Tests;

public class LoaderTests
{
    public static IEnumerable<object[]> XmlFixtures() =>
        new[] { "rich_ecu.xml", "dummy_ecu_can.xml", "dummy_ecu_can_ext.xml", "dummy_ecu_iso8.xml", "dummy_ecu_kwp.xml" }
            .Select(f => new object[] { f });

    [Theory]
    [MemberData(nameof(XmlFixtures))]
    public void Xml_dump_matches_python_dumpJson(string fixture)
    {
        var file = EcuXmlLoader.Load(TestPaths.Data(fixture));
        using var exp = JsonDocument.Parse(TestPaths.Text(fixture + ".expected.json"));
        using var act = JsonDocument.Parse(EcuJsonDumper.Dump(file));
        JsonAssert.Equal(exp.RootElement, act.RootElement);
    }

    [Theory]
    [MemberData(nameof(XmlFixtures))]
    public void Xml_identities_match_python_dump_idents(string fixture)
    {
        var f = EcuXmlLoader.Load(TestPaths.Data(fixture));
        using var exp = JsonDocument.Parse(TestPaths.Text(fixture + ".idents.expected.json"));
        var r = exp.RootElement;
        Assert.Equal(r.GetProperty("address").GetString(), f.FuncAddr);
        Assert.Equal(r.GetProperty("group").GetString(), f.FuncName);
        Assert.Equal(r.GetProperty("protocol").GetString(), f.Protocol.ToWire());
        Assert.Equal(r.GetProperty("ecuname").GetString(), f.EcuName);
        // the reference also records whitespace text nodes ("#text") as projects when the XML is indented; we only keep elements
        Assert.Equal(r.GetProperty("projects").EnumerateArray().Select(x => x.GetString()).Where(x => x != "#text"), f.Projects);
        var ais = r.GetProperty("autoidents").EnumerateArray().ToArray();
        Assert.Equal(ais.Length, f.AutoIdents.Count);
        for (int i = 0; i < ais.Length; i++)
        {
            Assert.Equal(ais[i].GetProperty("diagnostic_version").GetString(), f.AutoIdents[i].DiagVersion);
            Assert.Equal(ais[i].GetProperty("supplier_code").GetString(), f.AutoIdents[i].Supplier);
            Assert.Equal(ais[i].GetProperty("soft_version").GetString(), f.AutoIdents[i].Soft);
            Assert.Equal(ais[i].GetProperty("version").GetString(), f.AutoIdents[i].Version);
        }
    }

    [Fact]
    public void Json_fixture_loads_and_redumps_like_python()
    {
        var f = EcuJsonLoader.Load(TestPaths.Data("dummy_ecu_can.json"));
        Assert.Equal("JSON_ECU", f.EcuName);
        Assert.Equal(EcuProtocol.Can, f.Protocol);
        Assert.Equal("7E0", f.SendId);
        Assert.Equal("7E8", f.RecvId);
        Assert.Equal(250000, f.BaudRate);
        Assert.Equal(ByteOrder.Little, f.Endianness);
        Assert.Equal("1A", f.FuncAddr);
        Assert.Equal("VIN", f.Data["VIN"].Name);
        Assert.True(f.Data["VIN"].BytesAscii);
        Assert.Equal(17, f.Data["VIN"].BytesCount);

        using var exp = JsonDocument.Parse(TestPaths.Text("dummy_ecu_can.json.expected.json"));
        using var act = JsonDocument.Parse(EcuJsonDumper.Dump(f));
        // The Python JSON loader ignores "autoidents"; this port keeps them (superset behaviour).
        JsonAssert.Equal(exp.RootElement, act.RootElement, ignore: new HashSet<string> { "$.autoidents" });
        Assert.Single(f.AutoIdents);
    }

    [Theory]
    [MemberData(nameof(XmlFixtures))]
    public void Json_roundtrip_is_lossless(string fixture)
    {
        var a = EcuXmlLoader.Load(TestPaths.Data(fixture));
        string json1 = a.DumpJson();
        var b = EcuJsonLoader.LoadFromString(json1);
        string json2 = b.DumpJson();
        Assert.Equal(json1, json2);
    }

    [Fact]
    public void Rich_ecu_details()
    {
        var f = EcuXmlLoader.Load(TestPaths.Data("rich_ecu.xml"));
        Assert.Equal("RICH_ECU", f.EcuName);
        Assert.Equal("1A", f.FuncAddr);
        Assert.Equal("700", f.SendId);
        Assert.Equal("7E8", f.RecvId);
        Assert.Equal(500000, f.BaudRate);
        Assert.Equal(new[] { "X95", "X85" }, f.Projects);
        Assert.Equal(3, f.AutoIdents.Count);
        Assert.Equal(ByteOrder.Big, f.Endianness);
        Assert.Equal(new[] { "StartSession", "ReadData", "WriteConfig", "Reset" }, f.Requests.Select(r => r.Name));
        var rd = f.Requests["ReadData"];
        Assert.Equal(SessionAccess.NoSds | SessionAccess.Plant, rd.Denied);
        Assert.Equal(30, rd.MinBytes);
        Assert.Equal(7, rd.ReceiveItems.Count);
        Assert.Equal("2101", rd.SentBytes);
        var wc = f.Requests["WriteConfig"];
        Assert.True(wc.ManualSend);
        Assert.Equal(2, wc.ShiftBytesCount);
        Assert.Equal(3, wc.SendItems.Count);
        Assert.Equal(ByteOrder.Little, rd.ReceiveItems["LeVal"].Endian);
        Assert.NotNull(rd.ReceiveItems["Coolant"].Data);
        var coolant = f.Data["Coolant"];
        Assert.True(coolant.Scaled);
        Assert.Equal(-40, coolant.Offset);
        Assert.Equal("Coolant temperature", coolant.Description);
        Assert.Equal("<b>deg</b> C", coolant.Comment);
        Assert.Equal("C", coolant.Unit);
        Assert.Equal(16, f.Data["Rpm"].BitsCount);
        Assert.Equal(2, f.Data["Rpm"].BytesCount);
        Assert.True(f.Data["VIN"].BytesAscii && f.Data["VIN"].Byte);
        Assert.Equal(136, f.Data["VIN"].BitsCount);
        Assert.Equal("Reverse", f.Data["Gear"].Lists[255]);
        Assert.Equal(2, f.Data["Mode"].Items["OFF"]);
        Assert.True(f.Data["Temp16"].Signed);
        Assert.True(f.Data["FlagBits"].Binary);
        Assert.Single(f.Devices);
        Assert.Equal(4660, f.Devices["DTC1"].Dtc);
        Assert.Equal("FF", f.Devices["DTC1"].DeviceData["Coolant"]);
        Assert.Same(f.Requests["ReadData"], f.GetRequest("readdata"));
        Assert.Null(f.GetRequest("nope"));
    }

    [Fact]
    public void Kwp_and_iso8_protocol_fields()
    {
        var k = EcuXmlLoader.Load(TestPaths.Data("dummy_ecu_kwp.xml"));
        Assert.Equal(EcuProtocol.Kwp2000, k.Protocol);
        Assert.True(k.FastInit);
        Assert.Equal("81", k.Kw1);
        Assert.Equal("82", k.Kw2);
        Assert.Equal("26", k.FuncAddr);
        var i = EcuXmlLoader.Load(TestPaths.Data("dummy_ecu_iso8.xml"));
        Assert.Equal(EcuProtocol.Iso8, i.Protocol);
        Assert.False(i.FastInit);
    }

    [Fact]
    public void Extended_can_ids_are_hex_without_padding()
    {
        var f = EcuXmlLoader.Load(TestPaths.Data("dummy_ecu_can_ext.xml"));
        Assert.Equal("18DAF0F1", f.SendId);
        Assert.Equal(8, f.RecvId.Length);
    }

    [Fact]
    public void Malformed_xml_throws_format_exception()
    {
        Assert.Throws<EcuFormatException>(() => EcuXmlLoader.LoadFromString("<Ecu><Target"));
        Assert.Throws<EcuFormatException>(() => EcuJsonLoader.LoadFromString("{ nope"));
    }

    [Fact]
    public void Xml_without_requests_ignores_data_like_the_reference()
    {
        var f = EcuXmlLoader.LoadFromString("<Ecu><Target Name='T'/><Data Name='X'><Bits count='8'/></Data></Ecu>");
        Assert.Empty(f.Data);
        var g = EcuXmlLoader.LoadFromString("<Ecu><Target Name='T'/><Requests/><Data Name='X'><Bits count='8'/></Data></Ecu>");
        Assert.Single(g.Data);
    }

    [Fact]
    public void Xml_bits_signed_attribute_is_truthy_for_any_non_empty_value()
    {
        // quirk of the reference: signed="0" is still "signed"
        var f = EcuXmlLoader.LoadFromString("<Ecu><Requests/><Data Name='X'><Bits count='8' signed='0'/></Data><Data Name='Y'><Bits count='8'/></Data></Ecu>");
        Assert.True(f.Data["X"].Signed);
        Assert.False(f.Data["Y"].Signed);
    }

    [Fact]
    public void Xml_bytes_count_with_decimal_comma_rounds_up()
    {
        var f = EcuXmlLoader.LoadFromString("<Ecu><Requests/><Data Name='X'><Bytes count='1,5'/></Data></Ecu>");
        Assert.Equal(2, f.Data["X"].BytesCount);
        Assert.Equal(12, f.Data["X"].BitsCount);
    }

    [Fact]
    public void Xml_bits_override_bytes_regardless_of_element_order()
    {
        var f = EcuXmlLoader.LoadFromString("<Ecu><Requests/><Data Name='X'><Bits count='12'/><Bytes count='4'/></Data></Ecu>");
        Assert.Equal(12, f.Data["X"].BitsCount);
        Assert.Equal(2, f.Data["X"].BytesCount);
        Assert.True(f.Data["X"].Byte);
    }

    [Fact]
    public void Duplicate_names_replace_in_place()
    {
        var f = EcuXmlLoader.LoadFromString(
            "<Ecu><Requests><Request Name='A'><Sent><SentBytes>01</SentBytes></Sent></Request><Request Name='B'/><Request Name='A'><Sent><SentBytes>02</SentBytes></Sent></Request></Requests></Ecu>");
        Assert.Equal(new[] { "A", "B" }, f.Requests.Select(r => r.Name));
        Assert.Equal("02", f.Requests["A"].SentBytes);
    }

    [Fact]
    public void Json_loader_accepts_bom_and_string_numbers()
    {
        string json = "{\"ecuname\":\"E\",\"obd\":{\"protocol\":\"CAN\",\"send_id\":\"1\",\"recv_id\":\"2\",\"baudrate\":\"500000\",\"funcaddr\":\"01\",\"funcname\":\"G\"},\"data\":{},\"requests\":[],\"devices\":[]}";
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes(json)).ToArray();
        var f = EcuJsonLoader.Load(bytes);
        Assert.Equal(500000, f.BaudRate);
        Assert.Equal("E", f.EcuName);
    }

    [Fact]
    public void Json_kwp_obd_block()
    {
        var f = EcuJsonLoader.LoadFromString("{\"obd\":{\"protocol\":\"KWP2000\",\"fastinit\":true,\"funcaddr\":\"26\",\"funcname\":\"X\",\"kw1\":\"81\",\"kw2\":\"82\"}}");
        Assert.True(f.FastInit);
        Assert.Equal("81", f.Kw1);
        var d = EcuJsonDumper.Dump(f);
        Assert.Contains("\"fastinit\": true", d);
        Assert.Contains("\"kw2\": \"82\"", d);
    }

    [Fact]
    public void Dump_omits_defaults_and_strips_html_from_comments()
    {
        var f = new EcuFile();
        f.Data.Add(new EcuData { Name = "A", Comment = "<p>hi <b>there</b></p>", BitsCount = 8, BytesCount = 1 });
        string json = f.DumpJson();
        using var doc = JsonDocument.Parse(json);
        var a = doc.RootElement.GetProperty("data").GetProperty("A");
        Assert.Equal("hi there", a.GetProperty("comment").GetString());
        Assert.False(a.TryGetProperty("bitscount", out _));
        Assert.False(a.TryGetProperty("step", out _));
        Assert.False(doc.RootElement.TryGetProperty("endian", out _));
    }

    [Fact]
    public void Load_by_path_without_extension_finds_xml_then_json()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ddt4all-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(TestPaths.Data("dummy_ecu_kwp.xml"), Path.Combine(dir, "a.xml"));
            File.Copy(TestPaths.Data("dummy_ecu_can.json"), Path.Combine(dir, "b.json"));
            Assert.Equal("KWP_ECU", EcuFile.Load(Path.Combine(dir, "a")).EcuName);
            Assert.Equal("JSON_ECU", EcuFile.Load(Path.Combine(dir, "b")).EcuName);
            Assert.Throws<FileNotFoundException>(() => EcuFile.Load(Path.Combine(dir, "zzz")));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class LayoutTests
{
    [Fact]
    public void Layout_from_xml_matches_python_dumpDOC()
    {
        var layout = EcuLayoutXmlLoader.Load(TestPaths.Data("rich_ecu.xml"));
        using var exp = JsonDocument.Parse(TestPaths.Text("rich_ecu.xml.layout.expected.json"));
        using var act = JsonDocument.Parse(EcuLayoutJson.Dump(layout));
        JsonAssert.Equal(exp.RootElement, act.RootElement);
    }

    [Fact]
    public void Layout_model_is_ready_for_rendering()
    {
        var layout = EcuLayoutXmlLoader.Load(TestPaths.Data("rich_ecu.xml"));
        Assert.Equal(new[] { "Measures", "Other" }, layout.Categories.Select(c => c.Name));
        Assert.Equal(new[] { "Live data", "Empty" }, layout.GetCategory("Measures")!.ScreenNames);
        var s = layout.GetScreen("Live data")!;
        Assert.Equal((800, 600), (s.Width, s.Height));
        Assert.Equal(new LayoutColor(255, 255, 255), s.Color);
        Assert.Equal(new SendCommand("StartSession", 100), Assert.Single(s.PreSend));
        var lbl = Assert.Single(s.Labels);
        Assert.Equal("Coolant", lbl.Text);
        Assert.Equal(new LayoutColor(255, 0, 0), lbl.Color);       // COLORREF 255 = red
        Assert.Equal(new LayoutColor(0, 0, 255), lbl.FontColor);   // 16711680 = 0xFF0000 = blue in BGR
        Assert.Equal(new LayoutRect(10, 20, 120, 30), lbl.Bounds); // Top 20.7 truncated
        Assert.Equal(new LayoutFont("Arial", 8.5, true, false), lbl.Font);
        var d = Assert.Single(s.Displays);
        Assert.Equal(("Coolant", "ReadData", 80), (d.DataName, d.RequestName, d.Width));
        Assert.Equal(LayoutColor.DefaultGrey, d.FontColor);
        var inp = Assert.Single(s.Inputs);
        Assert.Equal(LayoutColor.DefaultGrey, inp.Color);          // input without colour
        Assert.Equal(new LayoutColor(255, 0, 0), inp.FontColor);
        Assert.Equal(2, s.Buttons.Count);
        Assert.Equal("Reset_0", s.Buttons[0].UniqueName);
        Assert.Equal("Reset_1", s.Buttons[1].UniqueName);
        Assert.Equal(new[] { "Really reset?", "Engine must be off" }, s.Buttons[0].Messages);
        Assert.Equal(new[] { new SendCommand("Reset", 0), new SendCommand("ReadData", 250) }, s.Buttons[0].Send);
        Assert.Empty(s.Buttons[1].Send);
        Assert.Empty(layout.GetScreen("Empty")!.Labels);
    }

    [Fact]
    public void Layout_json_roundtrip()
    {
        var layout = EcuLayoutXmlLoader.Load(TestPaths.Data("rich_ecu.xml"));
        string j1 = EcuLayoutJson.Dump(layout);
        string j2 = EcuLayoutJson.Dump(EcuLayoutJson.LoadFromString(j1));
        Assert.Equal(j1, j2);
    }

    [Fact]
    public void Layout_json_reads_python_output_including_string_flags()
    {
        var l = EcuLayoutJson.LoadFromString(TestPaths.Text("rich_ecu.xml.layout.expected.json"));
        Assert.Equal(2, l.Categories.Count);
        Assert.True(l.GetScreen("Live data")!.Labels[0].Font.Bold);
        Assert.Equal(250, l.GetScreen("Live data")!.Buttons[0].Send[1].DelayMs);
    }

    [Fact]
    public void Empty_python_layout_file_is_accepted()
    {
        var l = EcuLayoutJson.LoadFromString("{\"screens\": {}, \"categories\":{\"Category\":[]} }");
        Assert.Empty(l.Screens);
        Assert.Single(l.Categories);
    }

    [Fact]
    public void Colorref_conversion_matches_python()
    {
        Assert.Equal("rgb(255,0,0)", LayoutColor.FromColorRef(255).ToString());
        Assert.Equal("rgb(170,170,170)", LayoutColor.FromColorRef(0xAAAAAA).ToString());
        Assert.Equal("rgb(1,2,3)", LayoutColor.FromColorRef(0x030201).ToString());
        Assert.Equal("rgb(255,255,255)", LayoutColor.FromColorRef(-1).ToString());
        Assert.True(LayoutColor.TryParse("rgb( 1, 2 ,3)", out var c));
        Assert.Equal(new LayoutColor(1, 2, 3), c);
        Assert.False(LayoutColor.TryParse("rgb(1,2)", out _));
        Assert.False(LayoutColor.TryParse("rgb(1,2,300)", out _));
        Assert.Equal(0x010203u, new LayoutColor(1, 2, 3).ToRgb());
    }
}
