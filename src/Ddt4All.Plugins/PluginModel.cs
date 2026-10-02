using System.Globalization;
using Ddt4All.Core.Codec;

namespace Ddt4All.Plugins;

/// <summary>How a plugin parameter is edited and validated.</summary>
public enum PluginParameterKind
{
    /// <summary>Free text (validated by <see cref="PluginParameter.MinLength"/>/<see cref="PluginParameter.MaxLength"/>).</summary>
    Text,
    /// <summary>Hexadecimal digits; spaces ignored. <see cref="PluginParameter.MinLength"/> counts hex digits.</summary>
    Hex,
    /// <summary>One of <see cref="PluginParameter.Choices"/>.</summary>
    Choice,
}

/// <summary>An input an action needs from the user (VIN, PIN/ISK, algorithm...).</summary>
public sealed record PluginParameter(string Id, string Label)
{
    public string Description { get; init; } = "";
    public PluginParameterKind Kind { get; init; }
    public string DefaultValue { get; init; } = "";
    /// <summary>Minimum length (characters, or hex digits for <see cref="PluginParameterKind.Hex"/>); 0 = optional.</summary>
    public int MinLength { get; init; }
    /// <summary>Maximum length, 0 = unlimited.</summary>
    public int MaxLength { get; init; }
    /// <summary>Values for <see cref="PluginParameterKind.Choice"/>.</summary>
    public IReadOnlyList<string> Choices { get; init; } = [];

    /// <summary>Returns null when the value is acceptable, otherwise a human readable problem.</summary>
    public string? Validate(string? value)
    {
        value ??= "";
        switch (Kind)
        {
            case PluginParameterKind.Choice:
                return Choices.Contains(value) ? null : $"{Label}: choose one of {string.Join(", ", Choices)}.";
            case PluginParameterKind.Hex:
                var digits = value.Replace(" ", "");
                foreach (var c in digits)
                    if (HexUtil.HexValue(c) < 0) return $"{Label}: only hexadecimal digits are allowed.";
                if (digits.Length < MinLength || (MaxLength > 0 && digits.Length > MaxLength))
                    return MinLength == MaxLength ? $"{Label}: exactly {MinLength} hex digits are required." : $"{Label}: {MinLength}-{MaxLength} hex digits are required.";
                return null;
            default:
                if (value.Length < MinLength || (MaxLength > 0 && value.Length > MaxLength))
                    return MinLength == MaxLength ? $"{Label}: exactly {MinLength} characters are required." : $"{Label}: length must be {MinLength}-{MaxLength}.";
                return null;
        }
    }
}

/// <summary>One thing the user can run on a plugin ("Check status", "Reset", ...).</summary>
public sealed record PluginAction(string Id, string Title)
{
    public string Description { get; init; } = "";
    /// <summary>True when the action changes ECU state. Writing actions need expert mode and an explicit confirmation.</summary>
    public bool Writes { get; init; }
    public IReadOnlyList<PluginParameter> Parameters { get; init; } = [];
    /// <summary>Names of the ECU-definition requests the action will use; checked against the definition before any traffic is sent.</summary>
    public IReadOnlyList<string> RequiredRequests { get; init; } = [];
    /// <summary>Estimated number of <see cref="PluginContext.Step"/> calls (progress bar scale); 0 = unknown.</summary>
    public int StepCount { get; init; }
}

/// <summary>Static description of a plugin.</summary>
public sealed record PluginInfo(string Id, string Name, string Category, string Vehicle)
{
    public string Description { get; init; } = "";
    /// <summary>Warnings shown before anything runs (the red text of the original dialogs).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
    /// <summary>ECU definition (file name without extension) the plugin drives; null if it does not talk to an ECU.</summary>
    public string? EcuDefinition { get; init; }
    /// <summary>Diagnostic bus the ECU is expected on (informational; the real address comes from the definition).</summary>
    public string? Protocol { get; init; }
    /// <summary>Preconditions to prepare on the vehicle before running (the "TIPS" of the original dialogs).</summary>
    public IReadOnlyList<string> Instructions { get; init; } = [];
    /// <summary>Needs an adapter connection (Python <c>need_hw</c>).</summary>
    public bool RequiresHardware { get; init; } = true;
    /// <summary>Marked experimental / untested by the original authors.</summary>
    public bool Experimental { get; init; }
}

/// <summary>Final state of a run.</summary>
public enum PluginOutcome
{
    /// <summary>Everything went as expected.</summary>
    Success,
    /// <summary>Completed, but the ECU answered something unexpected or the state needs attention.</summary>
    Warning,
    /// <summary>The ECU refused, did not answer or a safety check failed.</summary>
    Failed,
    /// <summary>The user cancelled.</summary>
    Cancelled,
    /// <summary>Nothing was sent: expert mode / confirmation / connection / parameters missing.</summary>
    Blocked,
}

/// <summary>Result of a plugin run.</summary>
public sealed record PluginResult(PluginOutcome Outcome, string Summary)
{
    /// <summary>Values read from the ECU (VIN, counters...), in display order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Values { get; init; } = [];
    /// <summary>Plugin specific state key (e.g. "virgin", "coded", "crash") for programmatic use.</summary>
    public string? State { get; init; }
    public bool IsSuccess => Outcome == PluginOutcome.Success;

    public static PluginResult Ok(string summary, string? state = null, params KeyValuePair<string, string>[] values) => new(PluginOutcome.Success, summary) { State = state, Values = values };
    public static PluginResult Warn(string summary, string? state = null) => new(PluginOutcome.Warning, summary) { State = state };
    public static PluginResult Fail(string summary) => new(PluginOutcome.Failed, summary);
    public static PluginResult Blocked(string summary) => new(PluginOutcome.Blocked, summary);
}

public enum PluginEventKind { Step, Info, Tx, Rx, Warning, Error, Result }

/// <summary>One line of the step-by-step log.</summary>
public readonly record struct PluginEvent(PluginEventKind Kind, string Message, int Step, int TotalSteps, DateTimeOffset Time)
{
    /// <summary>0..1, or -1 when the total is unknown.</summary>
    public double Fraction => TotalSteps > 0 ? Math.Min(1.0, (double)Step / TotalSteps) : -1;
    public override string ToString() => $"[{Kind}] {Message}";
}

/// <summary>Raised for plugin level failures (missing request in the definition, refused session, bad input). Becomes a Failed result.</summary>
public sealed class PluginException(string message) : Exception(message);

/// <summary>Lets a plugin ask the operator something mid-run (e.g. "Validate or cancel the learned card?").</summary>
public interface IPluginInteraction
{
    /// <summary>Shows <paramref name="message"/> and returns the index of the chosen option.</summary>
    ValueTask<int> ChooseAsync(string title, string message, IReadOnlyList<string> choices, CancellationToken ct);
}

/// <summary>A plugin. Implementations are registered explicitly in <see cref="PluginRegistry"/> (no reflection scanning).</summary>
public interface IPlugin
{
    PluginInfo Info { get; }
    IReadOnlyList<PluginAction> Actions { get; }
    /// <summary>
    /// Executes one action. Called by <see cref="PluginRunner"/> only after the safety gates (expert mode, confirmation,
    /// parameter validation, definition/request availability) have passed and the transport is pointed at the ECU.
    /// </summary>
    ValueTask<PluginResult> RunAsync(PluginAction action, PluginContext ctx);
}
