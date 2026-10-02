using Ddt4All.Core.Ecu;

namespace Ddt4All.App.ViewModels;

/// <summary>Lightweight row of the request list (built once per ECU; the list is virtualised).</summary>
public sealed class RequestRow
{
    public RequestRow(EcuRequest request, bool isWrite)
    {
        Request = request;
        Name = request.Name;
        var t = request.SentBytes;
        Sid = t.Length >= 2 ? t[..2].ToUpperInvariant() : "--";
        Bytes = t.Length > 24 ? t[..24] + "…" : t;
        IsWrite = isWrite;
        Key = (request.Name + " " + t).ToLowerInvariant();
    }

    public EcuRequest Request { get; }
    public string Name { get; }
    public string Sid { get; }
    public string Bytes { get; }
    public bool IsWrite { get; }
    public string Key { get; }
}

/// <summary>One line of the decoded response table.</summary>
public sealed class ResponseRow
{
    public required string Name { get; init; }
    public required string Value { get; init; }
    public string Unit { get; init; } = "";
    public string Raw { get; init; } = "";
    public string Position { get; init; } = "";
    public bool IsMissing { get; init; }
    public string? Description { get; init; }
}

public sealed class HistoryEntry
{
    public required DateTime Time { get; init; }
    public required string RequestName { get; init; }
    public required string Sent { get; init; }
    public required string Received { get; init; }
    public required string Status { get; init; }
    public required bool Ok { get; init; }
    public EcuRequest? Request { get; init; }
    public EcuResponse? Response { get; init; }
    public string TimeText => Time.ToString("HH:mm:ss");
}
