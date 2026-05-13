// AgentLilara.PluginSDK/ComponentBase.cs
namespace AgentLilara.PluginSDK;

public abstract class LoopComponentBase : ILoopComponent
{
    public abstract ComponentMeta Meta { get; }
    public abstract IEnumerable<ITool> Tools { get; }

    public virtual Task OnInitAsync(ILoopComponentContext context, InitReason reason) => Task.CompletedTask;
    public virtual Task<ShutdownResponse> OnShutdownRequestedAsync(ShutdownReason reason) => Task.FromResult(ShutdownResponse.Ok);
    public virtual Task OnShutdownAsync(ShutdownReason reason) => Task.CompletedTask;
    public virtual Task OnEnabledAsync() => Task.CompletedTask;
    public virtual Task OnDisabledAsync() => Task.CompletedTask;
    public virtual Task OnActivatedAsync() => Task.CompletedTask;
    public virtual Task OnPauseAsync() => Task.CompletedTask;
    public virtual Task OnBeforeInvokeAsync() => Task.CompletedTask;
    public virtual Task OnAfterInvokeAsync() => Task.CompletedTask;
    public virtual string? BuildPromptSection() => null;
}

public abstract class GlobalComponentBase : IGlobalComponent
{
    public abstract ComponentMeta Meta { get; }
    public abstract IEnumerable<ITool> Tools { get; }

    public virtual Task OnInitAsync(IGlobalComponentContext context, InitReason reason) => Task.CompletedTask;
    public virtual Task<ShutdownResponse> OnShutdownRequestedAsync(ShutdownReason reason) => Task.FromResult(ShutdownResponse.Ok);
    public virtual Task OnShutdownAsync(ShutdownReason reason) => Task.CompletedTask;
    public virtual Task OnEnabledAsync() => Task.CompletedTask;
    public virtual Task OnDisabledAsync() => Task.CompletedTask;
    public virtual string? BuildPromptSection(LoopInfo caller) => null;
}
