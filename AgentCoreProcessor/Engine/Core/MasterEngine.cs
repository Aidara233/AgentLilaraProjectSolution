using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Client;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.MCP;
using AgentCoreProcessor.Memory;
using AgentCoreProcessor.Engine.Modules;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
        private IOcrProvider? ocrProvider;
        private McpServerManager? mcpManager;

        // ---- ISystemContext 实现 ----
        public MemoryRepository Memories { get; private set; } = null!;
        public TempMemoryRepository TempMemories { get; private set; } = null!;
        public MemoryLinkRepository MemoryLinks { get; private set; } = null!;
        public PersonaMemoryRepository PersonaMemories { get; private set; } = null!;
        public ReviewHintRepository ReviewHints { get; private set; } = null!;
        public DreamLogRepository DreamLogs { get; private set; } = null!;
        public ScheduledTaskRepository ScheduledTasks { get; private set; } = null!;
        public ModelCallLogRepository ModelCallLogs { get; private set; } = null!;
        public MemoryService MemorySvc { get; private set; } = null!;
        public SessionManager Session { get; private set; } = null!;
        public IEmbeddingProvider Embedding => embeddingProvider!;
        public IVisionProvider? Vision => visionProvider;
        public IOcrProvider? Ocr => ocrProvider;
        public AdapterManager Adapters => adapterManager;
        public EventBus EventBus => eventBus;
        public ImpulseConfig ImpulseConfig { get; private set; } = null!;
        public TrustProgressionConfig TrustConfig { get; private set; } = null!;

        // 内核级状态
        private DateTime lastMessageTime = DateTime.Now;
        public DateTime LastMessageTime => lastMessageTime;
        /// <summary>是否空闲。检查所有非基础设施引擎的 IsBusy 状态。</summary>
        public bool IsIdle
        {
            get
            {
                lock (engineLock)
                {
                    return !activeEngines.Any(e => !e.IsInfrastructure && e.IsBusy);
                }
            }
        }
        public TimeSpan IdleDuration => IsIdle ? DateTime.Now - lastMessageTime : TimeSpan.Zero;

        /// <summary>静音模式：内部处理照常，但不产生对外输出。</summary>
        public bool MuteMode { get; set; } = false;
        public SleepState CurrentSleepState { get; set; } = SleepState.None;
        public McpServerManager? McpManager => mcpManager;
        public TaskBridge TaskBridge { get; private set; } = null!;
        public DelegationRegistry Delegations { get; private set; } = null!;

        // ---- 引擎注册表 ----
        private readonly List<IEngineSpawnCheck> spawnChecks = new();
        private readonly List<ISubEngine> activeEngines = new();
        private readonly object engineLock = new();
        private readonly SemaphoreSlim eventLock = new(1, 1);
        private SystemEngine? systemEngine; // Phase 5: 保存 SystemEngine 引用用于工具注册

        // SpawnCheck 工厂（有序列表，Command 在 Channel 之前拦截命令消息）
        private static readonly List<(string Name, Func<IEngineSpawnCheck> Factory)> SpawnCheckFactory =
        [
            ("Command", () => new CommandSpawnCheck()),
            ("Timer",   () => new TimerEngineSpawnCheck()),
            ("System",  () => new SystemEngineSpawnCheck()),
            ("Channel",  () => new ChannelEngineSpawnCheck()),
            ("Dream",   () => new DreamEngineSpawnCheck()),
            ("Vision",  () => new Vision.VisionEngineSpawnCheck()),
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

        /// <summary>是否有正在处理消息的 ChannelEngine（常驻 Channel 忙碌中）。</summary>
        public bool HasBusyWorker()
        {
            lock (engineLock) { return activeEngines.Any(e => e is ChannelEngine w && w.IsAlive && w.IsBusy); }
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

        internal T? GetSpawnCheck<T>() where T : class, IEngineSpawnCheck
        {
            lock (engineLock) { return spawnChecks.OfType<T>().FirstOrDefault(); }
        }

        public void NotifyChannel(int channelId, string content)
        {
            var check = GetSpawnCheck<ChannelEngineSpawnCheck>();
            if (check == null) return;
            var channels = check.GetActiveChannels();
            if (channels.TryGetValue(channelId, out var engine) && engine.IsAlive)
            {
                engine.InjectNotification(content);
            }
            else
            {
                check.StashNotification(channelId, content);
                FrameworkLogger.Log("MasterEngine",
                    $"NotifyChannel: 频道 {channelId} 无活跃循环，通知已暂存");
            }
        }

        public List<ISubEngine> GetActiveEnginesSnapshot()
        {
            lock (engineLock) { return activeEngines.Where(e => e.IsAlive).ToList(); }
        }

        // ---- 初始化 ----

        public async Task InitAsync()
        {
            Directory.CreateDirectory(databaseDirectory);
            var dbPath = Path.Combine(databaseDirectory, "lilara.db");
            db = new DbManager(dbPath);
            await db.InitAsync();

            // 记忆表结构变更（v2: 加 Type/Subject，移除 TopicId），一次性重建
            var schemaMarker = Path.Combine(databaseDirectory, ".memory_schema_v2");
            if (!File.Exists(schemaMarker))
            {
                await db.RebuildMemoryTablesAsync();
                await File.WriteAllTextAsync(schemaMarker, "rebuilt");
                FrameworkLogger.Log("MasterEngine", "记忆表已重建（schema v2）");
            }

            // ImageRecord 扩展（v3: 加 Category/Description/SeenCount）
            var schemaV3 = Path.Combine(databaseDirectory, ".image_schema_v3");
            if (!File.Exists(schemaV3))
            {
                try
                {
                    await db.ExecuteAsync("ALTER TABLE ImageRecords ADD COLUMN Category TEXT");
                    await db.ExecuteAsync("ALTER TABLE ImageRecords ADD COLUMN Description TEXT");
                    await db.ExecuteAsync("ALTER TABLE ImageRecords ADD COLUMN SeenCount INTEGER DEFAULT 0");
                }
                catch { }
                await File.WriteAllTextAsync(schemaV3, "migrated");
                FrameworkLogger.Log("MasterEngine", "ImageRecords 已迁移（schema v3）");
            }

            var schemaV4 = Path.Combine(databaseDirectory, ".image_schema_v4");
            if (!File.Exists(schemaV4))
            {
                try
                {
                    await db.ExecuteAsync("ALTER TABLE ImageRecords ADD COLUMN OcrText TEXT");
                    await db.ExecuteAsync("ALTER TABLE ImageRecords ADD COLUMN HasText INTEGER");
                }
                catch { }
                await File.WriteAllTextAsync(schemaV4, "migrated");
                FrameworkLogger.Log("MasterEngine", "ImageRecords 已迁移（schema v4: OCR fields）");
            }

            var schemaV5 = Path.Combine(databaseDirectory, ".channel_schema_v5");
            if (!File.Exists(schemaV5))
            {
                try
                {
                    await db.ExecuteAsync("ALTER TABLE Channels ADD COLUMN LastExtractedMessageId INTEGER DEFAULT 0");
                }
                catch { }
                await File.WriteAllTextAsync(schemaV5, "migrated");
                FrameworkLogger.Log("MasterEngine", "Channels 已迁移（schema v5: extraction progress）");
            }

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
            DreamLogs = new DreamLogRepository(db);
            ScheduledTasks = new ScheduledTaskRepository(db);
            var images = new ImageRepository(db);
            ImageStorage.Init(images, eventBus);
            ModelCallLogs = new ModelCallLogRepository(db);
            CoreBase.CallLogRepo = ModelCallLogs;

            // Embedding（独立配置，不跟随 Base.json）
            var embConfigPath = Path.Combine(PathConfig.CoreConfigPath, "EmbeddingProvider.json");
            if (File.Exists(embConfigPath))
            {
                var embJson = JObject.Parse(File.ReadAllText(embConfigPath));
                var embKey = embJson["apiKey"]?.ToString() ?? "";
                var embEndpoint = embJson["endpoint"]?.ToString() ?? "https://api.siliconflow.cn/v1/embeddings";
                var embModel = embJson["model"]?.ToString() ?? "BAAI/bge-large-zh-v1.5";
                embeddingProvider = new SiliconFlowEmbeddingProvider(apiKey: embKey, endpoint: embEndpoint, model: embModel);
            }
            else
            {
                var baseConfigPath = Path.Combine(PathConfig.CoreConfigPath, "Base.json");
                var baseConfig = ApiClientCfg.FromJson(File.ReadAllText(baseConfigPath));
                embeddingProvider = new SiliconFlowEmbeddingProvider(apiKey: baseConfig.ApiKey);
            }

            // Vision（从 VisionProvider.json 读取，不依赖 Base.json）
            try
            {
                var vApiKey = "";
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

                if (!string.IsNullOrEmpty(vApiKey))
                {
                    visionProvider = new SiliconFlowVisionProvider(vApiKey, vEndpoint, vModel);
                    FrameworkLogger.Log("MasterEngine", $"视觉模型已加载: {vModel}");
                }
                else
                {
                    FrameworkLogger.Log("MasterEngine", "警告: VisionProvider.json 未配置或缺少 apiKey，视觉功能不可用");
                }
            }
            catch (Exception ex)
            {
                FrameworkLogger.Log("MasterEngine", $"视觉模型初始化失败: {ex.Message}");
            }

            // OCR（从 OcrProvider.json 读取，不依赖 Base.json）
            try
            {
                var ocrApiKey = "";
                var ocrEndpoint = "https://api.siliconflow.cn/v1/chat/completions";
                var ocrModel = "deepseek-ai/DeepSeek-OCR";

                var ocrConfigPath = Path.Combine(PathConfig.CoreConfigPath, "OcrProvider.json");
                if (File.Exists(ocrConfigPath))
                {
                    var ojson = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(ocrConfigPath));
                    ocrApiKey = ojson["apiKey"]?.ToString() ?? ocrApiKey;
                    ocrEndpoint = ojson["endpoint"]?.ToString() ?? ocrEndpoint;
                    ocrModel = ojson["model"]?.ToString() ?? ocrModel;
                }

                if (!string.IsNullOrEmpty(ocrApiKey))
                {
                    ocrProvider = new SiliconFlowOcrProvider(ocrApiKey, ocrEndpoint, ocrModel);
                    FrameworkLogger.Log("MasterEngine", $"OCR 模型已加载: {ocrModel}");
                }
                else
                {
                    FrameworkLogger.Log("MasterEngine", "警告: OcrProvider.json 未配置或缺少 apiKey，OCR 功能不可用");
                }
            }
            catch (Exception ex)
            {
                FrameworkLogger.Log("MasterEngine", $"OCR 模型初始化失败: {ex.Message}");
            }

            // 服务
            MemorySvc = new MemoryService(Memories, TempMemories, MemoryLinks, embeddingProvider, PersonaMemories);
            Session = new SessionManager(users, persons, channels, messages);

            // 冲动值配置
            ImpulseConfig = ImpulseConfig.Load(
                Path.Combine(PathConfig.StoragePath, "Engine", "ImpulseConfig.json"));
            TrustConfig = TrustProgressionConfig.Load(
                Path.Combine(PathConfig.StoragePath, "Engine", "TrustProgressionConfig.json"));

            // 人设记忆种子加载（表空时从文件导入）
            await LoadPersonaMemorySeedAsync();

            // 创建 TaskBridge
            var systemLoopPath = Path.Combine(PathConfig.StoragePath, "SystemLoop");
            Directory.CreateDirectory(systemLoopPath);
            TaskBridge = new TaskBridge(systemLoopPath);
            FrameworkLogger.Log("MasterEngine", "TaskBridge 已初始化");

            // 创建 DelegationRegistry
            Delegations = new DelegationRegistry(systemLoopPath);
            Delegations.OnDelegationSubmitted = () =>
            {
                // 委托提交时唤醒系统循环
                if (systemEngine != null)
                    systemEngine.SignalGate();
            };
            Delegations.OnDelegationCompleted = (channelId) =>
            {
                // 委托完成时唤醒对应频道循环
                eventBus.PublishSignal("delegation-completed", channelId);
            };
            FrameworkLogger.Log("MasterEngine", "DelegationRegistry 已初始化");

            // 注册核心工具（不可卸载的循环控制工具）
            Tool.ToolRegistry.Register(new Tool.Core.ContinueLoopTool());
            Tool.ToolRegistry.Register(new Tool.Core.WaitTool());
            FrameworkLogger.Log("MasterEngine", "核心工具已注册");

            // 内置工具（暂时直接注册，后续迁移为插件 DLL）
            Tool.ToolRegistry.Register(new Tool.Builtin.SpeakTool());
            Tool.ToolRegistry.Register(new Tool.Builtin.SendMediaTool());
            FrameworkLogger.Log("MasterEngine", $"内置工具已注册，共 {Tool.ToolRegistry.All.Count} 个");

            // TODO: Phase 2 完成后在此初始化 PluginLoader
            // var pluginLoader = new Tool.Host.PluginLoader(toolContext);
            // pluginLoader.LoadAll();

            FrameworkLogger.Log("MasterEngine", "工具系统初始化完成（插件加载待实现）");

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

                    // Phase 5: 保存 SystemEngine 引用
                    if (engine is SystemEngine sysEngine)
                    {
                        systemEngine = sysEngine;
                    }
                }
            }

            // TODO: Phase 3 实现子 agent 管理插件后移除
            // 子 agent 工具将作为插件加载

            // MCP Server 初始化
            try
            {
                var mcpConfigPath = Path.Combine(PathConfig.StoragePath, "MCP", "McpServers.json");
                mcpManager = new McpServerManager(mcpConfigPath);
                await mcpManager.InitAsync();
            }
            catch (Exception ex)
            {
                FrameworkLogger.LogError("MasterEngine", ex, "MCP 初始化失败（不影响核心功能）");
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

            // ①b 定时任务检查（每次 tick 时）
            if (e is TimerEvent timerEvt && timerEvt.TimerName == "tick")
                await CheckScheduledTasksAsync();

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

        /// <summary>检查到期的定时任务并投递给对应 owner。</summary>
        private async Task CheckScheduledTasksAsync()
        {
            if (ScheduledTasks == null) return;

            List<Database.ScheduledTask> dueTasks;
            try
            {
                dueTasks = await ScheduledTasks.GetDueTasksAsync(DateTime.Now);
            }
            catch { return; }

            foreach (var task in dueTasks)
            {
                var evt = new Modules.ScheduledTaskFiredEvent
                {
                    TaskId = task.Id,
                    Description = task.Description,
                    Payload = task.Payload
                };

                if (task.OwnerType == "system")
                {
                    // 投递给 SystemEngine
                    systemEngine?.EnqueueScheduledEvent(evt);
                }
                else
                {
                    // 投递给频道循环（作为通知）
                    TaskBridge.PostNotification(new Notification
                    {
                        Type = NotificationType.Notify,
                        SourceId = $"scheduled-{task.Id}",
                        Summary = $"[定时任务] {task.Description}" + (task.Payload != null ? $"\n{task.Payload}" : ""),
                        Timestamp = DateTime.Now
                    });
                }

                // 计算下次触发
                // TODO: Tool.ScheduleParser was deleted; need replacement for schedule computation
                // var nextFire = Tool.ScheduleParser.ComputeNextFire(task);
                DateTime? nextFire = null; // placeholder until schedule parser is reimplemented
                await ScheduledTasks.MarkFiredAsync(task.Id, nextFire);
            }
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
