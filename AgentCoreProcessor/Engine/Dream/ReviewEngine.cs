using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine.Modules;
using AgentCoreProcessor.Logging;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;
using AgentCoreProcessor.Tool.Host;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Engine
{
    internal class ReviewEngine : ISubEngine, IAgentHost
    {
        public string EngineType => "Review";
        public bool IsAlive { get; private set; } = true;

        private readonly ISystemContext _ctx;
        private readonly ReviewConfig _cfg;
        private readonly ReviewProgress _progress;

        private readonly AgentCore _core;
        private Agent? _agent;
        private ReviewControlImpl? _reviewControl;
        private readonly CancellationTokenSource _cts = new();

        // 游标状态
        internal int? CursorMessageId { get; set; }
        internal int? CursorChannelId { get; set; }

        // 空转检测
        private int _consecutiveNavRounds;

        // 预算追踪
        internal int TokensUsed { get; set; }

        // 种子类型（用于日志）
        private string _seedType = "random";

        // 评价缓冲
        internal List<EvaluationBufferEntry> EvaluationBuffer { get; } = new();

        // 思考笔记
        internal string ThinkingNotes { get; set; } = "";

        // 行动序号（ReviewActions 表）
        private int _actionSeqIndex;

        // 会话 ID（ReviewSessions 表）
        internal int? SessionId { get; set; }

        // 访问过的频道和遇到的人物
        internal HashSet<int> ChannelsVisited { get; } = new();
        internal HashSet<int> PersonsEncountered { get; } = new();

        private static readonly HashSet<string> ActionTools = new()
        {
            "review_thinking_notes", "review_update_person",
            "review_evaluate", "review_save_progress",
            "review_complete", "review_request_reinforcement",
            "review_log",
            "memory_store", "memory_link_create", "memory_link_delete"
        };

        private static string ReviewConfigPath =>
            Path.Combine(PathConfig.StoragePath, "Dream", "ReviewConfig.json");

        private static string ReviewProgressPath =>
            Path.Combine(PathConfig.StoragePath, "Dream", "ReviewProgress.json");

        public ReviewEngine(ISystemContext ctx)
        {
            _ctx = ctx;
            _cfg = ReviewConfig.Load(ReviewConfigPath);
            _progress = ReviewProgress.Load(ReviewProgressPath);
            _core = new AgentCore("ReviewCore", usePersona: false);
            _core.CallerTag = "Review:explore";

            // 恢复进度
            if (_progress.SavedAt != null)
            {
                CursorMessageId = _progress.CursorMessageId;
                CursorChannelId = _progress.CursorChannelId;
                EvaluationBuffer.AddRange(_progress.EvaluationBuffer);
                ThinkingNotes = _progress.ThinkingNotes;
                TokensUsed = _progress.TokensUsed;
                _seedType = "resume";
            }
        }

        public async Task RunAsync()
        {
            if (!_cfg.Enabled)
            {
                Signal.Event(LogGroup.Engine, "Review跳过（已禁用）", new { reason = "config.Enabled=false" });
                IsAlive = false;
                return;
            }

            var parentCtx = SignalContext.Current;
            var lifeCtx = Signal.Continue(
                parentCtx?.SignalId ?? Signal.NewId(), parentCtx?.CurrentSpanId,
                "review:main", LogGroup.Engine, "Review引擎",
                new { engineType = EngineType, seedType = _seedType });

            _reviewControl = new ReviewControlImpl(_cfg.ReserveBudget);
            _ctx.ToolContext.Register<IReviewControl>(_reviewControl);

            var reviewAccess = new ReviewAccessImpl(this, _ctx);
            _ctx.ToolContext.Register<IReviewAccess>(reviewAccess);

            ReviewSession? session = null;
            try
            {
                session = await _ctx.ReviewLogs.CreateSessionAsync(new ReviewSession
                {
                    StartTime = DateTime.Now,
                    SeedType = _seedType
                });
                SessionId = session.Id;

                var agentConfig = new AgentConfig
                {
                    MaxRounds = 999,
                    BackoffSeconds = new[] { 10, 30 },
                    ModelCallMaxAttempts = 3,
                    ModelCallRetryDelaySeconds = new[] { 5, 15 },
                };

                var authorized = _ctx.GlobalComponentHost?.GetToolWhitelist("review")
                    ?? new HashSet<string>();
                _core.EngineType = "review";
                _core.GlobalComponentTools = _ctx.GlobalComponentHost?.GetVisibleTools("review").ToList();
                _agent = new Agent(this, _core, agentConfig, authorized);

                // 全量记录工具调用到 ReviewActions
                _agent.OnToolExecuted = (call, result, _) =>
                {
                    var summary = result.IsSuccess
                        ? (result.Data?.Length > 120 ? result.Data[..120] + "…" : result.Data ?? "ok")
                        : (result.Error ?? "error");
                    return LogActionAsync(call.Tool, summary, call.RawInputJson);
                };

                await _agent.RunAsync(_cts.Token);

                // 应用评价缓冲
                var evalEngine = new EvaluationEngine(_ctx.EvaluationScores, _cfg);
                var evalCount = await evalEngine.ApplyAsync(EvaluationBuffer);

                // 标记信标为已处理
                if (_reviewControl?.IsCompleted == true)
                {
                    var hints = await _ctx.ReviewHints.GetUnprocessedAsync();
                    foreach (var h in hints)
                        await _ctx.ReviewHints.MarkProcessedAsync(h.Id);
                }

                // 记录评价数和信号ID（供 finally 写回 session）
                session.EvaluationCount = evalCount;
                session.SignalId = lifeCtx.SignalId;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Signal.Error(LogGroup.Engine, "Review异常", new { error = ex.GetType().Name, message = ex.Message });
            }
            finally
            {
                // 确保 session 落库（即使异常退出也不留僵尸记录）
                if (session != null)
                {
                    session.EndTime = DateTime.Now;
                    session.StopReason = _reviewControl?.IsCompleted == true ? "completed"
                        : TokensUsed >= _cfg.TokenBudget ? "budget" : "interrupted";
                    session.TokensUsed = TokensUsed;
                    session.RoundsExecuted = _agent?.TotalRounds ?? 0;
                    session.ChannelsVisited = System.Text.Json.JsonSerializer.Serialize(ChannelsVisited.ToList());
                    session.PersonsEncountered = System.Text.Json.JsonSerializer.Serialize(PersonsEncountered.ToList());
                    session.ThinkingNotes = ThinkingNotes;
                    await _ctx.ReviewLogs.UpdateSessionAsync(session);

                    Signal.Event(LogGroup.Engine, "Review完成", new
                    {
                        seedType = _seedType,
                        rounds = session.RoundsExecuted,
                        stopReason = session.StopReason,
                        completed = _reviewControl?.IsCompleted,
                        reserveUsed = _reviewControl?.ReserveGranted,
                        tokensUsed = TokensUsed,
                        evaluationsApplied = session.EvaluationCount
                    });
                }

                _ctx.ToolContext.Unregister<IReviewAccess>();
                _ctx.ToolContext.Unregister<IReviewControl>();
                IsAlive = false;
                lifeCtx.Close(new { engineType = EngineType, reason = _reviewControl?.IsCompleted == true ? "completed" : "budget_or_interrupted" });
            }
        }

        public void OnEvent(EngineEvent e)
        {
            if (e is MessageEvent)
                _reviewControl?.NotifyWake();
        }

        public void RequestStop()
        {
            _cts.Cancel();
        }

        internal WebUI.Services.EngineContextSnapshot? GetContextSnapshot()
        {
            if (_agent == null) return null;
            var history = _agent.History;
            var messages = new List<WebUI.Services.ContextMessageSnapshot>();
            int totalChars = 0;
            foreach (var m in history)
            {
                var est = m.Content?.Length ?? m.ContentParts?.Sum(p =>
                    (p.Text?.Length ?? 0) + (p.ToolInput?.Length ?? 0) + (p.ToolName?.Length ?? 0)) ?? 0;
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
                ConversationOffset = _agent.ConversationOffset,
                CompressionTier = Modules.CompressionTier.None,
                IsCompressing = false,
                Summary = null,
                TotalRounds = _agent.TotalRounds,
                IsInBackoff = _agent.IsInBackoff,
                Messages = messages
            };
        }

        // ---- IAgentHost ----

        public Task OnTokensUsedAsync(Usage usage)
        {
            TokensUsed += usage.TotalTokens;
            return Task.CompletedTask;
        }

        public async Task<List<Message>?> BuildStartInjectAsync()
        {
            var msgs = new List<Message>();

            msgs.Add(new Message { Role = "user", Content = BuildSystemPrompt() });

            // 种子内容
            var seedContent = await BuildSeedContentAsync();
            if (!string.IsNullOrEmpty(seedContent))
                msgs.Add(new Message { Role = "user", Content = seedContent });

            // 预算状态由 BuildRoundInjectAsync 负责注入（避免首轮重复）
            return msgs;
        }

        public Task<List<Message>?> BuildRoundInjectAsync()
        {
            if (_reviewControl?.IsCompleted == true)
                return Task.FromResult<List<Message>?>(null);

            // 预算超限检查
            var budget = _cfg.TokenBudget + (_reviewControl?.ReserveGranted == true ? _cfg.ReserveBudget : 0);
            if (TokensUsed >= budget)
            {
                _cts.Cancel();
                return Task.FromResult<List<Message>?>(null);
            }

            // 压缩检查
            if (_agent != null)
                TryCompressHistory();

            var msgs = new List<Message>();

            // 预算状态
            msgs.Add(new Message { Role = "user", Content = BuildBudgetStatus() });

            // 唤醒通知
            if (_reviewControl is { WakeNotified: true })
                msgs.Add(new Message { Role = "user", Content = "[通知] 系统已醒来。备用预算不可用，但你可以继续完成当前工作。" });

            // 空转提醒
            if (_consecutiveNavRounds >= _cfg.MaxNavigationRounds)
                msgs.Add(new Message { Role = "user", Content = "[提示] 你已经浏览了一段时间没有记录。如果有想法可以先写入 thinking_notes。" });

            return Task.FromResult<List<Message>?>(msgs);
        }

        /// <summary>工具执行后由 Agent 回调，用于空转检测。</summary>
        internal void OnToolsExecuted(IEnumerable<string> toolNames)
        {
            if (toolNames.Any(t => ActionTools.Contains(t)))
                _consecutiveNavRounds = 0;
            else
                _consecutiveNavRounds++;
        }

        /// <summary>记录行动到 ReviewActions 表。</summary>
        internal async Task LogActionAsync(string actionType, string summary, string? detailJson = null)
        {
            if (SessionId == null) return;

            var action = new ReviewAction
            {
                SessionId = SessionId.Value,
                SeqIndex = _actionSeqIndex++,
                Time = DateTime.Now,
                ActionType = actionType,
                Summary = summary,
                Detail = detailJson
            };
            await _ctx.ReviewLogs.CreateActionAsync(action);
        }

        /// <summary>保存当前进度到 JSON。</summary>
        internal void SaveProgress()
        {
            var progress = new ReviewProgress
            {
                CursorMessageId = CursorMessageId,
                CursorChannelId = CursorChannelId,
                EvaluationBuffer = EvaluationBuffer.ToList(),
                ThinkingNotes = ThinkingNotes,
                Findings = new List<string>(),
                NextSteps = new List<string>(),
                TokensUsed = TokensUsed,
                ReserveUsed = _reviewControl?.ReserveGranted ?? false,
                SavedAt = DateTime.Now
            };
            progress.Save(ReviewProgressPath);
        }

        /// <summary>清除进度文件（complete 后调用）。</summary>
        internal void ClearProgress()
        {
            if (File.Exists(ReviewProgressPath))
                File.Delete(ReviewProgressPath);
        }

        // ---- 压缩 ----

        private static readonly HashSet<string> NavigationTools = new()
        {
            "review_browse", "review_search_messages",
            "review_focus", "review_get_person", "review_list_beacons",
            "memory_search", "memory_link_get"
        };

        private bool _compressionApplied;

        private void TryCompressHistory()
        {
            var history = _agent!.History;
            var estimatedTokens = EstimateTokens(history);
            if (estimatedTokens < _cfg.CompressionThreshold)
                return;

            using var span = Signal.Open(LogGroup.Engine, $"review:压缩 ({estimatedTokens}t)",
                new { estimatedTokens, threshold = _cfg.CompressionThreshold, historyCount = history.Count });

            // 保留最近 3 轮（每轮 = 1 assistant + 1 user tool_result）
            const int retainRounds = 3;
            int retainMessages = retainRounds * 2;
            int conversationStart = _agent.ConversationOffset;
            int compressEnd = Math.Max(conversationStart, history.Count - retainMessages);

            int compressed = 0;
            for (int i = conversationStart; i < compressEnd; i++)
            {
                var msg = history[i];
                if (msg.ContentParts == null) continue;

                if (msg.Role == "assistant")
                {
                    // assistant 消息中的 tool_use：如果是导航工具，标记对应 result 待压缩
                    // 不修改 assistant 消息本身（保持 tool_use 结构完整）
                    continue;
                }

                if (msg.Role == "user" && msg.Content == "[tool results]")
                {
                    // 找到对应的 assistant 消息获取工具名
                    var prevAssistant = i > 0 ? history[i - 1] : null;
                    if (prevAssistant?.ContentParts == null) continue;

                    var newParts = new List<ContentPart>();
                    bool anyCompressed = false;

                    foreach (var part in msg.ContentParts)
                    {
                        if (part.Type != "tool_result" || part.ToolUseId == null)
                        {
                            newParts.Add(part);
                            continue;
                        }

                        // 找到对应的 tool_use 获取工具名和参数
                        var toolUsePart = prevAssistant.ContentParts
                            .FirstOrDefault(p => p.Type == "tool_use" && p.ToolUseId == part.ToolUseId);
                        if (toolUsePart == null)
                        {
                            newParts.Add(part);
                            continue;
                        }

                        var toolName = toolUsePart.ToolName ?? "";

                        if (NavigationTools.Contains(toolName))
                        {
                            // 导航工具结果 → 一行摘要
                            var summary = BuildToolSummary(toolName, toolUsePart.ToolInput, part.Text);
                            newParts.Add(ContentPart.FromToolResult(part.ToolUseId, summary, part.IsError ?? false));
                            anyCompressed = true;
                        }
                        else if (_compressionApplied && ActionTools.Contains(toolName))
                        {
                            // 二次压缩：action 工具结果也压缩为一行
                            var summary = BuildActionSummary(toolName, toolUsePart.ToolInput, part.Text);
                            newParts.Add(ContentPart.FromToolResult(part.ToolUseId, summary, part.IsError ?? false));
                            anyCompressed = true;
                        }
                        else
                        {
                            newParts.Add(part);
                        }
                    }

                    if (anyCompressed)
                    {
                        msg.ContentParts = newParts;
                        compressed++;
                    }
                }
            }

            // 在 conversationStart 位置插入压缩提示（替换旧的压缩提示如果有）
            if (compressed > 0)
            {
                var notice = new Message
                {
                    Role = "user",
                    Content = "[系统] 早期阅读内容已压缩。你的 thinking_notes 和所有行动记录完整保留。"
                };

                // 移除旧的压缩提示
                for (int i = conversationStart; i < compressEnd && i < history.Count; i++)
                {
                    if (history[i].Role == "user" && history[i].Content?.StartsWith("[系统] 早期阅读内容已压缩") == true)
                    {
                        history[i] = notice;
                        notice = null!;
                        break;
                    }
                }
                if (notice != null)
                    history.Insert(conversationStart, notice);

                _compressionApplied = true;
            }

            var afterTokens = EstimateTokens(history);
            span.SetCloseDetail(new
            {
                compressedResults = compressed,
                beforeTokens = estimatedTokens,
                afterTokens,
                secondPass = _compressionApplied && compressed > 0
            });
        }

        private static int EstimateTokens(List<Message> messages)
        {
            int total = 0;
            foreach (var msg in messages)
            {
                if (msg.ContentParts != null)
                {
                    foreach (var p in msg.ContentParts)
                        total += (p.Text?.Length ?? 0) + (p.ToolInput?.Length ?? 0) + (p.ToolName?.Length ?? 0);
                }
                else
                {
                    total += msg.Content?.Length ?? 0;
                }
            }
            return total / 3; // 粗估：3 字符 ≈ 1 token
        }

        private static string BuildToolSummary(string toolName, string? toolInput, string? resultText)
        {
            var inputInfo = ParseInputBrief(toolInput);
            var resultLen = resultText?.Split('\n').Length ?? 0;

            return toolName switch
            {
                "review_browse" => $"[已压缩] 浏览了 {resultLen} 行消息{inputInfo}",
                "review_search_messages" => $"[已压缩] 搜索消息{inputInfo}，返回 {resultLen} 行结果",
                "memory_search" => $"[已压缩] 搜索记忆{inputInfo}，返回 {resultLen} 行结果",
                "review_focus" => $"[已压缩] 移动游标{inputInfo}",
                "review_get_person" => $"[已压缩] 查看人物信息{inputInfo}",
                "review_list_beacons" => $"[已压缩] 列出信标，返回 {resultLen} 行",
                "memory_link_get" => $"[已压缩] 查看记忆关联{inputInfo}",
                _ => $"[已压缩] {toolName}{inputInfo}"
            };
        }

        private static string BuildActionSummary(string toolName, string? toolInput, string? resultText)
        {
            var inputInfo = ParseInputBrief(toolInput);
            return toolName switch
            {
                "review_evaluate" => $"[已压缩] 评价{inputInfo}",
                "memory_store" => $"[已压缩] 写入记忆{inputInfo}",
                "review_update_person" => $"[已压缩] 更新人物{inputInfo}",
                "memory_link_create" => $"[已压缩] 创建记忆关联{inputInfo}",
                "memory_link_delete" => $"[已压缩] 删除记忆关联{inputInfo}",
                "review_log" => $"[已压缩] 记录日志{inputInfo}",
                "review_thinking_notes" => resultText ?? "[已压缩] 更新笔记",
                _ => $"[已压缩] {toolName}"
            };
        }

        private static string ParseInputBrief(string? toolInput)
        {
            if (string.IsNullOrEmpty(toolInput)) return "";
            try
            {
                var obj = Newtonsoft.Json.Linq.JObject.Parse(toolInput);
                var parts = new List<string>();
                if (obj["channel_id"] != null) parts.Add($"频道#{obj["channel_id"]}");
                if (obj["person_id"] != null) parts.Add($"P#{obj["person_id"]}");
                if (obj["query"] != null) parts.Add($"query=\"{obj["query"]}\"");
                if (obj["target_type"] != null && obj["target_id"] != null)
                    parts.Add($"{obj["target_type"]}#{obj["target_id"]}");
                if (obj["dimension"] != null) parts.Add($"{obj["dimension"]}");
                if (obj["rating"] != null) parts.Add($"{obj["rating"]}");
                if (obj["count"] != null) parts.Add($"{obj["count"]}条");
                if (obj["memory_id"] != null) parts.Add($"mem#{obj["memory_id"]}");
                return parts.Count > 0 ? $" ({string.Join(", ", parts)})" : "";
            }
            catch { return ""; }
        }

        // ---- 私有方法 ----

        private async Task<string> BuildSeedContentAsync()
        {
            // 有未完成进度 → 恢复
            if (_seedType == "resume")
            {
                return BuildResumeContent();
            }

            // 有信标 → 列出所有未处理信标
            var hints = await _ctx.ReviewHints.GetUnprocessedAsync();
            if (hints.Count > 0)
            {
                _seedType = "beacon";
                return BuildBeaconSeed(hints);
            }

            // 无信标 → 随机选一个活跃频道
            _seedType = "random";
            return await BuildRandomSeedAsync();
        }

        private string BuildResumeContent()
        {
            var lines = new List<string> { "## 恢复上次进度" };
            lines.Add($"上次保存时间: {_progress.SavedAt:yyyy-MM-dd HH:mm}");

            if (!string.IsNullOrEmpty(_progress.ThinkingNotes))
            {
                lines.Add("");
                lines.Add("### 思考笔记");
                lines.Add(_progress.ThinkingNotes);
            }

            if (_progress.Findings.Count > 0)
            {
                lines.Add("");
                lines.Add("### 已有发现");
                foreach (var f in _progress.Findings)
                    lines.Add($"- {f}");
            }

            if (_progress.NextSteps.Count > 0)
            {
                lines.Add("");
                lines.Add("### 待完成步骤");
                foreach (var s in _progress.NextSteps)
                    lines.Add($"- {s}");
            }

            if (CursorChannelId != null)
                lines.Add($"\n游标位置: 频道 {CursorChannelId}, 消息 {CursorMessageId}");

            return string.Join("\n", lines);
        }

        private string BuildBeaconSeed(List<ReviewHint> hints)
        {
            var lines = new List<string> { "## 待处理信标" };
            lines.Add("以下是工作期间标记的需要关注的内容，选择你感兴趣的开始探索：");
            lines.Add("");

            foreach (var hint in hints)
            {
                var location = hint.ChannelId != null ? $"频道#{hint.ChannelId}" : "";
                if (hint.MessageId != null) location += $" 消息#{hint.MessageId}";
                if (hint.PersonId != null) location += $" 人物P#{hint.PersonId}";
                var source = hint.Source == "framework" ? " [自动]" : "";
                lines.Add($"- [{hint.CreatedAt:MM-dd HH:mm}]{source} {location}: {hint.Content}");
            }

            return string.Join("\n", lines);
        }

        private async Task<string> BuildRandomSeedAsync()
        {
            var channels = await _ctx.Session.GetAllChannelsAsync();
            if (channels.Count == 0)
                return "（无可用频道，请使用 search 工具探索记忆库）";

            var rng = new Random();
            var channel = channels[rng.Next(channels.Count)];

            var messages = await _ctx.Session.GetContextByChannelAsync(channel.Id, limit: 10);
            if (messages.Count == 0)
                return $"随机选择了频道「{channel.Name}」，但暂无消息。可以用 search 工具探索其他内容。";

            CursorChannelId = channel.Id;

            var lines = new List<string>
            {
                $"## 随机起点：频道「{channel.Name}」",
                "以下是该频道的近期消息预览，可以从这里开始探索：",
                ""
            };

            foreach (var msg in messages.TakeLast(8))
            {
                var name = msg.IsFromBot ? "Lilara"
                    : !string.IsNullOrEmpty(msg.SenderName) ? msg.SenderName
                    : $"U#{msg.UserId}";
                var time = msg.Time.ToString("MM-dd HH:mm");
                var preview = msg.Content.Length > 80 ? msg.Content[..80] + "..." : msg.Content;
                lines.Add($"[{time}] {name}: {preview}");
            }

            return string.Join("\n", lines);
        }

        private string BuildBudgetStatus()
        {
            var totalBudget = _cfg.TokenBudget + (_reviewControl?.ReserveGranted == true ? _cfg.ReserveBudget : 0);
            var reserveStatus = _reviewControl?.ReserveGranted == true
                ? $"已激活 (+{_cfg.ReserveBudget})"
                : _reviewControl?.WakeNotified == true
                    ? "不可用"
                    : "可申请";

            return $"[预算] 已用: ~{TokensUsed} / 上限: {totalBudget} | 备用预算: {reserveStatus}";
        }

        private string BuildSystemPrompt()
        {
            return """
你是 Lilara 的复盘模块。系统当前处于深度睡眠，你在离线状态下自由探索和整理。

## 工作方式
你的任务没有固定目标。从下方的起始内容出发，跟着好奇心走：
- 用 review_browse 顺序阅读消息流
- 用 review_focus 跳转到感兴趣的位置
- 用 review_search_messages / memory_search 拉取特定条件的消息或记忆
- 发现有价值的东西就行动（写记忆、更新人物、评价、修正矛盾）

## 习惯
- 边读边记：看到重要信息先写 review_thinking_notes，browse 的原文可能会被压缩
- 先查再写：写记忆前搜索确认无重复，更新人物前先查看现状
- 随手评价：看到某人的表现就记录印象，可以多次评价同一目标，最终取平均
- 批量操作：你可以一次调用多个工具，不需要一个一个来

## 预算
你有有限的 token 预算。当剩余预算不多时，用 review_save_progress 保存进度后 review_complete。
如果工作进行到一半确实需要更多资源，可以申请一次备用预算。
""";
        }
    }
}

