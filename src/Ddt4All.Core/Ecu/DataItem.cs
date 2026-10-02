namespace Ddt4All.Core.Ecu;

/// <summary>
/// Placement of a named <see cref="EcuData"/> inside a request (sent) or response (received) frame.
/// <see cref="FirstByte"/> is 1-based and counts the service id byte as byte 1.
/// </summary>
public sealed class DataItem : INamed
{
    /// <summary>Name of the referenced <see cref="EcuData"/>.</summary>
    public string Name { get; set; } = "";
    /// <summary>1-based first byte in the frame (0 = unset).</summary>
    public int FirstByte { get; set; }
    /// <summary>Bit offset inside the first byte.</summary>
    public int BitOffset { get; set; }
    /// <summary>"Ref" flag of the definition (kept for round-tripping).</summary>
    public bool Ref { get; set; }
    /// <summary>Per-item byte order override (<see cref="ByteOrder.Default"/> = inherit from the ECU file).</summary>
    public ByteOrder Endian { get; set; }

    /// <summary>
    /// Resolved data definition (set by <see cref="EcuFile.ResolveReferences"/>); null when the data is missing.
    /// </summary>
    public EcuData? Data { get; internal set; }

    /// <summary>Creates an item.</summary>
    public DataItem() { }

    /// <summary>Creates an item.</summary>
    public DataItem(string name, int firstByte, int bitOffset = 0, ByteOrder endian = ByteOrder.Default, bool isRef = false)
    {
        Name = name; FirstByte = firstByte; BitOffset = bitOffset; Endian = endian; Ref = isRef;
    }

    /// <summary>Effective endianness (item override wins, otherwise the ECU file default).</summary>
    public bool IsLittleEndian(ByteOrder ecuOrder) =>
        Endian == ByteOrder.Little || (Endian == ByteOrder.Default && ecuOrder == ByteOrder.Little);

    /// <inheritdoc />
    public override string ToString() => $"{Name} @{FirstByte}.{BitOffset}";
}
