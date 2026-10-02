namespace Ddt4All.Core.Abstractions;

/// <summary>
/// The one seam between ECU logic (Core) and hardware/simulation (Comms).
/// Request/response are raw diagnostic payload bytes (UDS/KWP service id first),
/// with ISO-TP / ELM framing already handled by the implementation.
/// </summary>
public interface IEcuTransport
{
    /// <summary>Send a diagnostic request; return the complete positive/negative response payload.</summary>
    ValueTask<byte[]> RequestAsync(ReadOnlyMemory<byte> request, CancellationToken ct = default);
}
