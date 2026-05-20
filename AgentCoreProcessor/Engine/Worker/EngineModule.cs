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
    /// BuildPromptSection / Attach / GetTools / Reset 标记 [Obsolete]，逐步移除。
    /// </summary>
    public abstract class EngineModule : IInjectProvider, IEngineLifecycle
    {
        public abstract string Name { get; }

        /// <summary>注入优先级（越小越靠前）。默认 50。</summary>
        public virtual int InjectPriority => 50;

        // ── IInjectProvider ──

        public virtual Task<string?> BuildStartInjectAsync(InjectContext ctx)
            => Task.FromResult(BuildPromptSection(MapMode(ctx.Mode)));

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

        // ── 废弃接口（保留编译兼容） ──

        [Obsolete("Use InjectPriority instead")]
        public virtual int PromptPriority => InjectPriority;

        [Obsolete("Use IEngineLifecycle.OnInitializeAsync")]
        public virtual void Attach(ILoopBus bus) { }

        [Obsolete("Override BuildStartInjectAsync instead")]
        public virtual string? BuildPromptSection(EngineMode mode) => null;

        [Obsolete("Use ITool directly")]
        public virtual IEnumerable<ITool> GetTools(EngineMode mode) => [];

        [Obsolete("Use IEngineLifecycle.OnShutdownAsync")]
        public virtual void Reset() { }

        // ── helpers ──

        private static EngineMode MapMode(string mode) => mode switch
        {
            "express" => EngineMode.Express,
            _ => EngineMode.Working
        };
    }
}
