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
        private IVisionProvider? visionProvider;

        // ---- ISystemContext 实现 ----
        public MemoryRepository Memories { get; private set; } = null!;
        public TempMemoryRepository TempMemories { get; private set; } = null!;
        public MemoryLinkRepository MemoryLinks { get; private set; } = null!;
        public PersonaMemoryRepository PersonaMemories { get; private set; } = null!;
        public ReviewHintRepository ReviewHints { get; private set; } = null!;
        public MemoryService MemorySvc { get; private set; } = null!;
        public SessionManager Session { get; private set; } = null!;
        public IEmbeddingProvider Embedding => embeddingProvider!;
        public IVisionProvider? Vision => visionProvider;
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

        /// <summary>静音模式：内部处理照常，但不产生对外输出。</summary>
        public bool MuteMode { get; set; } = false;

        // ---- 引擎注册表 ----
        private readonly List<IEngineSpawnCheck> spawnChecks = new();
        private readonly List<ISubEngine> activeEngines = new();
        private readonly object engineLock = new();
        private readonly SemaphoreSlim eventLock = new(1, 1);

        // SpawnCheck 工厂（有序列表，Command 在 Worker 之前拦截命令消息）
        private static readonly List<(string Name, Func<IEngineSpawnCheck> Factory)> SpawnCheckFactory =
        [
            ("Command", () => new CommandSpawnCheck()),
            ("Timer",   () => new TimerEngineSpawnCheck()),
            ("Worker",  () => new WorkerEngineSpawnCheck()),
            ("Dream",   () => new DreamEngineSpawnCheck()),
        ];


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

        /// <summary>是否有正在处理消息的 WorkerEngine（常驻 Worker 忙碌中）。</summary>
        public bool HasBusyWorker()
        {
            lock (engineLock) { return activeEngines.Any(e => e is WorkerEngine w && w.IsAlive && w.IsBusy); }
        }

        public int GetActiveEngineCount(string engineType)
        {
            lock (engineLock) { return activeEngines.Count(e => e.EngineType == engineType && e.IsAlive); }
        }

        public List<(string Type, int Count)> GetActiveEngineSummary()
        {
            lock (engineLock)
            {
                return activeEngines.Where(e => e.IsAlive)
                    .GroupBy(e => e.EngineType)
                    .Select(g => (g.Key, g.Count()))
                    .ToList();
            }
        }

        public void RequestStopEnginesByType(string engineType)
        {
            lock (engineLock)
            {
                foreach (var e in activeEngines.Where(e => e.EngineType == engineType && e.IsAlive))
                    e.RequestStop();
            }
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
            var messages = new MessageRepository(db);
            Memories = new MemoryRepository(db);
            TempMemories = new TempMemoryRepository(db);
            MemoryLinks = new MemoryLinkRepository(db);
            PersonaMemories = new PersonaMemoryRepository(db);
            ReviewHints = new ReviewHintRepository(db);
            var images = new ImageRepository(db);
            ImageStorage.Init(images);

            // Embedding
            var baseConfigPath = Path.Combine(PathConfig.CoreConfigPath, "Base.json");
            var baseConfig = ApiClientCfg.FromJson(File.ReadAllText(baseConfigPath));
            embeddingProvider = new SiliconFlowEmbeddingProvider(apiKey: baseConfig.ApiKey);

            // Vision（默认用 Base.json 的 apiKey，VisionProvider.json 可覆盖模型和端点）
            try
            {
                var vApiKey = baseConfig.ApiKey;
                var vEndpoint = "https://api.siliconflow.cn/v1/chat/completions";
                var vModel = "Qwen/Qwen2.5-VL-72B-Instruct";

                var visionConfigPath = Path.Combine(PathConfig.CoreConfigPath, "VisionProvider.json");
                if (File.Exists(visionConfigPath))
                {
                    var vjson = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(visionConfigPath));
                    vApiKey = vjson["apiKey"]?.ToString() ?? vApiKey;
                    vEndpoint = vjson["endpoint"]?.ToString() ?? vEndpoint;
                    vModel = vjson["model"]?.ToString() ?? vModel;
                }

                visionProvider = new SiliconFlowVisionProvider(vApiKey, vEndpoint, vModel);
                FrameworkLogger.Log("MasterEngine", $"视觉模型已加载: {vModel}");
            }
            catch (Exception ex)
            {
                FrameworkLogger.Log("MasterEngine", $"视觉模型初始化失败: {ex.Message}");
            }

            // 服务
            MemorySvc = new MemoryService(Memories, TempMemories, MemoryLinks, embeddingProvider, PersonaMemories);
            Session = new SessionManager(users, persons, channels, messages);

            // 人设记忆种子加载（表空时从文件导入）
            await LoadPersonaMemorySeedAsync();

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
            // 串行化事件处理，防止 SpawnCheck 并发状态冲突
            await eventLock.WaitAsync();
            try
            {
                await HandleEventCoreAsync(e);
            }
            finally
            {
                eventLock.Release();
            }
        }

        private async Task HandleEventCoreAsync(EngineEvent e)
        {
            // ① 内核更新
            if (e is MessageEvent msgEvent)
                lastMessageTime = msgEvent.Time;

            // ② SpawnCheck：OnEvent + ShouldSpawn
            // 复制列表避免遍历时修改
            var checks = spawnChecks.ToList();
            foreach (var check in checks)
            {
                if (e.Consumed) break;
                try
                {
                    await check.OnEventAsync(e, this);
                    if (await check.ShouldSpawnAsync(e, this))
                    {
                        var engine = check.Create(this);
                        StartEngine(engine);
                    }
                }
                catch (Exception ex)
                {
                    FrameworkLogger.LogError("MasterEngine", ex, $"SpawnCheck={check.EngineType}");
                }
            }

            // ③ 派发给活跃实例（已消费的事件跳过）
            if (!e.Consumed)
            {
                List<ISubEngine> engines;
                lock (engineLock) { engines = activeEngines.ToList(); }
                foreach (var engine in engines)
                {
                    try { engine.OnEvent(e); }
                    catch (Exception ex)
                    {
                        FrameworkLogger.LogError("MasterEngine", ex, $"引擎 OnEvent={engine.EngineType}");
                    }
                }
            }

            // ④ 清理已死亡的实例
            lock (engineLock) { activeEngines.RemoveAll(e => !e.IsAlive); }
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
                    FrameworkLogger.LogError("MasterEngine", ex, $"引擎类型={engine.EngineType}");
                }
            });
            return engine;
        }

        public void RequestStopEngine(ISubEngine engine)
        {
            engine.RequestStop();
            FrameworkLogger.Log("MasterEngine", $"请求停止引擎: {engine.EngineType}");
        }

        /// <summary>人设记忆种子加载：表空时从 Storage/PersonaMemorySeed.txt 导入。</summary>
        private async Task LoadPersonaMemorySeedAsync()
        {
            try
            {
                var count = await PersonaMemories.GetCountAsync();
                if (count > 0) return; // 已有数据，跳过

                var seedPath = Path.Combine(PathConfig.StoragePath, "PersonaMemorySeed.txt");
                if (!File.Exists(seedPath)) return;

                var lines = File.ReadAllLines(seedPath)
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrEmpty(l))
                    .ToList();

                if (lines.Count == 0) return;

                int loaded = 0;
                foreach (var line in lines)
                {
                    byte[]? embBytes = null;
                    try
                    {
                        var vec = await embeddingProvider!.GetEmbeddingAsync(line);
                        embBytes = SiliconFlowEmbeddingProvider.FloatsToBytes(vec);
                    }
                    catch { }

                    await PersonaMemories.CreateAsync(line, embBytes);
                    loaded++;
                }

                FrameworkLogger.Log("MasterEngine", $"人设记忆种子已加载: {loaded} 条");
            }
            catch (Exception ex)
            {
                FrameworkLogger.Log("MasterEngine", $"人设记忆种子加载失败: {ex.Message}");
            }
        }
    }
}
