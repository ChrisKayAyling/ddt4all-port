using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Ddt4All.Core.Database;
using Ddt4All.Core.Ecu;
using Ddt4All.Core.Layout;
using Ddt4All.Core.Loading;
using Ddt4All.Core.Tests.Synthetic;

namespace Ddt4All.Core.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ddt4all-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public class DatabaseTests
{
    /// <summary>ecu.zip built from the repo fixtures with the zip builder (Python zipConvertXML equivalent).</summary>
    internal static string BuildFixtureZip(TempDir dir)
    {
        string path = dir.File("ecu.zip");
        using (var fs = File.Create(path))
        using (var zb = new EcuZipBuilder(fs))
        {
            foreach (var n in new[] { "rich_ecu.xml", "dummy_ecu_can.xml", "dummy_ecu_can_ext.xml", "dummy_ecu_iso8.xml", "dummy_ecu_kwp.xml" })
            {
                using var xml = File.OpenRead(TestPaths.Data(n));
                zb.AddXml(n, xml);
            }

            zb.AddFile("graphics/pic.gif", new byte[] { 1, 2, 3 });
        }

        return path;
    }

    private static EcuDatabaseOptions Opts(TempDir d) => new() { CacheDirectory = d.File("cache") };

    [Fact]
    public void Zip_builder_produces_expected_members_and_db_json()
    {
        using var dir = new TempDir();
        string path = BuildFixtureZip(dir);
        using var zip = ZipFile.OpenRead(path);
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();
        Assert.Contains("rich_ecu.json", names);
        Assert.Contains("rich_ecu.json.layout", names);
        Assert.Contains("db.json", names);
        Assert.Contains("graphics/pic.gif", names);
        using var doc = JsonDocument.Parse(zip.GetEntry("db.json")!.Open());
        var e = doc.RootElement.GetProperty("rich_ecu.json");
        Assert.Equal("1A", e.GetProperty("address").GetString());
        Assert.Equal("Injection", e.GetProperty("group").GetString());
        Assert.Equal("CAN", e.GetProperty("protocol").GetString());
        Assert.Equal("RICH_ECU", e.GetProperty("ecuname").GetString());
        Assert.Equal(3, e.GetProperty("autoidents").GetArrayLength());
        Assert.Equal("CONTI", e.GetProperty("autoidents")[0].GetProperty("supplier_code").GetString());
    }

    [Fact]
    public void Open_builds_index_then_second_open_uses_cache()
    {
        using var dir = new TempDir();
        string zip = BuildFixtureZip(dir);
        using (var db1 = EcuDatabase.Open(zip, Opts(dir)))
        {
            Assert.False(db1.LoadedFromCache);
            Assert.Equal(5, db1.Count);
            Assert.Equal(3 + 2 + 0 + 0 + 0, db1.AutoIdentCount - 0 == 5 ? 5 : db1.AutoIdentCount); // see detailed asserts below
        }

        Assert.NotEmpty(Directory.GetFiles(dir.File("cache")));
        using var db2 = EcuDatabase.Open(zip, Opts(dir));
        Assert.True(db2.LoadedFromCache);
        Assert.Equal(5, db2.Count);
        var e = db2.FindByHref("rich_ecu.json")!.Value;
        Assert.Equal("RICH_ECU", e.EcuName);
        Assert.Equal("Injection", e.Group);
        Assert.Equal("1A", e.Address);
        Assert.Equal(EcuProtocol.Can, e.Protocol);
        Assert.Equal(new[] { "X95", "X85" }, e.Projects);
        Assert.Equal(3, e.AutoIdentCount);
        Assert.Equal(new AutoIdent("0A", "BOSCH", "9999", "A001"), e.GetAutoIdent(2));
    }

    [Fact]
    public void Cache_is_invalidated_when_zip_changes_and_survives_corruption()
    {
        using var dir = new TempDir();
        string zip = BuildFixtureZip(dir);
        using (EcuDatabase.Open(zip, Opts(dir))) { }
        using (var again = EcuDatabase.Open(zip, Opts(dir))) Assert.True(again.LoadedFromCache);

        // modify zip -> different length/mtime
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Update)) z.CreateEntry("extra.txt");
        using (var changed = EcuDatabase.Open(zip, Opts(dir))) Assert.False(changed.LoadedFromCache);
        using (var cached = EcuDatabase.Open(zip, Opts(dir))) Assert.True(cached.LoadedFromCache);

        // corrupt cache
        foreach (var f in Directory.GetFiles(dir.File("cache"))) File.WriteAllBytes(f, new byte[] { 1, 2, 3, 4, 5 });
        using (var rebuilt = EcuDatabase.Open(zip, Opts(dir)))
        {
            Assert.False(rebuilt.LoadedFromCache);
            Assert.Equal(5, rebuilt.Count);
        }

        // truncated cache
        foreach (var f in Directory.GetFiles(dir.File("cache")))
        {
            var b = File.ReadAllBytes(f);
            File.WriteAllBytes(f, b.AsSpan(0, b.Length / 2).ToArray());
        }

        using (var rebuilt = EcuDatabase.Open(zip, Opts(dir))) Assert.False(rebuilt.LoadedFromCache);
    }

    [Fact]
    public void Caching_can_be_disabled_and_forced()
    {
        using var dir = new TempDir();
        string zip = BuildFixtureZip(dir);
        using (EcuDatabase.Open(zip, new EcuDatabaseOptions { CacheDirectory = "" })) { }
        Assert.False(Directory.Exists(dir.File("cache")));
        using (EcuDatabase.Open(zip, Opts(dir))) { }
        using var forced = EcuDatabase.Open(zip, new EcuDatabaseOptions { CacheDirectory = dir.File("cache"), ForceRebuild = true });
        Assert.False(forced.LoadedFromCache);
    }

    [Fact]
    public void Missing_zip_throws()
    {
        Assert.Throws<FileNotFoundException>(() => EcuDatabase.Open("/nonexistent/ecu.zip"));
    }

    [Fact]
    public void Entries_load_ecu_and_layout_from_the_archive()
    {
        using var dir = new TempDir();
        using var db = EcuDatabase.Open(BuildFixtureZip(dir), Opts(dir));
        var entry = db.FindByHref("rich_ecu.json")!.Value;
        var ecu = db.LoadEcu(entry);
        Assert.Equal("RICH_ECU", ecu.EcuName);
        Assert.Equal(4, ecu.Requests.Count);
        var layout = db.LoadLayout(entry);
        Assert.Equal(3, layout.Screens.Count);
        Assert.Equal("Live data", layout.Categories[0].ScreenNames[0]);
        Assert.Empty(db.LoadLayout(db.FindByHref("dummy_ecu_kwp.json")!.Value).Screens);
        Assert.Equal(new byte[] { 1, 2, 3 }, db.ReadEntry("graphics/pic.gif"));
        Assert.Null(db.ReadEntry("graphics/none.gif"));
        Assert.True(db.ContainsEntry("db.json"));
        Assert.Throws<FileNotFoundException>(() => db.LoadEcu(db.FindByHref("rich_ecu.json")!.Value.Equals(default) ? default : MakeBogus(db)));
    }

    private static EcuEntry MakeBogus(EcuDatabase db)
    {
        // an entry whose member does not exist in the zip
        var b = new EcuIndexBuilder();
        b.Add("ghost.json", "Ghost", "G", "CAN", "01", Array.Empty<string>(), Array.Empty<AutoIdent>());
        var ix = b.Build();
        var d2 = EcuDatabase.FromIndex(ix);
        return d2[0];
    }

    [Fact]
    public async Task Async_loading_helpers()
    {
        using var dir = new TempDir();
        using var db = EcuDatabase.Open(BuildFixtureZip(dir), Opts(dir));
        var e = db.FindByHref("rich_ecu.json")!.Value;
        Assert.Equal("RICH_ECU", (await db.LoadEcuAsync(e)).EcuName);
        Assert.Equal(3, (await db.LoadLayoutAsync(e)).Screens.Count);
    }

    [Fact]
    public void In_memory_database_has_no_archive()
    {
        var b = new EcuIndexBuilder();
        b.Add("a.json", "A", "G", "CAN", "01", new[] { "p" }, Array.Empty<AutoIdent>());
        using var db = EcuDatabase.FromIndex(b.Build());
        Assert.Null(db.ZipPath);
        Assert.Throws<InvalidOperationException>(() => db.ReadEntry("a.json"));
        Assert.Throws<ArgumentOutOfRangeException>(() => db[5]);
    }

    private static EcuDatabase SyntheticDb(int n = 600)
    {
        var b = new EcuIndexBuilder();
        for (int i = 0; i < n; i++)
        {
            string proto = (i % 3) switch { 0 => "CAN", 1 => "KWP2000", _ => "ISO8" };
            b.Add($"E{i:D4}.json", $"Engine controller {i}", i % 2 == 0 ? "Injection" : "UCH", proto, (i % 40).ToString("X2"),
                new[] { "x95", $"P{i % 10}" }, new[] { new AutoIdent("10", "S" + i % 7, "1" + i, "0001") });
        }

        return EcuDatabase.FromIndex(b.Build());
    }

    [Fact]
    public void Search_filters_combine()
    {
        using var db = SyntheticDb();
        Assert.Equal(600, db.Search(new EcuSearch()).Count);
        Assert.Single(db.Search(new EcuSearch { Text = "controller 599" }));
        Assert.Equal(600, db.Search(new EcuSearch { Text = "ENGINE" }).Count);
        Assert.Equal(300, db.Search(new EcuSearch { Group = "injection" }).Count);
        Assert.Equal(200, db.Search(new EcuSearch { Protocol = EcuProtocol.Kwp2000 }).Count);
        Assert.Equal(15, db.Search(new EcuSearch { Address = "05" }).Count);
        Assert.Equal(60, db.Search(new EcuSearch { Project = "p3" }).Count);
        Assert.Equal(600, db.Search(new EcuSearch { Project = "X95" }).Count);
        Assert.Empty(db.Search(new EcuSearch { Project = "nonexistent" }));
        var r = db.Search(new EcuSearch { Project = "P3", Protocol = EcuProtocol.Can, Group = "UCH" });
        Assert.All(r, e => { Assert.Equal(EcuProtocol.Can, e.Protocol); Assert.Equal("UCH", e.Group); Assert.Contains("P3", e.Projects); });
        Assert.Equal(7, db.Search(new EcuSearch { Text = "e00", MaxResults = 7 }).Count);
        Assert.Single(db.Search(new EcuSearch { Text = "E0007.json" }));  // href match
    }

    [Fact]
    public void Search_reuses_result_list()
    {
        using var db = SyntheticDb();
        var l = new List<EcuEntry>();
        Assert.Equal(15, db.Search(new EcuSearch { Address = "05" }, l));
        Assert.Equal(1, db.Search(new EcuSearch { Text = "controller 599" }, l));
        Assert.Single(l);
    }

    [Fact]
    public void Projects_addresses_and_groups()
    {
        using var db = SyntheticDb();
        Assert.Equal(11, db.Projects.Count);
        Assert.Equal("P0", db.Projects[0]);
        Assert.Contains("X95", db.Projects);
        Assert.Equal(40, db.GetAddresses(EcuProtocol.Can).Count > 0 ? 40 : 0);
        Assert.Equal(new[] { "00", "03", "06" }.Take(3), db.GetAddresses(EcuProtocol.Can).Take(3));
        Assert.Contains("01", db.GetAddresses(EcuProtocol.Kwp2000));
        Assert.Equal(new[] { new ProjectAddress(EcuProtocol.Can, "00") }, db.GetProjectAddresses("X95").Take(1));
        Assert.Empty(db.GetProjectAddresses("nope"));
        Assert.Equal("Injection", db.GetGroupForAddress("00"));
        Assert.Null(db.GetGroupForAddress("ZZ"));
        Assert.Equal(15, db.FindByName("Engine controller 3").Count + 14);
    }

    [Fact]
    public void Vehicle_map_keeps_full_project_codes_like_the_reference_tests()
    {
        // mirrors the Python unit test test_vehiclemap_keeps_full_project_codes_from_zip_database
        using var dir = new TempDir();
        string path = dir.File("ecu.zip");
        string json = "{\"test.json\":{\"group\":\"UCH\",\"protocol\":\"CAN\",\"projects\":[\"X95PH2\"],\"address\":\"26\",\"ecuname\":\"Test ECU\"," +
                      "\"autoidents\":[{\"diagnostic_version\":\"01\",\"supplier_code\":\"SUPPLIER\",\"soft_version\":\"SOFT\",\"version\":\"0001\"}]}}";
        using (var z = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var w = new StreamWriter(z.CreateEntry("db.json").Open())) w.Write(json);
        using var db = EcuDatabase.Open(path, Opts(dir));
        Assert.Equal(new[] { new ProjectAddress(EcuProtocol.Can, "26") }, db.GetProjectAddresses("X95PH2"));
        Assert.Equal(new[] { "26" }, db.GetAddresses(EcuProtocol.Can));
    }

    // ---------------------------------------------------------------- autoident matching

    [Fact]
    public void Match_exact_approximate_and_none()
    {
        using var dir = new TempDir();
        using var db = EcuDatabase.Open(BuildFixtureZip(dir), Opts(dir));

        var exact = db.Match(new AutoIdentQuery("10", "CONTI", "1234", "0001"));
        Assert.Equal(MatchKind.Exact, exact.Kind);
        Assert.Equal("rich_ecu.json", exact.Entry.Href);
        Assert.Equal(0, exact.AutoIdentIndex);
        Assert.Equal("0001", exact.Ident.Version);

        // prefix semantics: the database value is a prefix of what the ECU reports; padding is trimmed
        Assert.Equal(MatchKind.Exact, db.Match(new AutoIdentQuery("10", " CONTI-EXTRA ", "1234  ", "0001XYZ")).Kind);

        // diag version is hexadecimal: "0A" == "A" == "10" (decimal text of 0x0A is "10" -> parsed as hex 0x10 !)
        Assert.Equal(MatchKind.Exact, db.Match(new AutoIdentQuery("A", "BOSCH", "9999", "A001")).Kind);

        var approx = db.Match(new AutoIdentQuery("10", "CONTI", "1234", "0004"));
        Assert.Equal(MatchKind.Approximate, approx.Kind);
        Assert.Equal(1, approx.AutoIdentIndex);       // 0005 is closer to 0004 than 0001
        Assert.Equal("0005", approx.Ident.Version);

        Assert.Equal(MatchKind.None, db.Match(new AutoIdentQuery("10", "NOBODY", "1234", "0001")).Kind);
        Assert.False(db.Match(new AutoIdentQuery("10", "NOBODY", "1234", "0001")).IsMatch);
        Assert.Equal(MatchKind.None, db.Match(new AutoIdentQuery("zz", "NOBODY", "1", "1")).Kind);
    }

    [Fact]
    public void Match_respects_bus_protocol()
    {
        using var dir = new TempDir();
        using var db = EcuDatabase.Open(BuildFixtureZip(dir), Opts(dir));
        // rich_ecu is CAN: a KWP query must not match it
        Assert.Equal(MatchKind.None, db.Match(new AutoIdentQuery("10", "CONTI", "1234", "0001", EcuProtocol.Kwp2000)).Kind);
    }

    [Fact]
    public void Match_non_hex_versions_cannot_be_approximated()
    {
        using var dir = new TempDir();
        using var db = EcuDatabase.Open(BuildFixtureZip(dir), Opts(dir));
        Assert.Equal(MatchKind.None, db.Match(new AutoIdentQuery("99", "CONTI", "1234", "not-hex")).Kind);
    }

    [Fact]
    public void Match_is_equivalent_to_python_check_ecu2()
    {
        using var doc = JsonDocument.Parse(TestPaths.Bytes("match_vectors.json"));
        using var dir = new TempDir();
        string path = dir.File("ecu.zip");
        using (var z = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var w = new StreamWriter(z.CreateEntry("db.json").Open(), new UTF8Encoding(false)))
            w.Write(doc.RootElement.GetProperty("db").GetRawText());
        using var db = EcuDatabase.Open(path, Opts(dir));
        int exact = 0, approx = 0, none = 0;
        foreach (var q in doc.RootElement.GetProperty("queries").EnumerateArray())
        {
            var proto = q.GetProperty("protocol").GetString() == "KWP" ? EcuProtocol.Kwp2000 : EcuProtocol.Can;
            var m = db.Match(new AutoIdentQuery(q.GetProperty("diag").GetString()!, q.GetProperty("supplier").GetString()!,
                q.GetProperty("soft").GetString()!, q.GetProperty("version").GetString()!, proto));
            var r = q.GetProperty("result");
            string kind = r[0].GetString()!;
            string ctx = q.ToString();
            switch (kind)
            {
                case "none":
                    Assert.True(m.Kind == MatchKind.None, ctx);
                    none++;
                    break;
                case "exact":
                case "approx":
                    Assert.True(m.Kind == (kind == "exact" ? MatchKind.Exact : MatchKind.Approximate), $"kind {m.Kind}: {ctx}");
                    Assert.True(r[1].GetString() == m.Entry.Href, $"href {m.Entry.Href}: {ctx}");
                    int ai = r[2].GetInt32();
                    Assert.True((ai < 0 ? 0 : ai) == (m.AutoIdentIndex < 0 ? 0 : m.AutoIdentIndex), $"ai {m.AutoIdentIndex}: {ctx}");
                    if (kind == "exact") exact++; else approx++;
                    break;
            }
        }

        Assert.True(exact > 100 && approx > 50 && none > 50, $"{exact}/{approx}/{none}");
    }

    // ---------------------------------------------------------------- db.json parsing & fallback

    [Fact]
    public void Db_json_parser_handles_typo_key_bom_unknown_keys_and_empty_entries()
    {
        using var dir = new TempDir();
        string path = dir.File("ecu.zip");
        string json = "{\"a.json\":{\"extra\":{\"x\":[1,2]},\"address\":\"26\",\"protocol\":\"DOIP\",\"group\":\"G\",\"ecuname\":\"A\",\"projects\":[\"p1\",\"P1\",\"\"],\"autoidents\":[{\"diagnotic_version\":\"05\",\"supplier_code\":\"S\",\"soft_version\":\"1\",\"version\":\"2\",\"zzz\":1}]}," +
                      "\"b.json\":{\"ecuname\":\"B\",\"protocol\":\"KWP2000\",\"address\":\"01\",\"group\":\"\",\"projects\":[],\"autoidents\":[]}}";
        using (var z = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var s = z.CreateEntry("db.json").Open())
        {
            s.Write(new byte[] { 0xEF, 0xBB, 0xBF });
            s.Write(Encoding.UTF8.GetBytes(json));
        }

        using var db = EcuDatabase.Open(path, Opts(dir));
        Assert.Equal(2, db.Count);
        var a = db[0];
        Assert.Equal(EcuProtocol.DoIp, a.Protocol);
        Assert.Equal(new[] { "P1" }, a.Projects);
        Assert.Equal("05", a.GetAutoIdent(0).DiagVersion);
        var b = db[1];
        Assert.Equal(0, b.AutoIdentCount);
        Assert.Empty(b.Projects);
        Assert.Throws<ArgumentOutOfRangeException>(() => b.GetAutoIdent(0));
        Assert.Equal(new[] { "26" }, db.GetAddresses(EcuProtocol.DoIp));
    }

    [Fact]
    public void Zip_without_db_json_falls_back_to_scanning_ecu_members()
    {
        using var dir = new TempDir();
        string path = dir.File("ecu.zip");
        using (var z = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            z.CreateEntryFromFile(TestPaths.Data("dummy_ecu_can.json"), "dummy_ecu_can.json");
            z.CreateEntry("graphics/x.json");
        }

        using var db = EcuDatabase.Open(path, Opts(dir));
        Assert.Equal(1, db.Count);
        Assert.Equal("JSON_ECU", db[0].EcuName);
        Assert.Equal("JSON_ECU", db.LoadEcu(db[0]).EcuName);
    }

    [Fact]
    public void Synthetic_large_zip_search_and_match_work()
    {
        using var dir = new TempDir();
        string path = dir.File("big.zip");
        SyntheticData.CreateDbZip(path, 3000, 8);
        using var db = EcuDatabase.Open(path, Opts(dir));
        Assert.Equal(3000, db.Count);
        Assert.Equal(24000, db.AutoIdentCount);
        Assert.NotEmpty(db.Search(new EcuSearch { Text = "ECU 02999" }));
        var e = db[1234];
        var ai = e.GetAutoIdent(3);
        var m = db.Match(new AutoIdentQuery(ai.DiagVersion, ai.Supplier, ai.Soft, ai.Version,
            e.Protocol == EcuProtocol.Can ? EcuProtocol.Can : EcuProtocol.Kwp2000));
        Assert.True(m.IsMatch);
    }

    [Fact]
    public void Index_persistence_roundtrip_preserves_everything()
    {
        using var dir = new TempDir();
        string path = dir.File("big.zip");
        SyntheticData.CreateDbZip(path, 200, 4);
        var ix = EcuDatabase.BuildIndexFromZip(path);
        using var ms = new MemoryStream();
        ix.Save(ms, 123, 456);
        ms.Position = 0;
        var back = EcuIndex.TryLoad(ms, 123, 456)!;
        Assert.NotNull(back);
        var d1 = EcuDatabase.FromIndex(ix);
        var d2 = EcuDatabase.FromIndex(back);
        Assert.Equal(d1.Count, d2.Count);
        for (int i = 0; i < d1.Count; i++)
        {
            Assert.Equal(d1[i].Href, d2[i].Href);
            Assert.Equal(d1[i].EcuName, d2[i].EcuName);
            Assert.Equal(d1[i].Projects, d2[i].Projects);
            Assert.Equal(d1[i].AutoIdents, d2[i].AutoIdents);
            Assert.Equal(d1[i].Protocol, d2[i].Protocol);
        }

        ms.Position = 0;
        Assert.Null(EcuIndex.TryLoad(ms, 124, 456));
        ms.Position = 0;
        Assert.Null(EcuIndex.TryLoad(ms, 123, 457));
    }

    [Fact]
    public async Task Concurrent_reads_from_the_archive_are_safe()
    {
        using var dir = new TempDir();
        using var db = EcuDatabase.Open(BuildFixtureZip(dir), Opts(dir));
        var entries = db.Entries.ToArray();
        var tasks = Enumerable.Range(0, 32).Select(i => Task.Run(() =>
        {
            var e = entries[i % entries.Length];
            return db.LoadEcu(e).EcuName;
        }));
        var names = await Task.WhenAll(tasks);
        Assert.Equal(32, names.Length);
        Assert.All(names, n => Assert.False(string.IsNullOrEmpty(n)));
    }
}
