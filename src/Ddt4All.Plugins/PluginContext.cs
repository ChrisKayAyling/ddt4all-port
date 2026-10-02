using Ddt4All.Comms;
using Ddt4All.Core.Codec;
using Ddt4All.Core.Ecu;

namespace Ddt4All.Plugins;

/// <summary>
/// Everything a plugin run can use: the ECU definition, the transport pointed at the ECU, parameters, logging and operator interaction.
/// All ECU traffic goes through <see cref="SendAsync"/> so it is logged, cancellable and subject to the read-only guard.
/// </summary>
public sealed class PluginContext
{
    /// <summary>Services that are harmless in read-only actions (same list as the Python reference / App SessionState).</summary>
    internal static readonly byte[] SafeServices = [0x10, 0x12, 0x14, 0x17, 0x19, 0x1A, 0x21, 0x22, 0x23, 0x3E];

    private readonly IProgress<PluginEvent>? _progress;
    private readonly IAddressableTransport? _transport;
    private int _step;

    public PluginContext(PluginAction action, EcuFile? definition, IAddressableTransport? transport, IReadOnlyDictionary<string, string> parameters,
        IProgress<PluginEvent>? progress, IPluginInteraction? interaction, Func<TimeSpan, CancellationToken, Task>? delay, CancellationToken ct)
    {
        Action = action; Definition = definition; _transport = transport; Parameters = parameters; _progress = progress;
        Interaction = interaction; Delay = delay ?? Task.Delay; Cancellation = ct; TotalSteps = action.StepCount;
    }

    public PluginAction Action { get; }
    /// <summary>The ECU definition (null for plugins that do not talk to an ECU).</summary>
    public EcuFile? Definition { get; }
    public IReadOnlyDictionary<string, string> Parameters { get; }
    public CancellationToken Cancellation { get; }
    public IPluginInteraction? Interaction { get; }
    public int CurrentStep => _step;
    public int TotalSteps { get; set; }
    /// <summary>Sleeps (replaced by an instant version in tests).</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; }
    /// <summary>True for read-only actions: write services are refused by <see cref="SendAsync"/>.</summary>
    public bool ReadOnly => !Action.Writes;

    public string Param(string id, string fallback = "") => Parameters.TryGetValue(id, out var v) ? v : fallback;

    // ------------------------------------------------------------------ logging

    public void Step(string title) { _step++; Emit(PluginEventKind.Step, title); }
    public void Info(string message) => Emit(PluginEventKind.Info, message);
    public void Warn(string message) => Emit(PluginEventKind.Warning, message);
    public void Error(string message) => Emit(PluginEventKind.Error, message);
    internal void Emit(PluginEventKind kind, string message) =>
        _progress?.Report(new PluginEvent(kind, message, _step, TotalSteps, DateTimeOffset.UtcNow));

    // ------------------------------------------------------------------ ECU traffic

    /// <summary>Request lookup that fails with a clear message when the definition lacks it.</summary>
    public EcuRequest Request(string name)
    {
        var def = Definition ?? throw new PluginException("This plugin has no ECU definition.");
        return def.GetRequest(name) ?? throw new PluginException($"ECU definition '{def.EcuName}' has no request '{name}'.");
    }

    /// <summary>Builds the named request with the given inputs, sends it, and decodes the reply.</summary>
    public ValueTask<EcuResponse> SendAsync(string requestName, params (string Name, string Value)[] inputs) =>
        SendCoreAsync(requestName, Cancellation, inputs);

    /// <summary>
    /// Like <see cref="SendAsync"/> but ignores the run's cancellation: for cleanup that must still reach the ECU
    /// after the user pressed cancel (e.g. leaving a learning mode). Bounded by <paramref name="timeout"/>.
    /// </summary>
    public async ValueTask<EcuResponse> SendCleanupAsync(string requestName, TimeSpan timeout, params (string Name, string Value)[] inputs)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await SendCoreAsync(requestName, cts.Token, inputs).ConfigureAwait(false);
    }

    private async ValueTask<EcuResponse> SendCoreAsync(string requestName, CancellationToken ct, (string Name, string Value)[] inputs)
    {
        ct.ThrowIfCancellationRequested();
        var transport = _transport ?? throw new PluginException("Not connected to an adapter.");
        var req = Request(requestName);
        byte[] payload;
        try { payload = req.BuildRequest(inputs.Select(i => new KeyValuePair<string, string>(i.Name, i.Value))); }
        catch (EcuRequestException ex) { throw new PluginException($"Cannot build request '{requestName}': {ex.Message}"); }
        if (payload.Length == 0) throw new PluginException($"Request '{requestName}' is empty.");
        if (ReadOnly && Array.IndexOf(SafeServices, payload[0]) < 0)
            throw new PluginException($"Refused: request '{requestName}' uses service 0x{payload[0]:X2}, which is not allowed in a read-only action.");

        Emit(PluginEventKind.Tx, $"{requestName}: {HexUtil.ToHex(payload, true)}");
        var reply = await transport.RequestAsync(payload, ct).ConfigureAwait(false);
        var rsp = req.DecodeResponse(reply);
        if (rsp.IsNegative)
            Emit(PluginEventKind.Rx, $"NEGATIVE {HexUtil.ToHex(reply, true)} ({rsp.NegativeResponseText})");
        else
            Emit(PluginEventKind.Rx, HexUtil.ToHex(reply, true));
        return rsp;
    }

    /// <summary>
    /// Python <c>start_session_can/iso</c>: sends the session request built from the definition. Returns true on a positive
    /// reply. In write actions a negative reply aborts (the Python code ignored it and carried on, which only ever produced a refused write).
    /// </summary>
    public async ValueTask<bool> StartSessionAsync(string requestName, params (string Name, string Value)[] inputs)
    {
        var rsp = await SendAsync(requestName, inputs).ConfigureAwait(false);
        if (rsp.IsPositive) return true;
        if (Action.Writes) throw new PluginException($"The ECU refused the diagnostic session ({requestName}): {rsp.NegativeResponseText ?? "no reply"}. Nothing was written.");
        Warn($"Session request '{requestName}' was refused; continuing read-only.");
        return false;
    }

    /// <summary>Python <c>send_request(...) is not None</c>: a non-empty, non-negative reply.</summary>
    public static bool Succeeded(EcuResponse r) => r.IsPositive;

    /// <summary>Asks the operator; throws <see cref="PluginException"/> when the host cannot show prompts.</summary>
    public async ValueTask<int> ChooseAsync(string title, string message, params string[] choices)
    {
        if (Interaction is null) throw new PluginException("This action needs operator input, which the host cannot provide.");
        return await Interaction.ChooseAsync(title, message, choices, Cancellation).ConfigureAwait(false);
    }
}
