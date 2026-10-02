using Ddt4All.Comms;

namespace Ddt4All.Plugins;

/// <summary>What the host supplies for one run.</summary>
public sealed class PluginRunRequest
{
    /// <summary>Connected adapter (null is fine for plugins that do not need hardware).</summary>
    public IAddressableTransport? Transport { get; init; }
    /// <summary>Session expert mode. Writing actions are blocked without it.</summary>
    public bool ExpertMode { get; init; }
    /// <summary>The operator explicitly confirmed this run after reading the warnings.</summary>
    public bool Confirmed { get; init; }
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
    public IPluginInteraction? Interaction { get; init; }
    /// <summary>Delay implementation for polling loops (tests pass an instant one).</summary>
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
}

/// <summary>
/// The only way plugins are executed. Enforces the safety gates in one place so individual plugins cannot forget them:
/// expert mode + explicit confirmation for writing actions, parameter validation, hardware present, definition and
/// request availability, protocol/addressing. Never throws for plugin or ECU problems: everything becomes a <see cref="PluginResult"/>.
/// </summary>
public sealed class PluginRunner(IEcuDefinitionProvider definitions)
{
    public async Task<PluginResult> RunAsync(IPlugin plugin, string actionId, PluginRunRequest request, IProgress<PluginEvent>? progress = null, CancellationToken ct = default)
    {
        var action = plugin.Actions.FirstOrDefault(a => a.Id == actionId);
        if (action is null) return Finish(progress, PluginResult.Blocked($"Unknown action '{actionId}'."), 0, 0);

        void Say(PluginEventKind k, string m) => progress?.Report(new PluginEvent(k, m, 0, action.StepCount, DateTimeOffset.UtcNow));

        // ---- gates: nothing is sent before all of these pass
        if (action.Writes && !request.ExpertMode)
            return Finish(progress, PluginResult.Blocked("Expert mode is required: this action writes to the ECU."), 0, action.StepCount);
        if (action.Writes && !request.Confirmed)
            return Finish(progress, PluginResult.Blocked("This action writes to the ECU and has not been confirmed."), 0, action.StepCount);
        foreach (var p in action.Parameters)
        {
            request.Parameters.TryGetValue(p.Id, out var v);
            if (p.Validate(v ?? p.DefaultValue) is { } problem)
                return Finish(progress, PluginResult.Blocked(problem), 0, action.StepCount);
        }
        var parameters = action.Parameters.ToDictionary(p => p.Id, p => request.Parameters.TryGetValue(p.Id, out var v) ? v : p.DefaultValue);

        Ddt4All.Core.Ecu.EcuFile? def = null;
        try
        {
            if (plugin.Info.RequiresHardware && request.Transport is null)
                return Finish(progress, PluginResult.Blocked("Not connected: connect to an adapter first."), 0, action.StepCount);

            if (plugin.Info.EcuDefinition is { } defName)
            {
                Say(PluginEventKind.Info, $"Loading ECU definition {defName}");
                def = await definitions.LoadAsync(defName, ct).ConfigureAwait(false);
                if (def is null)
                    return Finish(progress, PluginResult.Fail($"ECU definition '{defName}' was not found in the ECU database."), 0, action.StepCount);
                var missing = action.RequiredRequests.Where(r => def.GetRequest(r) is null).ToList();
                if (missing.Count > 0)
                    return Finish(progress, PluginResult.Fail($"ECU definition '{defName}' lacks required request(s): {string.Join("; ", missing)}. Nothing was sent."), 0, action.StepCount);

                if (request.Transport is { } t)
                {
                    var addr = EcuAddressing.FromDefinition(def, out var err);
                    if (addr is null) return Finish(progress, PluginResult.Fail(err!), 0, action.StepCount);
                    Say(PluginEventKind.Info, $"Connecting to {def.EcuName} ({addr.Protocol}{(addr.Protocol == EcuProtocol.Can ? $" {addr.TxHex}/{addr.RxHex}" : "")})");
                    await t.ConnectEcuAsync(addr, ct).ConfigureAwait(false);
                }
            }

            var ctx = new PluginContext(action, def, request.Transport, parameters, progress, request.Interaction, request.Delay, ct);
            var result = await plugin.RunAsync(action, ctx).ConfigureAwait(false);
            return Finish(progress, result, ctx.CurrentStep, ctx.TotalSteps);
        }
        catch (OperationCanceledException)
        {
            return Finish(progress, new PluginResult(PluginOutcome.Cancelled, "Cancelled by the user."), 0, action.StepCount);
        }
        catch (PluginException ex) { return Finish(progress, PluginResult.Fail(ex.Message), 0, action.StepCount); }
        catch (CommsException ex) { return Finish(progress, PluginResult.Fail($"Communication error: {ex.Message}"), 0, action.StepCount); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Finish(progress, PluginResult.Fail($"Unexpected error: {ex.Message}"), 0, action.StepCount);
        }
    }

    private static PluginResult Finish(IProgress<PluginEvent>? progress, PluginResult r, int step, int total)
    {
        progress?.Report(new PluginEvent(PluginEventKind.Result, $"{r.Outcome}: {r.Summary}", total > 0 ? total : step, total, DateTimeOffset.UtcNow));
        return r;
    }
}
