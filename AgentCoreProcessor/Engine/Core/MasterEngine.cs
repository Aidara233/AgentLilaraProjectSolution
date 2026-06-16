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
using AgentCoreProcessor.Tool.Host;
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
        public BeaconRepository Beacons { get; private set; } = null!;
        public PersonTraitRepository PersonTraits { get; private set; } = null!;
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
        public string? CurrentDreamPhase { get; set; }
        public DateTime? DreamStartTime { get; set; }
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
            ("Review",  () => new ReviewEngineSpawnCheck()),
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

        /// <summary>
        /// 即时切换组件在指定引擎类型下的启用状态。
        /// 同时持久化配置并通知所有运行中的实例。
        /// </summary>
        public async Task ToggleComponentLiveAsync(string componentName, string engineType, bool enabled)
        {
            // 1. 持久化配置
            var config = ComponentConfig.Load();
            config.SetEnabled(componentName, engineType, enabled);

            // 2. Global 组件：GlobalComponentHost 实例始终存在，直接切换
            if (GlobalComponentHost != null)
            {
                var reg = ComponentRegistry.Get(componentName);
                if (reg != null && reg.Scope == ComponentScope.Global)
                {
                    if (enabled)
                        await GlobalComponentHost.EnableComponentAsync(componentName);
                    else
                        await GlobalComponentHost.DisableComponentAsync(componentName);
                }
            }

            // 3. Loop 组件：遍历所有活跃引擎中匹配的 ComponentHost
            var loopType = engineType switch
            {
                "channel" => "channel",
                "system" => "system",
                "sub-agent" => "sub-agent",
                "review" => "review",
                _ => engineType
            };
            foreach (var engine in GetActiveEnginesSnapshot())
            {
                if (engine.ComponentHost == null) continue;
                if (engine.EngineType != loopType) continue;

                if (enabled)
                    await engine.ComponentHost.EnableComponentAsync(componentName);
                else
                    await engine.ComponentHost.DisableComponentAsync(componentName);
            }
        }

        /// <summary>
        /// 重载单个插件：卸载旧 DLL + 重新加载 + 为 Global 组件重建实例。
        /// </summary>
        public async Task ReloadPluginAsync(string pluginName)
        {
            // 先移除相关 Global 组件实例（关闭 + 从列表中移除）
            if (GlobalComponentHost != null)
            {
                var plugin = FindPluginByName(pluginName);
                if (plugin != null)
                {
                    foreach (var compName in plugin.ComponentNames)
                    {
                        var reg = ComponentRegistry.Get(compName);
                        if (reg != null && reg.Scope == ComponentScope.Global)
                            await GlobalComponentHost.RemoveComponentAsync(compName);
                    }
                }
            }

            // 卸载并重新加载 DLL
            PluginLoader.ReloadSingle(pluginName);
            ComponentConfig.Invalidate();

            // 为新注册的 Global 组件创建实例
            if (GlobalComponentHost != null)
            {
                var config = ComponentConfig.Load();
                var plugin = FindPluginByName(pluginName);
                if (plugin != null)
                {
                    foreach (var compName in plugin.ComponentNames)
                    {
                        var reg = ComponentRegistry.Get(compName);
                        if (reg != null && reg.Scope == ComponentScope.Global)
                        {
                            // 尊重配置：如果该组件对任何引擎类型都未禁用，则创建
                            if (config.IsEnabled(compName, "channel", true, reg.ChannelApplicability) ||
                                config.IsEnabled(compName, "system", true, reg.SystemApplicability) ||
                                config.IsEnabled(compName, "review", true, reg.ReviewApplicability) ||
                                config.IsEnabled(compName, "sub-agent", true, reg.SubAgentApplicability))
                            {
                                await GlobalComponentHost.CreateAndEnableComponentAsync(compName);
                            }
                        }
                    }
                }
            }

            Signal.Event(LogGroup.Plugin, "插件已重载", new { plugin = pluginName });
        }

        private PluginEntry? FindPluginByName(string name)
        {
            return PluginLoader.LoadedPlugins
                .FirstOrDefault(p => p.PluginId.Equals(name, StringComparison.OrdinalIgnoreCase)
                                 || Path.GetFileNameWithoutExtension(p.FileName).Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        // ---- 初始化 ----

        public async Task InitAsync()
        {
            Directory.CreateDirectory(databaseDirectory);
            var dbPath = Path.Combine(databaseDirectory, "lilara.db");
            db = new DbManager(dbPath);
            await db.InitAsync();
            Signal.Event(LogGroup.Engine, "数据库初始化完成");

            // Repository
            var persons = new PersonRepository(db);
            var users = new UserRepository(db, persons);
            var channels = new ChannelRepository(db);
            var messages = new MessageRepository(db);
            Memories = new MemoryRepository(db);
            TempMemories = new TempMemoryRepository(db);
            MemoryLinks = new MemoryLinkRepository(db);
            PersonaMemories = new PersonaMemoryRepository(db);
            Beacons = new BeaconRepository(db);
            PersonTraits = new PersonTraitRepository(db);
            DreamLogs = new DreamLogRepository(db);
            ReviewLogs = new ReviewLogRepository(db);
            EvaluationScores = new EvaluationScoreRepository(db);
            var images = new ImageRepository(db);
            ImageStorage.Init(images, eventBus);
            ModelCallLogs = new ModelCallLogRepository(db);
            CoreBase.CallLogRepo = ModelCallLogs;

            // Embedding（独立配置，支持 siliconflow / onnx）
            var embConfigPath = Path.Combine(PathConfig.CoreConfigPath, "EmbeddingProvider.json");
            if (File.Exists(embConfigPath))
            {
                var embCfg = JsonConvert.DeserializeObject<Client.EmbeddingProviderConfig>(
                    File.ReadAllText(embConfigPath));
                if (embCfg != null)
                {
                    if (embCfg.Provider == "onnx"
                        && File.Exists(Path.Combine(PathConfig.StoragePath, embCfg.OnnxModelPath))
                        && File.Exists(Path.Combine(PathConfig.StoragePath, embCfg.TokenizerVocabPath)))
                    {
                        try
                        {
                            var onnxModelPath = Path.Combine(PathConfig.StoragePath, embCfg.OnnxModelPath);
                            var vocabPath = Path.Combine(PathConfig.StoragePath, embCfg.TokenizerVocabPath);
                            embeddingProvider = new Client.OnnxEmbeddingProvider(new Client.EmbeddingProviderConfig
                            {
                                Provider = "onnx",
                                OnnxModelPath = onnxModelPath,
                                TokenizerVocabPath = vocabPath,
                                MaxLength = embCfg.MaxLength,
                                ExecutionProvider = embCfg.ExecutionProvider
                            });
                            Signal.Event(LogGroup.Engine, "ONNX Embedding 提供者已就绪");
                        }
                        catch (Exception ex)
                        {
                            Signal.Error(LogGroup.Engine, "ONNX Embedding 初始化失败，降级到 SiliconFlow",
                                new { error = ex.Message });
                            embeddingProvider = new SiliconFlowEmbeddingProvider(
                                apiKey: embCfg.ApiKey, endpoint: embCfg.Endpoint, model: embCfg.Model);
                        }
                    }
                    else
                    {
                        embeddingProvider = new SiliconFlowEmbeddingProvider(
                            apiKey: embCfg.ApiKey, endpoint: embCfg.Endpoint, model: embCfg.Model);
                    }
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

            // OCR（先本地 Umi-OCR，后远程 SiliconFlow，支持 fallback 链）
            try
            {
                var ocrProviders = new List<IOcrProvider>();

                // 1. 本地 Umi-OCR（优先）
                var umiConfigPath = Path.Combine(PathConfig.CoreConfigPath, "UmiOcr.json");
                if (File.Exists(umiConfigPath))
                {
                    try
                    {
                        var umiJson = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(umiConfigPath));
                        var umiEnabled = umiJson["enabled"]?.Value<bool>() ?? false;
                        if (umiEnabled)
                        {
                            var umiHost = umiJson["host"]?.ToString() ?? "127.0.0.1";
                            var umiPort = umiJson["port"]?.Value<int>() ?? 1846;
                            var umiAutoStart = umiJson["autoStart"]?.Value<bool>() ?? false;
                            var umiExePath = umiJson["exePath"]?.ToString() ?? "";

                            UmiOcrProvider? umiProvider;
                            if (umiAutoStart && !string.IsNullOrEmpty(umiExePath))
                            {
                                // 后台异步启动，不阻塞主路径
                                _ = UmiOcrProvider.CreateWithAutoStartAsync(umiExePath, umiHost, umiPort)
                                    .ContinueWith(t =>
                                    {
                                        if (!t.IsFaulted && t.Result != null)
                                        {
                                            var fp = ocrProvider as FallbackOcrProvider;
                                            fp?.InsertFirst(t.Result);
                                            Signal.Event(LogGroup.Engine, "Umi-OCR后台启动完成",
                                                new { host = umiHost, port = umiPort });
                                        }
                                    });
                                umiProvider = null;
                            }
                            else
                            {
                                umiProvider = new UmiOcrProvider(umiHost, umiPort);
                                if (!await umiProvider.HealthCheckAsync())
                                {
                                    Signal.Warn(LogGroup.Engine, "Umi-OCR未响应，请确认Umi-OCR.exe正在运行",
                                        new { host = umiHost, port = umiPort });
                                    umiProvider.Dispose();
                                    umiProvider = null;
                                }
                            }

                            if (umiProvider != null)
                            {
                                ocrProviders.Add(umiProvider);
                                Signal.Event(LogGroup.Engine, "Umi-OCR本地提供者已就绪",
                                    new { host = umiHost, port = umiPort });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Signal.Warn(LogGroup.Engine, "Umi-OCR配置读取失败", new { error = ex.Message });
                    }
                }

                // 2. 远程 SiliconFlow OCR（fallback）
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
                            ocrProviders.Add(new SiliconFlowOcrProvider(ocrApiKey, ocrEndpoint, ocrModel));
                            Signal.Event(LogGroup.Engine, "SiliconFlow OCR远程提供者已就绪");
                        }
                        else
                        {
                            Signal.Warn(LogGroup.Engine, "OCR远程提供者未配置apiKey");
                        }
                    }
                    else
                    {
                        Signal.Event(LogGroup.Engine, "OCR远程提供者已禁用");
                    }
                }

                if (ocrProviders.Count > 0)
                    ocrProvider = new FallbackOcrProvider(ocrProviders);
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
            Tool.ToolRegistry.Register(new Tool.Core.ContinueLoopTool(), isNonComponent: true);
            Tool.ToolRegistry.Register(new Tool.Core.WaitTool(), isNonComponent: true);

            Tool.ToolRegistry.Register(new Tool.Core.EscalateTool(), isNonComponent: true);
            Tool.ToolRegistry.Register(new Tool.Core.DeescalateTool(), isNonComponent: true);
            Tool.ToolRegistry.Register(new Tool.Core.SwitchModeTool(), isNonComponent: true);
            Tool.ToolRegistry.Register(new Tool.Core.RefineImageTool(), isNonComponent: true);
            Tool.ToolRegistry.Register(new Tool.Core.GetImageTextTool(), isNonComponent: true);

            // 插件加载
            _toolContext = new Tool.Host.ToolContextImpl(new Tool.Host.PluginStorageImpl("_system"));
            var memoryAccess = new Tool.Host.MemoryAccessImpl(Memories, TempMemories, MemoryLinks, Embedding);
            _toolContext.Register<AgentLilara.PluginSDK.Services.IMemoryAccess>(memoryAccess);
            _toolContext.Register<AgentLilara.PluginSDK.Services.IPersonAccess>(
                new Tool.Host.PersonAccessImpl(this));
            _toolContext.Register<AgentLilara.PluginSDK.Services.IChannelAccess>(
                new Tool.Host.ChannelAccessImpl(this));
            _toolContext.Register<AgentLilara.PluginSDK.Services.IBeaconAccess>(
                new Tool.Host.BeaconAccessImpl(Beacons));
            _toolContext.Register<AgentLilara.PluginSDK.Services.IAdapterAccess>(
                new Tool.Host.AdapterAccessImpl(adapterManager));
            var dicePool = new Tool.Host.DicePoolImpl();
            _toolContext.Register<AgentLilara.PluginSDK.Services.IDiceRegistry>(dicePool);
            _toolContext.Register<AgentLilara.PluginSDK.Services.IDiceService>(dicePool);
            _pluginLoader = new Tool.Host.PluginLoader(_toolContext, ProviderRegistry);

            // === Phase 2: 并行初始化（插件链 + MCP） ===

            var pluginChainTask = Task.Run(async () =>
            {
                _pluginLoader.LoadAll();
                Signal.Event(LogGroup.Plugin, "插件加载完成", new { count = Tool.ToolRegistry.All.Count });

                // Component 服务注册（依赖 PluginLoader 填充 ComponentRegistry）
                componentServices.Register<AgentLilara.PluginSDK.Services.ISubAgentAccess>(
                    new Component.SubAgentAccessAdapter(this));
                componentServices.Register<AgentLilara.PluginSDK.IToolContext>(_toolContext);
                componentServices.Register<AgentLilara.PluginSDK.Services.IMemoryAccess>(memoryAccess);
                componentServices.Register<AgentLilara.PluginSDK.Services.IBeaconAccess>(
                    new Tool.Host.BeaconAccessImpl(Beacons));
                componentServices.Register<AgentLilara.PluginSDK.Services.IChannelAccess>(
                    new Tool.Host.ChannelAccessImpl(this));
                componentServices.Register<AgentLilara.PluginSDK.Services.IAdapterAccess>(
                    new Tool.Host.AdapterAccessImpl(adapterManager));
                componentServices.Register<AgentLilara.PluginSDK.Services.IDiceRegistry>(dicePool);
                componentServices.Register<AgentLilara.PluginSDK.Services.IDiceService>(dicePool);
                if (_signalLogger != null)
                    componentServices.Register<ISignalLogger>(_signalLogger);

                var imageAccess = new Component.ImageAccessImpl(
                    Config.PathConfig.WorkspacePath, ocrProvider);
                _toolContext.Register<AgentLilara.PluginSDK.Services.IImageAccess>(imageAccess);
                componentServices.Register<AgentLilara.PluginSDK.Services.IImageAccess>(imageAccess);

                var currencyService = new Tool.Host.CurrencyServiceImpl(
                    Path.Combine(PathConfig.StoragePath, "PluginData", "currency"));
                _toolContext.Register<AgentLilara.PluginSDK.Services.ICurrencyService>(currencyService);
                componentServices.Register<AgentLilara.PluginSDK.Services.ICurrencyService>(currencyService);

                var productRegistry = new Tool.Host.ProductRegistryImpl();
                _toolContext.Register<AgentLilara.PluginSDK.Services.IProductRegistry>(productRegistry);
                componentServices.Register<AgentLilara.PluginSDK.Services.IProductRegistry>(productRegistry);

                globalComponentHost = new GlobalComponentHost(
                    _moduleBus, componentServices,
                    loopId => WakeLoop(loopId));
                await globalComponentHost.InitAsync();
            });

            Task mcpInitTask;
            try
            {
                var mcpConfigPath = Path.Combine(PathConfig.StoragePath, "MCP", "McpServers.json");
                mcpManager = new McpServerManager(mcpConfigPath);
                mcpInitTask = mcpManager.InitAsync();
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "MCP管理器初始化失败", new { error = ex.Message });
                mcpInitTask = Task.CompletedTask;
            }

            await Task.WhenAll(pluginChainTask, mcpInitTask);

            // === Phase 3: 最终注册 ===

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
            }
            catch (Exception ex)
            {
                Signal.Error(LogGroup.Engine, "冷启动异常", new { channelId, error = ex.Message });
            }
        }

        /// <summary>关闭 GlobalComponentHost 和 OCR 提供者（由外部调用或 Dispose 时）。</summary>
        public async Task ShutdownComponentsAsync()
        {
            if (globalComponentHost != null)
            {
                await globalComponentHost.ShutdownAsync(ShutdownReason.Destroy);
            }
            (ocrProvider as IDisposable)?.Dispose();
        }
    }
}
