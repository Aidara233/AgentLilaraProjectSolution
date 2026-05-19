using System;
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
        private readonly ThinkingNotesModule thinkingNotesModule = new("system");
        private readonly PinboardModule pinboardModule = new();
        private readonly LoopControlModule loopControlModule = new();
        private readonly PendingEventsModule pendingEventsModule = new();
        private readonly SystemStatusModule systemStatusModule;
        private readonly ContextPersistence persistence;
        private readonly ContextCompressionModule compressionModule;
        private List<EngineModule> modules = null!;

        // Component 系统
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

        public SystemEngine(ISystemContext ctx)
        {
            this.ctx = ctx;
            this.agentCore = new AgentCore("SystemCore", usePersona: false);
            agentCore.CallerTag = "System";
            useNativeTools = agentCore.UseNativeTools;

            // 初始化模块
            systemStatusModule = new SystemStatusModule(ctx, () => GetActiveSubAgents(), () => GetContextUsage());

            var systemLoopPath = Path.Combine(PathConfig.StoragePath, "SystemLoop");
            persistence = new ContextPersistence(systemLoopPath);
            compressionModule = new ContextCompressionModule(persistence);

            modules = new List<EngineModule>
            {
                new ToolStatusModule(),      // 优先级 30
                systemStatusModule,          // 优先级 35
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

            // 注册 TaskBridge 回调：任务提交时唤醒闸门 + 强制唤醒 DreamEngine
            ctx.TaskBridge.OnTaskSubmitted = () =>
            {
                gate.Signal();
                if (ctx.CurrentSleepState != SleepState.None)
                {
                    ctx.EventBus.PublishSignal("force-wake", "task-submitted");
                }
            };
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
            var estTokens = agent?.History.Sum(m => (m.Content?.Length ?? 0)) / 3 ?? 0;
            return (estTokens, (int)(estTokens * 100.0 / MaxContextTokens));
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
                "system", "system", ctx.ComponentEventBus, ctx.ComponentServices,
                () => gate.Signal());
            await componentHost.InitAsync();


            try
            {
                await gate.RunAsync(ct);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // 致命异常兜底：标记死亡后由 SpawnCheck 重启
                totalErrorCount++;
                lastErrorTime = DateTime.Now;
                lastErrorMessage = $"[致命] {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
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
            // 定期自检（每 5 分钟）
            if ((DateTime.Now - lastSleepCheck).TotalMinutes >= 5)
            {
                await PerformHealthCheckAsync();
                lastSleepCheck = DateTime.Now;
            }

            // ═══ 收集所有待处理事件 ═══
            var tasks = DrainTasks();
            var notifications = DrainNotifications();
            var scheduledEvents = DrainScheduledEvents();
            var pendingDelegations = ctx.Delegations.GetPendingForEvaluation();
            var retryDelegations = ctx.Delegations.GetRetryPending();

            using var iterSignal = Signal.Begin(LogGroup.Engine, "system:main", "系统循环轮次", new
            {
                tasks = tasks.Count,
                notifications = notifications.Count,
                scheduled = scheduledEvents.Count,
                delegations = pendingDelegations.Count,
                retryDelegations = retryDelegations.Count
            });

            Signal.Event(LogGroup.Engine, "任务队列检查", new
            {
                pending = tasks.Count,
                notifications = notifications.Count,
                scheduled = scheduledEvents.Count
            });

            if (pendingDelegations.Count > 0 || retryDelegations.Count > 0)
            {
                Signal.Event(LogGroup.Engine, "委托待评估", new
                {
                    pending = pendingDelegations.Count,
                    retry = retryDelegations.Count
                });
            }

            // 填充 PendingEventsModule
            pendingEventsModule.SetPendingEvents(tasks, notifications, scheduledEvents, lastRoundNoAction);
            pendingEventsModule.SetPendingDelegations(pendingDelegations);
            pendingEventsModule.SetRetryPendingDelegations(retryDelegations);

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
                consecutiveFailures = 0;
                lastRoundNoAction = agent.StopReason == AgentStopReason.Completed;

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

                if (consecutiveFailures >= agentConfig.MaxRounds)
                {
                    var backoff = agentConfig.BackoffSeconds[
                        Math.Min(consecutiveFailures - 1, agentConfig.BackoffSeconds.Length - 1)];
                    await Task.Delay(TimeSpan.FromSeconds(backoff), ct);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _busyFlag, 0);
                await componentHost!.OnPauseAsync();
            }
        }

        /// <summary>构建当前轮的 user 消息：仪表盘 + 模块注入 + 组件注入。</summary>
        private Message BuildCurrentTurnMsg()
        {
            var sb = new StringBuilder();

            // 模块注入（状态仪表盘、待处理事件等）
            var sections = modules
                .OrderBy(m => m.PromptPriority)
                .Select(m => m.BuildPromptSection(EngineMode.Working))
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            if (sections.Any())
                sb.AppendLine(string.Join("\n\n", sections));

            // Component 系统 prompt 注入
            if (componentHost != null)
            {
                var groups = ToolListFormatter.CollectGroups(componentHost, ctx.GlobalComponentHost);
                var toolOverview = ToolListFormatter.BuildToolOverviewSection(groups);
                if (toolOverview != null)
                    sb.AppendLine("\n" + toolOverview);

                var componentSections = componentHost.BuildPromptSections();
                foreach (var section in componentSections)
                    sb.AppendLine("\n" + section);

                var globalSections = ctx.GlobalComponentHost?.BuildPromptSections(
                    new LoopInfo("system", "system")) ?? new();
                foreach (var section in globalSections)
                    sb.AppendLine("\n" + section);
            }

            return new Message { Role = "user", Content = sb.ToString() };
        }

        private List<SystemTask> DrainTasks()
        {
            var tasks = new List<SystemTask>();
            while (ctx.TaskBridge.TaskReader.TryRead(out var task))
                tasks.Add(task);
            return tasks;
        }

        private List<Notification> DrainNotifications()
        {
            var notifications = new List<Notification>();
            while (ctx.TaskBridge.NotificationReader.TryRead(out var n))
                notifications.Add(n);
            return notifications;
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

        Task<List<Message>?> IAgentHost.BuildStartInjectAsync()
        {
            var msgs = new List<Message>();

            // 工具描述（固定前缀）
            if (!useNativeTools)
            {
                var allowed = GetAuthorizedTools();
                var toolDescriptions = ToolRegistry.GenerateDescriptions(filter: t => allowed.Contains(t.Name));
                if (!string.IsNullOrEmpty(toolDescriptions))
                    msgs.Add(new Message { Role = "user", Content = toolDescriptions });
            }

            // 上下文摘要
            var summary = compressionModule.GetSummary();
            if (!string.IsNullOrEmpty(summary))
                msgs.Add(new Message { Role = "user", Content = $"[上下文摘要]\n{summary}" });

            return Task.FromResult<List<Message>?>(msgs);
        }

        Task<List<Message>?> IAgentHost.BuildRoundInjectAsync()
        {
            var msgs = new List<Message>();
            var mainMsg = BuildCurrentTurnMsg();
            msgs.Add(mainMsg);

            // Compression tier hint
            if (compressionTierModule != null && agent != null)
            {
                var estTokens = agent.History.Sum(m => (m.Content?.Length ?? 0)) / 3;
                var compressText = compressionTierModule.GetInjectText(estTokens);
                if (!string.IsNullOrEmpty(compressText))
                {
                    msgs.Add(new Message { Role = "user", Content = compressText });
                    if (compressionTierModule.CurrentTier == CompressionTier.L1)
                        compressionTierModule.MarkL1Injected();
                }
            }
            return Task.FromResult<List<Message>?>(msgs);
        }

        private void SaveModuleState()
        {
            var state = new Dictionary<string, object>
            {
                ["pinboard"] = pinboardModule.Entries,
                ["timestamp"] = DateTime.Now
            };
            persistence.SaveState(state);
        }

        // ---- 事件处理 ----

        public void OnEvent(EngineEvent e)
        {
            // TimerEvent 不再唤醒闸门 — 由 gate 超时（waitTimeoutMinutes）控制周期

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

        // ---- 子 agent 管理 ----

        /// <summary>创建并启动子 agent。</summary>
        public IAgentSession CreateSubAgent(string instruction)
        {
            var pool = ctx.ToolProfiles.GetActiveTools("sub-agent");
            var session = new TaskSession(ctx, toolWhitelist: pool);
            lock (subAgentLock)
            {
                subAgents[session.SessionId] = session;
            }
            session.Start(instruction);
            return session;
        }

        /// <summary>创建并启动子 agent（关联委托）。完成后自动更新委托状态。</summary>
        public IAgentSession CreateSubAgentForDelegation(string instruction, string? delegationId)
        {
            var pool = ctx.ToolProfiles.GetActiveTools("sub-agent");
            var session = new TaskSession(ctx, delegationId, toolWhitelist: pool);
            lock (subAgentLock)
            {
                subAgents[session.SessionId] = session;
            }
            session.Start(instruction);
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

        internal WebUI.Services.SystemEngineSnapshot GetSnapshot()
        {
            var agentInfos = new System.Collections.Generic.List<WebUI.Services.SubAgentInfo>();
            lock (subAgentLock)
            {
                foreach (var kv in subAgents)
                {
                    agentInfos.Add(new WebUI.Services.SubAgentInfo
                    {
                        SessionId = kv.Value.SessionId,
                        Type = kv.Value.Type.ToString(),
                        IsAlive = kv.Value.IsAlive
                    });
                }
            }

            var (summary, rounds) = persistence.LoadContext();

            return new WebUI.Services.SystemEngineSnapshot
            {
                IsAlive = IsAlive,
                TaskQueueDepth = ctx.TaskBridge.PendingTaskCount,
                ActiveSubAgentCount = agentInfos.Count(a => a.IsAlive),
                HasPendingSleepRequest = pendingSleepRequest != null,
                SleepRequestId = pendingSleepRequest?.RequestId,
                SleepScore = pendingSleepRequest?.Score,
                SleepRequestTime = pendingSleepRequest?.RequestTime,
                LastHealthCheck = lastSleepCheck,
                SubAgents = agentInfos,
                PinboardEntries = new(pinboardModule.Entries),
                ThinkingNotes = new(thinkingNotesModule.Notes),
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
                    // 超时自动批准
                    pendingSleepRequest.Status = SleepRequestStatus.Approved;
                    await StartDreamEngineAsync();
                    pendingSleepRequest = null;
                }
                return;
            }

            // 评估睡觉需求
            var score = await EvaluateSleepNeedAsync();
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
            catch
            {
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
                ctx.NotifyChannel(channelId, message);
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
    }

    /// <summary>系统循环状态（供频道循环感知）。</summary>
    public enum SystemLoopState
    {
        Active,
        Compressing,
    }
}
