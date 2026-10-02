using Ddt4All.Core.Codec;
using Ddt4All.Core.Ecu;

namespace Ddt4All.Plugins.Vehicles;

/// <summary>ISK to after-sales PIN (APV code) algorithms for the MC9S12DG256 based Megane II / Scenic II UCH, and ISK extraction.</summary>
public static class MeganeCardAlgorithms
{
    // Copied verbatim from the Python plugin (including its repeated entries 4, 5 and 8).
    private static readonly int[] Mess =
    [
        19, 29, 39, 4, 24, 46, 31, 12,
        16, 28, 7, 10, 15, 0, 40, 26,
        18, 20, 3, 8, 34, 8, 47, 21,
        2, 25, 38, 6, 4, 22, 14, 33,
        17, 1, 44, 11, 32, 41, 36, 42,
        5, 45, 5, 43, 30, 37, 13, 27,
    ];
    private static readonly byte[] Xor1 = [0x10, 0x20, 0x20, 0x10, 0x81, 0x88];
    private static readonly byte[] Xor2 = [0x10, 0x20, 0x28, 0x10, 0x81, 0x88];

    /// <summary>Computes the 12 hex digit PIN from a 12 hex digit ISK; null if the ISK is not 6 bytes of hex. <paramref name="algo2"/> selects "algo 2".</summary>
    public static string? Pin(string iskHex, bool algo2 = false)
    {
        var isk = HexUtil.Parse(iskHex.Replace(" ", ""));
        if (isk is not { Length: 6 }) return null;
        var xor = algo2 ? Xor2 : Xor1;
        var outp = new byte[6];
        for (int i = 0; i < 48; i++)
        {
            int k = Mess[i];
            int src = (isk[k >> 3] >> (7 - (k & 7))) & 1;
            int bas = (xor[i >> 3] >> (7 - (i & 7))) & 1;
            if ((bas ^ src) != 0) outp[i >> 3] |= (byte)(0x80 >> (i & 7));
        }
        return Convert.ToHexString(outp);
    }

    /// <summary>The ISK is bytes 19..24 (0-based, service id included) of the "Trame AB" reply; null if the reply is too short.</summary>
    public static string? ExtractIsk(ReadOnlySpan<byte> reply) => reply.Length >= 25 ? Convert.ToHexString(reply.Slice(19, 6)) : null;
}

/// <summary>
/// Megane II / Scenic II card (key) programming through the UCH after-sales (APV) mode. Marked EXPERIMENTAL / NOT TESTED by the original authors.
/// The Python dialog was a live window polling the UCH every 1.5 s; here the same procedure is one guided action with operator prompts and a keep-alive.
/// </summary>
public sealed class Megane2CardProgrammingPlugin : IPlugin
{
    public const string Sds = "Start Diagnostic Session", EnterApv = "ACCEDER AU MODE APRES-VENTE", Learn = "APPRENDRE BADGE",
        BitsStatus = "Status général des opérations badges Bits", BytesStatus = "Status général des opérations badges Octets",
        ReservedFrame = "Trame AB: Trame réservée", Validate = "SORTIE DU MODE APV : VALIDATION", Abandon = "SORTIE DU MODE APV : ABANDON";

    /// <summary>The Python timer interval.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1500);
    private const int MaxPolls = 10;

    private static readonly PluginParameter AlgoParam = new("algo", "PIN algorithm")
    {
        Description = "Algo 2 is the alternative computation some UCH need when the first PIN is not accepted.",
        Kind = PluginParameterKind.Choice, Choices = ["Algo 1", "Algo 2"], DefaultValue = "Algo 1",
    };

    public PluginInfo Info { get; } = new("megane2-card-programming", "Megane/Scenic II card programming", "Keys", "Megane II / Scenic II (UCH 84 J84)")
    {
        Description = "Reads the ISK, computes the after-sales PIN, enters APV mode and learns new key cards.",
        Warnings =
        [
            "EXPERIMENTAL: NOT TESTED YET (original warning).",
            "Learning cards rewrites the anti-theft data of the vehicle. A wrongly validated card set can leave the car unable to start.",
            "Keep the battery charged and the ignition on for the whole procedure.",
        ],
        EcuDefinition = "UCH_84_J84_03_60", Protocol = "CAN", Experimental = true,
    };

    public IReadOnlyList<PluginAction> Actions { get; } =
    [
        new PluginAction("isk", "Read ISK and compute PIN")
        {
            Description = "Read-only: reads the ISK from the UCH and shows the PIN computed from it.",
            StepCount = 3, Parameters = [AlgoParam], RequiredRequests = [Sds, ReservedFrame],
        },
        new PluginAction("status", "Read learning status")
        {
            Description = "Read-only: PIN recognised / learning mode, number of cards learnt, current card IDE.",
            StepCount = 3, RequiredRequests = [Sds, BitsStatus, BytesStatus],
        },
        new PluginAction("learn", "Learn cards")
        {
            Description = "Enters after-sales mode with the PIN and guides you through learning cards, then validates or abandons.",
            Writes = true, StepCount = 8, Parameters =
            [
                AlgoParam,
                new PluginParameter("pin", "PIN override") { Description = "12 hex digits; leave empty to compute it from the ISK.", Kind = PluginParameterKind.Hex, MaxLength = 12 },
            ],
            RequiredRequests = [Sds, ReservedFrame, EnterApv, BitsStatus, BytesStatus, Learn, Validate, Abandon],
        },
    ];

    public async ValueTask<PluginResult> RunAsync(PluginAction action, PluginContext ctx)
    {
        switch (action.Id)
        {
            case "isk": return await ReadIskAsync(ctx).ConfigureAwait(false);
            case "status": return await ReadStatusAsync(ctx).ConfigureAwait(false);
            case "learn": return await LearnAsync(ctx).ConfigureAwait(false);
            default: return PluginResult.Fail($"Unknown action {action.Id}");
        }
    }

    private static bool Algo2(PluginContext ctx) => ctx.Param("algo") == "Algo 2";

    private static async ValueTask<(string? Isk, string? Pin)> GetIskAndPinAsync(PluginContext ctx)
    {
        ctx.Step("Reading ISK");
        var r = await ctx.SendAsync(ReservedFrame).ConfigureAwait(false);
        if (!r.IsPositive) { ctx.Error("Cannot get ISK, check connections and UCH compatibility"); return (null, null); }
        var isk = MeganeCardAlgorithms.ExtractIsk(r.Raw);
        if (isk is null) { ctx.Error("ISK frame is too short"); return (null, null); }
        return (isk, MeganeCardAlgorithms.Pin(isk, Algo2(ctx)));
    }

    private static async ValueTask<PluginResult> ReadIskAsync(PluginContext ctx)
    {
        ctx.Step("Starting diagnostic session");
        await ctx.StartSessionAsync(Sds).ConfigureAwait(false);
        var (isk, pin) = await GetIskAndPinAsync(ctx).ConfigureAwait(false);
        if (isk is null || pin is null) return PluginResult.Fail("Cannot get ISK, check connections and UCH compatibility");
        ctx.Step("Computing PIN");
        return PluginResult.Ok($"PIN {pin}", "isk", new("ISK", isk), new("PIN", pin));
    }

    private sealed record Status(bool PinRecognised, bool KeyLearning, bool UchLearning, string Text)
    {
        public bool Armed => PinRecognised && (KeyLearning || UchLearning);
    }

    private static async ValueTask<Status?> ReadBitsAsync(PluginContext ctx)
    {
        var r = await ctx.SendAsync(BitsStatus).ConfigureAwait(false);
        if (!r.IsPositive) return null;
        string? ok = r["VSC Code APV_Reconnu"], reaff = r["VSC ModeAPV_ReaffArmé"], uch = r["VSC ModeAPV_AppUCH_Armé"];
        if (ok == "0") return new(false, false, false, "PIN CODE NOT RECOGNIZED");
        if (reaff == "1") return new(true, true, false, "PIN CODE OK / KEY LEARNING");
        if (uch == "1") return new(true, false, true, "PIN CODE OK / UCH LEARNING");
        return new(ok == "1", false, false, "UCH NOT READY");
    }

    private static async ValueTask<PluginResult> ReadStatusAsync(PluginContext ctx)
    {
        ctx.Step("Starting diagnostic session");
        await ctx.StartSessionAsync(Sds).ConfigureAwait(false);
        ctx.Step("Reading APV status");
        var st = await ReadBitsAsync(ctx).ConfigureAwait(false);
        ctx.Step("Reading card counters");
        var bytes = await ctx.SendAsync(BytesStatus).ConfigureAwait(false);
        var values = new List<KeyValuePair<string, string>>();
        if (bytes.IsPositive)
        {
            values.Add(new("Cards learnt", bytes["VSC NbTotalDeBadgeAppris"] ?? "?"));
            values.Add(new("Card IDE", bytes["VSC Code_IDE"] ?? "?"));
        }
        if (st is null) return new PluginResult(PluginOutcome.Warning, "UNEXPECTED RESPONSE") { Values = values };
        return new PluginResult(st.Armed ? PluginOutcome.Success : PluginOutcome.Warning, st.Text) { Values = values, State = st.Armed ? "apv-armed" : "apv-idle" };
    }

    private static async ValueTask<Status?> PollUntilArmedAsync(PluginContext ctx)
    {
        Status? last = null;
        for (int i = 0; i < MaxPolls; i++)
        {
            last = await ReadBitsAsync(ctx).ConfigureAwait(false);
            if (last is { Armed: true }) return last;
            ctx.Info(last?.Text ?? "No status reply");
            await ctx.Delay(PollInterval, ctx.Cancellation).ConfigureAwait(false);
        }
        return last;
    }

    /// <summary>Shows an operator prompt while polling the UCH like the Python timer did, so the APV session does not time out.</summary>
    private static async ValueTask<int> ChooseWithKeepAliveAsync(PluginContext ctx, string title, string message, params string[] choices)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ctx.Cancellation);
        var keep = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await ctx.Delay(PollInterval, stop.Token).ConfigureAwait(false);
                    await ctx.SendCleanupAsync(BitsStatus, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* keep-alive is best effort */ }
        });
        try { return await ctx.ChooseAsync(title, message, choices).ConfigureAwait(false); }
        finally { await stop.CancelAsync().ConfigureAwait(false); await keep.ConfigureAwait(false); }
    }

    private static async ValueTask<PluginResult> LearnAsync(PluginContext ctx)
    {
        string pin = ctx.Param("pin").Replace(" ", "").ToUpperInvariant();
        if (pin.Length != 0 && pin.Length != 12) return PluginResult.Blocked("PIN override must be empty or exactly 12 hex digits.");

        ctx.Step("Starting diagnostic session");
        await ctx.StartSessionAsync(Sds).ConfigureAwait(false);
        if (pin.Length == 0)
        {
            var (isk, computed) = await GetIskAndPinAsync(ctx).ConfigureAwait(false);
            if (isk is null || computed is null) return PluginResult.Fail("Cannot get ISK, check connections and UCH compatibility. Nothing was written.");
            pin = computed;
            ctx.Info($"ISK {isk} -> PIN {pin}");
        }

        bool inApvMode = false;
        try
        {
            ctx.Step("Entering after-sales (APV) mode");
            var enter = await ctx.SendAsync(EnterApv, ("Code APV", pin)).ConfigureAwait(false);
            if (!PluginContext.Succeeded(enter)) return PluginResult.Fail($"The UCH refused the APV code ({enter.NegativeResponseText ?? "no reply"}).");
            inApvMode = true;

            ctx.Step("Waiting for the UCH to accept the PIN");
            var st = await PollUntilArmedAsync(ctx).ConfigureAwait(false);
            if (st is not { Armed: true })
            {
                await AbandonAsync(ctx).ConfigureAwait(false); inApvMode = false;
                return PluginResult.Fail(st?.Text is { } t ? $"{t}. APV mode abandoned, nothing was learnt." : "UCH NOT READY. APV mode abandoned.");
            }
            ctx.Info(st.Text);

            int learnt = 0;
            while (true)
            {
                ctx.Step("Waiting for operator");
                int pick = await ChooseWithKeepAliveAsync(ctx, "Card learning",
                    learnt == 0 ? "The UCH accepted the PIN. Insert the card to learn, then choose Learn." : $"{learnt} card(s) learnt in this session. Learn another card, validate, or abandon.",
                    "Learn card", "Validate", "Abandon").ConfigureAwait(false);

                if (pick == 0)
                {
                    ctx.Step("Learning card");
                    var l = await ctx.SendAsync(Learn).ConfigureAwait(false);
                    if (!PluginContext.Succeeded(l)) { ctx.Error($"Learn refused ({l.NegativeResponseText ?? "no reply"})"); continue; }
                    learnt++;
                    var b = await ctx.SendAsync(BytesStatus).ConfigureAwait(false);
                    if (b.IsPositive) ctx.Info($"Cards learnt: {b["VSC NbTotalDeBadgeAppris"]}, card IDE {b["VSC Code_IDE"]}");
                    continue;
                }

                ctx.Step(pick == 1 ? "Validating" : "Abandoning");
                if (pick == 1)
                {
                    var v = await ctx.SendAsync(Validate).ConfigureAwait(false);
                    inApvMode = false;
                    return PluginContext.Succeeded(v) ? PluginResult.Ok($"Validated: {learnt} card(s) learnt.", "validated") : PluginResult.Fail("VALIDATION FAILED");
                }
                await AbandonAsync(ctx).ConfigureAwait(false); inApvMode = false;
                return new PluginResult(PluginOutcome.Cancelled, "Abandoned: APV mode left without validating.");
            }
        }
        finally
        {
            // Cancelled or failed while the UCH is still in a learning mode: always try to abandon so it is not left half-programmed.
            if (inApvMode)
            {
                try { await ctx.SendCleanupAsync(Abandon, TimeSpan.FromSeconds(5)).ConfigureAwait(false); ctx.Warn("APV mode abandoned during cleanup."); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { ctx.Error("Could not abandon APV mode: " + ex.Message); }
            }
        }
    }

    private static async ValueTask AbandonAsync(PluginContext ctx)
    {
        var r = await ctx.SendCleanupAsync(Abandon, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (!PluginContext.Succeeded(r)) ctx.Warn("The UCH did not confirm leaving APV mode.");
    }
}
