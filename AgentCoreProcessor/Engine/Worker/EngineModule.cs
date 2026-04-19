using System.Collections.Generic;
using AgentCoreProcessor.Tool;

namespace AgentCoreProcessor.Engine
{
    internal enum EngineMode
    {
        Express,
        Working
    }

    /// <summary>
    /// 内务模块基类。内务模块双向依赖框架，通过 ILoopBus 通信。
    /// 外务模块（文件、终端等）不继承此基类，只实现 ITool。
    /// </summary>
    internal abstract class EngineModule
    {
        /// <summary>模块名称。</summary>
        public abstract string Name { get; }

        /// <summary>Prompt 注入优先级（越小越靠前）。</summary>
        public virtual int PromptPriority => 50;

        /// <summary>注册到事件总线（模块自己决定订阅哪些事件）。</summary>
        public abstract void Attach(ILoopBus bus);

        /// <summary>提供当前模式下的工具（无工具的模块返回空）。</summary>
        public virtual IEnumerable<ITool> GetTools(EngineMode mode) => [];

        /// <summary>注入 prompt 内容（返回 null 表示本轮不注入）。</summary>
        public virtual string? BuildPromptSection(EngineMode mode) => null;

        /// <summary>引擎生命周期结束，清理状态。</summary>
        public virtual void Reset() { }
    }
}
