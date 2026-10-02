using Ddt4All.Comms;
using Ddt4All.Comms.Simulation;
using Ddt4All.Core.Ecu;
using Ddt4All.Plugins;
using CoreProtocol = Ddt4All.Core.Ecu.EcuProtocol;

namespace Ddt4All.Plugins.Tests;

/// <summary>Builds synthetic ECU definitions (the real ones live in ecu.zip, which the tests do not need).</summary>
internal sealed class Def
{
    public EcuFile File { get; }

    public Def(string name, CoreProtocol protocol = CoreProtocol.Can, string send = "745", string recv = "765", string funcAddr = "26")
    {
        File = new EcuFile { EcuName = name, Protocol = protocol, SendId = send, RecvId = recv, FuncAddr = funcAddr, FastInit = true };
    }

    public Def List(string name, params (long Value, string Text)[] items)
    {
        var d = new EcuData { Name = name, BitsCount = 8, BytesCount = 1 };
        foreach (var (v, t) in items) d.AddListItem(v, t);
        File.Data.Add(d);
        return this;
    }

    public Def Hex(string name, int bytes) { File.Data.Add(new EcuData { Name = name, BitsCount = 8 * bytes, BytesCount = bytes, Byte = true }); return this; }
    public Def Ascii(string name, int bytes) { File.Data.Add(new EcuData { Name = name, BitsCount = 8 * bytes, BytesCount = bytes, BytesAscii = true }); return this; }

    public Def Req(string name, string sent, (string Data, int FirstByte)[]? send = null, (string Data, int FirstByte)[]? recv = null)
    {
        var r = new EcuRequest { Name = name, SentBytes = sent };
        foreach (var (d, b) in send ?? []) r.SendItems.Add(new DataItem(d, b));
        foreach (var (d, b) in recv ?? []) r.ReceiveItems.Add(new DataItem(d, b));
        File.Requests.Add(r);
        return this;
    }

    public EcuFile Build() { File.ResolveReferences(); return File; }
}

internal sealed class EventLog : IProgress<PluginEvent>
{
    public List<PluginEvent> Events { get; } = [];
    public void Report(PluginEvent value) { lock (Events) Events.Add(value); }
    public IEnumerable<string> Tx => Events.Where(e => e.Kind == PluginEventKind.Tx).Select(e => e.Message[(e.Message.IndexOf(": ", StringComparison.Ordinal) + 2)..].Replace(" ", ""));
}

internal sealed class ScriptedInteraction(params int[] answers) : IPluginInteraction
{
    private readonly Queue<int> _answers = new(answers);
    public List<string> Prompts { get; } = [];
    public ValueTask<int> ChooseAsync(string title, string message, IReadOnlyList<string> choices, CancellationToken ct)
    {
        Prompts.Add(message);
        return new(_answers.Count > 0 ? _answers.Dequeue() : choices.Count - 1);
    }
}

internal static class Harness
{
    public static (SimulatedTransport Transport, EcuScript Script) Bus(EcuFile def, EcuScript script)
    {
        var t = new SimulatedTransport();
        if (def.Protocol == CoreProtocol.Can) t.AddCan(def.EcuName, Convert.ToUInt32(def.SendId, 16), script);
        else t.AddKLine(def.EcuName, Convert.ToByte(def.FuncAddr, 16), script);
        return (t, script);
    }

    public static async Task<(PluginResult Result, EventLog Log, EcuScript Script)> RunAsync(IPlugin plugin, string action, EcuFile? def, EcuScript script,
        bool expert = true, bool confirmed = true, Dictionary<string, string>? parameters = null, IPluginInteraction? interaction = null, CancellationToken ct = default)
    {
        var provider = new DictionaryEcuDefinitionProvider();
        if (def is not null && plugin.Info.EcuDefinition is { } n) provider.Add(n, def);
        var (transport, _) = def is not null ? Bus(def, script) : (new SimulatedTransport(), script);
        var log = new EventLog();
        var res = await new PluginRunner(provider).RunAsync(plugin, action, new PluginRunRequest
        {
            Transport = transport, ExpertMode = expert, Confirmed = confirmed, Parameters = parameters ?? new(),
            Interaction = interaction, Delay = (_, c) => Task.Delay(1, c),
        }, log, ct);
        return (res, log, script);
    }

    public static IEnumerable<string> Wire(EcuScript s) => s.Received.Select(Convert.ToHexString);
}
