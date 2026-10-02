using System.Buffers;
using System.Text.Json;
using Ddt4All.Core.Ecu;
using Ddt4All.Core.Loading;

namespace Ddt4All.Core.Database;

/// <summary>
/// Streaming parser for the <c>db.json</c> member of ecu.zip (href to ident map) that feeds an
/// <see cref="EcuIndexBuilder"/> without materialising per-token strings for already seen values.
/// </summary>
internal static class DbJsonParser
{
    public static void Parse(ReadOnlySpan<byte> json, EcuIndexBuilder b)
    {
        json = EcuJsonLoader.StripBom(json);
        var r = new Utf8JsonReader(json, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        char[]? rented = null;
        var pids = new List<int>(16);
        var ai = new List<int>(64);

        if (!r.Read() || r.TokenType != JsonTokenType.StartObject) throw new EcuFormatException("db.json: object expected");
        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            int href = InternCurrent(ref r, b, ref rented);
            r.Read();
            if (r.TokenType != JsonTokenType.StartObject) { r.TrySkip(); continue; }

            int name = 0, group = 0, addr = 0;
            var protocol = EcuProtocol.Unknown;
            pids.Clear();
            ai.Clear();
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                // Property names are matched on raw bytes: no allocation
                if (r.ValueTextEquals("group"u8)) { r.Read(); group = InternCurrent(ref r, b, ref rented); }
                else if (r.ValueTextEquals("address"u8)) { r.Read(); addr = InternCurrent(ref r, b, ref rented); }
                else if (r.ValueTextEquals("ecuname"u8)) { r.Read(); name = InternCurrent(ref r, b, ref rented); }
                else if (r.ValueTextEquals("protocol"u8))
                {
                    r.Read();
                    var span = CopyCurrent(ref r, ref rented);
                    protocol = EcuProtocolNames.Classify(span);
                }
                else if (r.ValueTextEquals("projects"u8))
                {
                    r.Read();
                    if (r.TokenType == JsonTokenType.StartArray)
                    {
                        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                        {
                            if (r.TokenType != JsonTokenType.String) continue;
                            var span = CopyCurrent(ref r, ref rented);
                            if (!span.IsEmpty) pids.Add(b.InternProject(span));
                        }
                    }
                }
                else if (r.ValueTextEquals("autoidents"u8))
                {
                    r.Read();
                    if (r.TokenType == JsonTokenType.StartArray)
                    {
                        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                        {
                            if (r.TokenType != JsonTokenType.StartObject) { r.TrySkip(); continue; }
                            int diag = 0, sup = 0, soft = 0, ver = 0, diagVal = -1;
                            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                            {
                                if (r.ValueTextEquals("diagnostic_version"u8) || r.ValueTextEquals("diagnotic_version"u8))
                                {
                                    r.Read();
                                    var span = CopyCurrent(ref r, ref rented);
                                    diag = b.Intern(span);
                                    diagVal = EcuIndexBuilder.HexOrMinus1(span);
                                }
                                else if (r.ValueTextEquals("supplier_code"u8)) { r.Read(); sup = InternCurrent(ref r, b, ref rented); }
                                else if (r.ValueTextEquals("soft_version"u8)) { r.Read(); soft = InternCurrent(ref r, b, ref rented); }
                                else if (r.ValueTextEquals("version"u8)) { r.Read(); ver = InternCurrent(ref r, b, ref rented); }
                                else { r.Read(); r.TrySkip(); }
                            }

                            ai.Add(diag); ai.Add(sup); ai.Add(soft); ai.Add(ver); ai.Add(diagVal);
                        }
                    }
                }
                else { r.Read(); r.TrySkip(); }
            }

            b.CommitEntry(href, name, group, addr, protocol, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pids),
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(ai));
        }

        if (rented != null) ArrayPool<char>.Shared.Return(rented);
    }

    private static ReadOnlySpan<char> CopyCurrent(ref Utf8JsonReader r, ref char[]? rented)
    {
        if (r.TokenType != JsonTokenType.String && r.TokenType != JsonTokenType.PropertyName) { r.TrySkip(); return default; }
        int max = r.ValueSpan.Length;
        if (rented == null || rented.Length < max)
        {
            if (rented != null) ArrayPool<char>.Shared.Return(rented);
            rented = ArrayPool<char>.Shared.Rent(Math.Max(256, max));
        }

        int n = r.CopyString(rented);
        return rented.AsSpan(0, n);
    }

    private static int InternCurrent(ref Utf8JsonReader r, EcuIndexBuilder b, ref char[]? rented)
    {
        var span = CopyCurrent(ref r, ref rented);
        return span.IsEmpty ? 0 : b.Intern(span);
    }
}

