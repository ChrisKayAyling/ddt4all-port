using Ddt4All.Core.Abstractions;
using Ddt4All.Core.Codec;

namespace Ddt4All.Core.Ecu;

/// <summary>Diagnostic session types that are denied access to a request (the "DenyAccess" element).</summary>
[Flags]
public enum SessionAccess : byte
{
    /// <summary>Nothing denied.</summary>
    None = 0,
    /// <summary>NoSDS.</summary>
    NoSds = 1,
    /// <summary>Plant.</summary>
    Plant = 2,
    /// <summary>AfterSales.</summary>
    AfterSales = 4,
    /// <summary>Engineering.</summary>
    Engineering = 8,
    /// <summary>Supplier.</summary>
    Supplier = 16,
}

/// <summary>One decoded value of a response.</summary>
public readonly record struct DecodedValue(DataItem Item, string? Text)
{
    /// <summary>Data name.</summary>
    public string Name => Item.Name;
}

/// <summary>Result of sending a request / decoding a reply.</summary>
public sealed class EcuResponse
{
    private static readonly IReadOnlyList<DecodedValue> NoValues = Array.Empty<DecodedValue>();

    /// <summary>The complete reply payload as received.</summary>
    public byte[] Raw { get; init; } = Array.Empty<byte>();
    /// <summary>True when the reply is a negative response (starts with 0x7F).</summary>
    public bool IsNegative { get; init; }
    /// <summary>Rejected service id (negative responses only).</summary>
    public byte? RejectedServiceId { get; init; }
    /// <summary>Negative response code (negative responses only; null if the reply was truncated).</summary>
    public byte? NegativeResponseCode { get; init; }
    /// <summary>Human readable NRC description ("Unregistered error" for unknown codes).</summary>
    public string? NegativeResponseText { get; init; }
    /// <summary>True for NRC 0x78 (response pending): the caller should keep waiting for the real answer.</summary>
    public bool IsResponsePending => IsNegative && NegativeResponseCode == NegativeResponses.ResponsePending;
    /// <summary>True for a non-empty, non-negative reply.</summary>
    public bool IsPositive => !IsNegative && Raw.Length > 0;
    /// <summary>Decoded values in receive-item order (empty for negative/empty replies).</summary>
    public IReadOnlyList<DecodedValue> Values { get; init; } = NoValues;

    /// <summary>Looks up a decoded value text by data name.</summary>
    public bool TryGetValue(string name, out string? text)
    {
        foreach (var v in Values)
            if (v.Name == name) { text = v.Text; return true; }
        text = null;
        return false;
    }

    /// <summary>Decoded text by data name or null.</summary>
    public string? this[string name] => TryGetValue(name, out var t) ? t : null;
}

/// <summary>
/// A request/response pair of the ECU definition. Builds request payloads from user inputs and decodes replies.
/// </summary>
public sealed class EcuRequest : INamed
{
    /// <summary>Request name.</summary>
    public string Name { get; set; } = "";
    /// <summary>Minimum reply length in bytes.</summary>
    public int MinBytes { get; set; }
    /// <summary>ShiftBytesCount of the definition.</summary>
    public int ShiftBytesCount { get; set; }
    /// <summary>Default (simulation) reply as hex text.</summary>
    public string ReplyBytes { get; set; } = "";
    /// <summary>True for "ManuelSend" requests (only sent on explicit user action).</summary>
    public bool ManualSend { get; set; }
    /// <summary>Request payload template as hex text ("22F190").</summary>
    public string SentBytes { get; set; } = "";
    /// <summary>Session types that are denied access (default: none).</summary>
    public SessionAccess Denied { get; set; }
    /// <summary>Items placed into the sent frame (inputs), in definition order.</summary>
    public NamedCollection<DataItem> SendItems { get; } = new();
    /// <summary>Items read from the reply, in definition order.</summary>
    public NamedCollection<DataItem> ReceiveItems { get; } = new();
    /// <summary>Owning ECU file (set by the loaders / <see cref="EcuFile.ResolveReferences"/>).</summary>
    public EcuFile? File { get; internal set; }

    private byte[]? _template;
    private string? _templateSource;

    /// <summary>Names of the input data items (like <c>get_data_inputs</c>).</summary>
    public IEnumerable<string> InputNames => SendItems.Select(i => i.Name);

    /// <summary>Request template as bytes; null if <see cref="SentBytes"/> is not valid hex.</summary>
    public ReadOnlySpan<byte> GetSentBytesTemplate()
    {
        if (!ReferenceEquals(_templateSource, SentBytes) || _template == null)
        {
            _template = HexUtil.Parse(SentBytes);
            _templateSource = SentBytes;
        }

        return _template;
    }

    private ByteOrder EcuOrder => File?.Endianness ?? ByteOrder.Default;

    // ------------------------------------------------------------------ build

    /// <summary>Sets one input value into <paramref name="frame"/> (a copy of the template). List texts are resolved to raw values.</summary>
    public bool TrySetInput(Span<byte> frame, string name, string value, out string? error)
    {
        error = null;
        if (!SendItems.TryGetValue(name, out var item))
        { error = $"Data item '{name}' does not exist in request '{Name}'"; return false; }
        var data = item.Data ?? File?.GetData(name);
        if (data == null)
        { error = $"Data '{name}' does not exist"; return false; }

        if (data.Items.TryGetValue(value, out long raw) && !data.BytesAscii)
        {
            // list text selected
            if (data.BitsCount <= 64 && raw >= 0)
            {
                if (!BitCodec.TryWriteUInt64(frame, item.FirstByte, item.BitOffset, data.BitsCount, item.IsLittleEndian(EcuOrder), (ulong)raw))
                { error = $"Cannot store list value for '{name}'"; return false; }
                return true;
            }
        }

        if (!data.TrySetValue(frame, item, EcuOrder, value, out error))
        {
            error = $"{name}: {error}";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Builds the request payload from the template and the given inputs (data name to value text).
    /// Throws <see cref="EcuRequestException"/> for unknown items or invalid values.
    /// </summary>
    public byte[] BuildRequest(IEnumerable<KeyValuePair<string, string>>? inputs = null)
    {
        if (!TryBuildRequest(inputs, out var req, out var error))
            throw new EcuRequestException(error!);
        return req!;
    }

    /// <summary>Non-throwing variant of <see cref="BuildRequest"/>.</summary>
    public bool TryBuildRequest(IEnumerable<KeyValuePair<string, string>>? inputs, out byte[]? request, out string? error)
    {
        request = null;
        error = null;
        var tpl = GetSentBytesTemplate();
        if (tpl.IsEmpty && SentBytes.Length > 0)
        { error = $"SentBytes of '{Name}' is not valid hex"; return false; }

        var frame = tpl.ToArray();
        if (inputs != null)
        {
            foreach (var kv in inputs)
                if (!TrySetInput(frame, kv.Key, kv.Value, out error)) return false;
        }

        request = frame;
        return true;
    }

    // ------------------------------------------------------------------ decode

    /// <summary>Decodes one value of a reply (null if the frame is too short or the data is undefined).</summary>
    public string? DecodeValue(ReadOnlySpan<byte> response, DataItem item)
    {
        var data = item.Data ?? File?.GetData(item.Name);
        return data?.GetDisplayValue(response, item, EcuOrder);
    }

    /// <summary>Numeric (allocation-free) decode of one received item, see <see cref="EcuData.TryGetNumeric"/>.</summary>
    public bool TryDecodeNumeric(ReadOnlySpan<byte> response, DataItem item, out double value)
    {
        value = 0;
        var data = item.Data ?? File?.GetData(item.Name);
        return data != null && data.TryGetNumeric(response, item, EcuOrder, out value);
    }

    /// <summary>
    /// Decodes a reply: negative responses (0x7F) are reported with their NRC description; positive replies
    /// are decoded for every receive item (like <c>get_values_from_stream</c>).
    /// </summary>
    public EcuResponse DecodeResponse(byte[] response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Length > 0 && response[0] == NegativeResponses.NegativeResponseSid)
        {
            byte? sid = response.Length > 1 ? response[1] : null;
            byte? nrc = response.Length > 2 ? response[2] : null;
            return new EcuResponse
            {
                Raw = response,
                IsNegative = true,
                RejectedServiceId = sid,
                NegativeResponseCode = nrc,
                NegativeResponseText = nrc is { } n ? NegativeResponses.Describe(n) : "Unregistered error",
            };
        }

        if (response.Length == 0) return new EcuResponse { Raw = response };

        var values = new DecodedValue[ReceiveItems.Count];
        for (int i = 0; i < values.Length; i++)
        {
            var item = ReceiveItems[i];
            values[i] = new DecodedValue(item, DecodeValue(response, item));
        }

        return new EcuResponse { Raw = response, Values = values };
    }

    /// <summary>Builds the request, sends it through <paramref name="transport"/> and decodes the reply.</summary>
    public async ValueTask<EcuResponse> SendAsync(IEcuTransport transport,
        IEnumerable<KeyValuePair<string, string>>? inputs = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var payload = BuildRequest(inputs);
        var reply = await transport.RequestAsync(payload, ct).ConfigureAwait(false);
        return DecodeResponse(reply);
    }
}
