namespace Ddt4All.Plugins.Vehicles;

/// <summary>State classified from a status read.</summary>
public enum VirginState { Virgin, Coded, NotOperational, CrashDetected, NoCrash, Unexpected }

/// <summary>Outcome of a status check.</summary>
public sealed record VirginCheck(VirginState State, string Summary)
{
    public IReadOnlyList<KeyValuePair<string, string>> Values { get; init; } = [];

    public PluginResult ToResult() => State == VirginState.Unexpected
        ? PluginResult.Warn(Summary, State.ToString().ToLowerInvariant()) with { Values = Values }
        : new PluginResult(PluginOutcome.Success, Summary) { State = State.ToString().ToLowerInvariant(), Values = Values };
}

/// <summary>
/// Shared shape of the Python "Virginizer" dialogs: a read-only status check plus a destructive reset.
/// The Python reset buttons were only enabled once the check had reported "coded" (UCH/EPS) or always (airbag);
/// <see cref="ResetRequiresCoded"/> reproduces that by re-checking right before the reset and refusing otherwise.
/// </summary>
public abstract class VirginizerPlugin : IPlugin
{
    public abstract PluginInfo Info { get; }

    protected abstract string CheckTitle { get; }
    protected abstract string ResetTitle { get; }
    protected abstract string ResetDescription { get; }
    /// <summary>Requests the check uses.</summary>
    protected abstract IReadOnlyList<string> CheckRequests { get; }
    /// <summary>Requests the reset uses.</summary>
    protected abstract IReadOnlyList<string> ResetRequests { get; }
    /// <summary>Reset only allowed when the check reported <see cref="VirginState.Coded"/> (UCH/EPS), not for airbag.</summary>
    protected virtual bool ResetRequiresCoded => true;
    protected virtual string ResetDoneText => "CLEAR EXECUTED";
    protected virtual string ResetFailedText => "CLEAR FAILED";

    protected abstract ValueTask<VirginCheck> CheckAsync(PluginContext ctx);
    /// <summary>Starts the right session and sends the reset request; returns the reply of the reset request.</summary>
    protected abstract ValueTask<Ddt4All.Core.Ecu.EcuResponse> ResetAsync(PluginContext ctx);

    private IReadOnlyList<PluginAction>? _actions;
    public IReadOnlyList<PluginAction> Actions => _actions ??= BuildActions();

    protected virtual IReadOnlyList<PluginAction> BuildActions() =>
    [
        new PluginAction("check", CheckTitle) { Description = "Read-only: reads the current state from the ECU.", RequiredRequests = CheckRequests, StepCount = 3 },
        new PluginAction("reset", ResetTitle)
        {
            Description = ResetDescription, Writes = true, StepCount = ResetRequiresCoded ? 5 : 3,
            RequiredRequests = ResetRequiresCoded ? CheckRequests.Concat(ResetRequests).Distinct().ToList() : ResetRequests,
        },
    ];

    public virtual async ValueTask<PluginResult> RunAsync(PluginAction action, PluginContext ctx)
    {
        if (action.Id == "check")
        {
            ctx.Step("Reading state");
            return (await CheckAsync(ctx).ConfigureAwait(false)).ToResult();
        }

        if (action.Id != "reset") return PluginResult.Fail($"Unknown action {action.Id}");

        if (ResetRequiresCoded)
        {
            ctx.Step("Checking that the ECU is coded (safety gate)");
            var check = await CheckAsync(ctx).ConfigureAwait(false);
            if (check.State != VirginState.Coded)
                return PluginResult.Blocked(check.State is VirginState.Virgin or VirginState.NotOperational
                    ? $"Reset not needed: {check.Summary}."
                    : $"Reset refused: could not confirm the ECU is coded ({check.Summary}).");
            ctx.Info("ECU is coded: reset allowed.");
        }

        ctx.Step("Sending reset");
        var rsp = await ResetAsync(ctx).ConfigureAwait(false);
        return PluginContext.Succeeded(rsp)
            ? PluginResult.Ok(ResetDoneText, "reset-done")
            : PluginResult.Fail($"{ResetFailedText}{(rsp.IsNegative ? $": {rsp.NegativeResponseText}" : "")}");
    }

    protected static string? Value(Ddt4All.Core.Ecu.EcuResponse r, string name) => r.IsPositive ? r[name] : null;

    protected static VirginCheck Unexpected(Ddt4All.Core.Ecu.EcuResponse r) =>
        new(VirginState.Unexpected, r.IsNegative ? $"UNEXPECTED RESPONSE ({r.NegativeResponseText})" : "UNEXPECTED RESPONSE");
}
