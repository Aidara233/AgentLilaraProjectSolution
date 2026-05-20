using System;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Engine
{
    /// <summary>模块/插件生命周期接口。替代 EngineModule.Attach/Reset。</summary>
    public interface IEngineLifecycle
    {
        Task OnInitializeAsync(IServiceProvider services);
        Task OnShutdownAsync();
    }
}
