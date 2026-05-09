using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Engine.Modules;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 系统循环引擎。单例，长期运行，纯调度者。
    /// 闸门模型：任何人可升闸（唤醒），只有模型调 Wait 才落闸。
    /// </summary>
    internal class SystemEngine : ISubEngine
    {
        public string EngineType => "System";
        public bool IsAlive { get; private set; } = true;
        public bool IsInfrastructure => false;
        public bool IsBusy => Interlocked.Read(ref _busyFlag) == 1;
        private long _busyFlag = 0;

        private readonly ISystemContext ctx;
        private readonly AgentCore agentCore;
        private readonly LoopGate gate = new();
        private readonly LoopBus bus = new();
        private CancellationTokenSource? stopCts;

        // 模块
        private readonly ThinkingNotesModule thinkingNotesModule = new();
        private readonly PinboardModule pinboardModule = new();
        private readonly LoopControlModule loopControlModule = new();
        private readonly PendingEventsModule pendingEventsModule = new();
        private readonly SystemStatusModule systemStatusModule;
        private readonly ContextPersistence persistence;
        private readonly ContextCompressionModule compressionModule;
        private List<EngineModule> modules = null!;

        // Agent 循环状态
        private const int MaxRoundsPerWake = 20;
        private List<ToolCall>? lastRoundCalls;
        private List<ToolResult>? lastRoundResults;
        private bool lastRoundNoAction;
        private int waitTimeoutMinutes = 5;

        // 子 agent 管理
        private readonly Dictionary<string, IAgentSession> subAgents = new();
        private readonly object subAgentLock = new();

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
            this.agentCore = new AgentCore("SystemCore");

            // 初始化模块
            systemStatusModule = new SystemStatusModule(ctx, () => GetActiveSubAgents());

            var systemLoopPath = Path.Combine(PathConfig.StoragePath, "SystemLoop");
            persistence = new ContextPersistence(systemLoopPath);
            compressionModule = new ContextCompressionModule(persistence);

            modules = new List<EngineModule>
            {
                systemStatusModule,      // 优先级 35
                pendingEventsModule,     // 优先级 38
                thinkingNotesModule,     // 优先级 45
                pinboardModule,          // 优先级 55
                loopControlModule,       // 优先级 60
                compressionModule        // 优先级 100（不注入 prompt）
            };

            foreach (var m in modules) m.Attach(bus);
            compressionModule.LoadPersistedContext();

            // 注册 TaskBridge 回调：任务提交时唤醒闸门
            ctx.TaskBridge.OnTaskSubmitted = () => gate.Signal();
        }

        public async Task RunAsync()
        {
            stopCts = new CancellationTokenSource();
            var ct = stopCts.Token;

            FrameworkLogger.Log("SystemEngine", "系统循环就绪（闸门模型）");

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // ═══ 外层：闸门等待 ═══
                    await gate.WaitAsync(TimeSpan.FromMinutes(waitTimeoutMinutes), ct);

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

                    // 填充 PendingEventsModule
                    pendingEventsModule.SetPendingEvents(tasks, notifications, scheduledEvents, lastRoundNoAction);

                    // ═══ 内层：Agent 循环 ═══
                    Interlocked.Exchange(ref _busyFlag, 1);
                    lastRoundNoAction = false;
                    try
                    {
                        await RunAgentLoopAsync(ct);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _busyFlag, 0);
                        lastRoundCalls = null;
                        lastRoundResults = null;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                FrameworkLogger.Log("SystemEngine", "系统循环已停止");
            }
            catch (Exception ex)
            {
                FrameworkLogger.LogError("SystemEngine", ex, "系统循环异常");
            }
            finally
            {
                IsAlive = false;
                foreach (var m in modules) m.Reset();
            }
        }

        /// <summary>内层 Agent 循环：模型推理 → 工具执行 → 结果回馈 → 直到 Wait 或上限。</summary>
        private async Task RunAgentLoopAsync(CancellationToken ct)
        {
            for (int round = 0; round < MaxRoundsPerWake && !ct.IsCancellationRequested; round++)
            {
                // ① 构建 prompt
                var messages = BuildPromptMessages();

                // ② 调用模型
                FrameworkLogger.Log("SystemEngine", $"Agent 循环 round {round + 1}");
                var output = await agentCore.InvokeAsync(messages, EngineMode.Working);

                // ③ 处理响应
                if (output.IsText || output.ToolCalls == null || output.ToolCalls.Count == 0)
                {
                    // 模型返回纯文本或空工具列表 → 不落闸，标记无操作，下轮提示
                    var text = output.Text ?? "";
                    FrameworkLogger.Log("SystemEngine", $"模型无工具调用: {text.Truncate(100)}");
                    lastRoundNoAction = true;
                    PersistRound(messages, output);
                    // 不 break — 下一轮会带"你上一轮没有操作"提示再问一次
                    // 但如果连续两轮无操作，自动等待（防空转）
                    if (round > 0 && lastRoundCalls == null)
                    {
                        FrameworkLogger.Log("SystemEngine", "连续无操作，自动进入等待");
                        break;
                    }
                    lastRoundCalls = null;
                    lastRoundResults = null;
                    continue;
                }

                // ④ 执行工具
                lastRoundNoAction = false;
                var toolCalls = output.ToolCalls;
                var executor = new ToolExecutor(authorizedTools: GetAuthorizedTools());
                var results = await executor.ExecuteAsync(toolCalls);

                lastRoundCalls = toolCalls;
                lastRoundResults = results;

                // ⑤ 检查是否调用了 Wait（落闸信号）
                var waitCall = toolCalls.FirstOrDefault(c => c.Tool == "等待");
                if (waitCall != null)
                {
                    var waitTool = ToolRegistry.Get("等待") as WaitTool;
                    if (waitTool != null)
                    {
                        waitTimeoutMinutes = waitTool.TimeoutMinutes;
                        FrameworkLogger.Log("SystemEngine",
                            $"落闸: {waitTool.WaitReason}, 超时 {waitTimeoutMinutes}min");
                    }
                    PersistRound(messages, output);
                    break;
                }

                // ⑥ 持久化本轮
                PersistRound(messages, output);

                // ⑦ 更新 PendingEventsModule（后续轮次无新事件）
                pendingEventsModule.SetPendingEvents(
                    new List<SystemTask>(), new List<Notification>(),
                    new List<ScheduledTaskFiredEvent>(), false);
            }

            SaveModuleState();
        }

        // ---- 事件收集 ----

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

        // ---- Prompt 构建 ----

        private List<Message> BuildPromptMessages()
        {
            var messages = new List<Message>();

            // 压缩后的历史上下文
            messages.AddRange(compressionModule.GetContext());

            // 工具描述
            var toolDescs = ToolRegistry.GenerateDescriptions(authorizedTools: GetAuthorizedTools());
            if (!string.IsNullOrEmpty(toolDescs))
                messages.Add(new Message { Role = "user", Content = toolDescs });

            // 模块注入
            var sections = modules
                .OrderBy(m => m.PromptPriority)
                .Select(m => m.BuildPromptSection(EngineMode.Working))
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            if (sections.Any())
                messages.Add(new Message { Role = "user", Content = string.Join("\n\n", sections) });

            // 上一轮工具结果
            if (lastRoundResults != null && lastRoundCalls != null && lastRoundResults.Count > 0)
            {
                var sb = new StringBuilder("[上一轮工具执行结果]\n");
                for (int i = 0; i < lastRoundCalls.Count && i < lastRoundResults.Count; i++)
                {
                    var call = lastRoundCalls[i];
                    var result = lastRoundResults[i];
                    sb.Append($"[{call.Tool}");
                    if (call.Inputs.Count > 0)
                        sb.Append($"({string.Join(", ", call.Inputs).Truncate(80)})");
                    sb.Append("]: ");
                    if (result.IsSuccess)
                        sb.AppendLine(result.Data ?? "成功");
                    else
                        sb.AppendLine($"失败 - {result.Error ?? result.Status}");
                }
                messages.Add(new Message { Role = "user", Content = sb.ToString() });
            }

            return messages;
        }

        private HashSet<string> GetAuthorizedTools()
        {
            return new HashSet<string>
            {
                "等待", "继续",
                "创建子agent", "发送指令给子agent", "停止子agent",
                "发送消息到频道", "查看通知", "设置关注规则", "频道信息",
                "引擎管理", "适配器操作",
                "便签板", "思考笔记",
                "创建定时任务", "取消定时任务",
                "记忆读取", "记忆搜索"
            };
        }

        // ---- 持久化 ----

        private void PersistRound(List<Message> promptMessages, ModelOutput output)
        {
            var userMessages = promptMessages.Where(m => m.Role == "user").ToList();
            var assistantContent = output.Text ?? FormatToolCallsAsText(output.ToolCalls);
            var assistantMessage = new Message { Role = "assistant", Content = assistantContent };

            persistence.AppendRound(userMessages, new List<Message> { assistantMessage });

            var allMessages = new List<Message>(userMessages) { assistantMessage };
            bus.Publish(new RoundCompletedEvent { Messages = allMessages });
        }

        private static string FormatToolCallsAsText(List<ToolCall>? calls)
        {
            if (calls == null || calls.Count == 0) return "(无操作)";
            return string.Join("\n", calls.Select(c =>
                $"{c.Tool}({string.Join(", ", c.Inputs).Truncate(100)})"));
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
            if (e is TimerEvent)
            {
                gate.Signal();
            }

            if (e is SignalEvent signal)
            {
                switch (signal.SignalName)
                {
                    case "sleep-approve" when pendingSleepRequest != null:
                        if (pendingSleepRequest.RequestId == (string?)signal.Payload)
                        {
                            FrameworkLogger.Log("SystemEngine", $"睡觉请求 {signal.Payload} 已批准");
                            pendingSleepRequest.Status = SleepRequestStatus.Approved;
                            _ = StartDreamEngineAsync();
                            pendingSleepRequest = null;
                        }
                        break;
                    case "sleep-deny" when pendingSleepRequest != null:
                        if (pendingSleepRequest.RequestId == (string?)signal.Payload)
                        {
                            FrameworkLogger.Log("SystemEngine", $"睡觉请求 {signal.Payload} 已拒绝");
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
            FrameworkLogger.Log("SystemEngine", "收到停止请求");
            stopCts?.Cancel();
        }

        // ---- 子 agent 管理 ----

        /// <summary>创建并启动子 agent。</summary>
        public IAgentSession CreateSubAgent(string instruction)
        {
            var session = new TaskSession(ctx);
            lock (subAgentLock)
            {
                subAgents[session.SessionId] = session;
            }
            session.Start(instruction);
            FrameworkLogger.Log("SystemEngine", $"子 agent 已创建并启动: {session.SessionId}");
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
                HasContextSummary = summary != null
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
                    FrameworkLogger.Log("SystemEngine", "睡觉请求超时，自动批准");
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
                FrameworkLogger.Log("SystemEngine", "无管理员频道，自动批准睡觉");
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

            var message = $"[睡觉请求 {requestId}]\n" +
                          $"评分: {score:F1}/100\n" +
                          $"空闲时长: {ctx.IdleDuration.TotalMinutes:F0} 分钟\n" +
                          $"待处理记忆: {undreamedCount} 条\n" +
                          $"待复盘标记: {hintCount} 个\n\n" +
                          $"回复 /sleep approve {requestId} 批准\n" +
                          $"回复 /sleep deny {requestId} 拒绝";

            // 发送到所有管理员频道
            foreach (var channelId in adminChannels)
            {
                try
                {
                    var channel = await ctx.Session.GetChannelByIdAsync(channelId);
                    if (channel == null) continue;

                    var parts = channel.Name.Split(':', 2);
                    if (parts.Length != 2) continue;

                    await ctx.Adapters.SendMessageAsync(parts[0], new Adapter.OutgoingMessage
                    {
                        ChannelId = parts[1],
                        Content = message
                    });

                    await ctx.Session.SaveBotMessageAsync(channelId, message, null);
                }
                catch (Exception ex)
                {
                    FrameworkLogger.LogError("SystemEngine", ex, $"发送睡觉请求到频道 {channelId} 失败");
                }
            }

            // 记录请求状态
            pendingSleepRequest = new SleepRequest
            {
                RequestTime = DateTime.Now,
                Score = score,
                Status = SleepRequestStatus.Pending,
                RequestId = requestId
            };

            FrameworkLogger.Log("SystemEngine", $"睡觉请求已发送: {requestId}, 评分 {score:F1}");
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
            FrameworkLogger.Log("SystemEngine", "已触发 DreamEngine 启动");
            return Task.CompletedTask;
        }
    }
}
