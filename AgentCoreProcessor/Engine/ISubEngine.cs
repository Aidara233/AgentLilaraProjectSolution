using System;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Client;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Memory;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 子引擎实例接口。临时（用完即毁）或常驻（自行决定生命周期）。
    /// </summary>
    internal interface ISubEngine
    {
        string EngineType { get; }
        Task RunAsync();
        void OnEvent(EngineEvent e);
        bool IsAlive { get; }
        void RequestStop();

        /// <summary>
        /// 是否为基础设施引擎（如 Timer）。基础设施引擎不影响 IsIdle 判定。
        /// 默认 false。
        /// </summary>
        bool IsInfrastructure => false;
    }

    /// <summary>
    /// 引擎类型注册接口。每种引擎类型一个常驻实例，负责：
    /// 1. 接收事件更新内部状态 (OnEvent)
    /// 2. 判断是否需要创建新引擎实例 (ShouldSpawn)
    /// 3. 创建引擎实例 (Create)
    /// </summary>
    internal interface IEngineSpawnCheck
    {
        string EngineType { get; }
        Task OnEventAsync(EngineEvent e, ISystemContext ctx);
        Task<bool> ShouldSpawnAsync(EngineEvent e, ISystemContext ctx);
        ISubEngine Create(ISystemContext ctx);
    }

    /// <summary>
    /// 系统上下文接口。MasterEngine 暴露给子引擎的"系统调用"。
    /// </summary>
    internal interface ISystemContext
    {
        // 数据访问
        MemoryRepository Memories { get; }
        TempMemoryRepository TempMemories { get; }
        MemoryLinkRepository MemoryLinks { get; }
        MemoryService MemorySvc { get; }
        PersonaMemoryRepository PersonaMemories { get; }
        SessionManager Session { get; }
        IEmbeddingProvider Embedding { get; }

        // 视觉
        IVisionProvider? Vision { get; }

        // 适配器
        AdapterManager Adapters { get; }

        // 事件总线
        EventBus EventBus { get; }

        // 复盘标记
        ReviewHintRepository ReviewHints { get; }

        // 引擎状态查询
        bool IsIdle { get; }
        TimeSpan IdleDuration { get; }
        DateTime LastMessageTime { get; }
        bool HasActiveEngine(string engineType);
        int GetActiveEngineCount(string engineType);

        // 引擎管理（子引擎可请求启动/停止其他引擎）
        ISubEngine StartEngine(ISubEngine engine);
        void RequestStopEngine(ISubEngine engine);
    }
}
