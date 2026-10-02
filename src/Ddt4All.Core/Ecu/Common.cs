using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Ddt4All.Core.Ecu;

/// <summary>Byte order selector. <see cref="Default"/> means "not specified" (treated as big-endian).</summary>
public enum ByteOrder : byte
{
    /// <summary>Not specified in the file (big-endian behaviour).</summary>
    Default = 0,
    /// <summary>"Little" in the definition files.</summary>
    Little = 1,
    /// <summary>"Big" in the definition files.</summary>
    Big = 2,
}

/// <summary>Diagnostic transport protocol of an ECU definition.</summary>
public enum EcuProtocol : byte
{
    /// <summary>Not specified.</summary>
    None = 0,
    /// <summary>ISO 15765 CAN (DiagOnCAN).</summary>
    Can,
    /// <summary>KWP2000 (fast init or 5 baud).</summary>
    Kwp2000,
    /// <summary>ISO 9141 (ISO8).</summary>
    Iso8,
    /// <summary>ISO (legacy label).</summary>
    Iso,
    /// <summary>DoIP (ISO 13400).</summary>
    DoIp,
    /// <summary>Any other string found in a file.</summary>
    Unknown,
}

/// <summary>Conversions between <see cref="EcuProtocol"/> and the strings used in the data files.</summary>
public static class EcuProtocolNames
{
    /// <summary>Wire name as written by the Python dumper ("CAN", "KWP2000", "ISO8", "ISO", "DOIP", "").</summary>
    public static string ToWire(this EcuProtocol p) => p switch
    {
        EcuProtocol.Can => "CAN",
        EcuProtocol.Kwp2000 => "KWP2000",
        EcuProtocol.Iso8 => "ISO8",
        EcuProtocol.Iso => "ISO",
        EcuProtocol.DoIp => "DOIP",
        _ => "",
    };

    /// <summary>Exact (case-insensitive) parse used for ECU files.</summary>
    public static EcuProtocol Parse(ReadOnlySpan<char> s)
    {
        s = s.Trim();
        if (s.IsEmpty) return EcuProtocol.None;
        if (s.Equals("CAN", StringComparison.OrdinalIgnoreCase)) return EcuProtocol.Can;
        if (s.Equals("KWP2000", StringComparison.OrdinalIgnoreCase)) return EcuProtocol.Kwp2000;
        if (s.Equals("ISO8", StringComparison.OrdinalIgnoreCase)) return EcuProtocol.Iso8;
        if (s.Equals("ISO", StringComparison.OrdinalIgnoreCase)) return EcuProtocol.Iso;
        if (s.Equals("DOIP", StringComparison.OrdinalIgnoreCase)) return EcuProtocol.DoIp;
        return EcuProtocol.Unknown;
    }

    /// <summary>Substring-based classification used by the ECU database (EcuIdent): CAN, KWP, ISO8, DOIP.</summary>
    public static EcuProtocol Classify(ReadOnlySpan<char> s)
    {
        if (s.Contains("CAN", StringComparison.OrdinalIgnoreCase)) return EcuProtocol.Can;
        if (s.Contains("KWP", StringComparison.OrdinalIgnoreCase)) return EcuProtocol.Kwp2000;
        if (s.Contains("ISO8", StringComparison.OrdinalIgnoreCase)) return EcuProtocol.Iso8;
        if (s.Contains("DOIP", StringComparison.OrdinalIgnoreCase)) return EcuProtocol.DoIp;
        return EcuProtocol.Unknown;
    }

    internal static ByteOrder ParseOrder(ReadOnlySpan<char> s) =>
        s.SequenceEqual("Little") ? ByteOrder.Little : s.SequenceEqual("Big") ? ByteOrder.Big : ByteOrder.Default;

    internal static string OrderToWire(ByteOrder o) => o switch { ByteOrder.Little => "Little", ByteOrder.Big => "Big", _ => "" };
}

/// <summary>Something addressable by name in an <see cref="NamedCollection{T}"/>.</summary>
public interface INamed
{
    /// <summary>Unique (per collection) name.</summary>
    string Name { get; }
}

/// <summary>
/// Insertion-ordered, name-indexed collection (mirrors a Python dict keyed by name: adding an
/// existing name replaces the value in place). Enumerates in definition order.
/// </summary>
public sealed class NamedCollection<T> : IReadOnlyList<T> where T : class, INamed
{
    private readonly List<T> _list = new();
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    /// <summary>Number of items.</summary>
    public int Count => _list.Count;

    /// <summary>Item by position.</summary>
    public T this[int index] => _list[index];

    /// <summary>Item by exact name; throws <see cref="KeyNotFoundException"/> if absent.</summary>
    public T this[string name] => _list[_index[name]];

    /// <summary>Adds or replaces (in place) by name.</summary>
    public void Add(T item)
    {
        if (_index.TryGetValue(item.Name, out int i)) _list[i] = item;
        else { _index[item.Name] = _list.Count; _list.Add(item); }
    }

    /// <summary>Exact-name lookup.</summary>
    public bool TryGetValue(string name, [MaybeNullWhen(false)] out T value)
    {
        if (_index.TryGetValue(name, out int i)) { value = _list[i]; return true; }
        value = null;
        return false;
    }

    /// <summary>True if an item with that exact name exists.</summary>
    public bool Contains(string name) => _index.ContainsKey(name);

    /// <summary>Position of the item with that name, or -1.</summary>
    public int IndexOf(string name) => _index.TryGetValue(name, out int i) ? i : -1;

    /// <summary>Inserts at a position (a same-named item is removed first). Used by editors.</summary>
    public void Insert(int index, T item)
    {
        int old = IndexOf(item.Name);
        if (old >= 0) { _list.RemoveAt(old); if (old < index) index--; }
        _list.Insert(Math.Clamp(index, 0, _list.Count), item);
        Reindex();
    }

    /// <summary>Removes by name; returns false if absent.</summary>
    public bool Remove(string name)
    {
        if (!_index.TryGetValue(name, out int i)) return false;
        _list.RemoveAt(i);
        Reindex();
        return true;
    }

    /// <summary>Moves an item to a new position.</summary>
    public void Move(int from, int to)
    {
        if (from < 0 || from >= _list.Count) return;
        to = Math.Clamp(to, 0, _list.Count - 1);
        var it = _list[from];
        _list.RemoveAt(from);
        _list.Insert(to, it);
        Reindex();
    }

    /// <summary>Rebuilds the name index; call after changing the <c>Name</c> of an item that is in the collection.</summary>
    public void Reindex()
    {
        _index.Clear();
        for (int i = 0; i < _list.Count; i++) _index[_list[i].Name] = i;
    }

    /// <summary>Removes all items.</summary>
    public void Clear() { _list.Clear(); _index.Clear(); }

    /// <summary>Enumerator (struct, allocation free).</summary>
    public List<T>.Enumerator GetEnumerator() => _list.GetEnumerator();
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => _list.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _list.GetEnumerator();
}

/// <summary>Thrown for invalid definitions or invalid user input when building a request.</summary>
public class EcuException : Exception
{
    /// <summary>Creates the exception.</summary>
    public EcuException(string message) : base(message) { }
    /// <summary>Creates the exception.</summary>
    public EcuException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>An ECU definition could not be parsed.</summary>
public sealed class EcuFormatException : EcuException
{
    /// <summary>Creates the exception.</summary>
    public EcuFormatException(string message) : base(message) { }
    /// <summary>Creates the exception.</summary>
    public EcuFormatException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>A request could not be built (unknown data item, invalid value...).</summary>
public sealed class EcuRequestException : EcuException
{
    /// <summary>Creates the exception.</summary>
    public EcuRequestException(string message) : base(message) { }
}
