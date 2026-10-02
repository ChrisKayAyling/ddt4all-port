using Ddt4All.Core.Abstractions;

namespace Ddt4All.Core.Ecu;

/// <summary>
/// A fully parsed ECU definition: connection parameters, requests, data definitions and DTC devices.
/// Create with <see cref="Loading.EcuXmlLoader"/> / <see cref="Loading.EcuJsonLoader"/> / <see cref="Load(string)"/>.
/// </summary>
public sealed class EcuFile
{
    /// <summary>ECU name (Target/@Name).</summary>
    public string EcuName { get; set; } = "";
    /// <summary>Default byte order for all data of this ECU (Requests/@Endian).</summary>
    public ByteOrder Endianness { get; set; }
    /// <summary>Diagnostic protocol.</summary>
    public EcuProtocol Protocol { get; set; }
    /// <summary>CAN send (tester to ECU) id as upper-case hex without padding ("7E0"); "00" if absent.</summary>
    public string SendId { get; set; } = "00";
    /// <summary>CAN receive (ECU to tester) id as hex; "00" if absent.</summary>
    public string RecvId { get; set; } = "00";
    /// <summary>CAN baud rate (0 if absent).</summary>
    public int BaudRate { get; set; }
    /// <summary>KWP2000 fast init (vs. 5-baud init).</summary>
    public bool FastInit { get; set; }
    /// <summary>KWP/ISO8 key word 1 as hex ("" if absent).</summary>
    public string Kw1 { get; set; } = "";
    /// <summary>KWP/ISO8 key word 2 as hex ("" if absent).</summary>
    public string Kw2 { get; set; } = "";
    /// <summary>Functional group name ("Injection").</summary>
    public string FuncName { get; set; } = "";
    /// <summary>Functional (diagnostic) address as 2-digit upper-case hex.</summary>
    public string FuncAddr { get; set; } = "00";
    /// <summary>Project codes this ECU is used in (XML only).</summary>
    public List<string> Projects { get; } = new();
    /// <summary>Auto-identification records.</summary>
    public List<AutoIdent> AutoIdents { get; } = new();

    /// <summary>Requests in definition order.</summary>
    public NamedCollection<EcuRequest> Requests { get; } = new();
    /// <summary>Data definitions in definition order.</summary>
    public NamedCollection<EcuData> Data { get; } = new();
    /// <summary>DTC devices in definition order.</summary>
    public NamedCollection<EcuDevice> Devices { get; } = new();

    /// <summary>True if the ECU is little-endian by default.</summary>
    public bool IsLittleEndian => Endianness == ByteOrder.Little;

    /// <summary>Loads an ECU file by extension (.xml, otherwise JSON).</summary>
    public static EcuFile Load(string path) => Loading.EcuLoader.LoadFile(path);

    /// <summary>Request lookup: exact name first, then case-insensitive (like <c>get_request</c>).</summary>
    public EcuRequest? GetRequest(string name)
    {
        if (Requests.TryGetValue(name, out var r)) return r;
        foreach (var q in Requests)
            if (string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase)) return q;
        return null;
    }

    /// <summary>Data lookup by exact name.</summary>
    public EcuData? GetData(string name) => Data.TryGetValue(name, out var d) ? d : null;

    /// <summary>Connects every <see cref="DataItem"/> to its <see cref="EcuData"/> and back-links requests. Called by the loaders.</summary>
    public void ResolveReferences()
    {
        foreach (var req in Requests)
        {
            req.File = this;
            foreach (var di in req.SendItems) di.Data = Data.TryGetValue(di.Name, out var d) ? d : null;
            foreach (var di in req.ReceiveItems) di.Data = Data.TryGetValue(di.Name, out var d) ? d : null;
        }
    }

    /// <summary>Serialises to the internal JSON format (same schema as Python <c>dumpJson</c>).</summary>
    public string DumpJson() => Loading.EcuJsonDumper.Dump(this);

    /// <summary>Builds and sends a request, then decodes the reply. Convenience for <see cref="EcuRequest.SendAsync"/>.</summary>
    public ValueTask<EcuResponse> SendAsync(IEcuTransport transport, string requestName,
        IEnumerable<KeyValuePair<string, string>>? inputs = null, CancellationToken ct = default)
    {
        var req = GetRequest(requestName) ?? throw new EcuRequestException($"Request '{requestName}' does not exist");
        return req.SendAsync(transport, inputs, ct);
    }
}
