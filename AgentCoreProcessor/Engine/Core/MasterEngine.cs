using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Client;
using AgentCoreProcessor.Component;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.MCP;
using AgentCoreProcessor.Memory;
using AgentCoreProcessor.Engine.Modules;
using AgentCoreProcessor.Logging;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Logging;
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
        private readonly ISignalLogger? _signalLogger;
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
        public ReviewLogRepository ReviewLogs { get; private set; } = null!;
        public EvaluationScoreRepository EvaluationScores { get; private set; } = null!;
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

        // WebUI
        public WebUI.Shell.ProviderRegistry? ProviderRegistry { get; set; }

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
        public CrossRequestRegistry CrossRequests { get; private set; } = null!;
        public DelegationBus DelegationBus { get; private set; } = null!;

        // ---- ISystemContext: Component 系统 ----
        public ModuleBus ModuleBus => _moduleBus;
        public GlobalComponentHost? GlobalComponentHost => globalComponentHost;
        public IServiceProvider ComponentServices => componentServices;

        // ---- ISystemContext: Plugin 系统 ----
        public Tool.Host.PluginLoader PluginLoader => _pluginLoader;
        public Tool.Host.ToolContextImpl ToolContext => _toolContext;

        // ---- ISystemContext: 信号过滤器 ----
        public SignalFilterManager SignalFilters { get; private set; } = null!;

        // ---- 引擎注册表 ----
        private readonly List<IEngineSpawnCheck> spawnChecks = new();
        private readonly List<ISubEngine> activeEngines = new();
        private readonly List<(ISubEngine Engine, Task Task)> _engineTasks = new();
        private readonly object engineLock = new();
        private readonly SemaphoreSlim eventLock = new(1, 1);
        private SystemEngine? systemEngine; // Phase 5: 保存 SystemEngine 引用用于工具注册

        // ---- Component 系统 ----
        private readonly ModuleBus _moduleBus = new();
        private GlobalComponentHost? globalComponentHost;
        private readonly SimpleServiceProvider componentServices = new();
        private Tool.Host.PluginLoader _pluginLoader = null!;
        private Tool.Host.ToolContextImpl _toolContext = null!;

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


        public MasterEngine(AdapterManager adapterManager, EventBus eventBus, ISignalLogger? signalLogger = null, string? databaseDirectory = null)
        {
            this.adapterManager = adapterManager;
            this.eventBus = eventBus;
            this.databaseDirectory = databaseDirectory ?? DefaultDatabasePath;
            _signalLogger = signalLogger;
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

        internal SystemEngine? GetSystemEngine() => systemEngine;

        public List<ISubEngine> GetActiveEnginesSnapshot()
        {
            lock (engineLock) { return activeEngines.Where(e => e.IsAlive).ToList(); }
        }

        /// <summary>收集所有组件工具（Global + 所有活跃 Loop），供 WebUI 查询。</summary>
        public List<ITool> GetAllComponentTools()
        {
            var tools = new List<ITool>();
            if (GlobalComponentHost != null)
                tools.AddRange(GlobalComponentHost.GetAllTools());
            foreach (var engine in GetActiveEnginesSnapshot())
            {
                if (engine.ComponentHost != null)
                    tools.AddRange(engine.ComponentHost.GetVisibleTools());
            }
            return tools;
        }

        // ---- 初始化 ----

        public async Task InitAsync()
        {
            Directory.CreateDirectory(databaseDirectory);
            var dbPath = Path.Combine(databaseDirectory, "lilara.db");
            db = new DbManager(dbPath);
            await db.InitAsync();
            Signal.Event(LogGroup.Engine, "数据库初始化完成");

            // 记忆表结构变更（v2: 加 Type/Subject，移除 TopicId），一次性重建
            var schemaMarker = Path.Combine(databaseDirectory, ".memory_schema_v2");
            if (!File.Exists(schemaMarker))
            {
                await db.RebuildMemoryTablesAsync();
                await File.WriteAllTextAsync(schemaMarker, "rebuilt");
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
                catch (Exception ex) when (ex.Message.Contains("duplicate column")) { /* 列已存在，跳过 */ }
                await File.WriteAllTextAsync(schemaV3, "migrated");
            }

            var schemaV4 = Path.Combine(databaseDirectory, ".image_schema_v4");
            if (!File.Exists(schemaV4))
            {
                try
                {
                    await db.ExecuteAsync("ALTER TABLE ImageRecords ADD COLUMN OcrText TEXT");
                    await db.ExecuteAsync("ALTER TABLE ImageRecords ADD COLUMN HasText INTEGER");
                }
                catch (Exception ex) when (ex.Message.Contains("duplicate column")) { /* 列已存在，跳过 */ }
                await File.WriteAllTextAsync(schemaV4, "migrated");
            }

            var schemaV5 = Path.Combine(databaseDirectory, ".channel_schema_v5");
            if (!File.Exists(schemaV5))
            {
                try
                {
                    await db.ExecuteAsync("ALTER TABLE Channels ADD COLUMN LastExtractedMessageId INTEGER DEFAULT 0");
                }
                catch (Exception ex) when (ex.Message.Contains("duplicate column")) { /* 列已存在，跳过 */ }
                await File.WriteAllTextAsync(schemaV5, "migrated");
            }

            var dreamSchemaV1 = Path.Combine(databaseDirectory, ".dream_schema_v1");
            if (!File.Exists(dreamSchemaV1))
            {
                try
                {
                    await db.ExecuteAsync("ALTER TABLE DreamFragments ADD COLUMN InputMemoryIds TEXT");
                    await db.ExecuteAsync("ALTER TABLE DreamFragments ADD COLUMN OutputRaw TEXT");
                }
                catch (Exception ex) when (ex.Message.Contains("duplicate column")) { /* 列已存在，跳过 */ }
                await File.WriteAllTextAsync(dreamSchemaV1, "migrated");
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
            ReviewLogs = new ReviewLogRepository(db);
            EvaluationScores = new EvaluationScoreRepository(db);
            var images = new ImageRepository(db);
            ImageStorage.Init(images, eventBus);
            ModelCallLogs = new ModelCallLogRepository(db);
            CoreBase.CallLogRepo = ModelCallLogs;

            // Embedding（独立配置，不跟随 Base.json）
            var embConfigPath = Path.Combine(PathConfig.CoreConfigPath, "EmbeddingProvider.json");
            if (File.Exists(embConfigPath))
            {
                var embJson = JObject.Parse(File.ReadAllText(embConfigPath));
                var embEnabled = embJson["enabled"]?.Value<bool>() ?? true;
                if (embEnabled)
                {
                    var embKey = embJson["apiKey"]?.ToString() ?? "";
                    var embEndpoint = embJson["endpoint"]?.ToString() ?? "https://api.siliconflow.cn/v1/embeddings";
                    var embModel = embJson["model"]?.ToString() ?? "BAAI/bge-large-zh-v1.5";
                    embeddingProvider = new SiliconFlowEmbeddingProvider(apiKey: embKey, endpoint: embEndpoint, model: embModel);
                }
                else
                {
                    Signal.Event(LogGroup.Engine, "Embedding提供者已禁用");
                }
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
                var visionConfigPath = Path.Combine(PathConfig.CoreConfigPath, "VisionProvider.json");
                if (File.Exists(visionConfigPath))
                {
                    var vjson = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(visionConfigPath));
                    var vEnabled = vjson["enabled"]?.Value<bool>() ?? true;
                    if (vEnabled)
                    {
                        var vApiKey = vjson["apiKey"]?.ToString() ?? "";
                        var vEndpoint = vjson["endpoint"]?.ToString() ?? "https://api.siliconflow.cn/v1/chat/completions";
                        var vModel = vjson["model"]?.ToString() ?? "Qwen/Qwen2.5-VL-72B-Instruct";

                        if (!string.IsNullOrEmpty(vApiKey))
                        {
                            visionProvider = new SiliconFlowVisionProvider(vApiKey, vEndpoint, vModel);
                        }
                        else
                        {
                            Signal.Warn(LogGroup.Engine, "Vision提供者未配置apiKey，视觉处理不可用");
                        }
                    }
                    else
                    {
                        Signal.Event(LogGroup.Engine, "Vision提供者已禁用");
                    }
                }
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "视觉提供者初始化失败", new { error = ex.Message });
            }

            // OCR（从 OcrProvider.json 读取，不依赖 Base.json）
            try
            {
                var ocrConfigPath = Path.Combine(PathConfig.CoreConfigPath, "OcrProvider.json");
                if (File.Exists(ocrConfigPath))
                {
                    var ojson = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(ocrConfigPath));
                    var ocrEnabled = ojson["enabled"]?.Value<bool>() ?? true;
                    if (ocrEnabled)
                    {
                        var ocrApiKey = ojson["apiKey"]?.ToString() ?? "";
                        var ocrEndpoint = ojson["endpoint"]?.ToString() ?? "https://api.siliconflow.cn/v1/chat/completions";
                        var ocrModel = ojson["model"]?.ToString() ?? "deepseek-ai/DeepSeek-OCR";

                        if (!string.IsNullOrEmpty(ocrApiKey))
                        {
                            ocrProvider = new SiliconFlowOcrProvider(ocrApiKey, ocrEndpoint, ocrModel);
                        }
                        else
                        {
                            Signal.Warn(LogGroup.Engine, "OCR提供者未配置apiKey，OCR处理不可用");
                        }
                    }
                    else
                    {
                        Signal.Event(LogGroup.Engine, "OCR提供者已禁用");
                    }
                }
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "OCR提供者初始化失败", new { error = ex.Message });
            }

            // 服务
            MemorySvc = new MemoryService(Memories, TempMemories, MemoryLinks, embeddingProvider!, PersonaMemories);
            Session = new SessionManager(users, persons, channels, messages);

            // 冲动值配置
            ImpulseConfig = ImpulseConfig.Load(
                Path.Combine(PathConfig.StoragePath, "Engine", "ImpulseConfig.json"));
            TrustConfig = TrustProgressionConfig.Load(
                Path.Combine(PathConfig.StoragePath, "Engine", "TrustProgressionConfig.json"));

            // 信号过滤器配置
            SignalFilters = new SignalFilterManager(
                Path.Combine(PathConfig.StoragePath, "Engine", "SignalFilter.json"));

            // 人设记忆种子加载（表空时从文件导入）
            await LoadPersonaMemorySeedAsync();

            // 创建 DelegationBus + CrossRequestRegistry（委托系统）
            var systemLoopPath = Path.Combine(PathConfig.StoragePath, "SystemLoop");
            Directory.CreateDirectory(systemLoopPath);
            DelegationBus = new DelegationBus();
            CrossRequests = new CrossRequestRegistry(systemLoopPath, DelegationBus);
            CrossRequests.OnRequestSubmitted += initiatorId =>
            {
                if (initiatorId == LoopId.System && systemEngine != null)
                    systemEngine.SignalGate();
                else if (LoopId.IsChannel(initiatorId, out var chId))
                    WakeLoop(initiatorId);
            };
            CrossRequests.OnRequestUpdated += loopId =>
            {
                WakeLoop(loopId);
            };
            CrossRequests.OnRequestCompleted += request =>
            {
                if (LoopId.IsChannel(request.InitiatorId, out var chId))
                {
                    var lastResp = request.Responses.LastOrDefault();
                    var status = request.State == CrossRequestState.Completed ? "完成" : "拒绝";
                    var msg = $"[委托结果]「{request.Title}」已{status}";
                    if (lastResp != null && !string.IsNullOrEmpty(lastResp.Content))
                        msg += $"：{lastResp.Content.Truncate(300)}";
                    var payload = JsonConvert.SerializeObject(new { channelId = chId, message = msg });
                    eventBus.PublishSignal("delegation-result", payload);
                }
            };

            // 注册核心工具（不可卸载的循环控制工具）
            Tool.ToolRegistry.Register(new Tool.Core.ContinueLoopTool());
            Tool.ToolRegistry.Register(new Tool.Core.WaitTool());

            Tool.ToolRegistry.Register(new Tool.Core.EscalateTool());
            Tool.ToolRegistry.Register(new Tool.Core.DeescalateTool());

            // 插件加载
            _toolContext = new Tool.Host.ToolContextImpl(new Tool.Host.PluginStorageImpl("_system"));
            var memoryAccess = new Tool.Host.MemoryAccessImpl(Memories, TempMemories, MemoryLinks, Embedding);
            _toolContext.Register<AgentLilara.PluginSDK.Services.IMemoryAccess>(memoryAccess);
            _toolContext.Register<AgentLilara.PluginSDK.Services.IPersonAccess>(
                new Tool.Host.PersonAccessImpl(this));
            _toolContext.Register<AgentLilara.PluginSDK.Services.IChannelAccess>(
                new Tool.Host.ChannelAccessImpl(this));
            _toolContext.Register<AgentLilara.PluginSDK.Services.IBeaconAccess>(
                new Tool.Host.BeaconAccessImpl(ReviewHints));
            _toolContext.Register<AgentLilara.PluginSDK.Services.IAdapterAccess>(
                new Tool.Host.AdapterAccessImpl(adapterManager));
            _pluginLoader = new Tool.Host.PluginLoader(_toolContext, ProviderRegistry);
            _pluginLoader.LoadAll();
            Signal.Event(LogGroup.Plugin, "插件加载完成", new { count = Tool.ToolRegistry.All.Count });

            // Component 系统初始化（PluginLoader 已填充 ComponentRegistry）
            componentServices.Register<AgentLilara.PluginSDK.Services.ISubAgentAccess>(
                new Component.SubAgentAccessAdapter(this));
            componentServices.Register<AgentLilara.PluginSDK.IToolContext>(_toolContext);
            componentServices.Register<AgentLilara.PluginSDK.Services.IMemoryAccess>(memoryAccess);
            componentServices.Register<AgentLilara.PluginSDK.Services.IBeaconAccess>(
                new Tool.Host.BeaconAccessImpl(ReviewHints));
            componentServices.Register<AgentLilara.PluginSDK.Services.IChannelAccess>(
                new Tool.Host.ChannelAccessImpl(this));
            componentServices.Register<AgentLilara.PluginSDK.Services.IAdapterAccess>(
                new Tool.Host.AdapterAccessImpl(adapterManager));
            if (_signalLogger != null)
                componentServices.Register<ISignalLogger>(_signalLogger);

            globalComponentHost = new GlobalComponentHost(
                _moduleBus, componentServices,
                loopId => WakeLoop(loopId));
            await globalComponentHost.InitAsync();

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

                    // Phase 5: 保存 SystemEngine 引用
                    if (engine is SystemEngine sysEngine)
                    {
                        systemEngine = sysEngine;
                    }
                }
            }

            // MCP Server 初始化
            try
            {
                var mcpConfigPath = Path.Combine(PathConfig.StoragePath, "MCP", "McpServers.json");
                mcpManager = new McpServerManager(mcpConfigPath);
                await mcpManager.InitAsync();
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "MCP管理器初始化失败", new { error = ex.Message });
            }

            Signal.Event(LogGroup.Engine, "引擎就绪");
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
                    Signal.Error(LogGroup.Engine, "SpawnCheck异常", new { checkType = check.GetType().Name, error = ex.Message });
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
                        Signal.Error(LogGroup.Engine, "引擎事件处理异常", new { engineType = engine.EngineType, error = ex.Message });
                    }
                }
            }

            // ④ 清理已死亡的实例
            lock (engineLock)
            {
                activeEngines.RemoveAll(e => !e.IsAlive);
                _engineTasks.RemoveAll(t => !t.Engine.IsAlive);
            }
        }

        // ---- 引擎管理 ----

        public ISubEngine StartEngine(ISubEngine engine)
        {
            lock (engineLock) { activeEngines.Add(engine); }
            var task = Task.Run(async () =>
            {
                try
                {
                    await engine.RunAsync();
                }
                catch (Exception ex)
                {
                    Signal.Error(LogGroup.Engine, "未捕获异常", new {
                        exception = ex.GetType().Name,
                        message = ex.Message,
                        stack = ex.StackTrace
                    });
                }
            });
            lock (engineLock) { _engineTasks.Add((engine, task)); }
            return engine;
        }

        public void RequestStopEngine(ISubEngine engine)
        {
            engine.RequestStop();
        }

        /// <summary>请求所有活跃引擎停止。</summary>
        public void RequestStopAll()
        {
            lock (engineLock)
            {
                foreach (var e in activeEngines.Where(e => e.IsAlive))
                    e.RequestStop();
            }
        }

        /// <summary>等待所有引擎 Task 完成。返回 true 表示全部退出，false 表示超时。</summary>
        public async Task<bool> WaitAllStoppedAsync(TimeSpan timeout)
        {
            List<Task> tasks;
            lock (engineLock) { tasks = _engineTasks.Select(t => t.Task).ToList(); }
            if (tasks.Count == 0) return true;
            var allDone = Task.WhenAll(tasks);
            var completed = await Task.WhenAny(allDone, Task.Delay(timeout));
            return completed == allDone;
        }

        /// <summary>获取仍存活的引擎数量。</summary>
        public int GetAliveCount()
        {
            lock (engineLock) { return activeEngines.Count(e => e.IsAlive); }
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
                    catch (Exception ex) { Signal.Warn(LogGroup.Engine, "Persona嵌入失败", new { line, error = ex.Message }); }

                    await PersonaMemories.CreateAsync(line, embBytes);
                    loaded++;
                }

            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "人设记忆种子加载失败", new { error = ex.Message });
            }
        }

        /// <summary>唤醒指定 loopId 的循环（供 GlobalComponentHost / 委托系统使用）。</summary>
        private void WakeLoop(string loopId)
        {
            if (LoopId.IsSystem(loopId))
            {
                systemEngine?.SignalGate();
                return;
            }

            if (LoopId.IsChannel(loopId, out var channelId))
            {
                var check = GetSpawnCheck<ChannelEngineSpawnCheck>();
                if (check != null)
                {
                    var channels = check.GetActiveChannels();
                    if (channels.TryGetValue(channelId, out var engine) && engine.IsAlive)
                    {
                        engine.SignalGate();
                    }
                    else
                    {
                        _ = ColdStartChannelAsync(channelId, check);
                    }
                }
                return;
            }

            // task: / review: — 不直接唤醒，由各自的 Gate 管理
        }

        private async Task ColdStartChannelAsync(int channelId, ChannelEngineSpawnCheck check)
        {
            try
            {
                var channel = await Session.GetChannelByIdAsync(channelId);
                if (channel == null)
                {
                    Signal.Warn(LogGroup.Engine, "冷启动失败：频道不存在", new { channelId });
                    return;
                }

                var engine = check.TryColdStart(channel, this);
                if (engine == null) return;

                StartEngine(engine);
                Signal.Event(LogGroup.Engine, "频道引擎冷启动", new { channelId, channelName = channel.Name });

                await Task.Delay(200);
                engine.SignalGate();
            }
            catch (Exception ex)
            {
                Signal.Error(LogGroup.Engine, "冷启动异常", new { channelId, error = ex.Message });
            }
        }

        /// <summary>关闭 GlobalComponentHost（由外部调用或 Dispose 时）。</summary>
        public async Task ShutdownComponentsAsync()
        {
            if (globalComponentHost != null)
            {
                await globalComponentHost.ShutdownAsync(ShutdownReason.Destroy);
            }
        }
    }
}
