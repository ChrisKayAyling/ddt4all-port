using Ddt4All.Core.Ecu;

namespace Ddt4All.Plugins.Vehicles;

/// <summary>AB90 airbag computer (Clio III / J77 / X85): check and clear the crash flag.</summary>
public sealed class Ab90AirbagPlugin : VirginizerPlugin
{
    public const string Sds = "Start Diagnostic Session", ReadState = "Synthèse état UCE", Reset = "Reset crash ou accès au mode fournisseur";
    public const string CodeInput = "code d'accès pour reset UCE", CodeValue = "22041998", CrashData = "crash détecté";

    public override PluginInfo Info { get; } = new("ab90-airbag-reset", "AB90 AIRBAG Reset", "Airbag Tools", "Clio III / Modus (AB90)")
    {
        Description = "Reads the crash state of the AB90 airbag computer and clears the stored crash (virginizes the ACU).",
        Warnings = ["This plugin will UNLOCK AIRBAG CRASH DATA (clears the stored crash).", "Go away if you have no idea what that means."],
        EcuDefinition = "AB90_J77_X85", Protocol = "CAN",
    };
    protected override string CheckTitle => "Check ACU virgin";
    protected override string ResetTitle => "Virginize ACU";
    protected override string ResetDescription => "Sends the crash reset with the access code. The airbag computer forgets the recorded crash.";
    protected override IReadOnlyList<string> CheckRequests => [Sds, ReadState];
    protected override IReadOnlyList<string> ResetRequests => [Sds, Reset];
    protected override bool ResetRequiresCoded => false;

    protected override async ValueTask<VirginCheck> CheckAsync(PluginContext ctx)
    {
        await ctx.StartSessionAsync(Sds).ConfigureAwait(false);
        var r = await ctx.SendAsync(ReadState).ConfigureAwait(false);
        var crash = Value(r, CrashData);
        if (crash is null) return Unexpected(r);
        return crash == "crash détecté" ? new(VirginState.CrashDetected, "CRASH DETECTED") : new(VirginState.NoCrash, "NO CRASH DETECTED");
    }

    protected override async ValueTask<EcuResponse> ResetAsync(PluginContext ctx)
    {
        await ctx.StartSessionAsync(Sds).ConfigureAwait(false);
        return await ctx.SendAsync(Reset, (CodeInput, CodeValue)).ConfigureAwait(false);
    }
}

/// <summary>Megane III (MRSZ) airbag computer.</summary>
public sealed class Megane3AirbagPlugin : VirginizerPlugin
{
    public const string Sds = "Start Diagnostic Session", ReadState = "Synthèse état UCE avant crash", Reset = "Reset crash ou accès au mode fournisseur";
    public const string CodeInput = "code d'accès pour reset UCE", CodeValue = "27081977", CrashData = "crash détecté";

    public override PluginInfo Info { get; } = new("megane3-airbag-reset", "Megane3 AIRBAG Reset", "Airbag Tools", "Megane III / Fluence / Laguna III (MRSZ)")
    {
        Description = "Reads the crash state of the MRSZ airbag computer and clears the stored crash.",
        Warnings = ["This plugin will UNLOCK AIRBAG CRASH DATA (clears the stored crash).", "Go away if you have no idea what that means."],
        EcuDefinition = "MRSZ_X95_L38_L43_L47_20110505T101858", Protocol = "CAN",
    };
    protected override string CheckTitle => "Check ACU virgin";
    protected override string ResetTitle => "Virginize ACU";
    protected override string ResetDescription => "Enters the supplier session ($FA) and sends the crash reset with the access code.";
    protected override IReadOnlyList<string> CheckRequests => [Sds, ReadState];
    protected override IReadOnlyList<string> ResetRequests => [Sds, Reset];
    protected override bool ResetRequiresCoded => false;

    protected override async ValueTask<VirginCheck> CheckAsync(PluginContext ctx)
    {
        await ctx.StartSessionAsync(Sds, ("Session Name", "extendedDiagnosticSession")).ConfigureAwait(false);
        var r = await ctx.SendAsync(ReadState).ConfigureAwait(false);
        var crash = Value(r, CrashData);
        if (crash is null) return Unexpected(r);
        return crash == "crash détecté" ? new(VirginState.CrashDetected, "CRASH DETECTED") : new(VirginState.NoCrash, "NO CRASH DETECTED");
    }

    protected override async ValueTask<EcuResponse> ResetAsync(PluginContext ctx)
    {
        await ctx.StartSessionAsync(Sds, ("Session Name", "systemSupplierSpecific")).ConfigureAwait(false);
        return await ctx.SendAsync(Reset, (CodeInput, CodeValue)).ConfigureAwait(false);
    }
}

/// <summary>RSAT4 airbag computer.</summary>
public sealed class Rsat4AirbagPlugin : VirginizerPlugin
{
    public const string Sds = "Start Diagnostic Session", ReadState = "Reading of ECU state synthesis", Reset = "Reset Crash";
    public const string CodeInput = "CLEDEV For reset crash", CodeValue = "13041976", CrashData = "crash detected";

    public override PluginInfo Info { get; } = new("rsat4-airbag-reset", "RSAT4 AIRBAG Reset", "Airbag Tools", "RSAT4 airbag computer")
    {
        Description = "Reads the crash state of the RSAT4 ACU and clears the stored crash.",
        Warnings = ["This plugin will UNLOCK AIRBAG CRASH DATA (clears the stored crash).", "Go away if you have no idea what that means."],
        EcuDefinition = "RSAT4_ACU_eng_v15_20150511T131328", Protocol = "CAN",
    };
    protected override string CheckTitle => "Check ACU virgin";
    protected override string ResetTitle => "Virginize ACU";
    protected override string ResetDescription => "Enters the extended session and sends 'Reset Crash' with the access code.";
    protected override IReadOnlyList<string> CheckRequests => [Sds, ReadState];
    protected override IReadOnlyList<string> ResetRequests => [Sds, Reset];
    protected override bool ResetRequiresCoded => false;

    protected override async ValueTask<VirginCheck> CheckAsync(PluginContext ctx)
    {
        await ctx.StartSessionAsync(Sds, ("Session Name", "extendedDiagnosticSession")).ConfigureAwait(false);
        var r = await ctx.SendAsync(ReadState).ConfigureAwait(false);
        return Value(r, CrashData) switch
        {
            "crash detected" => new(VirginState.CrashDetected, "CRASH DETECTED"),
            "no crash detected" => new(VirginState.NoCrash, "NO CRASH DETECTED"),
            _ => Unexpected(r),
        };
    }

    protected override async ValueTask<EcuResponse> ResetAsync(PluginContext ctx)
    {
        await ctx.StartSessionAsync(Sds, ("Session Name", "extendedDiagnosticSession")).ConfigureAwait(false);
        return await ctx.SendAsync(Reset, (CodeInput, CodeValue)).ConfigureAwait(false);
    }
}
