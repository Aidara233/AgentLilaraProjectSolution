using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Engine.Modules;
using AgentCoreProcessor.Models;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 系统循环引擎。单例，长期运行，纯调度者。
    /// Phase 2: 完整 Agent 循环 + 上下文持久化 + 压缩。
    /// </summary>
    internal class SystemEngine : ISubEngine
    {
        public string EngineType => "System";
        public bool IsAlive { get; private set; } = true;
        public bool IsInfrastructure => false;
        public bool IsBusy => Interlocked.Read(ref _busyFlag) == 1;
        private long _busyFlag = 0;

        private readonly ISystemContext ctx;
        private readonly AgentCore agentCore = new();
        private readonly LoopGate gate = new();
        private readonly LoopBus bus = new();
        private CancellationTokenSource? stopCts;

        // 模块
        private readonly ThinkingNotesModule thinkingNotesModule = new();
        private readonly PinboardModule pinboardModule = new();
        private readonly LoopControlModule loopControlModule = new();
        private readonly TaskQueueModule taskQueueModule;
        private readonly SystemStatusModule systemStatusModule;
        private readonly ContextPersistence persistence;
        private readonly ContextCompressionModule compressionModule;
        private List<EngineModule> modules = null!;

        // 子 agent 管理
        private readonly Dictionary<string, IAgentSession> subAgents = new();
        private readonly object subAgentLock = new();

        // Phase 8: 睡觉评估和许可管理
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

        public SystemEngine(ISystemContext ctx)
        {
            this.ctx = ctx;

            // 初始化模块
            taskQueueModule = new TaskQueueModule(ctx);
            systemStatusModule = new SystemStatusModule(ctx, () => GetActiveSubAgents());

            var systemLoopPath = System.IO.Path.Combine(PathConfig.StoragePath, "SystemLoop");
            persistence = new ContextPersistence(systemLoopPath);
            compressionModule = new ContextCompressionModule(persistence);

            modules = new List<EngineModule>
            {
                systemStatusModule,      // 优先级 35
                taskQueueModule,         // 优先级 40
                thinkingNotesModule,     // 优先级 45
                pinboardModule,          // 优先级 55
                loopControlModule,       // 优先级 60
                compressionModule        // 优先级 100（不注入 prompt）
            };

            foreach (var m in modules) m.Attach(bus);

            // 加载持久化的上下文
            compressionModule.LoadPersistedContext();
        }

        public async Task RunAsync()
        {
            stopCts = new CancellationTokenSource();
            var ct = stopCts.Token;

            FrameworkLogger.Log("SystemEngine", "系统循环就绪");

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // ① 等待任务到达或定时器唤醒
                    await gate.WaitAsync(TimeSpan.FromMinutes(5), ct);

                    // ② Phase 8: 定期自检（每 5 分钟）
                    if ((DateTime.Now - lastSleepCheck).TotalMinutes >= 5)
                    {
                        await PerformHealthCheckAsync();
                        lastSleepCheck = DateTime.Now;
                    }

                    // ③ 读取任务
                    if (!ctx.TaskBridge.TaskReader.TryRead(out var task))
                    {
                        // 定时器唤醒，无任务
                        continue;
                    }

                    FrameworkLogger.Log("SystemEngine", $"处理任务: {task.TaskId} - {task.Description}");
                    Interlocked.Exchange(ref _busyFlag, 1);
                    try
                    {
                        // ④ 构建 prompt
                        var messages = BuildPromptMessages(task);

                        // ⑤ 调用模型
                        var output = await agentCore.InvokeAsync(messages, EngineMode.Working);

                        // ⑥ 处理响应（Phase 2: 暂时只记录，Phase 3+ 会处理工具调用）
                        var result = ProcessResponse(output, task);

                        // ⑦ 完成任务
                        ctx.TaskBridge.CompleteTask(task.TaskId, result);

                        // ⑦ 持久化
                        var userMessages = messages.Where(m => m.Role == "user").ToList();
                        var assistantMessage = new Message
                        {
                            Role = "assistant",
                            Content = output.Text ?? string.Join("\n", output.ToolCalls?.Select(c => $"{c.Tool}({string.Join(", ", c.Inputs)})") ?? new List<string>())
                        };
                        persistence.AppendRound(userMessages, new List<Message> { assistantMessage });

                        // ⑧ 发布 RoundCompletedEvent（触发压缩检查）
                        var allMessages = new List<Message>();
                        allMessages.AddRange(userMessages);
                        allMessages.Add(assistantMessage);
                        bus.Publish(new RoundCompletedEvent { Messages = allMessages });

                        // ⑨ 保存模块状态
                        SaveModuleState();
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _busyFlag, 0);
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

        private List<Message> BuildPromptMessages(SystemTask task)
        {
            var messages = new List<Message>();

            // 添加压缩后的上下文
            messages.AddRange(compressionModule.GetContext());

            // 添加模块注入的 prompt sections
            var sections = modules
                .Select(m => m.BuildPromptSection(EngineMode.Working))
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            if (sections.Any())
            {
                var combined = string.Join("\n\n", sections);
                messages.Add(new Message { Role = "user", Content = combined });
            }

            // 添加当前任务
            var taskPrompt = $"[新任务]\n" +
                             $"任务 ID: {task.TaskId}\n" +
                             $"来源频道: {task.SourceChannelId}\n" +
                             $"请求者: Person#{task.RequestingPersonId}\n" +
                             $"优先级: {task.Priority}\n" +
                             $"描述: {task.Description}\n";

            if (!string.IsNullOrEmpty(task.ContextSummary))
            {
                taskPrompt += $"上下文摘要: {task.ContextSummary}\n";
            }

            messages.Add(new Message { Role = "user", Content = taskPrompt });

            return messages;
        }

        private TaskResult ProcessResponse(ModelOutput output, SystemTask task)
        {
            // Phase 2: 暂时只记录响应，不处理工具调用
            // Phase 3+ 会添加工具处理逻辑

            var responseText = output.Text ?? string.Join("\n", output.ToolCalls?.Select(c => $"{c.Tool}({string.Join(", ", c.Inputs)})") ?? new List<string>());

            FrameworkLogger.Log("SystemEngine", $"任务 {task.TaskId} 响应: {responseText.Substring(0, Math.Min(100, responseText.Length))}...");

            return new TaskResult
            {
                TaskId = task.TaskId,
                Success = true,
                Result = $"[Phase 2] 任务已处理。响应：{responseText}"
            };
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

        public void OnEvent(EngineEvent e)
        {
            // 定时器事件 → 唤醒闸门
            if (e is TimerEvent)
            {
                gate.Signal();
            }

            // Phase 8: 睡觉许可信号
            if (e is SignalEvent signal)
            {
                if (signal.SignalName == "sleep-approve" && pendingSleepRequest != null)
                {
                    if (pendingSleepRequest.RequestId == (string?)signal.Payload)
                    {
                        FrameworkLogger.Log("SystemEngine", $"睡觉请求 {signal.Payload} 已批准");
                        pendingSleepRequest.Status = SleepRequestStatus.Approved;
                        _ = StartDreamEngineAsync();
                        pendingSleepRequest = null;
                    }
                }
                else if (signal.SignalName == "sleep-deny" && pendingSleepRequest != null)
                {
                    if (pendingSleepRequest.RequestId == (string?)signal.Payload)
                    {
                        FrameworkLogger.Log("SystemEngine", $"睡觉请求 {signal.Payload} 已拒绝");
                        pendingSleepRequest = null;
                    }
                }
            }

            // 任务到达事件（通过 TaskBridge 的 TaskReader 自动触发）
            // 这里不需要处理，RunAsync 中的 ReadAsync 会自动唤醒
        }

        public void RequestStop()
        {
            FrameworkLogger.Log("SystemEngine", "收到停止请求");
            stopCts?.Cancel();
        }

        // ---- 子 agent 管理 ----

        /// <summary>创建子 agent（TaskSession）。</summary>
        public IAgentSession CreateSubAgent()
        {
            var session = new TaskSession(ctx);
            lock (subAgentLock)
            {
                subAgents[session.SessionId] = session;
            }
            FrameworkLogger.Log("SystemEngine", $"子 agent 已创建: {session.SessionId}");
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
