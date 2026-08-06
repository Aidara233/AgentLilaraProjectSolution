using AgentCoreProcessor.Engine;

namespace AgentCoreProcessor.Adapter
{
    /// <summary>
    /// 需要访问 MasterEngine 的适配器（聊天记录查询 + HasBusyWorker 突发检测）。
    /// 由 AdapterManager 在引擎就绪后注入。同程序集 internal。
    /// </summary>
    internal interface IEngineAwareAdapter
    {
        void AttachEngine(MasterEngine engine);
    }
}
