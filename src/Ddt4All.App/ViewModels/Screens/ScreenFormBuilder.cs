using System.Text.RegularExpressions;
using Ddt4All.Core.Layout;

namespace Ddt4All.App.ViewModels.Screens;

public enum FormEntryKind : byte { Display, Input, Button, Note }

/// <summary>One thing in a form section. <see cref="Index"/> points into the source screen's Displays / Inputs / Buttons.</summary>
public sealed class FormEntry
{
    public FormEntryKind Kind { get; internal set; }
    /// <summary>Index into screen.Displays (Display), screen.Inputs (Input) or screen.Buttons (Button); -1 for notes.</summary>
    public int Index { get; internal set; } = -1;
    /// <summary>Human readable caption (paired label text, or the cleaned data name).</summary>
    public string Caption { get; internal set; } = "";
    /// <summary>Technical name (DataName / button text) shown as tooltip and searchable.</summary>
    public string RawName { get; internal set; } = "";
    /// <summary>True when the caption came from a layout label (rather than from the data name).</summary>
    public bool CaptionFromLabel { get; internal set; }
    /// <summary>Extra short labels sitting next to the control ("= 0", "1", "IGN ON").</summary>
    public List<string> Hints { get; } = new();
    /// <summary>Note text (Kind == Note).</summary>
    public string Text { get; internal set; } = "";
    /// <summary>Input only: the live display with the same data that DDT placed beside it (shown inside the input tile).</summary>
    public int ReadingSlot { get; internal set; } = -1;
    internal Box Box;
    internal List<object>? Labels;
}

public sealed class FormSection
{
    public string Title { get; internal set; } = "";
    public bool HasTitle => Title.Length > 0;
    public List<FormEntry> Entries { get; } = new();
    internal Box Box;
    internal bool IsContainer;
    internal int Order;
}

public sealed class FormStats
{
    public int Displays, Inputs, Buttons, Labels;
    /// <summary>Every layout label ends up in exactly one of these buckets.</summary>
    public int LabelsPaired, LabelsHint, LabelsTitle, LabelsFrame, LabelsNote, LabelsDecor, LabelsPageTitle;
    public int ReadingsMerged;
    public int Sections, EmptySectionsDropped, StrayNotes;
    public int Placed;      // displays + inputs + buttons that appear in the form
}

public sealed class ScreenForm
{
    public string PageTitle { get; internal set; } = "";
    public List<FormSection> Sections { get; } = new();
    public FormStats Stats { get; } = new();
    public int EntryCount => Sections.Sum(s => s.Entries.Count);
}

internal readonly record struct Box(double L, double T, double R, double B)
{
    public double W => R - L;
    public double H => B - T;
    public double CX => (L + R) / 2;
    public double CY => (T + B) / 2;
    public double Area => Math.Max(0, W) * Math.Max(0, H);
    public static Box From(LayoutRect r) => new(r.Left, r.Top, r.Left + Math.Max(1, r.Width), r.Top + Math.Max(1, r.Height));
    public static double VOverlap(Box a, Box b) => Math.Min(a.B, b.B) - Math.Max(a.T, b.T);
    public static double HOverlap(Box a, Box b) => Math.Min(a.R, b.R) - Math.Max(a.L, b.L);
    public Box Union(Box o) => new(Math.Min(L, o.L), Math.Min(T, o.T), Math.Max(R, o.R), Math.Max(B, o.B));
    public bool ContainsCentre(Box o) => o.CX >= L && o.CX <= R && o.CY >= T && o.CY <= B;
}

/// <summary>
/// Turns DDT's absolute-position screen definition into a flow of titled sections of captioned controls.
/// Everything is expressed relative to the screen's own row height (median control height), so it works for both
/// the pixel-based and the twip-based layouts found in real ecu.zip files. Pure logic, no UI types.
/// </summary>
public static partial class ScreenFormBuilder
{
    private sealed class W
    {
        public FormEntryKind Kind; public int Index; public Box Box; public string Name = "";
        public bool OwnCaption;               // DDT draws the DataName inside the control (Width > 0)
        public string? Label; public bool Merged;
        public List<string> Hints = new();
        public FormEntry? Entry; public int Section = -1; public int ReadingSlot = -1;
    }

    private sealed class L
    {
        public string Text = ""; public Box Box; public int Index; public bool Used; public bool Container; public bool Decor; public char Cat = '?'; public bool Banner;
    }

    private sealed class Sec
    {
        public string Title = ""; public Box Box; public bool IsContainer; public Box Anchor;
        public double LastBottom; public int ContainerLabel = -1; public List<W> Items = new(); public List<L> Notes = new();
        public bool HasAnchor; public L? HeaderLabel;
    }

    /// <summary>Frame titles that only mark the read-back / write column of a DDT table; meaningless once readings sit inside the inputs.</summary>
    [GeneratedRegex(@"^\s*(read|write|reading|writing|lecture|ecriture|écriture|lire|ecrire|écrire|lesen|schreiben|leer|escribir|lettura|scrittura)\s*(all|values?)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ColumnMarker();

    /// <summary>Widgets sorted by top edge: finds the ones in a vertical band without scanning the whole screen.</summary>
    private sealed class Index
    {
        private readonly W[] _w; private readonly double _maxH;
        public Index(List<W> ws)
        {
            _w = ws.OrderBy(x => x.Box.T).ToArray();
            _maxH = _w.Length == 0 ? 0 : _w.Max(x => x.Box.H);
        }
        public IEnumerable<W> Band(double top, double bottom)
        {
            int lo = 0, hi = _w.Length; double min = top - _maxH;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (_w[mid].Box.T < min) lo = mid + 1; else hi = mid; }
            for (int i = lo; i < _w.Length && _w[i].Box.T <= bottom; i++) yield return _w[i];
        }
    }

    [GeneratedRegex(@"^(?:DM)?\$[0-9A-Fa-f]{2,5}_+")]
    private static partial Regex IdPrefix();

    /// <summary>Readable form of a technical data name: drops DDT id prefixes ("DM$1201_"), underscores become spaces.</summary>
    public static string Pretty(string raw)
    {
        var s = raw.Trim();
        var m = IdPrefix().Match(s);
        if (m.Success && m.Length < s.Length) s = s[m.Length..];
        if (s.IndexOf(' ') < 0 && s.IndexOf('_') >= 0) s = s.Replace('_', ' ');
        return s.Length == 0 ? raw : s;
    }

    public static ScreenForm Build(Screen screen)
    {
        var form = new ScreenForm();
        var st = form.Stats;
        st.Displays = screen.Displays.Count; st.Inputs = screen.Inputs.Count; st.Buttons = screen.Buttons.Count; st.Labels = screen.Labels.Count;

        // ---- widgets
        var ws = new List<W>(st.Displays + st.Inputs + st.Buttons);
        for (int i = 0; i < screen.Displays.Count; i++)
        { var d = screen.Displays[i]; ws.Add(new W { Kind = FormEntryKind.Display, Index = i, Box = Box.From(d.Bounds), Name = d.DataName, OwnCaption = d.Width > 0 }); }
        for (int i = 0; i < screen.Inputs.Count; i++)
        { var d = screen.Inputs[i]; ws.Add(new W { Kind = FormEntryKind.Input, Index = i, Box = Box.From(d.Bounds), Name = d.DataName, OwnCaption = d.Width > 0 }); }
        for (int i = 0; i < screen.Buttons.Count; i++)
        { var b = screen.Buttons[i]; ws.Add(new W { Kind = FormEntryKind.Button, Index = i, Box = Box.From(b.Bounds), Name = b.Text }); }

        double u = Unit(screen, ws);   // row height

        // ---- labels
        var labels = new List<L>(screen.Labels.Count);
        for (int i = 0; i < screen.Labels.Count; i++)
        {
            var l = screen.Labels[i];
            var text = l.Text.Trim();
            var lab = new L { Text = text, Box = Box.From(l.Bounds), Index = i, Decor = text.StartsWith("::pic:", StringComparison.Ordinal), Banner = l.Alignment == "2" && l.Font.Bold };
            if (lab.Decor) lab.Cat = 'd';
            labels.Add(lab);
        }

        // DDT often stacks two identical labels (drop shadow): keep one
        var seenText = new Dictionary<string, List<L>>();
        foreach (var a in labels)
        {
            if (a.Decor || a.Text.Length == 0) continue;
            if (!seenText.TryGetValue(a.Text, out var same)) seenText[a.Text] = same = new List<L>();
            foreach (var b in same)
            {
                double inter = Math.Max(0, Box.HOverlap(a.Box, b.Box)) * Math.Max(0, Box.VOverlap(a.Box, b.Box));
                if (inter >= 0.6 * Math.Min(a.Box.Area, b.Box.Area)) { a.Decor = true; a.Cat = 'd'; break; }
            }
            if (!a.Decor) same.Add(a);
        }

        // ---- read-back displays beside the input with the same data are shown inside that input
        MergeReadings(ws, u);
        ws.RemoveAll(w => w.Merged);
        var idx = new Index(ws);

        // ---- containers: labels framing several controls (group boxes / panels)
        foreach (var l in labels)
        {
            if (l.Decor) continue;
            int inside = 0; foreach (var w in idx.Band(l.Box.T, l.Box.B)) if (l.Box.ContainsCentre(w.Box)) inside++;
            bool big = l.Box.H >= 2.2 * u && l.Box.W >= 2 * u;
            if (inside >= 2 || (inside >= 1 && big)) l.Container = true;
            if (l.Text.Length == 0 && !l.Container) { l.Decor = true; l.Cat = 'd'; }
        }

        // ---- pair labels with displays / inputs (one label per control, one control per label, best score first)
        var cands = new List<(double Score, L Label, W Widget)>();
        foreach (var l in labels)
        {
            if (l.Decor || l.Container) continue;
            foreach (var w in idx.Band(l.Box.T - 1.2 * u, l.Box.B + 1.2 * u))
            {
                if (w.Kind == FormEntryKind.Button) continue;
                double sc = PairScore(l, w, u, idx);
                if (sc >= 0) cands.Add((sc, l, w));
            }
        }
        cands.Sort((a, b) => a.Score.CompareTo(b.Score));
        foreach (var (_, l, w) in cands)
        {
            if (l.Used || w.Label is not null) continue;
            l.Used = true; l.Cat = 'p'; w.Label = l.Text;
        }

        // ---- short leftovers next to a control on the same row become hints ("= 0", "1", "IGN ON")
        foreach (var l in labels)
        {
            if (l.Used || l.Decor || l.Container || l.Text.Length > 12) continue;
            W? best = null; double bd = double.MaxValue;
            foreach (var w in idx.Band(l.Box.T - 0.5 * u, l.Box.B + 0.5 * u))
            {
                if (w.Kind == FormEntryKind.Button) continue;
                if (Box.VOverlap(l.Box, w.Box) < 0.5 * Math.Min(l.Box.H, w.Box.H)) continue;
                double dx = l.Box.CX < w.Box.CX ? w.Box.L - l.Box.R : l.Box.L - w.Box.R;
                if (dx < -0.3 * u || dx > 8 * u) continue;
                if (dx < bd) { bd = dx; best = w; }
            }
            if (best is null) continue;
            l.Used = true; l.Cat = 'h';
            if (best.Label is null) best.Label = l.Text; else best.Hints.Add(l.Text);
        }

        // inputs and displays that carry the very same caption on one row are the write / read side of one value
        foreach (var inp in ws.Where(x => x.Kind == FormEntryKind.Input && x.ReadingSlot < 0 && x.Label is not null))
        {
            W? best = null; double bd = double.MaxValue;
            foreach (var d in idx.Band(inp.Box.T, inp.Box.B))
            {
                if (d.Kind != FormEntryKind.Display || d.Merged || d.Label is null || !string.Equals(d.Label, inp.Label, StringComparison.OrdinalIgnoreCase)) continue;
                if (Box.VOverlap(d.Box, inp.Box) < 0.5 * Math.Min(d.Box.H, inp.Box.H)) continue;
                double dx = Math.Abs(d.Box.CX - inp.Box.CX);
                if (dx > 40 * u || dx >= bd) continue;
                bd = dx; best = d;
            }
            if (best is null) continue;
            best.Merged = true; inp.ReadingSlot = best.Index; inp.Box = inp.Box.Union(best.Box);
        }
        ws.RemoveAll(w => w.Merged && w.Kind == FormEntryKind.Display);
        idx = new Index(ws);

        // READ / WRITE column markers carry no information in a form
        foreach (var l in labels)
            if (!l.Used && !l.Decor && !l.Container && ColumnMarker().IsMatch(l.Text)) { l.Used = true; l.Cat = 'f'; }

        // ---- headers vs notes among the remaining labels
        var headers = new List<L>(); var notes = new List<L>();
        foreach (var l in labels)
        {
            if (l.Used || l.Decor || l.Container) continue;
            bool below = false;
            foreach (var w in idx.Band(l.Box.T - 0.3 * u, l.Box.B + 3 * u))
                if (w.Box.T >= l.Box.T - 0.3 * u && w.Box.T - l.Box.B <= 3 * u && Box.HOverlap(l.Box, w.Box) > 0.2 * Math.Min(l.Box.W, w.Box.W)) { below = true; break; }
            if (below && l.Text.Length <= 70 && !l.Text.Contains('\n')) headers.Add(l); else notes.Add(l);
        }

        // ---- section skeletons
        var containers = new List<Sec>();
        foreach (var c in labels.Where(l => l.Container))
        {
            var s = new Sec { Box = c.Box, IsContainer = true, Anchor = c.Box };
            if (c.Text.Length == 0) c.Cat = 'f';
            else if (c.Text.Length <= 80 && !ColumnMarker().IsMatch(c.Text)) { s.Title = c.Text; c.Cat = 't'; }
            else if (c.Text.Length <= 80) c.Cat = 'f';
            else { c.Cat = 'f'; s.Notes.Add(c); }
            containers.Add(s);
        }
        foreach (var s in containers.Where(x => x.Title.Length == 0))
        {
            var t = headers.Where(h => h.Box.CX >= s.Box.L && h.Box.CX <= s.Box.R && h.Box.CY >= s.Box.T - 1.6 * u && h.Box.CY <= s.Box.T + 1.6 * u)
                           .OrderBy(h => Math.Abs(h.Box.CY - s.Box.T)).FirstOrDefault();
            if (t is not null) { s.Title = t.Text; t.Cat = 't'; headers.Remove(t); }
        }
        var hdrSecs = headers.Select(h => new Sec { Title = h.Text, Box = h.Box, Anchor = h.Box, HasAnchor = true, LastBottom = h.Box.B, HeaderLabel = h }).ToList();
        var secs = new List<Sec>(containers);
        secs.AddRange(hdrSecs);

        // ---- assignment: innermost container, else the nearest header above (same column, no big gap), else spatial clusters
        var cluster = new List<W>();
        foreach (var w in ws.OrderBy(w => w.Box.T).ThenBy(w => w.Box.L))
        {
            Sec? c = Innermost(containers, w.Box);
            if (c is not null) { c.Items.Add(w); continue; }
            var best = NearestHeader(hdrSecs, w.Box, u, wide: false);
            if (best is not null) { best.Items.Add(w); best.LastBottom = Math.Max(best.LastBottom, w.Box.B); best.Box = best.Box.Union(w.Box); continue; }
            cluster.Add(w);
        }
        var looseNotes = new List<L>();
        foreach (var n in notes.OrderBy(n => n.Box.T))
        {
            Sec? c = Innermost(containers, n.Box);
            if (c is not null) { c.Notes.Add(n); continue; }
            var best = NearestHeader(hdrSecs, n.Box, u, wide: true);
            if (best is not null) { best.Notes.Add(n); best.LastBottom = Math.Max(best.LastBottom, n.Box.B); continue; }
            looseNotes.Add(n);
        }
        foreach (var s in containers.Where(x => x.Notes.Count > 0 && x.Items.Count == 0)) { } // long-titled empty frames handled below

        var pool = new List<(Box Box, W? Widget, L? Note)>();
        foreach (var w in cluster) pool.Add((w.Box, w, null));
        foreach (var n in looseNotes) pool.Add((n.Box, null, n));
        var parent = Enumerable.Range(0, pool.Count).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        double gapLimit = 0.9 * u;
        double maxPoolH = pool.Count == 0 ? 0 : pool.Max(x => x.Box.H);
        var byTop = Enumerable.Range(0, pool.Count).OrderBy(k => pool[k].Box.T).ToArray();
        for (int ii = 0; ii < byTop.Length; ii++)
            for (int jj = ii + 1; jj < byTop.Length; jj++)
            {
                int i = byTop[ii], j = byTop[jj];
                var a = pool[i].Box; var b = pool[j].Box;
                if (b.T - a.B > gapLimit && b.T - pool[i].Box.T > 0 && pool[j].Box.T - pool[i].Box.T > gapLimit + maxPoolH) break;
                double gx = Math.Max(a.L, b.L) - Math.Min(a.R, b.R);   // negative: overlap
                double gy = Math.Max(a.T, b.T) - Math.Min(a.B, b.B);
                if (gx <= gapLimit * 1.6 && gy <= gapLimit && (gx < 0 || gy < 0 || gx + gy <= gapLimit * 2)) parent[Find(i)] = Find(j);
            }
        var groups = new Dictionary<int, Sec>();
        for (int i = 0; i < pool.Count; i++)
        {
            int r = Find(i);
            if (!groups.TryGetValue(r, out var s)) { groups[r] = s = new Sec { Box = pool[i].Box }; secs.Add(s); }
            s.Box = s.Box.Union(pool[i].Box);
            if (pool[i].Widget is { } w) s.Items.Add(w); else s.Notes.Add(pool[i].Note!);
        }

        // ---- emit
        var stray = new List<L>();
        int order = 0;
        foreach (var s in secs)
        {
            if (s.Items.Count == 0 && s.Notes.Count == 0)
            {
                if (s.HeaderLabel is { } hl)
                {
                    // header that captured nothing: page banner if it sits at the top, otherwise a note
                    if (form.PageTitle.Length == 0 && hl.Box.T <= 2.5 * u + (ws.Count > 0 ? ws.Min(w => w.Box.T) * 0 : 0)) { form.PageTitle = Clean(hl.Text); hl.Cat = 'P'; }
                    else { hl.Cat = 'n'; stray.Add(hl); }
                }
                else st.EmptySectionsDropped++;
                continue;
            }
            if (s.HeaderLabel is { } h2) h2.Cat = 't';
            var fs = new FormSection { Title = Clean(s.Title), Box = s.Box, IsContainer = s.IsContainer, Order = order++ };
            foreach (var w in s.Items.Where(x => !x.Merged)) fs.Entries.Add(MakeEntry(w));
            foreach (var row in NoteRows(s.Notes, u)) fs.Entries.Add(row);
            if (fs.Entries.Count == 0) { continue; }
            SortReading(fs.Entries, u);
            form.Sections.Add(fs);
        }
        // stray notes (headers that governed nothing) join the nearest section
        foreach (var n in stray)
        {
            st.StrayNotes++;
            n.Cat = 'n';
            var e = new FormEntry { Kind = FormEntryKind.Note, Text = n.Text, Box = n.Box, Labels = new List<object> { n } };
            FormSection? best = null; double bd = double.MaxValue;
            foreach (var s in form.Sections)
            {
                double dy = Math.Max(0, Math.Max(s.Box.T - n.Box.B, n.Box.T - s.Box.B));
                double dx = Math.Max(0, Math.Max(s.Box.L - n.Box.R, n.Box.L - s.Box.R));
                double d = dy + dx * 0.5;
                if (d < bd) { bd = d; best = s; }
            }
            if (best is null) { form.Sections.Add(best = new FormSection { Box = n.Box }); }
            best.Entries.Add(e); SortReading(best.Entries, u);
        }

        // tiny untitled leftovers (a lone button, a single value) join the section they sit next to
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < form.Sections.Count; i++)
            {
                var t = form.Sections[i];
                if (t.HasTitle || t.Entries.Count > 3) continue;
                FormSection? best = null; double bd = double.MaxValue;
                foreach (var o in form.Sections)
                {
                    if (ReferenceEquals(o, t)) continue;
                    double ov = Box.HOverlap(t.Box, o.Box);
                    if (ov < 0.3 * Math.Min(t.Box.W, o.Box.W)) continue;
                    double gap = Math.Max(t.Box.T - o.Box.B, o.Box.T - t.Box.B);   // negative: overlapping rows
                    if (gap > 4 * u) continue;
                    double score = Math.Max(0, gap) + (o.Box.B <= t.Box.T ? 0 : 0.5 * u);   // prefer the section above
                    if (score < bd) { bd = score; best = o; }
                }
                if (best is null) continue;
                best.Entries.AddRange(t.Entries); best.Box = best.Box.Union(t.Box); SortReading(best.Entries, u);
                form.Sections.RemoveAt(i); i--;
            }

        // a note that merely repeats the screen / page / a section title is noise
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Clean(screen.Name), form.PageTitle };
        foreach (var fs in form.Sections) if (fs.HasTitle) titles.Add(fs.Title);
        titles.Remove("");
        foreach (var fs in form.Sections)
            fs.Entries.RemoveAll(e =>
            {
                if (e.Kind != FormEntryKind.Note || !titles.Contains(Clean(e.Text))) return false;
                if (e.Labels is not null) foreach (var o in e.Labels) ((L)o).Cat = 'd';
                return true;
            });
        form.Sections.RemoveAll(x => x.Entries.Count == 0);

        // reading order of sections: top-to-bottom in bands of two rows, then left-to-right
        form.Sections.Sort((a, b) =>
        {
            int ra = (int)Math.Floor(a.Box.T / (2 * u)), rb = (int)Math.Floor(b.Box.T / (2 * u));
            int c = ra.CompareTo(rb);
            if (c != 0) return c;
            c = a.Box.L.CompareTo(b.Box.L);
            return c != 0 ? c : a.Order.CompareTo(b.Order);
        });
        // consecutive untitled clusters read better as one block (the tiles flow anyway)
        for (int i = form.Sections.Count - 1; i > 0; i--)
        {
            var a = form.Sections[i - 1]; var b = form.Sections[i];
            if (!a.HasTitle && !b.HasTitle && a.Entries.Count + b.Entries.Count <= 60)
            { a.Entries.AddRange(b.Entries); a.Box = a.Box.Union(b.Box); SortReading(a.Entries, u); form.Sections.RemoveAt(i); }
        }

        st.Sections = form.Sections.Count;
        foreach (var s in form.Sections)
            foreach (var e in s.Entries)
                if (e.Kind != FormEntryKind.Note) { st.Placed++; if (e.ReadingSlot >= 0) { st.Placed++; st.ReadingsMerged++; } }
        foreach (var l in labels)
            switch (l.Cat)
            {
                case 'd': st.LabelsDecor++; break;
                case 'p': st.LabelsPaired++; break;
                case 'h': st.LabelsHint++; break;
                case 't': st.LabelsTitle++; break;
                case 'f': st.LabelsFrame++; break;
                case 'P': st.LabelsPageTitle++; break;
                case 'n': st.LabelsNote++; break;
            }
        return form;
    }

    /// <summary>Notes sharing a text row ("label | = 1 | explanation") become one line.</summary>
    private static IEnumerable<FormEntry> NoteRows(List<L> notes, double u)
    {
        foreach (var n in notes) n.Cat = 'n';
        var sorted = notes.OrderBy(n => n.Box.T).ToList();
        var rows = new List<List<L>>();
        foreach (var n in sorted)
        {
            var row = rows.Count > 0 ? rows[^1] : null;
            if (row is not null && Math.Abs(n.Box.CY - row[0].Box.CY) <= 0.5 * Math.Max(u, row[0].Box.H)) row.Add(n); else rows.Add(new List<L> { n });
        }
        foreach (var r in rows)
        {
            r.Sort((a, b) => a.Box.L.CompareTo(b.Box.L));
            var box = r[0].Box; foreach (var x in r) box = box.Union(x.Box);
            yield return new FormEntry { Kind = FormEntryKind.Note, Text = string.Join("   ", r.Select(x => x.Text)), Box = box, Labels = r.Cast<object>().ToList() };
        }
    }

    private static Sec? Innermost(List<Sec> containers, Box b)
    {
        Sec? c = null;
        foreach (var s in containers) if (s.Box.ContainsCentre(b) && (c is null || s.Box.Area < c.Box.Area)) c = s;
        return c;
    }

    private static Sec? NearestHeader(List<Sec> hdrs, Box b, double u, bool wide)
    {
        Sec? best = null; double bs = double.MaxValue;
        foreach (var s in hdrs)
        {
            if (s.Anchor.T > b.T + 0.3 * u) continue;
            double ov = Box.HOverlap(s.Anchor, b);
            if (ov < 0.4 * Math.Min(b.W, s.Anchor.W) && (wide || ov < 0.4 * b.W)) continue;
            double gap = b.T - Math.Max(s.Anchor.B, s.LastBottom);
            if (gap > 2.5 * u) continue;
            double score = Math.Max(0, b.T - s.Anchor.B) / u + (1 - Math.Min(1, ov / Math.Max(1, b.W))) * 3 + Math.Abs(s.Anchor.L - b.L) / (8 * u);
            if (score < bs) { bs = score; best = s; }
        }
        return best;
    }

    private static string Clean(string t) => t.Trim().TrimEnd(':').Trim();

    private static double Unit(Screen screen, List<W> ws)
    {
        var hs = ws.Where(w => w.Kind != FormEntryKind.Button).Select(w => w.Box.H).ToList();
        if (hs.Count == 0) hs = ws.Select(w => w.Box.H).ToList();
        if (hs.Count == 0) hs = screen.Labels.Select(l => Box.From(l.Bounds).H).ToList();
        if (hs.Count == 0) return 20;
        hs.Sort();
        return Math.Max(1, hs[hs.Count / 2]);
    }

    /// <summary>&gt;= 0: lower is a better caption candidate. Negative: not a candidate.</summary>
    private static double PairScore(L l, W w, double u, Index all)
    {
        var a = l.Box; var b = w.Box;
        // label lying on the control
        double inter = Math.Max(0, Box.HOverlap(a, b)) * Math.Max(0, Box.VOverlap(a, b));
        if (inter > 0.4 * Math.Min(a.Area, b.Area) && a.W <= b.W * 1.4) return 0.05 + Math.Abs(a.L - b.L) / (50 * u);
        // left of the control, same row
        double vo = Box.VOverlap(a, b);
        double gx = b.L - a.R;
        if (vo >= 0.5 * Math.Min(a.H, b.H) && gx >= -0.35 * u && gx <= 1.5 * u && (Math.Abs(a.T - b.T) <= 0.5 * u || Math.Abs(a.CY - b.CY) <= 0.4 * u))
            return 0.1 + Math.Max(0, gx) / u + 0.3 * Math.Abs(a.CY - b.CY) / u;
        // above the control
        double ho = Box.HOverlap(a, b);
        double gy = b.T - a.B;
        if (ho >= 0.5 * Math.Min(a.W, b.W) && gy >= -0.35 * u && gy <= 1.0 * u && a.W <= b.W * 1.35 && !l.Banner && StackBelow(a, all, u) < 2)
            return 0.8 + Math.Max(0, gy) / u * 1.2 + Math.Abs(a.L - b.L) / (20 * u);
        return -1;
    }

    /// <summary>How many display/input controls sit stacked under the label (a column heading rather than one caption).</summary>
    private static int StackBelow(Box l, Index all, double u)
    {
        int n = 0;
        foreach (var w in all.Band(l.B - 0.35 * u, l.B + 6 * u))
        {
            if (w.Kind == FormEntryKind.Button) continue;
            if (w.Box.T >= l.B - 0.35 * u && w.Box.T <= l.B + 6 * u && Box.HOverlap(l, w.Box) >= 0.5 * Math.Min(l.W, w.Box.W)) n++;
        }
        return n;
    }

    private static FormEntry MakeEntry(W w)
    {
        var e = new FormEntry { Kind = w.Kind, Index = w.Index, RawName = w.Name, Box = w.Box, ReadingSlot = w.ReadingSlot };
        if (w.Kind == FormEntryKind.Button) { e.Caption = w.Name.Trim().Length > 0 ? w.Name.Trim() : "Button"; e.CaptionFromLabel = true; }
        else if (!string.IsNullOrWhiteSpace(w.Label)) { e.Caption = w.Label!; e.CaptionFromLabel = true; }
        else e.Caption = Pretty(w.Name);
        e.Hints.AddRange(w.Hints);
        return e;
    }

    private static void MergeReadings(List<W> ws, double u)
    {
        var displays = ws.Where(x => x.Kind == FormEntryKind.Display).ToList();
        if (displays.Count == 0) return;
        var di = new Index(displays);
        foreach (var inp in ws.Where(x => x.Kind == FormEntryKind.Input))
        {
            W? best = null; double bd = double.MaxValue;
            foreach (var d in di.Band(inp.Box.T, inp.Box.B))
            {
                if (d.Merged || !string.Equals(d.Name, inp.Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (Box.VOverlap(d.Box, inp.Box) < 0.5 * Math.Min(d.Box.H, inp.Box.H)) continue;
                double dx = d.Box.CX < inp.Box.CX ? inp.Box.L - d.Box.R : d.Box.L - inp.Box.R;
                if (dx > 80 * u) continue;
                if (dx < bd) { bd = dx; best = d; }
            }
            if (best is null) continue;
            best.Merged = true; inp.ReadingSlot = best.Index;
            inp.Box = inp.Box.Union(best.Box);
            inp.OwnCaption |= best.OwnCaption;
        }
    }

    private static void SortReading(List<FormEntry> entries, double u)
    {
        if (entries.Count < 2) return;
        entries.Sort((a, b) => a.Box.T.CompareTo(b.Box.T));
        // group into rows: entries whose top lies within 0.6u of the row's first entry
        var rows = new List<List<FormEntry>>();
        foreach (var e in entries)
        {
            if (rows.Count > 0 && e.Box.T - rows[^1][0].Box.T <= 0.6 * u) rows[^1].Add(e); else rows.Add(new List<FormEntry> { e });
        }
        entries.Clear();
        foreach (var r in rows) { r.Sort((a, b) => a.Box.L.CompareTo(b.Box.L)); entries.AddRange(r); }
    }
}
