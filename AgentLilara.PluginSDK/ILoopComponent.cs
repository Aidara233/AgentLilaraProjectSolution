// AgentLilara.PluginSDK/ILoopComponent.cs
namespace AgentLilara.PluginSDK;

public interface ILoopComponent
{
    ComponentMeta Meta { get; }
    IEnumerable<ITool> Tools { get; }

    Task OnInitAsync(ILoopComponentContext context, InitReason reason);
    Task<ShutdownResponse> OnShutdownRequestedAsync(ShutdownReason reason);
    Task OnShutdownAsync(ShutdownReason reason);

    Task OnEnabledAsync();
    Task OnDisabledAsync();

    Task OnActivatedAsync();
    Task OnPauseAsync();
    Task OnBeforeInvokeAsync();
    Task OnAfterInvokeAsync();

    string? BuildPromptSection();
}
