using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.MindTools;

/// <summary>
/// 情绪注入组件（Global）。每轮注入当前情绪状态描述（非五轴原始数值）。
/// 按 caller.LoopType 区分：Working/Express 注入，Review 不注入（冷静验证不带情绪偏置）。
/// 只读 IEmotionAccess，不写情绪（唯一写入者是 EmotionService）。
/// </summary>
[Component(Name = "mind-emotion", Scope = ComponentScope.Global)]
public class MindEmotionComponent : GlobalComponentBase
{
    private IEmotionAccess? _emotion;

    public override ComponentMeta Meta => new()
    {
        Name = "mind-emotion",
        Description = "情绪状态注入",
        DefaultEnabled = true,
        PromptPriority = 20,   // 靠前：状态描述应在组件通知之前
    };

    public override IEnumerable<ITool> Tools => Array.Empty<ITool>();

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        _emotion = context.GetService<IEmotionAccess>();
        return Task.CompletedTask;
    }

    public override string? BuildPromptSection(LoopInfo caller)
    {
        // Review 不注入情绪（验证应冷静、看证据）
        if (string.Equals(caller.LoopType, "review", StringComparison.OrdinalIgnoreCase))
            return null;

        if (_emotion == null) return null;

        // BuildPromptSection 是同步签名，用同步阻塞读当前状态
        var state = _emotion.GetCurrentAsync().GetAwaiter().GetResult();
        if (state == null) return null;

        // 生成状态描述（模型感知状态词，不是数值）
        var parts = new List<string>();
        if (state.Excitement > 0.3) parts.Add("精神不错、有干劲");
        else if (state.Excitement < -0.3) parts.Add("有点提不起劲");
        if (state.Joy > 0.3) parts.Add("心情愉快");
        else if (state.Joy < -0.3) parts.Add("心情有点低落");
        if (state.Alertness > 0.3) parts.Add("感觉敏锐");
        else if (state.Alertness < -0.3) parts.Add("有点迟钝麻木");
        if (state.Satisfaction > 0.3) parts.Add("感到满足");
        else if (state.Satisfaction < -0.3) parts.Add("觉得还差点什么");
        if (state.Optimism > 0.3) parts.Add("对未来看好");
        else if (state.Optimism < -0.3) parts.Add("不太乐观");

        if (parts.Count == 0) return "（情绪平稳）";
        return "（情绪：我" + string.Join("，", parts) + "）";
    }
}
