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
}
