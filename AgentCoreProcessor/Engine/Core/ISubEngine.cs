using System;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Client;
using AgentCoreProcessor.Component;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.MCP;
using AgentCoreProcessor.Memory;
using AgentCoreProcessor.Tool.Host;

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

        /// <summary>
        /// 是否正在主动处理任务（调用模型、执行工具等）。
        /// 默认等于 IsAlive（短生命周期引擎活着就是在忙）。
        /// 常驻引擎应覆盖此属性，仅在实际工作时返回 true。
        /// </summary>
        bool IsBusy => IsAlive;
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

        // OCR
        IOcrProvider? Ocr { get; }

        // 适配器
        AdapterManager Adapters { get; }

        // 事件总线
        EventBus EventBus { get; }

        // 复盘标记
        ReviewHintRepository ReviewHints { get; }

        // 做梦日志
        DreamLogRepository DreamLogs { get; }

        // 定时任务
        ScheduledTaskRepository ScheduledTasks { get; }

        // 配置
        ImpulseConfig ImpulseConfig { get; }
        TrustProgressionConfig TrustConfig { get; }

        // 引擎状态查询
        bool IsIdle { get; }
        TimeSpan IdleDuration { get; }
        DateTime LastMessageTime { get; }
        bool HasActiveEngine(string engineType);
        int GetActiveEngineCount(string engineType);

        // 引擎管理（子引擎可请求启动/停止其他引擎）
        ISubEngine StartEngine(ISubEngine engine);
        void RequestStopEngine(ISubEngine engine);
        List<(string Type, int Count)> GetActiveEngineSummary();
        void RequestStopEnginesByType(string engineType);

        /// <summary>获取所有活跃引擎的快照（用于工具查询）。</summary>
        List<ISubEngine> GetActiveEnginesSnapshot();

        /// <summary>静音模式：内部处理照常，但不产生对外输出。</summary>
        bool MuteMode { get; set; }

        /// <summary>当前睡眠状态（None=清醒）。DreamEngine 启动/结束时设置。</summary>
        SleepState CurrentSleepState { get; set; }

        /// <summary>MCP Server 管理器（可能为 null，如果 MCP 未初始化）。</summary>
        McpServerManager? McpManager { get; }

        /// <summary>任务桥梁：频道循环 ↔ 系统循环异步通信。</summary>
        TaskBridge TaskBridge { get; }

        /// <summary>委托注册表：频道循环 ↔ 系统循环的委托生命周期管理。</summary>
        DelegationRegistry Delegations { get; }

        /// <summary>向指定频道循环注入系统通知。频道循环醒来后自行决定如何回应。</summary>
        void NotifyChannel(int channelId, string content);

        /// <summary>工具 Profile 管理器。</summary>
        Tool.Host.ToolProfileManager ToolProfiles { get; }

        /// <summary>模块间通信总线（全局单例）。替代 ComponentEventBus。</summary>
        ModuleBus ModuleBus { get; }

        /// <summary>全局组件宿主（MasterEngine 持有）。</summary>
        GlobalComponentHost? GlobalComponentHost { get; }

        /// <summary>组件系统的服务提供者。</summary>
        IServiceProvider ComponentServices { get; }

        /// <summary>插件加载器。提供 InjectProviderTypes/LifecycleTypes 及实例化方法。</summary>
        PluginLoader PluginLoader { get; }

        /// <summary>插件工具上下文（服务容器）。引擎可动态注册/注销服务。</summary>
        Tool.Host.ToolContextImpl ToolContext { get; }
    }
}
