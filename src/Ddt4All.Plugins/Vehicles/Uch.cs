using Ddt4All.Core.Ecu;

namespace Ddt4All.Plugins.Vehicles;

/// <summary>Laguna II UCH (K-line): the Python dialog opened the study session immediately, then used the after-sales (APV) session for the check.</summary>
public sealed class Laguna2UchPlugin : VirginizerPlugin
{
    public const string Sds = "Start Diagnostic Session", ReadState = "Lecture Etats Antidémarrage et acces", Erase = "Effacement_données_antidem_acces";

    public override PluginInfo Info { get; } = new("laguna2-uch-reset", "Laguna II UCH Reset", "UCH Tools", "Laguna II (UCH M2S X74 / X73)")
    {
        Description = "Reads whether the UCH is virgin and erases the anti-start / access data.",
        Warnings = ["This plugin will ERASE YOUR UCH.", "Go away if you have no idea what that means."],
        EcuDefinition = "UCH___M2S_X74_et_X73", Protocol = "KWP2000 (K-line)",
    };
    protected override string CheckTitle => "Check UCH virgin";
    protected override string ResetTitle => "Virginize UCH";
    protected override string ResetDescription => "Only offered when the UCH is coded. Starts the study session and erases the anti-start data.";
    protected override IReadOnlyList<string> CheckRequests => [Sds, ReadState];
    protected override IReadOnlyList<string> ResetRequests => [Sds, Erase];

    protected override async ValueTask<VirginCheck> CheckAsync(PluginContext ctx)
    {
        await ctx.StartSessionAsync(Sds, ("Session Name", "Etude")).ConfigureAwait(false);   // "Start comm immediately"
        await ctx.StartSessionAsync(Sds, ("Session Name", "APV")).ConfigureAwait(false);
        var r = await ctx.SendAsync(ReadState).ConfigureAwait(false);
        return Value(r, "UCH vierge") switch
        {
            "oui" => new(VirginState.Virgin, "UCH virgin"),
            "non" => new(VirginState.Coded, "UCH coded"),
            _ => Unexpected(r),
        };
    }

    protected override async ValueTask<EcuResponse> ResetAsync(PluginContext ctx)
    {
        await ctx.StartSessionAsync(Sds, ("Session Name", "Etude")).ConfigureAwait(false);
        return await ctx.SendAsync(Erase).ConfigureAwait(false);
    }
}

/// <summary>Laguna III BCM ("UCH").</summary>
public sealed class Laguna3UchPlugin : VirginizerPlugin
{
    public const string Sds = "Start Diagnostic Session", ReadState = "Read_A_AC_General_Identifiers_Learning_Status_(bits)_BCM_Input/Output", Reset = "SR_RESERVED VSC 1";

    public override PluginInfo Info { get; } = new("laguna3-uch-reset", "Laguna III UCH Reset", "UCH Tools", "Laguna III (BCM X91 / L43)")
    {
        Description = "Reads BCM_IS_BLANK_S and erases the UCH (key learning data) with SR_RESERVED VSC 1.",
        Warnings = ["This plugin will ERASE YOUR UCH.", "Go away if you have no idea what that means."],
        EcuDefinition = "BCM_X91_L43_S_S_SWC_v1.30_20140613T140906", Protocol = "CAN",
    };
    protected override string CheckTitle => "Check UCH virgin";
    protected override string ResetTitle => "Virginize UCH";
    protected override string ResetDescription => "Only offered when the UCH is coded. Starts the after-sales session and sends SR_RESERVED VSC 1.";
    protected override IReadOnlyList<string> CheckRequests => [Sds, ReadState];
    protected override IReadOnlyList<string> ResetRequests => [Sds, Reset];

    protected override async ValueTask<VirginCheck> CheckAsync(PluginContext ctx)
    {
        await ctx.StartSessionAsync(Sds).ConfigureAwait(false);
        var r = await ctx.SendAsync(ReadState).ConfigureAwait(false);
        return Value(r, "BCM_IS_BLANK_S") switch
        {
            "true" => new(VirginState.Virgin, "UCH virgin"),
            "false" => new(VirginState.Coded, "UCH coded"),
            _ => Unexpected(r),
        };
    }

    protected override async ValueTask<EcuResponse> ResetAsync(PluginContext ctx)
    {
        await ctx.StartSessionAsync(Sds).ConfigureAwait(false);
        return await ctx.SendAsync(Reset).ConfigureAwait(false);
    }
}

/// <summary>Megane II / Scenic II UCH.</summary>
public sealed class Megane2UchPlugin : VirginizerPlugin
{
    public const string Sds = "Start Diagnostic Session", SdsStudy = "StartDiagSession Etude", ReadState = "Status général des opérations badges Bits", Erase = "RAZ EEPROM";

    public override PluginInfo Info { get; } = new("megane2-uch-reset", "Megane/Scenic II UCH Reset", "UCH Tools", "Megane II / Scenic II (UCH 84 J84)")
    {
        Description = "Reads whether the UCH is virgin (no card learnt) and erases its EEPROM (RAZ EEPROM).",
        Warnings = ["This plugin will ERASE YOUR UCH.", "Go away if you have no idea what that means."],
        EcuDefinition = "UCH_84_J84_03_60", Protocol = "CAN",
    };
    protected override string CheckTitle => "Check UCH virgin";
    protected override string ResetTitle => "Virginize UCH";
    protected override string ResetDescription => "Only offered when the UCH is coded. Starts the study session and sends RAZ EEPROM.";
    protected override IReadOnlyList<string> CheckRequests => [Sds, ReadState];
    protected override IReadOnlyList<string> ResetRequests => [SdsStudy, Erase];

    protected override async ValueTask<VirginCheck> CheckAsync(PluginContext ctx)
    {
        await ctx.StartSessionAsync(Sds).ConfigureAwait(false);
        var r = await ctx.SendAsync(ReadState).ConfigureAwait(false);
        return Value(r, "VSC UCH vierge (NbBadgeAppris=0)") switch
        {
            "Vierge" => new(VirginState.Virgin, "UCH virgin"),
            "Codée" => new(VirginState.Coded, "UCH coded"),
            _ => Unexpected(r),
        };
    }

    protected override async ValueTask<EcuResponse> ResetAsync(PluginContext ctx)
    {
        await ctx.StartSessionAsync(SdsStudy).ConfigureAwait(false);
        return await ctx.SendAsync(Erase).ConfigureAwait(false);
    }
}

/// <summary>Megane III / Scenic III BCM ("UCH").</summary>
public sealed class Megane3UchPlugin : VirginizerPlugin
{
    public const string Sds = "Start Diagnostic Session", ReadState = "Read_A_AC_General_Identifiers_Learning_Status_(bits)_BCM_Input/Output", Reset = "SR_RESERVED VSC 1";

    public override PluginInfo Info { get; } = new("megane3-uch-reset", "Megane/Scenic III UCH Reset", "UCH Tools", "Megane III / Scenic III (BCM X95)")
    {
        Description = "Reads the 'UCH virgin' flag and erases the UCH with SR_RESERVED VSC 1.",
        Warnings = ["This plugin will ERASE YOUR UCH.", "Go away if you have no idea what that means."],
        EcuDefinition = "BCM_X95_SW_2_V_1_2", Protocol = "CAN",
    };
    protected override string CheckTitle => "Check UCH virgin";
    protected override string ResetTitle => "Virginize UCH";
    protected override string ResetDescription => "Only offered when the UCH is coded. Starts the after-sales session and sends SR_RESERVED VSC 1.";
    protected override IReadOnlyList<string> CheckRequests => [Sds, ReadState];
    protected override IReadOnlyList<string> ResetRequests => [Sds, Reset];

    protected override async ValueTask<VirginCheck> CheckAsync(PluginContext ctx)
    {
        await ctx.StartSessionAsync(Sds).ConfigureAwait(false);
        var r = await ctx.SendAsync(ReadState).ConfigureAwait(false);
        return Value(r, "VSC UCH vierge (NbBadgeAppris=0)") switch
        {
            "Actif" => new(VirginState.Virgin, "UCH virgin"),
            "inactif" => new(VirginState.Coded, "UCH coded"),
            _ => Unexpected(r),
        };
    }

    protected override async ValueTask<EcuResponse> ResetAsync(PluginContext ctx)
    {
        await ctx.StartSessionAsync(Sds).ConfigureAwait(false);
        return await ctx.SendAsync(Reset).ConfigureAwait(false);
    }
}
