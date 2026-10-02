using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ddt4All.Core.Ecu;
using Ddt4All.Core.Loading;

namespace Ddt4All.Core.Layout;

internal sealed class FlexBoolConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Number => reader.GetDouble() != 0,
            JsonTokenType.String => reader.GetString() is "1" or "true" or "True",
            _ => false,
        };

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
}

internal sealed class FlexIntConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetInt32(out int i) ? i : (int)reader.GetDouble();
            case JsonTokenType.String:
                var s = reader.GetString();
                return int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v) ? v : 0;
            default:
                return 0;
        }
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}

internal sealed class RectDto
{
    [JsonPropertyName("left")] [JsonConverter(typeof(FlexIntConverter))] public int Left { get; set; }
    [JsonPropertyName("top")] [JsonConverter(typeof(FlexIntConverter))] public int Top { get; set; }
    [JsonPropertyName("height")] [JsonConverter(typeof(FlexIntConverter))] public int Height { get; set; }
    [JsonPropertyName("width")] [JsonConverter(typeof(FlexIntConverter))] public int Width { get; set; }
}

internal sealed class FontDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("size")] public double Size { get; set; }
    [JsonPropertyName("bold")] [JsonConverter(typeof(FlexBoolConverter))] public bool Bold { get; set; }
    [JsonPropertyName("italic")] [JsonConverter(typeof(FlexBoolConverter))] public bool Italic { get; set; }
}

internal sealed class SendDto
{
    [JsonPropertyName("Delay")] [JsonConverter(typeof(FlexIntConverter))] public int Delay { get; set; }
    [JsonPropertyName("RequestName")] public string? RequestName { get; set; }
}

internal sealed class LabelDto
{
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("color")] public string? Color { get; set; }
    [JsonPropertyName("alignment")] public string? Alignment { get; set; }
    [JsonPropertyName("fontcolor")] public string? FontColor { get; set; }
    [JsonPropertyName("bbox")] public RectDto? Bbox { get; set; }
    [JsonPropertyName("font")] public FontDto? Font { get; set; }
}

internal sealed class DisplayDto
{
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("request")] public string? Request { get; set; }
    [JsonPropertyName("color")] public string? Color { get; set; }
    [JsonPropertyName("fontcolor")] public string? FontColor { get; set; }
    [JsonPropertyName("width")] [JsonConverter(typeof(FlexIntConverter))] public int Width { get; set; }
    [JsonPropertyName("rect")] public RectDto? Rect { get; set; }
    [JsonPropertyName("font")] public FontDto? Font { get; set; }
}

internal sealed class ButtonDto
{
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("uniquename")] public string? UniqueName { get; set; }
    [JsonPropertyName("rect")] public RectDto? Rect { get; set; }
    [JsonPropertyName("font")] public FontDto? Font { get; set; }
    [JsonPropertyName("messages")] public List<string>? Messages { get; set; }
    [JsonPropertyName("send")] public List<SendDto>? Send { get; set; }
}

internal sealed class ScreenDto
{
    [JsonPropertyName("width")] [JsonConverter(typeof(FlexIntConverter))] public int Width { get; set; }
    [JsonPropertyName("height")] [JsonConverter(typeof(FlexIntConverter))] public int Height { get; set; }
    [JsonPropertyName("color")] public string? Color { get; set; }
    [JsonPropertyName("labels")] public List<LabelDto>? Labels { get; set; }
    [JsonPropertyName("presend")] public List<SendDto>? PreSend { get; set; }
    [JsonPropertyName("displays")] public List<DisplayDto>? Displays { get; set; }
    [JsonPropertyName("buttons")] public List<ButtonDto>? Buttons { get; set; }
    [JsonPropertyName("inputs")] public List<DisplayDto>? Inputs { get; set; }
}

internal sealed class LayoutFileDto
{
    [JsonPropertyName("screens")] public Dictionary<string, ScreenDto>? Screens { get; set; }
    [JsonPropertyName("categories")] public Dictionary<string, List<string>>? Categories { get; set; }
}

/// <summary>Reads/writes the ".layout" JSON files (schema of Python <c>helpers.dumpDOC</c>).</summary>
public static class EcuLayoutJson
{
    /// <summary>Parses UTF-8 layout JSON.</summary>
    public static EcuLayout Load(ReadOnlySpan<byte> utf8Json)
    {
        LayoutFileDto? dto;
        try { dto = JsonSerializer.Deserialize(EcuJsonLoader.StripBom(utf8Json), CoreJsonContext.Default.LayoutFileDto); }
        catch (JsonException ex) { throw new EcuFormatException("Invalid layout JSON: " + ex.Message, ex); }
        return Convert(dto);
    }

    /// <summary>Parses layout JSON from a stream.</summary>
    public static EcuLayout Load(Stream stream)
    {
        LayoutFileDto? dto;
        try { dto = JsonSerializer.Deserialize(stream, CoreJsonContext.Default.LayoutFileDto); }
        catch (JsonException ex) { throw new EcuFormatException("Invalid layout JSON: " + ex.Message, ex); }
        return Convert(dto);
    }

    /// <summary>Parses layout JSON text.</summary>
    public static EcuLayout LoadFromString(string json) => Load(Encoding.UTF8.GetBytes(json));

    private static LayoutColor Col(string? s, LayoutColor def) => s != null && LayoutColor.TryParse(s, out var c) ? c : def;
    private static LayoutRect Rect(RectDto? r) => r == null ? default : new LayoutRect(r.Left, r.Top, r.Width, r.Height);
    private static LayoutFont Font(FontDto? f) => f == null ? LayoutFont.None : new LayoutFont(f.Name ?? "", f.Size, f.Bold, f.Italic);

    private static EcuLayout Convert(LayoutFileDto? dto)
    {
        var layout = new EcuLayout();
        if (dto == null) return layout;
        if (dto.Categories != null)
            foreach (var kv in dto.Categories)
            {
                var c = layout.GetOrAddCategory(kv.Key);
                if (kv.Value != null) c.ScreenNames.AddRange(kv.Value);
            }

        if (dto.Screens != null)
            foreach (var kv in dto.Screens)
            {
                var d = kv.Value;
                var s = new Screen { Name = kv.Key, Width = d.Width, Height = d.Height, Color = Col(d.Color, default) };
                if (d.PreSend != null) foreach (var p in d.PreSend) s.PreSend.Add(new SendCommand(p.RequestName ?? "", p.Delay));
                if (d.Labels != null)
                    foreach (var l in d.Labels)
                        s.Labels.Add(new ScreenLabel
                        {
                            Text = l.Text ?? "", Color = Col(l.Color, default), Alignment = l.Alignment ?? "",
                            FontColor = Col(l.FontColor, LayoutColor.DefaultGrey), Bounds = Rect(l.Bbox), Font = Font(l.Font),
                        });
                if (d.Displays != null)
                    foreach (var x in d.Displays)
                        s.Displays.Add(new ScreenDisplay
                        {
                            DataName = x.Text ?? "", RequestName = x.Request ?? "", Color = Col(x.Color, default),
                            FontColor = Col(x.FontColor, LayoutColor.DefaultGrey), Width = x.Width, Bounds = Rect(x.Rect), Font = Font(x.Font),
                        });
                if (d.Inputs != null)
                    foreach (var x in d.Inputs)
                        s.Inputs.Add(new ScreenInput
                        {
                            DataName = x.Text ?? "", RequestName = x.Request ?? "", Color = Col(x.Color, LayoutColor.DefaultGrey),
                            FontColor = Col(x.FontColor, LayoutColor.DefaultGrey), Width = x.Width, Bounds = Rect(x.Rect), Font = Font(x.Font),
                        });
                if (d.Buttons != null)
                    foreach (var x in d.Buttons)
                    {
                        var b = new ScreenButton { Text = x.Text ?? "", UniqueName = x.UniqueName ?? "", Bounds = Rect(x.Rect), Font = Font(x.Font) };
                        if (x.Messages != null) b.Messages.AddRange(x.Messages);
                        if (x.Send != null) foreach (var p in x.Send) b.Send.Add(new SendCommand(p.RequestName ?? "", p.Delay));
                        s.Buttons.Add(b);
                    }

                layout.Screens.Add(s);
            }

        return layout;
    }

    /// <summary>Serialises a layout (same keys and ordering as the Python dumper).</summary>
    public static string Dump(EcuLayout layout)
    {
        var buf = new ArrayBufferWriter<byte>(1 << 14);
        Write(layout, buf);
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    /// <summary>Serialises to a buffer writer.</summary>
    public static void Write(EcuLayout layout, IBufferWriter<byte> output)
    {
        using var w = new Utf8JsonWriter(output, new JsonWriterOptions
        {
            Indented = true, IndentCharacter = '\t', IndentSize = 1,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        w.WriteStartObject();
        w.WriteStartObject("screens");
        foreach (var s in layout.Screens)
        {
            w.WriteStartObject(s.Name);
            w.WriteNumber("width", s.Width);
            w.WriteNumber("height", s.Height);
            w.WriteString("color", s.Color.ToString());
            w.WriteStartArray("labels");
            foreach (var l in s.Labels)
            {
                w.WriteStartObject();
                w.WriteString("text", l.Text);
                w.WriteString("color", l.Color.ToString());
                w.WriteString("alignment", l.Alignment);
                w.WriteString("fontcolor", l.FontColor.ToString());
                WriteRect(w, "bbox", l.Bounds);
                WriteFont(w, l.Font);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            WriteSends(w, "presend", s.PreSend);
            w.WriteStartArray("displays");
            foreach (var d in s.Displays) WriteDisplay(w, d);
            w.WriteEndArray();
            w.WriteStartArray("buttons");
            foreach (var b in s.Buttons)
            {
                w.WriteStartObject();
                w.WriteString("text", b.Text);
                w.WriteString("uniquename", b.UniqueName);
                WriteRect(w, "rect", b.Bounds);
                WriteFont(w, b.Font);
                w.WriteStartArray("messages");
                foreach (var m in b.Messages) w.WriteStringValue(m);
                w.WriteEndArray();
                if (b.Send.Count > 0) WriteSends(w, "send", b.Send);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("inputs");
            foreach (var d in s.Inputs) WriteDisplay(w, d);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndObject();
        w.WriteStartObject("categories");
        foreach (var c in layout.Categories)
        {
            w.WriteStartArray(c.Name);
            foreach (var n in c.ScreenNames) w.WriteStringValue(n);
            w.WriteEndArray();
        }
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteRect(Utf8JsonWriter w, string key, LayoutRect r)
    {
        w.WriteStartObject(key);
        w.WriteNumber("left", r.Left);
        w.WriteNumber("top", r.Top);
        w.WriteNumber("height", r.Height);
        w.WriteNumber("width", r.Width);
        w.WriteEndObject();
    }

    private static void WriteFont(Utf8JsonWriter w, LayoutFont f)
    {
        w.WriteStartObject("font");
        w.WriteString("name", f.Name);
        w.WriteNumber("size", f.Size);
        w.WriteString("bold", f.Bold ? "1" : "0");
        w.WriteString("italic", f.Italic ? "1" : "0");
        w.WriteEndObject();
    }

    private static void WriteSends(Utf8JsonWriter w, string key, List<SendCommand> sends)
    {
        w.WriteStartArray(key);
        foreach (var s in sends)
        {
            w.WriteStartObject();
            w.WriteString("Delay", s.DelayMs.ToString(CultureInfo.InvariantCulture));
            w.WriteString("RequestName", s.RequestName);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteDisplay(Utf8JsonWriter w, ScreenDisplay d)
    {
        w.WriteStartObject();
        w.WriteString("text", d.DataName);
        w.WriteString("request", d.RequestName);
        w.WriteString("color", d.Color.ToString());
        if (d is ScreenInput)
        {
            w.WriteString("fontcolor", d.FontColor.ToString());
            w.WriteNumber("width", d.Width);
        }
        else
        {
            w.WriteNumber("width", d.Width);
        }

        WriteRect(w, "rect", d.Bounds);
        WriteFont(w, d.Font);
        if (d is not ScreenInput) w.WriteString("fontcolor", d.FontColor.ToString());
        w.WriteEndObject();
    }
}
