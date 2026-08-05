using System.Text;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.MindTools;

/// <summary>
/// 写经历工具：把一段经历/反思写入 Journals 表（L2 Experience / L3 Reflection）。
/// 可携带情绪信号（JSON 数组字符串），写入时实时应用情绪（Writer 的 CollectSignals）。
/// </summary>
[ToolMeta(Group = "mind")]
public class JournalTool : ITool
{
    private readonly IToolContext _ctx;

    public JournalTool(IToolContext ctx) => _ctx = ctx;

    public string Name => "journal";
    public string Description =>
        "写一条经历到记忆库（Journal）。kind=Experience 记 L2 经历、Reflection 记 L3 反思；" +
        "emotion 传情绪信号 JSON 数组（[{\"Axis\":\"joy\",\"Target\":0.4,\"Reason\":\"...\"}]），写入时实时应用情绪。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("content", "经历正文（第一人称）", 0),
        new("kind", "（可选）Experience（L2经历）/ Reflection（L3反思），默认 Experience", 1, false),
        new("emotion", "（可选）情绪信号 JSON 数组字符串，如 [{\"Axis\":\"joy\",\"Target\":0.4,\"Reason\":\"...\"}]", 2, false),
        new("channel_id", "（可选）内部频道ID", 3, false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        // 服务取不到时返回 failed 并说明（不用 Require 抛异常）
        var writer = _ctx.GetService<IWriterAccess>();
        if (writer == null)
            return new ToolResult { Status = "failed", Error = "写入口服务（IWriterAccess）不可用，请确认 Writer 模块已初始化。" };

        if (inputs.Count == 0 || string.IsNullOrWhiteSpace(inputs[0]))
            return new ToolResult { Status = "failed", Error = "content 不能为空。" };

        var content = inputs[0].Trim();
        var kind = inputs.Count > 1 && !string.IsNullOrWhiteSpace(inputs[1])
            ? inputs[1].Trim()
            : "Experience";
        var emotionJson = inputs.Count > 2 && !string.IsNullOrWhiteSpace(inputs[2])
            ? inputs[2].Trim()
            : null;
        var channelId = ParseInt(inputs, 3);

        if (kind is not ("Experience" or "Reflection"))
            return new ToolResult { Status = "failed", Error = $"kind 必须为 Experience 或 Reflection，收到：{kind}" };

        var journal = new JournalDto
        {
            Content = content,
            Kind = kind,
            ChannelId = channelId,
            EmotionSignalsJson = emotionJson,
        };

        int id = await writer.WriteJournalAsync(journal);

        var sb = new StringBuilder();
        sb.AppendLine($"Journal #{id} 已写入（{kind}）。");
        sb.AppendLine($"内容：{content}");
        if (channelId != null) sb.AppendLine($"频道：{channelId}");
        if (emotionJson != null) sb.AppendLine($"情绪信号：{emotionJson}");
        return new ToolResult { Status = "success", Data = sb.ToString().TrimEnd() };
    }

    private static int? ParseInt(List<string> inputs, int index)
        => inputs.Count > index && int.TryParse(inputs[index], out var v) ? v : null;
}
