using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Client;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Memory;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 引擎启动配置。
    /// </summary>
    internal class EngineConfig
    {
        public List<string> AutoStart { get; set; } = new();

        public static EngineConfig Load(string path)
        {
            if (!File.Exists(path)) return new EngineConfig();
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<EngineConfig>(json) ?? new EngineConfig();
        }
    }

    /// <summary>
    /// 主引擎（内核）。职责：资源初始化、引擎注册表、事件分发。
    /// 不包含任何子引擎的业务逻辑。
    /// </summary>
    internal class MasterEngine : ISystemContext
    {
        private static string DefaultDatabasePath => PathConfig.DatabasePath;
        private static string DefaultEngineConfigPath =>
            Path.Combine(PathConfig.StoragePath, "Engine", "EngineConfig.json");

        private string databaseDirectory;
        private readonly AdapterManager adapterManager;
        private readonly EventBus eventBus;
        private DbManager? db;
        private IEmbeddingProvider? embeddingProvider;

        // ---- ISystemContext 实现 ----
        public MemoryRepository Memories { get; private set; } = null!;
        public TempMemoryRepository TempMemories { get; private set; } = null!;
        public MemoryLinkRepository MemoryLinks { get; private set; } = null!;
        public ReviewHintRepository ReviewHints { get; private set; } = null!;
        public MemoryService MemorySvc { get; private set; } = null!;
        public SessionManager Session { get; private set; } = null!;
        public IEmbeddingProvider Embedding => embeddingProvider!;
        public AdapterManager Adapters => adapterManager;
        public EventBus EventBus => eventBus;

        // 内核级状态
        private DateTime lastMessageTime = DateTime.Now;
        public DateTime LastMessageTime => lastMessageTime;
        /// <summary>是否空闲。排除基础设施引擎（IsInfrastructure=true）。</summary>
        public bool IsIdle
        {
            get
            {
                lock (engineLock)
                {
                    return !activeEngines.Any(e => e.IsAlive && !e.IsInfrastructure);
                }
            }
        }
        public TimeSpan IdleDuration => IsIdle ? DateTime.Now - lastMessageTime : TimeSpan.Zero;

        // ---- 引擎注册表 ----
        private readonly List<IEngineSpawnCheck> spawnChecks = new();
        private readonly List<ISubEngine> activeEngines = new();
        private readonly object engineLock = new();

        // SpawnCheck 工厂
        private static readonly Dictionary<string, Func<IEngineSpawnCheck>> SpawnCheckFactory = new()
        {
            ["Timer"] = () => new TimerEngineSpawnCheck(),
            ["Worker"] = () => new WorkerEngineSpawnCheck(),
            ["Dream"] = () => new DreamEngineSpawnCheck(),
        };


        public MasterEngine(AdapterManager adapterManager, EventBus eventBus, string? databaseDirectory = null)
        {
            this.adapterManager = adapterManager;
            this.eventBus = eventBus;
            this.databaseDirectory = databaseDirectory ?? DefaultDatabasePath;
        }

        // ---- 引擎状态查询 ----

        public bool HasActiveEngine(string engineType)
        {
            lock (engineLock) { return activeEngines.Any(e => e.EngineType == engineType && e.IsAlive); }
        }

        public int GetActiveEngineCount(string engineType)
        {
            lock (engineLock) { return activeEngines.Count(e => e.EngineType == engineType && e.IsAlive); }
        }

        // ---- 初始化 ----

        public async Task InitAsync()
        {
            Directory.CreateDirectory(databaseDirectory);
            var dbPath = Path.Combine(databaseDirectory, "lilara.db");
            db = new DbManager(dbPath);
            await db.InitAsync();

            // Repository
            var persons = new PersonRepository(db);
            var users = new UserRepository(db, persons);
            var channels = new ChannelRepository(db);
            var topics = new TopicRepository(db);
            var messages = new MessageRepository(db);
            Memories = new MemoryRepository(db);
            TempMemories = new TempMemoryRepository(db);
            MemoryLinks = new MemoryLinkRepository(db);
            ReviewHints = new ReviewHintRepository(db);

            // Embedding
            var baseConfigPath = Path.Combine(PathConfig.CoreConfigPath, "Base.json");
            var baseConfig = ApiClientCfg.FromJson(File.ReadAllText(baseConfigPath));
            embeddingProvider = new SiliconFlowEmbeddingProvider(apiKey: baseConfig.ApiKey);

            // 服务
            MemorySvc = new MemoryService(Memories, TempMemories, MemoryLinks, embeddingProvider);
            Session = new SessionManager(users, persons, channels, topics, messages, embeddingProvider);

            // 注册所有 SpawnCheck
            foreach (var (_, factory) in SpawnCheckFactory)
                spawnChecks.Add(factory());

            // 订阅事件总线
            eventBus.OnEvent += e => _ = HandleEventAsync(e);

            // 加载开机自启配置
            var engineCfg = EngineConfig.Load(DefaultEngineConfigPath);
            foreach (var type in engineCfg.AutoStart)
            {
                var check = spawnChecks.FirstOrDefault(c => c.EngineType == type);
                if (check != null)
                {
                    var engine = check.Create(this);
                    StartEngine(engine);
                    FrameworkLogger.Log("MasterEngine", $"自启动引擎: {type}");
                }
            }

            FrameworkLogger.Log("MasterEngine", "内核初始化完成");
        }

        // ---- 事件分发（内核流水线） ----

        public async Task HandleEventAsync(EngineEvent e)
        {
            // ① 内核更新
            if (e is MessageEvent msgEvent)
                lastMessageTime = msgEvent.Time;

            // ② SpawnCheck：OnEvent + ShouldSpawn
            // 复制列表避免遍历时修改
            var checks = spawnChecks.ToList();
            foreach (var check in checks)
            {
                try
                {
                    check.OnEvent(e, this);
                    if (check.ShouldSpawn(e, this))
                    {
                        var engine = check.Create(this);
                        StartEngine(engine);
                    }
                }
                catch (Exception ex)
                {
                    FrameworkLogger.Log("MasterEngine", $"SpawnCheck 异常 [{check.EngineType}]: {ex.Message}");
                }
            }

            // ③ 派发给活跃实例
            List<ISubEngine> engines;
            lock (engineLock) { engines = activeEngines.ToList(); }
            foreach (var engine in engines)
            {
                try { engine.OnEvent(e); }
                catch (Exception ex)
                {
                    FrameworkLogger.Log("MasterEngine", $"引擎 OnEvent 异常 [{engine.EngineType}]: {ex.Message}");
                }
            }

            // ④ 清理已死亡的实例
            lock (engineLock) { activeEngines.RemoveAll(e => !e.IsAlive); }

            await Task.CompletedTask;
        }

        // ---- 引擎管理 ----

        public ISubEngine StartEngine(ISubEngine engine)
        {
            lock (engineLock) { activeEngines.Add(engine); }
            FrameworkLogger.Log("MasterEngine", $"引擎启动: {engine.EngineType}");
            _ = Task.Run(async () =>
            {
                try
                {
                    await engine.RunAsync();
                }
                catch (Exception ex)
                {
                    FrameworkLogger.Log("MasterEngine", $"引擎异常 [{engine.EngineType}]: {ex.Message}");
                }
            });
            return engine;
        }

        public void RequestStopEngine(ISubEngine engine)
        {
            engine.RequestStop();
            FrameworkLogger.Log("MasterEngine", $"请求停止引擎: {engine.EngineType}");
        }
    }
}
