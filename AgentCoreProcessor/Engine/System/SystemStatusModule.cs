using System;

namespace AgentCoreProcessor.Engine.Modules
{
    /// <summary>
    /// 系统状态模块。跟踪引擎启动时间。
    /// </summary>
    internal class SystemStatusModule : EngineModule
    {
        public override string Name => "系统状态";

        private readonly ISystemContext ctx;
        private readonly Func<System.Collections.Generic.List<IAgentSession>>? getSubAgents;
        private readonly Func<(int tokens, int percent)>? getContextUsage;
        private DateTime? engineStartTime;

        public SystemStatusModule(
            ISystemContext ctx,
            Func<System.Collections.Generic.List<IAgentSession>>? getSubAgents = null,
            Func<(int tokens, int percent)>? getContextUsage = null)
        {
            this.ctx = ctx;
            this.getSubAgents = getSubAgents;
            this.getContextUsage = getContextUsage;
        }

        public override void Attach(ILoopBus bus)
        {
            engineStartTime = DateTime.Now;
        }
    }
}
