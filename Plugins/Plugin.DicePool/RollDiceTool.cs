using System.Text;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.DicePool;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "摇骰子：从大骰子随机摇 N 个面，用随机碎片碰撞灵感")]
public class RollDiceTool : ITool
{
    private readonly IDiceService? _dice;

    public string Name => "roll_dice";
    public string Description => "从大骰子中随机抽取 N 个面，把不相关的碎片放在一起碰撞出灵感。推荐每次摇 3-5 个，太多容易信息过载。大部分结果没价值属于正常情况，随缘追感兴趣的就好。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("count", "要摇几个面，默认 3", 0, false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public RollDiceTool(IDiceService? diceService)
    {
        _dice = diceService;
    }

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (_dice == null)
            return Fail("骰子服务不可用");

        var count = 3;
        if (inputs.Count > 0 && int.TryParse(inputs[0], out var n) && n > 0)
            count = n;

        var results = await _dice.RollAsync(count, ct);

        if (results.Count == 0)
            return Ok("骰子上还没刻面呢——没有插件注册骰子面。");

        var sb = new StringBuilder();
        foreach (var r in results)
        {
            sb.AppendLine($"▸ {r.Meta}");
            sb.AppendLine($"  {r.Content}");
            if (!string.IsNullOrWhiteSpace(r.FollowUp))
                sb.AppendLine($"  ▶ {r.FollowUp}");
            sb.AppendLine();
        }
        return Ok(sb.ToString().TrimEnd());
    }

    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
}
