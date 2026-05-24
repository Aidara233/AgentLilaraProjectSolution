using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Component;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Engine.Modules;
using AgentCoreProcessor.Logging;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 系统循环引擎。单例，长期运行，纯调度者。
    /// 闸门模型：任何人可升闸（唤醒），只有模型调 Wait 才落闸。
    /// 追加式上下文：固定前缀 + 持续增长的对话历史 + 每轮新增的状态/事件。
    /// </summary>
    internal class SystemEngine : ISubEngine, IAgentHost
    {
        public string EngineType => "System";
        public bool IsAlive { get; private set; } = true;
        public bool IsInfrastructure => false;
        public bool IsBusy => Interlocked.Read(ref _busyFlag) == 1;
        private long _busyFlag = 0;

        // ChannelSignal buffer + IInjectProvider collection (Task 9)
        private readonly ConcurrentQueue<ChannelSignal> _signalBuffer = new();
        private readonly List<IInjectProvider> _injectProviders = new();

        private readonly ISystemContext ctx;
        private readonly AgentCore agentCore;
        private Gate gate = null!;
        private Agent? agent;
        private AgentConfig agentConfig = null!;
        private CompressionTierModule? compressionTierModule;
        private readonly LoopBus bus = new();
        private CancellationTokenSource? stopCts;
        private readonly DateTime startTime = DateTime.Now;

        // 模块
        private readonly LoopControlModule loopControlModule = new();
        private readonly PendingEventsModule pendingEventsModule = new();
        private readonly ContextPersistence persistence;
        private readonly ContextCompressionModule compressionModule;
        private List<EngineModule> modules = null!;

        // Component 系统
        private readonly ModuleBus _moduleBus = new();
        private ComponentHost? componentHost;

        private const int MaxContextTokens = 80000;
        private const int SoftThresholdPercent = 60;
        private const int HardThresholdPercent = 85;

        // 原生工具调用
        private readonly bool useNativeTools;

        // 系统状态（供频道循环感知）
        public SystemLoopState CurrentState { get; private set; } = SystemLoopState.Active;

        // Agent 循环状态
        private const int MaxRoundsPerWake = 20;
        private bool lastRoundNoAction;

        // 子 agent 管理
        private readonly Dictionary<string, IAgentSession> subAgents = new();
        private readonly object subAgentLock = new();

        // 错误追踪
        private int consecutiveFailures = 0;
        private DateTime? lastErrorTime = null;
        private string? lastErrorMessage = null;
        private int totalErrorCount = 0;
        private int _totalCycles = 0;

        // 睡觉评估和许可管理
        private class SleepRequest
        {
            public DateTime RequestTime { get; set; }
            public float Score { get; set; }
            public SleepRequestStatus Status { get; set; }
            public string? RequestId { get; set; }
        }

        private enum SleepRequestStatus { Pending, Approved, Denied }
        private SleepRequest? pendingSleepRequest = null;
        private DateTime lastSleepCheck = DateTime.MinValue;

        // 待处理的定时任务到期事件（由 OnEvent 写入，RunAsync 读取）
        private readonly List<ScheduledTaskFiredEvent> pendingScheduledEvents = new();
        private readonly object scheduledEventsLock = new();

        // 跨循环请求队列（新委托系统）
        private readonly ConcurrentQueue<CrossRequest> _pendingCrossRequests = new();
        internal IAgentMessaging? _messaging;

        public SystemEngine(ISystemContext ctx)
        {
            this.ctx = ctx;
            this.agentCore = new AgentCore("SystemCore", usePersona: false);
            agentCore.CallerTag = "System";
            useNativeTools = agentCore.UseNativeTools;

            var systemLoopPath = Path.Combine(PathConfig.StoragePath, "SystemLoop");
            persistence = new ContextPersistence(systemLoopPath);
            compressionModule = new ContextCompressionModule(persistence);

            modules = new List<EngineModule>
            {
                pendingEventsModule,         // 优先级 38
                loopControlModule,           // 优先级 60
                compressionModule            // 优先级 100（不注入 prompt）
            };

            foreach (var m in modules) m.Attach(bus);

            // ── Agent 配置 ──
            agentConfig = new AgentConfig
            {
                MaxRounds = MaxRoundsPerWake,
                CompressL1Tokens = MaxContextTokens * SoftThresholdPercent / 100,
                CompressL2Tokens = MaxContextTokens * 70 / 100,
                CompressL3Tokens = MaxContextTokens * HardThresholdPercent / 100,
                CompressMinTokens = 5000,
                CompressRetainedMessageCount = 10,
                CompressRetainedMaxTokens = 2000
            };

            // ── 压缩模块 ──
            compressionTierModule = new CompressionTierModule(agentConfig,
                () => agent?.History ?? new List<Message>(),
                () =>
                {
                    if (compressionTierModule != null && agent != null)
                    {
                        compressionTierModule.CompressSyncAsync(
                            agent.History,
                            (summary, retained) =>
                            {
                                compressionModule.SetSummary(summary);
                                agent.ClearHistory();
                                foreach (var m in retained)
                                    agent.AddToHistory(m);
                                // Persist after compression
                                persistence.SaveSummaryAndClearContext(summary);
                                PersistAgentHistory();
                            }).GetAwaiter().GetResult();
                    }
                });
            compressionTierModule.SetSummary(compressionModule.GetSummary());

            // ── Agent ──
            agent = new Agent(this, agentCore, agentConfig, GetAuthorizedTools());

            // ── 恢复持久化上下文 ──
            RestoreContext();

            // ── Gate（替代 LoopGate）──
            gate = new Gate(ctx.EventBus);
            gate.ShouldActivate = () => Task.FromResult(true);
            gate.ExecuteAsync = ExecuteSystemCycleAsync;
            gate.EventFilter = e => e is TimerEvent or SignalEvent;

        }

        private void RestoreContext()
        {
            var (summary, rounds) = persistence.LoadContext();
            compressionModule.SetSummary(summary);
            compressionTierModule?.SetSummary(summary);

            // Load persisted rounds into Agent
            if (agent != null && rounds.Count > 0)
            {
                foreach (var round in rounds)
                    foreach (var msg in round)
                        agent.AddToHistory(msg);
            }
        }

        private void PersistAgentHistory()
        {
            if (agent == null) return;
            var history = agent.History;
            if (history.Count >= 2)
            {
                var lastUser = history[history.Count - 2];
                var lastAsst = history[history.Count - 1];
                if (lastUser.Role == "user" && lastAsst.Role == "assistant")
                {
                    persistence.AppendRound(
                        new List<Message> { lastUser },
                        new List<Message> { lastAsst });
                }
            }
        }

        private (int tokens, int percent) GetContextUsage()
        {
            var estTokens = agent?.History.Sum(m => EstimateMessageTokens(m)) / 3 ?? 0;
            return (estTokens, (int)(estTokens * 100.0 / MaxContextTokens));
        }

        private static int EstimateMessageTokens(Message m)
        {
            if (m.Content != null)
                return m.Content.Length;
            if (m.ContentParts != null)
                return m.ContentParts.Sum(p =>
                    (p.Text?.Length ?? 0) + (p.ToolInput?.Length ?? 0) + (p.ToolName?.Length ?? 0));
            return 0;
        }

        public async Task RunAsync()
        {
            stopCts = new CancellationTokenSource();
            var ct = stopCts.Token;

            // 引擎生命周期信号（Continue 从 startup signal，自动建立因果连线）
            var parentCtx = SignalContext.Current;
            var lifeCtx = Signal.Continue(
                parentCtx?.SignalId ?? Signal.NewId(), parentCtx?.CurrentSpanId,
                "system:main", LogGroup.Engine, "System引擎",
                new { engineType = EngineType });

            // 初始化 ComponentHost
            componentHost = new ComponentHost(
                LoopId.System, "system", _moduleBus, ctx.ComponentServices,
                () => gate.Signal());
            await componentHost.InitAsync();

            // 注册到委托总线
            _messaging = new Component.AgentMessagingImpl(LoopId.System, ctx.CrossRequests,
                () => gate.Signal(), loopId => ctx.DelegationBus.IsLoopActive(loopId));
            ctx.DelegationBus.RegisterLoop(LoopId.System, OnCrossRequestReceived);

            // 收集 IInjectProvider：内部模块 + 插件实例
            _injectProviders.Clear();
            _injectProviders.AddRange(modules);  // EngineModule : IInjectProvider

            if (ctx.PluginLoader != null)
            {
                var engineServices = BuildEngineServiceProvider();
                foreach (var type in ctx.PluginLoader.InjectProviderTypes)
                {
                    try
                    {
                        var provider = ctx.PluginLoader.InstantiateInjectProvider(type, engineServices);
                        if (provider != null)
                            _injectProviders.Add(provider);
                    }
                    catch (Exception ex) { Signal.Warn(LogGroup.Engine, $"插件实例化失败: {type.Name}", new { type = type.FullName, error = ex.Message }); }
                }
            }

            try
            {
                await gate.RunAsync(ct);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                totalErrorCount++;
                lastErrorTime = DateTime.Now;
                lastErrorMessage = $"[致命] {ex.GetType().Name}: {ex.Message}";
                Signal.Error(LogGroup.Engine, "系统引擎致命异常", new { error = ex.GetType().Name, message = ex.Message });
            }
            finally
            {
                // 注销委托总线
                ctx.DelegationBus.UnregisterLoop(LoopId.System);

                // 关闭 ComponentHost
                if (componentHost != null)
                    await componentHost.ShutdownAsync(ShutdownReason.Destroy);

                IsAlive = false;
                foreach (var m in modules) m.Reset();

                lifeCtx.Close(new { engineType = EngineType, reason = "shutdown" });
            }
        }

        /// <summary>系统循环执行体。Gate 每次开闸时调用一次。包含事件收集 + Agent 多轮推理。</summary>
        private async Task ExecuteSystemCycleAsync(CancellationToken ct)
        {
            _totalCycles++;

            // 定期自检（每 5 分钟）
            if ((DateTime.Now - lastSleepCheck).TotalMinutes >= 5)
            {
                await PerformHealthCheckAsync();
                lastSleepCheck = DateTime.Now;
            }

            // ═══ 收集待处理事件 ═══
            var crossRequests = DrainCrossRequests();
            ctx.CrossRequests.EnforceTimeouts();

            // 从 Gate 触发事件继承因果链（Timer 心跳 / 任务提交等）
            var triggerSignalId = gate.LastTriggerSignalId;
            var triggerSpanId = gate.LastTriggerSpanId;
            using var iterSignal = triggerSignalId != null
                ? Signal.Continue(triggerSignalId, triggerSpanId, "system:main", LogGroup.Engine, $"系统循环 #{_totalCycles}", new
                {
                    crossRequests = crossRequests.Count
                })
                : Signal.Begin(LogGroup.Engine, "system:main", $"系统循环 #{_totalCycles}", new
                {
                    crossRequests = crossRequests.Count
                });

            // 填充 PendingEventsModule
            pendingEventsModule.SetPendingCrossRequests(crossRequests);

            // Lazy register compress tool (agent guaranteed to exist here)
            if (agent != null && compressionTierModule != null)
            {
                var existingTool = ToolRegistry.Get("compress");
                if (existingTool == null)
                {
                    ToolRegistry.Register(new Tool.Core.CompressTool(
                        compressionTierModule,
                        agent.History,
                        (summary, retained) =>
                        {
                            compressionModule.SetSummary(summary);
                            agent.ClearHistory();
                            foreach (var m in retained)
                                agent.AddToHistory(m);
                            PersistAgentHistory();
                        }));
                }
            }

            // ═══ Agent 多轮推理 ═══
            Interlocked.Exchange(ref _busyFlag, 1);
            lastRoundNoAction = false;
            try
            {
                await componentHost!.OnActivatedAsync();
                await componentHost.OnBeforeInvokeAsync();
                await agent!.RunAsync(ct);
                await componentHost.OnAfterInvokeAsync();

                if (agent.StopReason == AgentStopReason.Error)
                {
                    consecutiveFailures++;
                    totalErrorCount++;
                    lastErrorTime = DateTime.Now;
                    lastErrorMessage = "Agent 连续模型调用失败";
                    var backoff = agentConfig.BackoffSeconds[
                        Math.Min(consecutiveFailures - 1, agentConfig.BackoffSeconds.Length - 1)];
                    Signal.Warn(LogGroup.Engine, "系统循环退避（Agent Error）",
                        new { consecutiveFailures, backoffSeconds = backoff });
                    await Task.Delay(TimeSpan.FromSeconds(backoff), ct);
                }
                else
                {
                    consecutiveFailures = 0;
                }
                lastRoundNoAction = agent.StopReason == AgentStopReason.Completed;

                Signal.Event(LogGroup.Engine, "Agent轮次完成", new
                {
                    stopReason = agent.StopReason?.ToString(),
                    totalRounds = agent.TotalRounds,
                    historyCount = agent.History.Count,
                    estimatedTokens = agent.History.Sum(m => (m.Content?.Length ?? 0)) / 3
                });

                // Persist after agent round
                PersistAgentHistory();
                SaveModuleState();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                consecutiveFailures++;
                totalErrorCount++;
                lastErrorTime = DateTime.Now;
                lastErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
                Signal.Error(LogGroup.Engine, "系统循环处理异常",
                    new { error = ex.GetType().Name, message = ex.Message, consecutiveFailures });

                if (consecutiveFailures >= agentConfig.MaxRounds)
                {
                    var backoff = agentConfig.BackoffSeconds[
                        Math.Min(consecutiveFailures - 1, agentConfig.BackoffSeconds.Length - 1)];
                    Signal.Warn(LogGroup.Engine, "系统循环退避",
                        new { consecutiveFailures, backoffSeconds = backoff });
                    await Task.Delay(TimeSpan.FromSeconds(backoff), ct);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _busyFlag, 0);
                await componentHost!.OnPauseAsync();
            }
        }

        private IServiceProvider BuildEngineServiceProvider()
        {
            var services = new Dictionary<Type, object>
            {
                [typeof(EventBus)] = ctx.EventBus,
                [typeof(ModuleBus)] = _moduleBus,
                [typeof(Gate)] = gate!,
                [typeof(IAgentMessaging)] = _messaging!,
            };
            return new Component.SimpleServiceProvider(services);
        }

        private List<ScheduledTaskFiredEvent> DrainScheduledEvents()
        {
            lock (scheduledEventsLock)
            {
                var copy = new List<ScheduledTaskFiredEvent>(pendingScheduledEvents);
                pendingScheduledEvents.Clear();
                return copy;
            }
        }

        /// <summary>外部投递定时任务到期事件。</summary>
        internal void EnqueueScheduledEvent(ScheduledTaskFiredEvent evt)
        {
            lock (scheduledEventsLock)
            {
                pendingScheduledEvents.Add(evt);
            }
            gate.Signal();
        }

        private HashSet<string> GetAuthorizedTools()
        {
            return ctx.ToolProfiles.GetActiveTools("system");
        }

        // ---- IAgentHost 实现 ----

        async Task<List<Message>?> IAgentHost.BuildStartInjectAsync()
        {
            var msgs = new List<Message>();

            // 工具描述（engine-level，不是 IInjectProvider）
            if (!useNativeTools)
            {
                var allowed = GetAuthorizedTools();
                var toolDescriptions = ToolRegistry.GenerateDescriptions(filter: t => allowed.Contains(t.Name));
                if (!string.IsNullOrEmpty(toolDescriptions))
                    msgs.Add(new Message { Role = "user", Content = toolDescriptions });
            }

            // 上下文摘要（engine-level，compressionModule.BuildPromptSection 返回 null）
            var summary = compressionModule.GetSummary();
            if (!string.IsNullOrEmpty(summary))
                msgs.Add(new Message { Role = "user", Content = $"[上下文摘要]\n{summary}" });

            // IInjectProvider start injections（内部模块 + 插件）
            var ctx2 = new InjectContext
            {
                Mode = "system",
                CurrentRound = 0,
                MaxRounds = agentConfig.MaxRounds
            };
            foreach (var p in _injectProviders.OrderBy(p => p.InjectPriority))
            {
                try
                {
                    var s = await p.BuildStartInjectAsync(ctx2);
                    if (!string.IsNullOrEmpty(s))
                        msgs.Add(new Message { Role = "user", Content = s });
                }
                catch (Exception ex) { Signal.Warn(LogGroup.Engine, $"InjectProvider.Start失败: {p.GetType().Name}", new { provider = p.GetType().Name, error = ex.Message }); }
            }

            if (msgs.Count > 0)
            {
                Signal.Event(LogGroup.Engine, "上下文组装完成", new
                {
                    mode = "system",
                    totalMessages = msgs.Count,
                    hasSummary = !string.IsNullOrEmpty(compressionModule.GetSummary()),
                    estimatedTokens = msgs.Sum(m => (m.Content?.Length ?? 0)) / 3
                });
            }

            return msgs.Count > 0 ? msgs : null;
        }

        async Task<List<Message>?> IAgentHost.BuildRoundInjectAsync()
        {
            var msgs = new List<Message>();

            // Drain signal buffer — format each signal type
            while (_signalBuffer.TryDequeue(out var signal))
            {
                switch (signal)
                {
                    case BusEventSignal bes:
                        msgs.Add(new Message { Role = "user", Content = $"[系统事件] {bes.Event.GetType().Name}" });
                        break;
                    case CompressionSignal cs:
                        compressionModule.SetSummary(cs.Summary);
                        agent?.ClearHistory();
                        agent?.AddToHistory(new Message { Role = "user", Content = $"[上下文摘要]\n{cs.Summary}" });
                        foreach (var msg in cs.RetainedHistory)
                            agent?.AddToHistory(msg);
                        break;
                    case ModeSwitchSignal mss:
                        // SystemEngine 始终是 "system" 模式，但尊重切换
                        break;
                    default: break;
                }
            }

            // IInjectProvider round injections（内部模块 + 插件）
            var ctx2 = new InjectContext
            {
                Mode = "system",
                CurrentRound = agent?.TotalRounds ?? 1,
                MaxRounds = agentConfig.MaxRounds,
                EstimatedTokens = agent?.History.Sum(m => (m.Content?.Length ?? 0)) / 3 ?? 0
            };
            foreach (var p in _injectProviders.OrderBy(p => p.InjectPriority))
            {
                try
                {
                    var s = await p.BuildRoundInjectAsync(ctx2);
                    if (!string.IsNullOrEmpty(s))
                        msgs.Add(new Message { Role = "user", Content = s });
                }
                catch (Exception ex) { Signal.Warn(LogGroup.Engine, $"InjectProvider.Round失败: {p.GetType().Name}", new { provider = p.GetType().Name, error = ex.Message }); }
            }

            // Compression tier hint（engine-level）
            if (compressionTierModule != null && agent != null)
            {
                var estTokens = agent.History.Sum(m => EstimateMessageTokens(m)) / 3;
                var text = compressionTierModule.GetInjectText(estTokens);
                if (!string.IsNullOrEmpty(text))
                {
                    msgs.Add(new Message { Role = "user", Content = text });
                    if (compressionTierModule.CurrentTier == CompressionTier.L1)
                        compressionTierModule.MarkL1Injected();
                }
            }

            return msgs.Count > 0 ? msgs : null;
        }

        private void SaveModuleState()
        {
            var state = new Dictionary<string, object>
            {
                ["pinboard"] = ReadPinboardEntries(),
                ["timestamp"] = DateTime.Now
            };
            persistence.SaveState(state);
        }

        // ---- 事件处理 ----

        public void OnEvent(EngineEvent e)
        {
            // TimerEvent 不再唤醒闸门 — 由 gate 超时（waitTimeoutMinutes）控制周期

            // 入队信号供 BuildRoundInjectAsync 排空
            _signalBuffer.Enqueue(new BusEventSignal(e));

            if (e is SignalEvent signal)
            {
                switch (signal.SignalName)
                {
                    case "sleep-approve" when pendingSleepRequest != null:
                        if (pendingSleepRequest.RequestId == (string?)signal.Payload)
                        {
                            pendingSleepRequest.Status = SleepRequestStatus.Approved;
                            _ = StartDreamEngineAsync();
                            pendingSleepRequest = null;
                        }
                        break;
                    case "sleep-deny" when pendingSleepRequest != null:
                        if (pendingSleepRequest.RequestId == (string?)signal.Payload)
                        {
                            pendingSleepRequest = null;
                        }
                        break;
                    case "task-submitted":
                        gate.Signal();
                        break;
                }
            }
        }

        public void RequestStop()
        {
            stopCts?.Cancel();
            gate.Signal();
        }

        /// <summary>外部唤醒闸门（委托提交时调用）。</summary>
        public void SignalGate() => gate.Signal();

        /// <summary>强制唤醒（跳过 ShouldActivate）。</summary>
        public void ForceWake() => gate.ForceWake();

        /// <summary>强制触发上下文压缩。</summary>
        public void ForceCompress()
        {
            if (agent == null || compressionTierModule == null || compressionTierModule.IsCompressing) return;
            _ = compressionTierModule.CompressAsync(agent.History, (summary, retained) =>
            {
                agent.History.Clear();
                foreach (var m in retained) agent.AddToHistory(m);
                compressionTierModule.SetSummary(summary);
            });
        }

        /// <summary>获取上下文快照（供 WebUI 详情页）。</summary>
        internal WebUI.Services.EngineContextSnapshot? GetContextSnapshot()
        {
            if (agent == null) return null;
            var history = agent.History;
            var messages = new List<WebUI.Services.ContextMessageSnapshot>();
            int totalChars = 0;
            foreach (var m in history)
            {
                var est = EstimateMessageTokens(m);
                totalChars += est;
                var snap = new WebUI.Services.ContextMessageSnapshot
                {
                    Role = m.Role,
                    Content = m.Content,
                    EstimatedTokens = est / 3
                };
                if (m.ContentParts != null)
                {
                    snap.Parts = m.ContentParts.Select(p => new WebUI.Services.ContextPartSnapshot
                    {
                        Type = p.Type ?? "text",
                        Text = p.Text?.Truncate(500),
                        ToolName = p.ToolName,
                        ToolInput = p.ToolInput?.Truncate(200),
                        IsError = p.IsError
                    }).ToList();
                }
                messages.Add(snap);
            }
            return new WebUI.Services.EngineContextSnapshot
            {
                EstimatedTokens = totalChars / 3,
                MessageCount = history.Count,
                ConversationOffset = agent.ConversationOffset,
                CompressionTier = compressionTierModule?.CurrentTier ?? CompressionTier.None,
                IsCompressing = compressionTierModule?.IsCompressing ?? false,
                Summary = compressionTierModule?.CurrentSummary,
                TotalRounds = agent.TotalRounds,
                IsInBackoff = agent.IsInBackoff,
                Messages = messages
            };
        }

        // ---- 子 agent 管理 ----

        /// <summary>创建并启动子 agent。</summary>
        public IAgentSession CreateSubAgent(string instruction)
        {
            var pool = ctx.ToolProfiles.GetActiveTools("sub-agent");
            var session = new TaskSession(ctx, toolWhitelist: pool);

            session.OnCompleted = s =>
            {
                var result = s.LastResult ?? "(无结果)";
                var isFailed = result.StartsWith("异常终止") || result == "达到最大轮次限制"
                    || result == "API 调用连续失败，子 agent 中止";
                _messaging?.SubmitFireAndForget(LoopId.System,
                    isFailed ? "SubAgentFailed" : "SubAgentComplete",
                    $"子 agent {(isFailed ? "失败" : "完成")}: {result.Truncate(100)}");
            };

            lock (subAgentLock)
            {
                subAgents[session.SessionId] = session;
            }
            session.Start(instruction);
            Signal.Event(LogGroup.Engine, "子agent创建", new { sessionId = session.SessionId, instructionPreview = instruction.Length > 100 ? instruction[..100] : instruction });
            return session;
        }

        /// <summary>创建并启动子 agent（关联委托）。完成后自动更新委托状态。</summary>
        public IAgentSession CreateSubAgentForDelegation(string instruction, string? delegationId)
        {
            var pool = ctx.ToolProfiles.GetActiveTools("sub-agent");
            var session = new TaskSession(ctx, delegationId, toolWhitelist: pool);

            // 设置完成回调（迁移至新委托系统）
            session.OnCompleted = s =>
            {
                var result = s.LastResult ?? "(无结果)";
                var isFailed = result.StartsWith("异常终止") || result == "达到最大轮次限制"
                    || result == "API 调用连续失败，子 agent 中止";

                if (!string.IsNullOrEmpty(s.DelegationId))
                {
                    var crossReq = ctx.CrossRequests.Get(s.DelegationId!);
                    if (crossReq != null)
                    {
                        // 新路径：通过 CrossRequestRegistry 完成
                        if (isFailed)
                        {
                            var channelMsg = $"[系统] 委托「{crossReq.Title.Truncate(30)}」执行遇到问题: {result.Truncate(60)}。系统正在评估是否重试。";
                            if (LoopId.IsChannel(crossReq.InitiatorId, out var chId))
                                _messaging?.SubmitFireAndForget(LoopId.ForChannel(chId),
                                    "委托执行失败", channelMsg);
                            _messaging?.SubmitFireAndForget(LoopId.System,
                                "SubAgentFailed",
                                $"子 agent 执行失败: {result.Truncate(100)}");
                        }
                        else
                        {
                            ctx.CrossRequests.Respond(s.DelegationId!, LoopId.System,
                                CrossRequestResponseType.Complete, result);
                        }
                    }
                    // else: 旧委托路径，由 TaskSession.DefaultNotifyCompletion 兜底
                }
                else
                {
                    _messaging?.SubmitFireAndForget(LoopId.System,
                        isFailed ? "SubAgentFailed" : "SubAgentComplete",
                        $"子 agent {(isFailed ? "失败" : "完成")}: {result.Truncate(100)}");
                }
            };

            lock (subAgentLock)
            {
                subAgents[session.SessionId] = session;
            }
            session.Start(instruction);
            Signal.Event(LogGroup.Engine, "子agent创建(委托)", new { sessionId = session.SessionId, delegationId, instructionPreview = instruction.Length > 100 ? instruction[..100] : instruction });
            return session;
        }

        /// <summary>获取子 agent。</summary>
        public IAgentSession? GetSubAgent(string sessionId)
        {
            lock (subAgentLock)
            {
                subAgents.TryGetValue(sessionId, out var session);
                return session;
            }
        }

        /// <summary>获取所有活跃子 agent。</summary>
        public List<IAgentSession> GetActiveSubAgents()
        {
            lock (subAgentLock)
            {
                // 清理已死亡的
                var dead = subAgents.Where(kv => !kv.Value.IsAlive).Select(kv => kv.Key).ToList();
                foreach (var key in dead) subAgents.Remove(key);

                return subAgents.Values.ToList();
            }
        }

        // ---- WebUI 状态暴露 ----

        // ═══════ 跨循环请求 ═══════

        /// <summary>DelegationBus 回调：接收到定向委托或广播。</summary>
        private void OnCrossRequestReceived(CrossRequest request)
        {
            _pendingCrossRequests.Enqueue(request);
            gate.Signal();
        }

        /// <summary>Drain 跨循环请求队列。</summary>
        internal List<CrossRequest> DrainCrossRequests()
        {
            var list = new List<CrossRequest>();
            while (_pendingCrossRequests.TryDequeue(out var req))
                list.Add(req);
            return list;
        }

        internal WebUI.Services.SystemEngineSnapshot GetSnapshot()
        {
            var agentInfos = new System.Collections.Generic.List<WebUI.Services.SubAgentInfo>();
            lock (subAgentLock)
            {
                foreach (var kv in subAgents)
                {
                    var session = kv.Value as TaskSession;
                    agentInfos.Add(new WebUI.Services.SubAgentInfo
                    {
                        SessionId = kv.Value.SessionId,
                        Type = kv.Value.Type.ToString(),
                        IsAlive = kv.Value.IsAlive,
                        CurrentInstruction = session?.CurrentInstruction,
                        LastResult = session?.LastResult,
                        DelegationId = session?.DelegationId
                    });
                }
            }

            var (summary, rounds) = persistence.LoadContext();

            return new WebUI.Services.SystemEngineSnapshot
            {
                IsAlive = IsAlive,
                TaskQueueDepth = 0,
                ActiveSubAgentCount = agentInfos.Count(a => a.IsAlive),
                HasPendingSleepRequest = pendingSleepRequest != null,
                SleepRequestId = pendingSleepRequest?.RequestId,
                SleepScore = pendingSleepRequest?.Score,
                SleepRequestTime = pendingSleepRequest?.RequestTime,
                LastHealthCheck = lastSleepCheck,
                SubAgents = agentInfos,
                PinboardEntries = new(ReadPinboardEntries()),
                ThinkingNotes = new(ReadSystemThinkingNotes()),
                ContextRoundCount = rounds.Count,
                HasContextSummary = summary != null,
                ConsecutiveFailures = consecutiveFailures,
                TotalErrorCount = totalErrorCount,
                LastErrorTime = lastErrorTime,
                LastErrorMessage = lastErrorMessage
            };
        }

        // ---- Phase 8: 睡觉评估和许可管理 ----

        /// <summary>定期健康检查（每 5 分钟）。</summary>
        private async Task PerformHealthCheckAsync()
        {
            // 如果已有待处理的睡觉请求，检查超时
            if (pendingSleepRequest != null)
            {
                var elapsed = (DateTime.Now - pendingSleepRequest.RequestTime).TotalMinutes;
                if (elapsed > 10 && pendingSleepRequest.Status == SleepRequestStatus.Pending)
                {
                    Signal.Event(LogGroup.Engine, "睡觉请求超时自动批准",
                        new { requestId = pendingSleepRequest.RequestId, elapsedMinutes = elapsed });
                    pendingSleepRequest.Status = SleepRequestStatus.Approved;
                    await StartDreamEngineAsync();
                    pendingSleepRequest = null;
                }
                return;
            }

            // 评估睡觉需求
            var score = await EvaluateSleepNeedAsync();
            Signal.Event(LogGroup.Engine, "睡觉评估", new { score, threshold = 60f, triggered = score >= 60f });
            if (score >= 60f)
            {
                await RequestSleepPermissionAsync(score);
            }
        }

        /// <summary>评估睡觉需求（4 因子评分）。</summary>
        private async Task<float> EvaluateSleepNeedAsync()
        {
            float score = 0f;

            // 因子1：空闲时长（最高 40 分）
            if (ctx.IsIdle)
            {
                var idleMinutes = ctx.IdleDuration.TotalMinutes;
                score += Math.Min(40f, (float)(idleMinutes / 30f * 40f));
            }

            // 因子2：记忆积压（最高 30 分）
            // 简化：使用最近 100 条记忆中未做梦的数量估算
            var recentMemories = await ctx.Memories.GetRecentAsync(100);
            var undreamedCount = recentMemories.Count(m => m.LastDreamTime == null);
            score += Math.Min(30f, undreamedCount / 50f * 30f);

            // 因子3：复盘标记（最高 20 分）
            var unprocessedHints = await ctx.ReviewHints.GetUnprocessedAsync();
            var hintCount = unprocessedHints.Count;
            score += Math.Min(20f, hintCount / 10f * 20f);

            // 因子4：上次睡觉时间（最高 10 分）
            var lastSleep = await GetLastSleepTimeAsync();
            if (lastSleep.HasValue)
            {
                var hoursSince = (DateTime.Now - lastSleep.Value).TotalHours;
                score += Math.Min(10f, (float)(hoursSince / 24f * 10f));
            }

            return score;
        }

        /// <summary>获取上次睡觉时间（从 DreamStats 读取）。</summary>
        private async Task<DateTime?> GetLastSleepTimeAsync()
        {
            var statsPath = Path.Combine(PathConfig.StoragePath, "Dream", "DreamStats.json");
            if (!File.Exists(statsPath)) return null;

            try
            {
                var json = await File.ReadAllTextAsync(statsPath);
                // 简化：直接解析 JSON，不依赖 Dream 命名空间
                dynamic? stats = Newtonsoft.Json.JsonConvert.DeserializeObject(json);
                if (stats == null) return null;

                // 从 DailyRecords 找最近一次 Processed > 0 的日期
                var records = stats.DailyRecords as Newtonsoft.Json.Linq.JArray;
                if (records == null) return null;

                foreach (var record in records.OrderByDescending(r => (string?)r["Date"]))
                {
                    var processed = (int?)record["Processed"];
                    var dateStr = (string?)record["Date"];
                    if (processed > 0 && dateStr != null)
                    {
                        return DateTime.Parse(dateStr);
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "读取DreamStats失败", new { error = ex.Message });
                return null;
            }
        }

        /// <summary>请求睡觉许可（发送到管理员频道）。</summary>
        private async Task RequestSleepPermissionAsync(float score)
        {
            // 查找管理员频道
            var adminChannels = await FindAdminChannelsAsync();
            if (adminChannels.Count == 0)
            {
                // 没有管理员，自动批准（兜底）
                await StartDreamEngineAsync();
                return;
            }

            // 生成请求 ID
            var requestId = Guid.NewGuid().ToString("N").Substring(0, 8);

            // 构建请求消息
            var recentMemories = await ctx.Memories.GetRecentAsync(100);
            var undreamedCount = recentMemories.Count(m => m.LastDreamTime == null);
            var unprocessedHints = await ctx.ReviewHints.GetUnprocessedAsync();
            var hintCount = unprocessedHints.Count;

            var message = $"[系统内部·睡觉请求] 请转告管理员：我想睡觉整理记忆了（评分{score:F1}/100，" +
                          $"空闲{ctx.IdleDuration.TotalMinutes:F0}分钟，{undreamedCount}条待处理记忆）。" +
                          $"管理员可以用 /sleep approve {requestId} 批准，/sleep deny {requestId} 拒绝。";

            // 通知所有管理员频道
            foreach (var channelId in adminChannels)
            {
                _messaging?.SubmitFireAndForget(LoopId.ForChannel(channelId),
                    "系统通知", message);
            }

            // 记录请求状态
            pendingSleepRequest = new SleepRequest
            {
                RequestTime = DateTime.Now,
                Score = score,
                Status = SleepRequestStatus.Pending,
                RequestId = requestId
            };

        }

        /// <summary>查找管理员最近活跃的频道。</summary>
        private async Task<List<int>> FindAdminChannelsAsync()
        {
            var allUsers = await ctx.Session.GetAllUsersAsync();
            var admins = allUsers.Where(u => u.PermissionLevel == Database.PermissionLevel.Admin).ToList();

            if (admins.Count == 0) return new List<int>();

            // 简化：返回所有频道（管理员可能在任何频道）
            var allChannels = await ctx.Session.GetAllChannelsAsync();
            return allChannels.Select(c => c.Id).ToList();
        }

        /// <summary>启动 DreamEngine（通过 EventBus 发布信号）。</summary>
        private Task StartDreamEngineAsync()
        {
            ctx.EventBus.PublishSignal("force-sleep", "deepsleep");
            return Task.CompletedTask;
        }
    // ── 内联文件读取（原 PinboardModule / ThinkingNotesModule） ──

    private static Dictionary<string, string> ReadPinboardEntries()
    {
        var path = Path.Combine(PathConfig.StoragePath, "PluginData", "_system", "pinboard.json");
        if (!File.Exists(path)) return new();
        try
        {
            var json = File.ReadAllText(path);
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
        catch (Exception ex) { Signal.Warn(LogGroup.Engine, "读取Pinboard失败", new { error = ex.Message }); return new(); }
    }

    private static Dictionary<string, string> ReadSystemThinkingNotes()
    {
        var path = Path.Combine(PathConfig.StoragePath, "PluginData", "_system", "notebooks", "system.txt");
        if (!File.Exists(path)) return new();
        try
        {
            var content = File.ReadAllText(path);
            return new Dictionary<string, string> { ["system"] = content };
        }
        catch (Exception ex) { Signal.Warn(LogGroup.Engine, "读取ThinkingNotes失败", new { error = ex.Message }); return new(); }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        if (name.Length > 64) name = name[..64];
        return name;
    }
}

/// <summary>系统循环状态（供频道循环感知）。</summary>
    public enum SystemLoopState
    {
        Active,
        Compressing,
    }
}
