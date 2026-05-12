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
    /// 追加式上下文：固定前缀 + 持续增长的对话历史 + 每轮新增的状态/事件。
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

        // 追加式对话历史
        private readonly List<Message> conversationHistory = new();
        private string toolDescriptions = "";
        private int estimatedTokens = 0;
        private const int MaxContextTokens = 80000;
        private const int SoftThresholdPercent = 60;
        private const int HardThresholdPercent = 85;

        // 原生工具调用
        private readonly bool useNativeTools;

        // 系统状态（供频道循环感知）
        public SystemLoopState CurrentState { get; private set; } = SystemLoopState.Active;

        // Agent 循环状态
        private const int MaxRoundsPerWake = 20;
        private List<ToolCall>? lastRoundCalls;
        private List<ToolResult>? lastRoundResults;
        private bool lastRoundNoAction;
        private int waitTimeoutMinutes = 5;

        // 子 agent 管理
        private readonly Dictionary<string, IAgentSession> subAgents = new();
        private readonly object subAgentLock = new();

        // 错误追踪
        private int consecutiveFailures = 0;
        private DateTime? lastErrorTime = null;
        private string? lastErrorMessage = null;
        private int totalErrorCount = 0;
        private const int MaxConsecutiveFailures = 5;
        private static readonly int[] BackoffSeconds = { 10, 30, 60, 120, 300 };

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
                thinkingNotesModule,         // 优先级 45
                pinboardModule,              // 优先级 55
                loopControlModule,           // 优先级 60
                compressionModule            // 优先级 100（不注入 prompt）
            };

            foreach (var m in modules) m.Attach(bus);

            // 启动时生成工具描述（固定前缀，不再每轮重建）
            var allowed = GetAuthorizedTools();
            if (useNativeTools)
            {
                // 原生模式：工具通过 API 发送，不注入文本描述
                toolDescriptions = "";
                // TODO: ToolFilter removed from AgentCore; will be replaced by ProfileManager
                // agentCore.ToolFilter = t => allowed.Contains(t.Name);
            }
            else
            {
                toolDescriptions = ToolRegistry.GenerateDescriptions(filter: t => allowed.Contains(t.Name));
            }

            // 恢复持久化的对话历史
            RestoreContext();

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

            foreach (var round in rounds)
            {
                conversationHistory.AddRange(round);
            }

            RecalculateTokens();

            if (conversationHistory.Count > 0)
                FrameworkLogger.Log("SystemEngine", $"已恢复 {conversationHistory.Count} 条历史消息, 估算 {estimatedTokens} tokens");
        }

        private void RecalculateTokens()
        {
            estimatedTokens = conversationHistory.Sum(m => (m.Content?.Length ?? 0)) / 3;
        }

        private (int tokens, int percent) GetContextUsage()
        {
            return (estimatedTokens, (int)(estimatedTokens * 100.0 / MaxContextTokens));
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
                    pendingEventsModule.SetPendingDelegations(ctx.Delegations.GetPendingForEvaluation());

                    // ═══ 内层：Agent 循环 ═══
                    Interlocked.Exchange(ref _busyFlag, 1);
                    lastRoundNoAction = false;
                    try
                    {
                        await RunAgentLoopAsync(ct);
                        consecutiveFailures = 0;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        consecutiveFailures++;
                        totalErrorCount++;
                        lastErrorTime = DateTime.Now;
                        lastErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
                        FrameworkLogger.LogError("SystemEngine", ex, $"Agent 循环异常 (连续第 {consecutiveFailures} 次)");

                        if (consecutiveFailures >= MaxConsecutiveFailures)
                        {
                            var backoff = BackoffSeconds[Math.Min(consecutiveFailures - 1, BackoffSeconds.Length - 1)];
                            FrameworkLogger.Log("SystemEngine", $"连续失败 {consecutiveFailures} 次，退避 {backoff}s");
                            await Task.Delay(TimeSpan.FromSeconds(backoff), ct);
                        }
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
                FrameworkLogger.LogError("SystemEngine", ex, "系统循环致命异常，将自动重启");
                // 致命异常兜底：标记死亡后由 SpawnCheck 重启
                totalErrorCount++;
                lastErrorTime = DateTime.Now;
                lastErrorMessage = $"[致命] {ex.GetType().Name}: {ex.Message}";
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
                // ① 检查是否需要压缩（硬阈值）
                var usagePercent = (int)(estimatedTokens * 100.0 / MaxContextTokens);
                if (usagePercent >= HardThresholdPercent)
                {
                    await CompressContextAsync();
                }

                // ② 构建当前轮的 user 消息（状态 + 事件 + 上轮工具结果）
                var currentTurnMsg = BuildCurrentTurnMsg();

                // ③ 组装完整消息列表（固定前缀 + 历史 + 当前轮）
                var messages = BuildFullMessages(currentTurnMsg);

                // ④ 调用模型
                FrameworkLogger.Log("SystemEngine", $"Agent 循环 round {round + 1}, 上下文 {estimatedTokens}/{MaxContextTokens} tokens ({usagePercent}%)");
                var output = await agentCore.InvokeWithHistoryAsync(messages);

                // ⑤ 处理响应
                if (output.IsText || output.ToolCalls == null || output.ToolCalls.Count == 0)
                {
                    var text = output.Thinking ?? output.Text ?? "";
                    FrameworkLogger.Log("SystemEngine", $"模型无工具调用: {text.Truncate(100)}");
                    lastRoundNoAction = true;

                    // 追加到历史
                    AppendToHistory(currentTurnMsg, new Message { Role = "assistant", Content = text });

                    if (round > 0 && lastRoundCalls == null)
                    {
                        FrameworkLogger.Log("SystemEngine", "连续无操作，自动进入等待");
                        break;
                    }
                    lastRoundCalls = null;
                    lastRoundResults = null;
                    continue;
                }

                // ⑥ 执行工具
                lastRoundNoAction = false;
                var toolCalls = output.ToolCalls;
                var executor = new ToolExecutor(authorizedTools: GetAuthorizedTools());
                var results = await executor.ExecuteAsync(toolCalls);

                lastRoundCalls = toolCalls;
                lastRoundResults = results;

                // ⑦ 追加到历史（assistant 响应，含思考文本）
                var assistantMsg = BuildAssistantMsg(toolCalls, output.Thinking);
                AppendToHistory(currentTurnMsg, assistantMsg);

                // ⑧ 自动感知：检查是否还有待处理事件
                bool hasMoreWork = ctx.TaskBridge.HasPendingTasks()
                    || ctx.TaskBridge.HasPendingNotifications()
                    || ctx.Delegations.GetPendingForEvaluation().Count > 0;

                // 纯通知类工具（不产生后续工作）→ 本轮结束
                var terminalTools = new HashSet<string> { "notify_channel", "check_notifications" };
                bool isTerminalOnly = toolCalls.All(c => terminalTools.Contains(c.Tool));

                if (isTerminalOnly && !hasMoreWork)
                {
                    FrameworkLogger.Log("SystemEngine", "处理完毕，无待处理事件，休眠");
                    break;
                }

                if (!hasMoreWork && toolCalls.Any(c => c.Tool == "wait"))
                {
                    var reason = toolCalls.First(c => c.Tool == "wait").Inputs.FirstOrDefault() ?? "";
                    FrameworkLogger.Log("SystemEngine", $"显式休眠: {reason}");
                    break;
                }

                // ⑨ 更新 PendingEventsModule（后续轮次无新事件）
                pendingEventsModule.SetPendingEvents(
                    new List<SystemTask>(), new List<Notification>(),
                    new List<ScheduledTaskFiredEvent>(), false);
            }

            SaveModuleState();
        }

        // ---- 追加式历史管理 ----

        private void AppendToHistory(Message userMsg, Message assistantMsg)
        {
            conversationHistory.Add(userMsg);
            conversationHistory.Add(assistantMsg);

            var addedTokens = ((userMsg.Content?.Length ?? 0) + (assistantMsg.Content?.Length ?? 0)) / 3;
            estimatedTokens += addedTokens;

            // 持久化
            persistence.AppendRound(
                new List<Message> { userMsg },
                new List<Message> { assistantMsg });

            bus.Publish(new RoundCompletedEvent { Messages = new List<Message> { userMsg, assistantMsg } });
        }

        private async Task CompressContextAsync()
        {
            CurrentState = SystemLoopState.Compressing;
            ctx.TaskBridge.SystemState = SystemLoopState.Compressing;
            FrameworkLogger.Log("SystemEngine", $"触发上下文压缩: {estimatedTokens} tokens ({estimatedTokens * 100 / MaxContextTokens}%)");

            try
            {
                await compressionModule.CompressAsync(conversationHistory);

                // 压缩后更新历史
                var kept = compressionModule.GetKeptMessages();
                conversationHistory.Clear();
                conversationHistory.AddRange(kept);
                RecalculateTokens();

                // 持久化压缩结果
                persistence.SaveSummaryAndClearContext(compressionModule.GetSummary() ?? "");
                foreach (var msg in conversationHistory)
                {
                    persistence.AppendRound(
                        msg.Role == "user" ? new List<Message> { msg } : new List<Message>(),
                        msg.Role == "assistant" ? new List<Message> { msg } : new List<Message>());
                }

                FrameworkLogger.Log("SystemEngine", $"压缩完成: → {estimatedTokens} tokens");
            }
            finally
            {
                CurrentState = SystemLoopState.Active;
                ctx.TaskBridge.SystemState = SystemLoopState.Active;
            }
        }

        // ---- Prompt 构建 ----

        /// <summary>
        /// 组装完整消息列表：固定前缀 + 摘要 + 历史 + 当前轮。
        /// 前缀（工具描述）不变，历史只增不减（除压缩），缓存友好。
        /// </summary>
        private List<Message> BuildFullMessages(Message currentTurnMsg)
        {
            var messages = new List<Message>();

            // [固定前缀] 工具描述（启动时生成，不变）
            if (!string.IsNullOrEmpty(toolDescriptions))
                messages.Add(new Message { Role = "user", Content = toolDescriptions });

            // [摘要] 压缩后的旧轮次摘要
            var summary = compressionModule.GetSummary();
            if (!string.IsNullOrEmpty(summary))
                messages.Add(new Message { Role = "user", Content = $"[上下文摘要]\n{summary}" });

            // [历史] 已发生的 user/assistant 对（不再碰）
            messages.AddRange(conversationHistory);

            // [当前轮] 状态 + 事件 + 工具结果
            messages.Add(currentTurnMsg);

            return messages;
        }

        /// <summary>构建当前轮的 user 消息：仪表盘 + 模块注入 + 工具结果。</summary>
        private Message BuildCurrentTurnMsg()
        {
            var sb = new StringBuilder();

            // 模块注入（状态仪表盘、待处理事件、思考笔记等）
            var sections = modules
                .OrderBy(m => m.PromptPriority)
                .Select(m => m.BuildPromptSection(EngineMode.Working))
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            if (sections.Any())
                sb.AppendLine(string.Join("\n\n", sections));

            // 上一轮工具结果
            if (lastRoundResults != null && lastRoundCalls != null && lastRoundResults.Count > 0)
            {
                sb.AppendLine("\n[上一轮工具执行结果]");
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
            }

            var content = sb.ToString();

            // 原生模式：工具结果以 tool_result ContentParts 回传
            if (useNativeTools && lastRoundResults != null && lastRoundCalls != null && lastRoundResults.Count > 0)
            {
                var parts = new List<ContentPart> { ContentPart.FromText(content) };
                for (int i = 0; i < lastRoundCalls.Count && i < lastRoundResults.Count; i++)
                {
                    if (lastRoundCalls[i].ToolUseId != null)
                    {
                        var data = lastRoundResults[i].IsSuccess
                            ? (lastRoundResults[i].Data ?? "成功")
                            : $"失败: {lastRoundResults[i].Error ?? lastRoundResults[i].Status}";
                        parts.Add(ContentPart.FromToolResult(
                            lastRoundCalls[i].ToolUseId!, data, !lastRoundResults[i].IsSuccess));
                    }
                    else
                    {
                        // 无 ToolUseId 时退化为文本说明
                        parts.Add(ContentPart.FromText(
                            $"\n[{lastRoundCalls[i].Tool}]: {(lastRoundResults[i].IsSuccess ? "成功" : "失败")}"));
                    }
                }
                return new Message { Role = "user", Content = content, ContentParts = parts };
            }

            return new Message { Role = "user", Content = content };
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
            return new HashSet<string>
            {
                "wait", "continue_loop",
                "create_sub_agent", "send_to_sub_agent", "stop_sub_agent",
                "check_notifications", "set_watch_rule", "channel_info",
                "engine_management", "adapter_action",
                "pinboard", "thinking_notes",
                "create_scheduled_task", "cancel_scheduled_task",
                "memory",
                "evaluate_delegation", "notify_channel"
            };
        }

        /// <summary>构建 assistant 消息。原生模式使用 tool_use ContentParts，否则使用文本格式。</summary>
        private Message BuildAssistantMsg(List<ToolCall>? calls, string? thinking = null)
        {
            if (!useNativeTools || calls == null || calls.Count == 0)
            {
                // 文本格式（兼容旧路径）
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(thinking))
                    parts.Add(thinking);
                if (calls == null || calls.Count == 0)
                    parts.Add("(无操作)");
                else
                    parts.AddRange(calls.Select(c =>
                        $"{c.Tool}({string.Join(", ", c.Inputs).Truncate(100)})"));
                return new Message { Role = "assistant", Content = string.Join("\n", parts) };
            }

            // 原生格式：ContentParts 含 thinking text + tool_use 块
            var contentParts = new List<ContentPart>();
            if (!string.IsNullOrEmpty(thinking))
                contentParts.Add(ContentPart.FromText(thinking));

            foreach (var call in calls)
            {
                if (call.ToolUseId != null)
                {
                    var inputJson = call.Inputs.Count > 0
                        ? Newtonsoft.Json.JsonConvert.SerializeObject(call.Inputs)
                        : "[]";
                    contentParts.Add(ContentPart.FromToolUse(
                        call.ToolUseId, call.Tool, inputJson));
                }
                else
                {
                    // 无 ToolUseId 时退化为文本
                    contentParts.Add(ContentPart.FromText(
                        $"\n{toolCallText(call)}"));
                }
            }

            return new Message
            {
                Role = "assistant",
                Content = thinking ?? "[tool calls]",
                ContentParts = contentParts
            };
        }

        private static string toolCallText(ToolCall c)
            => $"{c.Tool}({string.Join(", ", c.Inputs).Truncate(100)})";

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

        /// <summary>外部唤醒闸门（委托提交时调用）。</summary>
        public void SignalGate() => gate.Signal();

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

        /// <summary>创建并启动子 agent（关联委托）。完成后自动更新委托状态。</summary>
        public IAgentSession CreateSubAgentForDelegation(string instruction, string? delegationId)
        {
            var session = new TaskSession(ctx, delegationId);
            lock (subAgentLock)
            {
                subAgents[session.SessionId] = session;
            }
            session.Start(instruction);
            FrameworkLogger.Log("SystemEngine", $"子 agent 已创建（委托 {delegationId}）: {session.SessionId}");
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

    /// <summary>系统循环状态（供频道循环感知）。</summary>
    public enum SystemLoopState
    {
        Active,
        Compressing,
    }
}
