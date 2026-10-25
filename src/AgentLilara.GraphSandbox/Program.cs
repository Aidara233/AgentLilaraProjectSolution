using AgentLilara.GraphRuntime.Contracts;
using AgentLilara.GraphRuntime.Graph;
using AgentLilara.GraphSandbox;

var directory = "sandbox-inbox";
var intervalMs = 2000;
var once = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--dir":
            directory = args[++i];
            break;
        case "--interval-ms":
            intervalMs = int.Parse(args[++i]);
            break;
        case "--once":
            once = true;
            break;
        default:
            Console.WriteLine($"unknown arg '{args[i]}'");
            return 2;
    }
}

System.IO.Directory.CreateDirectory(directory);

var runner = new SandboxRunner(SampleGraphs.LoadPresetMention(), SampleGraphs.LoadPresetCounter());
var poller = new FilePoller(directory, "*.txt", "*.md");
long seq = 0;

Console.WriteLine($"sandbox: watching {Path.GetFullPath(directory)} (interval {intervalMs} ms, once={once})");
Console.WriteLine("  图只产出意图；骨架（入库/会话/出向）尚未接入。");

do
{
    var messages = poller.Poll();

    if (messages.Count == 0)
    {
        // 没有新消息也要推进动态图（时钟沿）
        Print(runner.Tick(null, seq++), null);
    }
    else
    {
        foreach (var message in messages)
        {
            // 沙箱里用文件名当发送者，正文里出现 @ 视为点名
            var evt = InboundEvent.Message(
                adapterId: "file",
                channelKey: "sandbox",
                text: message.Text,
                channelKind: ChannelKind.Group,
                senderId: message.FileName,
                mentionsBot: message.Text.Contains('@'));

            Print(runner.Tick(evt, seq++), message.FileName);
        }
    }

    if (once)
    {
        break;
    }

    Thread.Sleep(intervalMs);
}
while (true);

return 0;

static void Print(SandboxReport report, string? source)
{
    Console.WriteLine($"--- seq {report.Seq}" + (source is null ? " (no event)" : $" | {source}") + " ---");

    if (report.Event is { } evt)
    {
        Console.WriteLine($"  event  {evt.Kind} adapter={evt.AdapterId} channel={evt.ChannelKey} mention={evt.MentionsBot} text={Truncate(evt.Text)}");
    }

    Console.WriteLine(report.StaticOk
        ? $"  static  ok   order=[{string.Join(" → ", report.StaticOrder)}]"
        : $"  static  SOLVE-INVALID unresolved=[{string.Join(",", report.StaticUnresolved)}]");

    if (report.StaticIntents.Count == 0)
    {
        Console.WriteLine("  static  intents: (none)");
    }

    foreach (var intent in report.StaticIntents)
    {
        Console.WriteLine($"  static  intent {intent.Kind} target='{intent.Target}' args={Describe(intent.Args)}");
    }

    Console.WriteLine(report.DynamicOk
        ? "  dynamic ok"
        : $"  dynamic SOLVE-INVALID unresolved=[{string.Join(",", report.DynamicUnresolved)}]");

    foreach (var (name, cell) in report.DynamicSnapshot)
    {
        Console.WriteLine($"  dynamic read {name} = {cell}   (本拍快照)");
    }

    foreach (var (name, cell) in report.DynamicVariables)
    {
        Console.WriteLine($"  dynamic var  {name} = {cell}   (提交后)");
    }

    if (report.DynamicSuppressed.Count > 0)
    {
        Console.WriteLine($"  dynamic suppressed=[{string.Join(",", report.DynamicSuppressed)}]（电平为真但无上升沿）");
    }

    foreach (var intent in report.DynamicIntents)
    {
        Console.WriteLine($"  dynamic intent {intent.Kind} target='{intent.Target}' args={Describe(intent.Args)}");
    }
}

static string Describe(IReadOnlyDictionary<string, Cell> args) =>
    string.Join(", ", args.Where(kv => kv.Key != "event").Select(kv => $"{kv.Key}={kv.Value}"));

static string Truncate(string text) => text.Length <= 60 ? text : text[..60] + "…";
