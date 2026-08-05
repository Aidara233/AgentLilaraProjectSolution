using System.Globalization;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.MindTools;

/// <summary>
/// 写断言工具：把一条三元组断言写入 Claims 表。
/// strength 为态度强度（方向+程度，如 0.9=爱、-0.8=不喜欢），credibility 为可信度锚点（写入者定初值）。
/// </summary>
[ToolMeta(Group = "mind")]
public class ClaimTool : ITool
{
    private readonly IToolContext _ctx;

    public ClaimTool(IToolContext ctx) => _ctx = ctx;

    public string Name => "claim";
    public string Description =>
        "写一条断言到记忆库（Claim 三元组）。subject/predicate/object 为表面词；" +
        "strength 为态度强度 [-1,1]（0.9=爱、0.4=喜欢、-0.8=不喜欢）；" +
        "credibility 为可信度锚点 (0,1]（明确陈述≈1.0，推断给 0.4~0.7）；raw 可传整句原文。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("subject", "主体表面词", 0),
        new("predicate", "谓词表面词", 1),
        new("object", "客体表面词", 2),
        new("subject_type", "（可选）主体槽位类型，默认 entity", 3, false),
        new("object_type", "（可选）客体槽位类型，默认 entity", 4, false),
        new("strength", "（可选）态度强度 [-1,1]，如 0.9=爱、0.4=喜欢、-0.8=不喜欢", 5, false),
        new("credibility", "（可选）可信度锚点 (0,1]，明确陈述≈1.0，推断给 0.4~0.7", 6, false),
        new("raw", "（可选）整句原文，默认拼 \"subject predicate object\"", 7, false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        // 服务取不到时返回 failed 并说明（不用 Require 抛异常）
        var writer = _ctx.GetService<IWriterAccess>();
        if (writer == null)
            return new ToolResult { Status = "failed", Error = "写入口服务（IWriterAccess）不可用，请确认 Writer 模块已初始化。" };

        if (inputs.Count < 3
            || string.IsNullOrWhiteSpace(inputs[0])
            || string.IsNullOrWhiteSpace(inputs[1])
            || string.IsNullOrWhiteSpace(inputs[2]))
            return new ToolResult { Status = "failed", Error = "subject、predicate、object 均不能为空。" };

        var subject = inputs[0].Trim();
        var predicate = inputs[1].Trim();
        var obj = inputs[2].Trim();
        var subjectType = inputs.Count > 3 && !string.IsNullOrWhiteSpace(inputs[3]) ? inputs[3].Trim() : "entity";
        var objectType = inputs.Count > 4 && !string.IsNullOrWhiteSpace(inputs[4]) ? inputs[4].Trim() : "entity";

        // strength：可选，[-1,1] 内才接受
        float? strength = null;
        if (inputs.Count > 5 && !string.IsNullOrWhiteSpace(inputs[5]))
        {
            if (!float.TryParse(inputs[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
                return new ToolResult { Status = "failed", Error = $"strength 无法解析为数值：{inputs[5]}" };
            if (s < -1f || s > 1f)
                return new ToolResult { Status = "failed", Error = $"strength 必须在 [-1,1] 内，收到：{s}" };
            strength = s;
        }

        // credibility：可选，可信度锚点 (0,1]（0 与超出 1 都会破坏 logit 基础项）
        float? credibility = null;
        if (inputs.Count > 6 && !string.IsNullOrWhiteSpace(inputs[6]))
        {
            if (!float.TryParse(inputs[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var c))
                return new ToolResult { Status = "failed", Error = $"credibility 无法解析为数值：{inputs[6]}" };
            if (c <= 0f || c > 1f)
                return new ToolResult { Status = "failed", Error = $"credibility 必须在 (0,1] 内，收到：{c}" };
            credibility = c;
        }

        var raw = inputs.Count > 7 && !string.IsNullOrWhiteSpace(inputs[7])
            ? inputs[7].Trim()
            : $"{subject} {predicate} {obj}";

        var claim = new ClaimDto
        {
            Subject = new SlotDto { Type = subjectType, Index = subject },
            Predicate = new SlotDto { Type = "relation", Index = predicate },
            Object = new SlotDto { Type = objectType, Index = obj },
            Raw = raw,
            Strength = strength,
            Credibility = credibility,
        };

        int id = await writer.WriteClaimAsync(claim);
        return new ToolResult { Status = "success", Data = $"Claim #{id} 已写入：{raw}" };
    }
}
