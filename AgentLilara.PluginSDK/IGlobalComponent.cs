// AgentLilara.PluginSDK/IGlobalComponent.cs
namespace AgentLilara.PluginSDK;

public interface IGlobalComponent
{
    ComponentMeta Meta { get; }
    IEnumerable<ITool> Tools { get; }

    Task OnInitAsync(IGlobalComponentContext context, InitReason reason);
    Task<ShutdownResponse> OnShutdownRequestedAsync(ShutdownReason reason);
    Task OnShutdownAsync(ShutdownReason reason);

    Task OnEnabledAsync();
    Task OnDisabledAsync();

    string? BuildPromptSection(LoopInfo caller);

    /// <summary>会话开头一次性静态参考段（技能清单等）。仅在会话首次上下文构建时调用一次；
    /// 恢复会话跳过（内容已随首轮持久化在存档历史中）。区别于 BuildPromptSection（每轮动态注入）。</summary>
    string? BuildStartPromptSection(LoopInfo caller) => null;
}
