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

    /// <summary>所属 session 准备 end_session 时被询问。返回 false 阻止退出，注入提醒文本由调用方做。</summary>
    Task<bool> OnCanExitAsync();
}
