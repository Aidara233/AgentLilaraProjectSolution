using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AgentCoreProcessor.Tool;

namespace AgentCoreProcessor.Engine
{
    public enum EngineMode
    {
        Express,
        Working
    }

    /// <summary>
    /// 模块基类。实现 IInjectProvider + IEngineLifecycle。
    /// </summary>
    public abstract class EngineModule : IInjectProvider, IEngineLifecycle
    {
        public abstract string Name { get; }

        /// <summary>注入优先级（越小越靠前）。默认 50。</summary>
        public virtual int InjectPriority => 50;

        // ── IInjectProvider ──

        public virtual Task<string?> BuildStartInjectAsync(InjectContext ctx)
            => Task.FromResult<string?>(null);

        public virtual Task<string?> BuildRoundInjectAsync(InjectContext ctx)
            => Task.FromResult<string?>(null);

        // ── IEngineLifecycle ──

        public virtual Task OnInitializeAsync(IServiceProvider services)
            => Task.CompletedTask;

        public virtual Task OnShutdownAsync()
        {
            Reset();
            return Task.CompletedTask;
        }

        // ── 保留接口 ──

        public virtual void Attach(ILoopBus bus) { }

        public virtual IEnumerable<ITool> GetTools(EngineMode mode) => [];

        public virtual void Reset() { }
    }
}
