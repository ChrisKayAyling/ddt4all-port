using System.Diagnostics;
using Ddt4All.App.ViewModels.Screens;
using Ddt4All.Core.Layout;

namespace Ddt4All.App.Tests;

/// <summary>The layout heuristics that turn DDT's absolute-position screens into captioned sections (pure logic, no UI).</summary>
public class ScreenFormBuilderTests
{
    private static ScreenLabel L(string text, int l, int t, int w, int h, string align = "0", bool bold = false) =>
        new() { Text = text, Bounds = new LayoutRect(l, t, w, h), Alignment = align, Font = new LayoutFont("Arial", 9, bold, false) };
    private static ScreenDisplay D(string name, int l, int t, int w, int h, int captionWidth = 0, string req = "Read") =>
        new() { DataName = name, RequestName = req, Bounds = new LayoutRect(l, t, w, h), Width = captionWidth };
    private static ScreenInput I(string name, int l, int t, int w, int h, int captionWidth = 0, string req = "Write") =>
        new() { DataName = name, RequestName = req, Bounds = new LayoutRect(l, t, w, h), Width = captionWidth };
    private static ScreenButton B(string text, int l, int t, int w, int h) =>
        new() { Text = text, Bounds = new LayoutRect(l, t, w, h) };

    private static IEnumerable<FormEntry> Entries(ScreenForm f) => f.Sections.SelectMany(s => s.Entries);

    [Fact]
    public void Label_left_of_a_control_becomes_its_caption()
    {
        var s = new Screen { Name = "t" };
        s.Labels.Add(L("Coolant temperature", 10, 10, 150, 20));
        s.Displays.Add(D("DM$1102_Coolant", 165, 10, 100, 20));
        var f = ScreenFormBuilder.Build(s);
        var e = Assert.Single(Entries(f));
        Assert.Equal("Coolant temperature", e.Caption);
        Assert.True(e.CaptionFromLabel);
        Assert.Equal(1, f.Stats.LabelsPaired);
    }

    [Fact]
    public void Control_without_a_label_gets_a_readable_caption_from_its_data_name()
    {
        var s = new Screen { Name = "t" };
        s.Displays.Add(D("DM$1201_engine speed", 10, 10, 200, 20, 100));
        s.Displays.Add(D("Dev_Reaction_Mask.BCM", 10, 40, 200, 20, 100));
        s.Displays.Add(D("Vehicle_speed_limit", 10, 70, 200, 20, 100));
        var caps = Entries(ScreenFormBuilder.Build(s)).Select(e => e.Caption).ToList();
        Assert.Equal(new[] { "engine speed", "Dev Reaction Mask.BCM", "Vehicle speed limit" }, caps);
        Assert.Equal("Vehicle speed", ScreenFormBuilder.Pretty("DM$1102_Vehicle speed"));
        Assert.Equal("Odometer", ScreenFormBuilder.Pretty("$F182_Odometer"));
    }

    [Fact]
    public void Label_over_a_column_of_controls_is_a_section_title_not_a_caption()
    {
        var s = new Screen { Name = "t" };
        s.Labels.Add(L("Temperatures", 10, 10, 220, 20, "2", bold: true));
        for (int i = 0; i < 4; i++) s.Displays.Add(D("T" + i, 10, 36 + i * 26, 220, 20, 100));
        var f = ScreenFormBuilder.Build(s);
        var sec = Assert.Single(f.Sections);
        Assert.Equal("Temperatures", sec.Title);
        Assert.Equal(4, sec.Entries.Count);
        Assert.Equal(1, f.Stats.LabelsTitle);
    }

    [Fact]
    public void Container_rectangle_groups_its_controls_and_names_the_section()
    {
        var s = new Screen { Name = "t" };
        s.Labels.Add(L("Idle", 0, 0, 260, 120));
        s.Labels.Add(L("Boost", 300, 0, 260, 120));
        for (int i = 0; i < 3; i++) { s.Displays.Add(D("A" + i, 10, 22 + i * 30, 240, 24, 100)); s.Displays.Add(D("B" + i, 310, 22 + i * 30, 240, 24, 100)); }
        var f = ScreenFormBuilder.Build(s);
        Assert.Equal(new[] { "Idle", "Boost" }, f.Sections.Select(x => x.Title));
        Assert.All(f.Sections, x => Assert.Equal(3, x.Entries.Count));
    }

    [Fact]
    public void Read_back_display_beside_an_input_folds_into_the_input_tile()
    {
        var s = new Screen { Name = "t" };
        s.Displays.Add(D("Idle mode", 10, 10, 200, 24, 100, "Read"));
        s.Inputs.Add(I("Idle mode", 230, 10, 200, 24, 100, "Write"));
        var f = ScreenFormBuilder.Build(s);
        var e = Assert.Single(Entries(f));
        Assert.Equal(FormEntryKind.Input, e.Kind);
        Assert.Equal(0, e.ReadingSlot);
        Assert.Equal(2, f.Stats.Placed);        // both widgets are accounted for
        Assert.Equal(1, f.Stats.ReadingsMerged);
    }

    [Fact]
    public void Decorative_pictures_and_empty_labels_are_dropped_but_counted()
    {
        var s = new Screen { Name = "t" };
        s.Labels.Add(L("::pic:borders\\borderh_50x8", 0, 0, 300, 8));
        s.Labels.Add(L("", 0, 100, 50, 50));
        s.Displays.Add(D("X", 10, 20, 200, 24, 100));
        var f = ScreenFormBuilder.Build(s);
        Assert.Equal(2, f.Stats.LabelsDecor);
        Assert.Single(Entries(f));
    }

    [Fact]
    public void Reading_order_is_top_to_bottom_then_left_to_right_and_buttons_stay_with_their_section()
    {
        var s = new Screen { Name = "t" };
        s.Labels.Add(L("Block", 0, 0, 500, 20, "2", true));
        s.Displays.Add(D("right", 260, 30, 240, 24, 100));
        s.Displays.Add(D("left", 10, 31, 240, 24, 100));
        s.Displays.Add(D("below", 10, 60, 240, 24, 100));
        s.Buttons.Add(B("Go", 10, 95, 80, 26));
        var f = ScreenFormBuilder.Build(s);
        Assert.Equal(new[] { "left", "right", "below", "Go" }, Entries(f).Select(e => e.Caption));
        Assert.Equal(FormEntryKind.Button, Entries(f).Last().Kind);
    }

    [Fact]
    public void Twip_scaled_layouts_are_detected_for_the_classic_canvas()
    {
        var px = new Screen(); px.Displays.Add(D("a", 0, 0, 300, 28));
        var tw = new Screen(); tw.Displays.Add(D("a", 0, 0, 4500, 300));
        Assert.Equal(1.0, Ddt4All.App.Views.Screens.ScreenScene.LayoutScale(px));
        Assert.Equal(1.0 / 15, Ddt4All.App.Views.Screens.ScreenScene.LayoutScale(tw), 6);
    }

    [Fact]
    public void Demo_fixture_screen_reads_as_two_titled_sections()
    {
        var (_, layout) = ScreenFixtures.Demo();
        var f = ScreenFormBuilder.Build(layout.Screens["Engine parameters"]);
        Assert.Equal(new[] { "Engine measurements", "Adjustments" }, f.Sections.Select(x => x.Title));
        Assert.Equal(12, f.Sections[0].Entries.Count(e => e.Kind == FormEntryKind.Display));
        Assert.Equal(2, f.Sections[1].Entries.Count(e => e.Kind == FormEntryKind.Input));
        Assert.Equal(3, f.Sections[1].Entries.Count(e => e.Kind == FormEntryKind.Button));
    }

    /// <summary>Random layouts (overlapping, huge, tiny, negative coordinates): never throws, never loses or duplicates a widget, accounts for every label.</summary>
    [Fact]
    public void Fuzzed_layouts_never_lose_a_widget()
    {
        var rnd = new Random(12345);
        for (int n = 0; n < 400; n++)
        {
            var s = new Screen { Name = "fuzz" + n };
            int unit = rnd.Next(1, 4) == 1 ? 300 : 24;
            int nl = rnd.Next(0, 30), nd = rnd.Next(0, 40), ni = rnd.Next(0, 15), nb = rnd.Next(0, 8);
            LayoutRect R(int wmax) => new(rnd.Next(-2, 40) * unit / 2, rnd.Next(-1, 60) * unit / 2, rnd.Next(0, wmax) * unit, rnd.Next(0, 3) * unit + rnd.Next(0, unit));
            string[] texts = ["", "READ", "WRITE", "= 1", "Coolant", "::pic:arrows\\lineh_50x7", "Some long explanatory sentence about this block of values", "A", "Group"];
            for (int i = 0; i < nl; i++) s.Labels.Add(new ScreenLabel { Text = texts[rnd.Next(texts.Length)], Bounds = R(8), Alignment = rnd.Next(3).ToString(), Font = new LayoutFont("A", 9, rnd.Next(2) == 0, false) });
            for (int i = 0; i < nd; i++) s.Displays.Add(new ScreenDisplay { DataName = "D" + rnd.Next(30), RequestName = "R", Bounds = R(10), Width = rnd.Next(0, 3) * unit });
            for (int i = 0; i < ni; i++) s.Inputs.Add(new ScreenInput { DataName = "D" + rnd.Next(30), RequestName = "W", Bounds = R(10), Width = rnd.Next(0, 3) * unit });
            for (int i = 0; i < nb; i++) s.Buttons.Add(new ScreenButton { Text = rnd.Next(4) == 0 ? "" : "Btn" + i, Bounds = R(4) });

            var f = ScreenFormBuilder.Build(s);
            var st = f.Stats;
            Assert.Equal(nd + ni + nb, st.Placed);
            int labels = st.LabelsPaired + st.LabelsHint + st.LabelsTitle + st.LabelsFrame + st.LabelsNote + st.LabelsDecor + st.LabelsPageTitle;
            Assert.Equal(nl, labels);
            var seen = new HashSet<(FormEntryKind, int)>();
            foreach (var e in Entries(f))
            {
                if (e.Kind == FormEntryKind.Note) continue;
                Assert.True(seen.Add((e.Kind, e.Index)), $"duplicate {e.Kind} {e.Index} in {s.Name}");
                if (e.ReadingSlot >= 0) Assert.True(seen.Add((FormEntryKind.Display, e.ReadingSlot)), $"duplicate reading {e.ReadingSlot} in {s.Name}");
            }
            Assert.All(f.Sections, x => Assert.NotEmpty(x.Entries));
        }
    }

    [Fact]
    public void Huge_screens_build_quickly()
    {
        var s = new Screen { Name = "big" };
        for (int i = 0; i < 4000; i++)
        {
            s.Labels.Add(L("Value " + i, (i % 6) * 520, (i / 6) * 28, 240, 24));
            s.Displays.Add(D("V" + i, (i % 6) * 520 + 250, (i / 6) * 28, 240, 24));
        }
        var sw = Stopwatch.StartNew();
        var f = ScreenFormBuilder.Build(s);
        sw.Stop();
        Assert.Equal(4000, f.Stats.Placed);
        Assert.Equal(4000, f.Stats.LabelsPaired);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"{sw.ElapsedMilliseconds} ms");
    }
}

/// <summary>The heuristics over the user's real ecu.zip (skipped when it is not there).</summary>
public class ScreenFormRealZipTests
{
    [Fact]
    public void Every_real_layout_builds_with_no_crash_no_lost_widget_and_a_form_whenever_there_is_content()
    {
        RealZip.RequireOrSkip();
        var db = RealZip.Db();
        bool full = Environment.GetEnvironmentVariable("DDT4ALL_REAL_ZIP_FULL") == "1";
        int step = full ? 1 : 5;
        long ecus = 0, screens = 0, widgets = 0, labels = 0, paired = 0, hint = 0, titles = 0, notes = 0, decor = 0, frames = 0, pageTitles = 0, merged = 0, empty = 0, sections = 0, crashes = 0, lost = 0, mismatch = 0;
        double worstMs = 0; string worst = "";
        var problems = new List<string>();
        for (int i = 0; i < db.Count; i += step)
        {
            var e = db[i];
            Ddt4All.Core.Layout.EcuLayout layout;
            try { layout = db.LoadLayout(e); } catch { continue; }
            ecus++;
            foreach (var sc in layout.Screens)
            {
                screens++;
                try
                {
                    var t0 = Stopwatch.GetTimestamp();
                    var f = ScreenFormBuilder.Build(sc);
                    double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    if (ms > worstMs) { worstMs = ms; worst = $"{e.EcuName}/{sc.Name}"; }
                    var st = f.Stats;
                    int expect = st.Displays + st.Inputs + st.Buttons;
                    widgets += expect; labels += st.Labels;
                    paired += st.LabelsPaired; hint += st.LabelsHint; titles += st.LabelsTitle; notes += st.LabelsNote; decor += st.LabelsDecor; frames += st.LabelsFrame; pageTitles += st.LabelsPageTitle;
                    merged += st.ReadingsMerged; sections += f.Sections.Count;
                    if (st.Placed != expect) { lost += expect - st.Placed; if (problems.Count < 5) problems.Add($"{e.EcuName}/{sc.Name}: placed {st.Placed}/{expect}"); }
                    int acc = st.LabelsPaired + st.LabelsHint + st.LabelsTitle + st.LabelsFrame + st.LabelsNote + st.LabelsDecor + st.LabelsPageTitle;
                    if (acc != st.Labels) { mismatch++; if (problems.Count < 5) problems.Add($"{e.EcuName}/{sc.Name}: labels accounted {acc}/{st.Labels}"); }
                    var seen = new HashSet<(FormEntryKind, int)>();
                    foreach (var en in f.Sections.SelectMany(x => x.Entries))
                    {
                        if (en.Kind == FormEntryKind.Note) continue;
                        if (!seen.Add((en.Kind, en.Index))) lost++;
                        if (en.ReadingSlot >= 0 && !seen.Add((FormEntryKind.Display, en.ReadingSlot))) lost++;
                    }
                    bool hasContent = expect > 0 || (st.Labels - st.LabelsDecor) > 0;
                    if (hasContent && f.Sections.Count == 0 && st.LabelsPageTitle == 0 && st.LabelsFrame == st.Labels - st.LabelsDecor) { /* only frames: nothing to show */ }
                    else if (hasContent && f.Sections.Count == 0 && st.LabelsPageTitle == 0) { empty++; if (problems.Count < 5) problems.Add($"{e.EcuName}/{sc.Name}: empty form"); }
                }
                catch (Exception ex) { crashes++; if (problems.Count < 5) problems.Add($"{e.EcuName}/{sc.Name}: {ex.GetType().Name}: {ex.Message}"); }
            }
        }
        var summary = $"{ecus} ECUs / {screens} screens (step {step}): widgets {widgets}, labels {labels} = paired {paired} + hint {hint} + title {titles} + frame {frames} + note {notes} + decor {decor} + pageTitle {pageTitles}; readings merged {merged}; sections {sections}; empty forms {empty}; lost widgets {lost}; crashes {crashes}; slowest {worstMs:0.0} ms ({worst})";
        PerfLog.Write("real zip: " + summary);
        Assert.True(crashes == 0 && lost == 0 && mismatch == 0 && empty == 0, summary + "\n" + string.Join("\n", problems));
    }
}
