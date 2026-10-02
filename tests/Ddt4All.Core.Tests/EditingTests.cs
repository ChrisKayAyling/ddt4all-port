using Ddt4All.Core.Ecu;
using Ddt4All.Core.Editing;
using Ddt4All.Core.Loading;

namespace Ddt4All.Core.Tests;

public class EditingTests
{
    public static IEnumerable<object[]> Fixtures() =>
        new[] { "rich_ecu.xml", "dummy_ecu_can.xml", "dummy_ecu_can_ext.xml", "dummy_ecu_iso8.xml", "dummy_ecu_kwp.xml" }.Select(f => new object[] { f });

    [Theory, MemberData(nameof(Fixtures))]
    public void Clone_is_deep_and_equal(string fixture)
    {
        var a = EcuXmlLoader.Load(TestPaths.Data(fixture));
        var b = a.Clone();
        Assert.Equal(EcuJsonDumper.Dump(a), EcuJsonDumper.Dump(b));
        if (a.Data.Count > 0)
        {
            b.Data[0].Unit = "changed";
            Assert.NotEqual("changed", a.Data[0].Unit);
        }
        foreach (var r in b.Requests)
            foreach (var i in r.ReceiveItems) Assert.Same(b.Data.TryGetValue(i.Name, out var d) ? d : null, i.Data);
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void Xml_export_roundtrips(string fixture)
    {
        var a = EcuXmlLoader.Load(TestPaths.Data(fixture));
        var xml = EcuXmlWriter.Write(a);
        var b = EcuXmlLoader.LoadFromString(xml);
        Assert.Equal(EcuJsonDumper.Dump(a), EcuJsonDumper.Dump(b));
        Assert.Equal(a.Projects, b.Projects);
        Assert.Equal(a.AutoIdents, b.AutoIdents);
    }

    [Fact]
    public void Json_roundtrip_of_new_ecu()
    {
        var e = EcuEditing.NewEcu("X");
        var back = EcuJsonLoader.Load(System.Text.Encoding.UTF8.GetBytes(e.DumpJson()));
        Assert.Equal("X", back.EcuName);
        Assert.Single(back.Requests);
        Assert.DoesNotContain(EcuValidator.Validate(e), i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Rename_data_fixes_references_and_remove_cascades()
    {
        var e = EcuEditing.NewEcu();
        Assert.True(e.RenameData("Identification", "Ident"));
        Assert.True(e.Requests[0].ReceiveItems.Contains("Ident"));
        Assert.Equal("Ident", e.Requests[0].ReceiveItems[0].Data!.Name);
        Assert.False(e.RenameData("Ident", ""));
        Assert.Equal(new[] { "ReadIdentification" }, e.DataUsage("Ident"));
        Assert.False(e.RemoveData("Ident", cascade: false));
        Assert.True(e.RemoveData("Ident"));
        Assert.Empty(e.Requests[0].ReceiveItems);
        Assert.Empty(e.Data);
    }

    [Fact]
    public void Edited_request_still_builds_and_decodes()
    {
        var e = EcuEditing.NewEcu();
        e.Data["Identification"].SetBytes(2);
        var r = e.Requests[0];
        var resp = r.DecodeResponse(new byte[] { 0x62, 0xF1, 0x90, 0x12, 0x34 });
        Assert.Equal("1234", resp["Identification"]);
    }

    [Fact]
    public void Validator_reports_problems()
    {
        var e = EcuEditing.NewEcu();
        e.Requests[0].SentBytes = "22F";
        e.Requests[0].ReceiveItems.Add(new DataItem("Missing", 1));
        e.Data["Identification"].Scaled = true;
        e.Data["Identification"].DivideBy = 0;
        e.Data["Identification"].AddListItem(1, "A");
        e.Data["Identification"].AddListItem(2, "A");
        var issues = EcuValidator.Validate(e);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("not valid hex"));
        Assert.Contains(issues, i => i.Message.Contains("no data definition"));
        Assert.Contains(issues, i => i.Message.Contains("divides by zero"));
        Assert.Contains(issues, i => i.Message.Contains("Duplicate list"));
    }

    [Fact]
    public void Named_collection_edit_operations()
    {
        var c = new NamedCollection<EcuData>();
        foreach (var n in "abcd") c.Add(new EcuData { Name = n.ToString() });
        c.Move(0, 2);
        Assert.Equal("bcad", string.Concat(c.Select(x => x.Name)));
        Assert.Equal(2, c.IndexOf("a"));
        Assert.True(c.Remove("c"));
        Assert.False(c.Remove("zz"));
        Assert.Equal("bad", string.Concat(c.Select(x => x.Name)));
        c.Insert(0, new EcuData { Name = "d" });
        Assert.Equal("dba", string.Concat(c.Select(x => x.Name)));
        c[0].Name = "q";
        c.Reindex();
        Assert.True(c.Contains("q"));
        Assert.False(c.Contains("d"));
    }
}
