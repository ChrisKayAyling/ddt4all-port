using System.Text.Json.Serialization;

namespace Ddt4All.Core.Loading;

// Wire DTOs for the internal JSON format (identical keys to the Python dumpJson output).
// Everything is nullable so that missing keys keep the model defaults, like the Python loader.

internal sealed class EcuFileDto
{
    [JsonPropertyName("autoidents")] public List<AutoIdentDto>? AutoIdents { get; set; }
    [JsonPropertyName("ecuname")] public string? EcuName { get; set; }
    [JsonPropertyName("obd")] public ObdDto? Obd { get; set; }
    [JsonPropertyName("endian")] public string? Endian { get; set; }
    [JsonPropertyName("devices")] public List<DeviceDto>? Devices { get; set; }
    [JsonPropertyName("requests")] public List<RequestDto>? Requests { get; set; }
    [JsonPropertyName("data")] public Dictionary<string, DataDto>? Data { get; set; }
}

internal sealed class AutoIdentDto
{
    [JsonPropertyName("diagversion")] public string? DiagVersion { get; set; }
    [JsonPropertyName("supplier")] public string? Supplier { get; set; }
    [JsonPropertyName("soft")] public string? Soft { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
}

internal sealed class ObdDto
{
    [JsonPropertyName("protocol")] public string? Protocol { get; set; }
    [JsonPropertyName("send_id")] public string? SendId { get; set; }
    [JsonPropertyName("recv_id")] public string? RecvId { get; set; }
    [JsonPropertyName("baudrate")] public int? BaudRate { get; set; }
    [JsonPropertyName("fastinit")] public bool? FastInit { get; set; }
    [JsonPropertyName("funcaddr")] public string? FuncAddr { get; set; }
    [JsonPropertyName("funcname")] public string? FuncName { get; set; }
    [JsonPropertyName("kw1")] public string? Kw1 { get; set; }
    [JsonPropertyName("kw2")] public string? Kw2 { get; set; }
}

internal sealed class DeviceDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("dtc")] public int? Dtc { get; set; }
    [JsonPropertyName("dtctype")] public int? DtcType { get; set; }
    [JsonPropertyName("devicedata")] public Dictionary<string, string>? DeviceData { get; set; }
}

internal sealed class DataItemDto
{
    [JsonPropertyName("firstbyte")] public int? FirstByte { get; set; }
    [JsonPropertyName("bitoffset")] public int? BitOffset { get; set; }
    [JsonPropertyName("ref")] public bool? Ref { get; set; }
    [JsonPropertyName("endian")] public string? Endian { get; set; }
}

internal sealed class RequestDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("minbytes")] public int? MinBytes { get; set; }
    [JsonPropertyName("shiftbytescount")] public int? ShiftBytesCount { get; set; }
    [JsonPropertyName("replybytes")] public string? ReplyBytes { get; set; }
    [JsonPropertyName("manualsend")] public bool? ManualSend { get; set; }
    [JsonPropertyName("sentbytes")] public string? SentBytes { get; set; }
    [JsonPropertyName("deny_sds")] public List<string>? DenySds { get; set; }
    [JsonPropertyName("sendbyte_dataitems")] public Dictionary<string, DataItemDto>? SendByteDataItems { get; set; }
    [JsonPropertyName("receivebyte_dataitems")] public Dictionary<string, DataItemDto>? ReceiveByteDataItems { get; set; }
}

internal sealed class DataDto
{
    [JsonPropertyName("bitscount")] public int? BitsCount { get; set; }
    [JsonPropertyName("bytescount")] public int? BytesCount { get; set; }
    [JsonPropertyName("bytesascii")] public bool? BytesAscii { get; set; }
    [JsonPropertyName("scaled")] public bool? Scaled { get; set; }
    [JsonPropertyName("signed")] public bool? Signed { get; set; }
    [JsonPropertyName("byte")] public bool? Byte { get; set; }
    [JsonPropertyName("binary")] public bool? Binary { get; set; }
    [JsonPropertyName("step")] public double? Step { get; set; }
    [JsonPropertyName("offset")] public double? Offset { get; set; }
    [JsonPropertyName("divideby")] public double? DivideBy { get; set; }
    [JsonPropertyName("format")] public string? Format { get; set; }
    [JsonPropertyName("unit")] public string? Unit { get; set; }
    [JsonPropertyName("comment")] public string? Comment { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("lists")] public Dictionary<string, string>? Lists { get; set; }
}

// db.json / layout DTOs are in their own files; one shared source-generated context.
[JsonSourceGenerationOptions(
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(EcuFileDto))]
[JsonSerializable(typeof(Layout.LayoutFileDto))]
internal sealed partial class CoreJsonContext : JsonSerializerContext
{
}
