using Ddt4All.Core.Ecu;

namespace Ddt4All.Core.Editing;

/// <summary>Mutation helpers for ECU definition editors (deep copy, rename with reference fix-up, new ECU templates).</summary>
public static class EcuEditing
{
    /// <summary>Deep copy of an ECU definition (requests, data, devices, idents). References are resolved in the copy.</summary>
    public static EcuFile Clone(this EcuFile src)
    {
        var f = new EcuFile
        {
            EcuName = src.EcuName, Endianness = src.Endianness, Protocol = src.Protocol, SendId = src.SendId, RecvId = src.RecvId,
            BaudRate = src.BaudRate, FastInit = src.FastInit, Kw1 = src.Kw1, Kw2 = src.Kw2, FuncName = src.FuncName, FuncAddr = src.FuncAddr,
        };
        f.Projects.AddRange(src.Projects);
        f.AutoIdents.AddRange(src.AutoIdents);
        foreach (var d in src.Data) f.Data.Add(d.Clone());
        foreach (var r in src.Requests) f.Requests.Add(r.Clone());
        foreach (var d in src.Devices)
        {
            var nd = new EcuDevice { Name = d.Name, Dtc = d.Dtc, DtcType = d.DtcType };
            foreach (var kv in d.DeviceData) nd.DeviceData[kv.Key] = kv.Value;
            f.Devices.Add(nd);
        }

        f.ResolveReferences();
        return f;
    }

    /// <summary>Deep copy of a data definition.</summary>
    public static EcuData Clone(this EcuData d)
    {
        var n = new EcuData
        {
            Name = d.Name, Description = d.Description, Comment = d.Comment, BitsCount = d.BitsCount, BytesCount = d.BytesCount,
            Scaled = d.Scaled, Signed = d.Signed, Byte = d.Byte, Binary = d.Binary, BytesAscii = d.BytesAscii,
            Step = d.Step, Offset = d.Offset, DivideBy = d.DivideBy, Format = d.Format, Unit = d.Unit,
        };
        foreach (var kv in d.Lists) n.AddListItem(kv.Key, kv.Value);
        return n;
    }

    /// <summary>Deep copy of a data item.</summary>
    public static DataItem Clone(this DataItem i) => new(i.Name, i.FirstByte, i.BitOffset, i.Endian, i.Ref);

    /// <summary>Deep copy of a request (the copy has no owning file until <see cref="EcuFile.ResolveReferences"/>).</summary>
    public static EcuRequest Clone(this EcuRequest r)
    {
        var n = new EcuRequest
        {
            Name = r.Name, MinBytes = r.MinBytes, ShiftBytesCount = r.ShiftBytesCount, ReplyBytes = r.ReplyBytes,
            ManualSend = r.ManualSend, SentBytes = r.SentBytes, Denied = r.Denied,
        };
        foreach (var i in r.SendItems) n.SendItems.Add(i.Clone());
        foreach (var i in r.ReceiveItems) n.ReceiveItems.Add(i.Clone());
        return n;
    }

    /// <summary>An empty but valid ECU (CAN, 500 kbit/s, ids 7E0/7E8) with one read request.</summary>
    public static EcuFile NewEcu(string name = "NEW_ECU")
    {
        var f = new EcuFile { EcuName = name, Protocol = EcuProtocol.Can, SendId = "7E0", RecvId = "7E8", BaudRate = 500000, FuncName = "Engine", FuncAddr = "00" };
        f.Data.Add(new EcuData { Name = "Identification", BitsCount = 16, BytesCount = 2 });
        var r = new EcuRequest { Name = "ReadIdentification", SentBytes = "22F190", MinBytes = 5, ReplyBytes = "62F1900000" };
        r.ReceiveItems.Add(new DataItem("Identification", 4));
        f.Requests.Add(r);
        f.ResolveReferences();
        return f;
    }

    /// <summary>Renames a data definition and every request item that references it. False if the new name is taken/empty.</summary>
    public static bool RenameData(this EcuFile f, string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || !f.Data.TryGetValue(oldName, out var d)) return false;
        if (oldName == newName) return true;
        if (f.Data.Contains(newName)) return false;
        d.Name = newName;
        f.Data.Reindex();
        foreach (var r in f.Requests)
        {
            Rename(r.SendItems, oldName, newName);
            Rename(r.ReceiveItems, oldName, newName);
        }

        f.ResolveReferences();
        return true;
    }

    /// <summary>Renames a request. False if the new name is taken/empty.</summary>
    public static bool RenameRequest(this EcuFile f, string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || !f.Requests.TryGetValue(oldName, out var r)) return false;
        if (oldName == newName) return true;
        if (f.Requests.Contains(newName)) return false;
        r.Name = newName;
        f.Requests.Reindex();
        return true;
    }

    private static void Rename(NamedCollection<DataItem> items, string oldName, string newName)
    {
        if (!items.TryGetValue(oldName, out var it)) return;
        it.Name = newName;
        items.Reindex();
    }

    /// <summary>Removes a data definition. With <paramref name="cascade"/> its request items are removed too, otherwise it is refused while referenced.</summary>
    public static bool RemoveData(this EcuFile f, string name, bool cascade = true)
    {
        if (!f.Data.Contains(name)) return false;
        if (!cascade && f.DataUsage(name).Count > 0) return false;
        f.Data.Remove(name);
        foreach (var r in f.Requests) { r.SendItems.Remove(name); r.ReceiveItems.Remove(name); }
        f.ResolveReferences();
        return true;
    }

    /// <summary>Names of the requests that reference a data definition.</summary>
    public static List<string> DataUsage(this EcuFile f, string dataName)
    {
        var l = new List<string>();
        foreach (var r in f.Requests)
            if (r.SendItems.Contains(dataName) || r.ReceiveItems.Contains(dataName)) l.Add(r.Name);
        return l;
    }

    /// <summary>Unique name based on <paramref name="baseName"/> (appends _2, _3...).</summary>
    public static string UniqueName(Func<string, bool> exists, string baseName)
    {
        if (!exists(baseName)) return baseName;
        for (int i = 2; ; i++)
        {
            var n = baseName + "_" + i;
            if (!exists(n)) return n;
        }
    }

    /// <summary>Sets the width of a data definition keeping bits/bytes consistent (bits = rounded up to whole bytes of the field).</summary>
    public static void SetBits(this EcuData d, int bits)
    {
        d.BitsCount = Math.Max(1, bits);
        d.BytesCount = (d.BitsCount + 7) / 8;
    }

    /// <summary>Sets the width in whole bytes.</summary>
    public static void SetBytes(this EcuData d, int bytes)
    {
        d.BytesCount = Math.Max(1, bytes);
        d.BitsCount = d.BytesCount * 8;
    }

    /// <summary>Removes one enumeration entry.</summary>
    public static void RemoveListItem(this EcuData d, long value)
    {
        if (d.Lists.Remove(value, out var text)) d.Items.Remove(text);
    }

    /// <summary>Replaces the enumeration (keeps Lists and Items in sync).</summary>
    public static void SetList(this EcuData d, IEnumerable<KeyValuePair<long, string>> entries)
    {
        d.Lists.Clear();
        d.Items.Clear();
        foreach (var kv in entries) d.AddListItem(kv.Key, kv.Value);
    }
}
