namespace Ddt4All.Plugins.Vehicles;

/// <summary>Zoe EVC water pump (WEP) usage counters: read and reset (clears the "SYST ELEC A CONTROLLER" fault cause).</summary>
public sealed class ZoeWaterPumpPlugin : IPlugin
{
    public const string SdsDefault = "StartDiagnosticSession.Default", SdsExtended = "StartDiagnosticSession.ExtendedDiagnosticSession";

    /// <summary>The four counters: id, label, data (and value) name, read request, write request.</summary>
    public sealed record Counter(string Id, string Label, string DataName)
    {
        public string ReadRequest => "DataRead." + DataName;
        public string WriteRequest => "DataWrite." + DataName;
    }

    public static readonly IReadOnlyList<Counter> Counters =
    [
        new("low", "Low Speed", "($3349) Time Counter for the driving WEP in Low Speed"),
        new("middle", "Middle Speed", "($334A) Time Counter for the driving WEP in Middle Speed"),
        new("high", "High Speed", "($334B) Time Counter for the driving WEP in High Speed"),
        new("timer", "V_Timer_DrivWEP_ON", "($3531) V_Timer_DrivWEP_ON"),
    ];

    public PluginInfo Info { get; } = new("zoe-waterpump-reset", "Zoe water pump Reset", "EVC Tools", "Renault Zoe (EVC)")
    {
        Description = "Reads and resets the water pump counters (low, middle and high speed, plus the WEP timer) after the 'SYST ELEC A CONTROLLER' fault.",
        Warnings = ["This plugin will ERASE the WATER PUMP COUNTERS.", "Go away if you have no idea what that means."],
        Instructions = ["Insert the key CARD", "Put the gear selector in D", "Press and hold START until the 'Remove card' message appears", "Put the gear selector in P", "Keep the key card inserted"],
        EcuDefinition = "EVC_3180_RH5_510_V1.1_20210422T184714", Protocol = "CAN",
    };

    public IReadOnlyList<PluginAction> Actions { get; } = BuildActions();

    private static IReadOnlyList<PluginAction> BuildActions()
    {
        var list = new List<PluginAction>
        {
            new("read", "Check values") { Description = "Read-only: reads the four counters.", StepCount = 5,
                RequiredRequests = [SdsDefault, .. Counters.Select(c => c.ReadRequest)] },
        };
        foreach (var c in Counters)
            list.Add(new PluginAction("reset-" + c.Id, $"Reset {c.Label}")
            {
                Description = $"Extended session, then writes 0 to {c.DataName}.", Writes = true, StepCount = 2,
                RequiredRequests = [SdsExtended, c.WriteRequest],
            });
        list.Add(new PluginAction("reset-all", "Reset all counters")
        {
            Description = "Extended session, then writes 0 to all four counters.", Writes = true, StepCount = 5,
            RequiredRequests = [SdsExtended, .. Counters.Select(c => c.WriteRequest)],
        });
        return list;
    }

    public async ValueTask<PluginResult> RunAsync(PluginAction action, PluginContext ctx)
    {
        if (action.Id == "read")
        {
            ctx.Step("Starting diagnostic session");
            await ctx.StartSessionAsync(SdsDefault).ConfigureAwait(false);
            var values = new List<KeyValuePair<string, string>>();
            foreach (var c in Counters)
            {
                ctx.Step($"Reading {c.Label}");
                var r = await ctx.SendAsync(c.ReadRequest).ConfigureAwait(false);
                string v = r.IsPositive ? r[c.DataName] ?? "n/a" : "n/a";
                values.Add(new(c.Label, v));
            }
            bool ok = values.All(v => v.Value != "n/a");
            return new PluginResult(ok ? PluginOutcome.Success : PluginOutcome.Warning, ok ? "Values read" : "Some values could not be read") { Values = values, State = "read" };
        }

        var targets = action.Id == "reset-all" ? Counters : Counters.Where(c => "reset-" + c.Id == action.Id).ToList();
        if (targets.Count == 0) return PluginResult.Fail($"Unknown action {action.Id}");

        // Python "reset all" started the Default session (writes there are normally refused) and only the per-counter
        // buttons used the extended session. All writes are done in the extended session here.
        ctx.Step("Starting extended diagnostic session");
        await ctx.StartSessionAsync(SdsExtended).ConfigureAwait(false);
        var failures = new List<string>();
        foreach (var c in targets)
        {
            ctx.Step($"Resetting {c.Label}");
            var r = await ctx.SendAsync(c.WriteRequest, (c.DataName, "0")).ConfigureAwait(false);
            if (PluginContext.Succeeded(r)) ctx.Info($"CLEAR {c.Label} EXECUTED");
            else { ctx.Error($"CLEAR {c.Label} FAILED"); failures.Add(c.Label); }
        }
        return failures.Count == 0
            ? PluginResult.Ok(targets.Count == 1 ? $"CLEAR {targets[0].Label} EXECUTED" : "CLEAR EXECUTED (all counters)", "reset-done")
            : PluginResult.Fail($"CLEAR FAILED: {string.Join(", ", failures)}");
    }
}
