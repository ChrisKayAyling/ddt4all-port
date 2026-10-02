using Ddt4All.Core.Codec;
using Ddt4All.Core.Ecu;

namespace Ddt4All.Core.Editing;

/// <summary>Severity of a validation finding.</summary>
public enum IssueSeverity { Info, Warning, Error }

/// <summary>One validation finding. <see cref="Kind"/> is "ecu", "request", "data" or "device"; <see cref="Target"/> its name.</summary>
public readonly record struct EcuIssue(IssueSeverity Severity, string Kind, string Target, string Message);

/// <summary>Static checks of an ECU definition (things that would break loading, sending or decoding).</summary>
public static class EcuValidator
{
    /// <summary>Validates the whole file.</summary>
    public static List<EcuIssue> Validate(EcuFile f)
    {
        var l = new List<EcuIssue>();
        if (string.IsNullOrWhiteSpace(f.EcuName)) l.Add(new(IssueSeverity.Error, "ecu", "", "ECU name is empty"));
        if (f.Protocol == EcuProtocol.Can)
        {
            if (!IsHex(f.SendId)) l.Add(new(IssueSeverity.Error, "ecu", "", "CAN send id is not hexadecimal"));
            if (!IsHex(f.RecvId)) l.Add(new(IssueSeverity.Error, "ecu", "", "CAN receive id is not hexadecimal"));
            if (f.BaudRate <= 0) l.Add(new(IssueSeverity.Warning, "ecu", "", "CAN baud rate is not set"));
        }

        if (f.Requests.Count == 0) l.Add(new(IssueSeverity.Warning, "ecu", "", "The ECU has no requests"));
        foreach (var d in f.Data) ValidateData(f, d, l);
        foreach (var r in f.Requests) ValidateRequest(f, r, l);
        return l;
    }

    private static bool IsHex(string s) => s.Length > 0 && s.All(c => HexUtil.HexValue(c) >= 0);

    /// <summary>Validates one data definition.</summary>
    public static void ValidateData(EcuFile f, EcuData d, List<EcuIssue> l)
    {
        void Add(IssueSeverity s, string m) => l.Add(new(s, "data", d.Name, m));
        if (string.IsNullOrWhiteSpace(d.Name)) Add(IssueSeverity.Error, "Data name is empty");
        if (d.BitsCount <= 0) Add(IssueSeverity.Error, "Bit count must be positive");
        if (d.BytesCount != (d.BitsCount + 7) / 8) Add(IssueSeverity.Warning, "Byte count does not match the bit count");
        if (d.Scaled && d.DivideBy == 0) Add(IssueSeverity.Error, "Scaled value divides by zero");
        if (d.BytesAscii && d.BitsCount % 8 != 0) Add(IssueSeverity.Warning, "ASCII data should be a whole number of bytes");
        if (d.BitsCount > 64 && (d.Scaled || d.HasList)) Add(IssueSeverity.Warning, "Scaling and lists only work up to 64 bits");
        if (d.HasList)
        {
            var seen = new HashSet<string>();
            foreach (var t in d.Lists.Values)
                if (!seen.Add(t)) { Add(IssueSeverity.Warning, $"Duplicate list text '{t}'"); break; }
            if (d.BitsCount is > 0 and < 63)
            {
                long max = (1L << d.BitsCount) - 1;
                foreach (var k in d.Lists.Keys)
                    if (k < 0 || k > max) { Add(IssueSeverity.Warning, $"List value {k} does not fit in {d.BitsCount} bits"); break; }
            }
        }

        if (f.DataUsage(d.Name).Count == 0) Add(IssueSeverity.Info, "Not used by any request");
    }

    /// <summary>Validates one request: hex template, items inside frame, overlaps, missing data.</summary>
    public static void ValidateRequest(EcuFile f, EcuRequest r, List<EcuIssue> l)
    {
        void Add(IssueSeverity s, string m) => l.Add(new(s, "request", r.Name, m));
        if (string.IsNullOrWhiteSpace(r.Name)) Add(IssueSeverity.Error, "Request name is empty");
        var tpl = r.GetSentBytesTemplate();
        if (r.SentBytes.Length == 0) Add(IssueSeverity.Warning, "SentBytes is empty");
        else if (tpl.IsEmpty) Add(IssueSeverity.Error, "SentBytes is not valid hex");
        if (!string.IsNullOrEmpty(r.ReplyBytes) && HexUtil.Parse(r.ReplyBytes) == null) Add(IssueSeverity.Error, "ReplyBytes is not valid hex");
        CheckItems(f, r, r.SendItems, tpl.Length, "sent", Add);
        int minReply = r.MinBytes;
        CheckItems(f, r, r.ReceiveItems, minReply, "received", Add);
    }

    private static void CheckItems(EcuFile f, EcuRequest r, NamedCollection<DataItem> items, int frameLen, string kind, Action<IssueSeverity, string> add)
    {
        var ranges = new List<(long Start, long End, string Name)>();
        foreach (var it in items)
        {
            if (!f.Data.TryGetValue(it.Name, out var d)) { add(IssueSeverity.Error, $"{kind} item '{it.Name}' has no data definition"); continue; }
            if (it.FirstByte < 1) { add(IssueSeverity.Error, $"{kind} item '{it.Name}' FirstByte must be 1 or more"); continue; }
            if (it.BitOffset is < 0 or > 7) add(IssueSeverity.Error, $"{kind} item '{it.Name}' BitOffset must be 0..7");
            long start = (long)(it.FirstByte - 1) * 8 + it.BitOffset;
            long end = start + d.BitsCount;
            if (frameLen > 0 && kind == "sent" && end > (long)frameLen * 8) add(IssueSeverity.Warning, $"sent item '{it.Name}' extends beyond the {frameLen} byte template");
            if (frameLen > 0 && kind == "received" && end > (long)frameLen * 8 && frameLen >= it.FirstByte)
                add(IssueSeverity.Info, $"received item '{it.Name}' extends beyond MinBytes ({frameLen})");
            ranges.Add((start, end, it.Name));
        }

        for (int i = 0; i < ranges.Count; i++)
            for (int j = i + 1; j < ranges.Count; j++)
                if (ranges[i].Start < ranges[j].End && ranges[j].Start < ranges[i].End)
                    add(IssueSeverity.Warning, $"{kind} items '{ranges[i].Name}' and '{ranges[j].Name}' overlap");
    }
}
