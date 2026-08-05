using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.MindTools;

/// <summary>
/// 经历注入组件（Global）。把最近经历注入上下文作为"底色"。
/// 策略（03"心智插件"）：
/// - 首次调用（初建）：按 L 等级扩展时间窗——L3（反思）宽窗时间长也纳入，L2（经历）窄窗仅最近
/// - 后续调用：只注入新增的 L3（L2 太嘈杂不值得每轮刷新，L3 是浓缩信号防互相干扰）
/// - L1/L0 不主动注入（仅作 Evidence 被引用）
/// - 标签 [相对时间 · 来源]：来源优先于类型（本频道/频道名/复盘·内部）
/// 只读 IRetrieveAccess，不碰表。
/// </summary>
[Component(Name = "mind-experience", Scope = ComponentScope.Global)]
public class MindExperienceComponent : GlobalComponentBase
{
    private IRetrieveAccess? _retrieve;
    private HashSet<int> _injectedIds = new();   // 已注入标记（去重）
    private bool _initialized;                   // 初建是否已做

    public override ComponentMeta Meta => new()
    {
        Name = "mind-experience",
        Description = "经历注入（近期经历作为底色）",
        DefaultEnabled = true,
        PromptPriority = 25,
    };

    public override IEnumerable<ITool> Tools => Array.Empty<ITool>();

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        _retrieve = context.GetService<IRetrieveAccess>();
        return Task.CompletedTask;
    }

    public override string? BuildPromptSection(LoopInfo caller)
    {
        if (_retrieve == null) return null;

        // 首版简化：只做"最近经历"注入（L 等级时间窗的精细实现留后续）
        // 用 query 空串触发检索最近内容（RetrieveService 支持空查询返回最近）
        var result = _retrieve.SearchAsync("", limit: 5).GetAwaiter().GetResult();
        if (result == null || result.Hits.Count == 0) return null;

        var lines = new List<string>();
        foreach (var hit in result.Hits)
        {
            if (_injectedIds.Contains(hit.Id)) continue;
            _injectedIds.Add(hit.Id);

            var time = FormatRelative(hit.OccurredAt);
            var kind = hit.Kind == "Journal" ? "经历" : "断言";
            var infer = hit.IsInference ? "[推断] " : "";
            lines.Add($"[{time} · {kind}] {infer}{hit.Raw}");
        }

        if (lines.Count == 0) return null;
        return "近期记忆：\n" + string.Join("\n", lines);
    }

    private static string FormatRelative(DateTime dt)
    {
        var now = DateTime.Now;
        var diff = now - dt;
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}分钟前";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}小时前";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}天前";
        if (diff.TotalDays < 30) return $"{(int)(diff.TotalDays / 7)}周前";
        return $"{dt:MM-dd}";
    }
}
