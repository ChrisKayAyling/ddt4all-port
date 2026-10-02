using Ddt4All.Core.Ecu;

namespace Ddt4All.Plugins.Vehicles;

/// <summary>Modus / Clio III electric power steering (DAE): dongle (immobiliser) reset and VIN read/write.</summary>
public sealed class Clio3EpsPlugin : VirginizerPlugin
{
    public const string Sds = "Start Diagnostic Session", SdsFb = "SDS - Start Diagnostic $FB", ReadVin = "RDBLI - VIN", SystemFrame = "RDBLI - System Frame",
        WriteVin = "WDBLI - VIN", Erase = "WDBLI - Erase of Dongle_ID code";
    public const string VirginText = "Système VIERGE - Aucun code mémorisé";

    public override PluginInfo Info { get; } = new("clio3-eps-reset", "Modus/Clio III EPS Reset", "EPS Tools", "Modus / Clio III (DAE, J77/X85)")
    {
        Description = "Reads the EPS blank status and VIN, resets the EPS immobiliser data, and writes a VIN (with CRC).",
        Warnings = ["This plugin will RESET EPS IMMO DATA.", "Go away if you have no idea what that means."],
        EcuDefinition = "DAE_J77_X85_Gen2___v3.7", Protocol = "CAN",
    };
    protected override string CheckTitle => "Blank status & VIN read";
    protected override string ResetTitle => "Virginize EPS";
    protected override string ResetDescription => "Only offered when the EPS is coded. Enters session $FB and erases the dongle ID code.";
    protected override IReadOnlyList<string> CheckRequests => [Sds, ReadVin, SystemFrame];
    protected override IReadOnlyList<string> ResetRequests => [SdsFb, Erase];

    protected override IReadOnlyList<PluginAction> BuildActions() =>
    [
        .. base.BuildActions(),
        new PluginAction("write-vin", "Write VIN")
        {
            Description = "Writes the VIN and its CRC (CRC-16/X-25, bytes swapped) into the EPS.",
            Writes = true, StepCount = 2, RequiredRequests = [Sds, WriteVin],
            Parameters = [new PluginParameter("vin", "VIN") { Description = "Exactly 17 ASCII characters (upper-cased).", MinLength = 17, MaxLength = 17 }],
        },
    ];

    protected override async ValueTask<VirginCheck> CheckAsync(PluginContext ctx)
    {
        await ctx.StartSessionAsync(Sds).ConfigureAwait(false);
        ctx.Step("Reading VIN");
        var vinRsp = await ctx.SendAsync(ReadVin).ConfigureAwait(false);
        string vin = vinRsp.IsPositive ? vinRsp["VIN"] ?? "" : "";
        ctx.Step("Reading system frame");
        var r = await ctx.SendAsync(SystemFrame).ConfigureAwait(false);
        var values = new[] { new KeyValuePair<string, string>("VIN", vin) };
        if (!r.IsPositive || r["Dongle status"] is not { } status) return Unexpected(r) with { Values = values };
        return status == VirginText
            ? new VirginCheck(VirginState.Virgin, "EPS virgin") { Values = values }
            : new VirginCheck(VirginState.Coded, "EPS coded") { Values = values };
    }

    protected override async ValueTask<EcuResponse> ResetAsync(PluginContext ctx)
    {
        await ctx.StartSessionAsync(SdsFb).ConfigureAwait(false);
        return await ctx.SendAsync(Erase).ConfigureAwait(false);
    }

    public override async ValueTask<PluginResult> RunAsync(PluginAction action, PluginContext ctx)
    {
        if (action.Id != "write-vin") return await base.RunAsync(action, ctx).ConfigureAwait(false);
        string vin = ctx.Param("vin").ToUpperInvariant();
        // Python: toAscii() failure -> "VIN - INVALID", length != 17 -> "VIN - BAD LENGTH"
        if (vin.Any(c => c < 0x20 || c > 0x7E)) return PluginResult.Fail("VIN - INVALID");
        if (vin.Length != 17) return PluginResult.Fail("VIN - BAD LENGTH");
        string crc = VinCrc.Calc(vin);
        ctx.Step("Starting diagnostic session");
        await ctx.StartSessionAsync(Sds).ConfigureAwait(false);
        ctx.Step($"Writing VIN {vin} (CRC {crc})");
        var rsp = await ctx.SendAsync(WriteVin, ("VIN", vin), ("CRC VIN", crc)).ConfigureAwait(false);
        return PluginContext.Succeeded(rsp) ? PluginResult.Ok("VIN WRITE OK", "vin-written") : PluginResult.Fail("VIN WRITE FAILED");
    }
}

/// <summary>Clio IV electric power steering.</summary>
public sealed class Clio4EpsPlugin : VirginizerPlugin
{
    public const string SdsExtended = "StartDiagnosticSession.extendedSession", SdsSupplier = "StartDiagnosticSession.supplierSession",
        ReadState = "DataRead.DongleState", Blank = "SRBLID.DongleBlanking.Request";
    public const string CodeInput = "Dongle.Code", CodeValue = "1976";

    public override PluginInfo Info { get; } = new("clio4-eps-reset", "Clio IV EPS Reset", "EPS Tools", "Clio IV (X98 ph2 / X87 ph2)")
    {
        Description = "Reads the EPS dongle state and blanks the EPS immobiliser data when it is learnt.",
        Warnings = ["This plugin will RESET EPS IMMO DATA.", "Go away if you have no idea what that means."],
        EcuDefinition = "X98ph2_X87ph2_EPS_HFP_v1.00_20150622T140219_20160726T172209", Protocol = "CAN",
    };
    protected override string CheckTitle => "Check EPS virgin";
    protected override string ResetTitle => "Virginize EPS";
    protected override string ResetDescription => "Only offered when the dongle state is OperationalLearnt. Enters the supplier session and blanks the dongle.";
    protected override IReadOnlyList<string> CheckRequests => [SdsExtended, ReadState];
    protected override IReadOnlyList<string> ResetRequests => [SdsSupplier, Blank];

    protected override async ValueTask<VirginCheck> CheckAsync(PluginContext ctx)
    {
        await ctx.StartSessionAsync(SdsExtended).ConfigureAwait(false);
        var r = await ctx.SendAsync(ReadState).ConfigureAwait(false);
        return Value(r, "DongleState") switch
        {
            "NotOperational" => new(VirginState.NotOperational, "EPS not operational"),
            "OperationalBlanked" => new(VirginState.Virgin, "EPS virgin"),
            "OperationalLearnt" => new(VirginState.Coded, "EPS coded"),
            _ => Unexpected(r),
        };
    }

    protected override async ValueTask<EcuResponse> ResetAsync(PluginContext ctx)
    {
        await ctx.StartSessionAsync(SdsSupplier).ConfigureAwait(false);
        return await ctx.SendAsync(Blank, (CodeInput, CodeValue)).ConfigureAwait(false);
    }
}

/// <summary>ZOE / Fluence / Megane III / Scenic III electric power steering.</summary>
public sealed class Megane3EpsPlugin : VirginizerPlugin
{
    public const string SdsC0 = "SDS - Start Diagnostic Session $C0", SdsFa = "SDS - Start Diagnostic Session $FA",
        ReadState = "DataRead.DID - Dongle state", Blank = "SRBLID - Dongle blanking";

    public override PluginInfo Info { get; } = new("megane3-eps-reset", "ZOE/FLENCE/Megane III/Scenic III EPS Reset", "EPS Tools", "ZOE / Fluence / Megane III / Scenic III (DAE X95/X38/X10)")
    {
        Description = "Reads the EPS dongle state and blanks the EPS immobiliser data when it is learnt.",
        Warnings = ["This plugin will RESET EPS IMMO DATA.", "Go away if you have no idea what that means."],
        EcuDefinition = "DAE_X95_X38_X10_v1.88_20120228T113904", Protocol = "CAN",
    };
    protected override string CheckTitle => "Check EPS virgin";
    protected override string ResetTitle => "Virginize EPS";
    protected override string ResetDescription => "Only offered when the dongle is 'Operational learnt'. Enters session $FA and blanks the dongle.";
    protected override IReadOnlyList<string> CheckRequests => [SdsC0, ReadState];
    protected override IReadOnlyList<string> ResetRequests => [SdsFa, Blank];

    protected override async ValueTask<VirginCheck> CheckAsync(PluginContext ctx)
    {
        await ctx.StartSessionAsync(SdsC0).ConfigureAwait(false);
        var r = await ctx.SendAsync(ReadState).ConfigureAwait(false);
        return Value(r, "DID - Dongle state") switch
        {
            "Not operational" => new(VirginState.NotOperational, "EPS not operational"),
            "Operational blank" => new(VirginState.Virgin, "EPS virgin"),
            "Operational learnt" => new(VirginState.Coded, "EPS coded"),
            _ => Unexpected(r),
        };
    }

    protected override async ValueTask<EcuResponse> ResetAsync(PluginContext ctx)
    {
        await ctx.StartSessionAsync(SdsFa).ConfigureAwait(false);
        return await ctx.SendAsync(Blank).ConfigureAwait(false);
    }
}
