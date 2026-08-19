using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Logging;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;
using AgentCoreProcessor.Engine.Modules;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 可复用 Agent。封装"构建上下文→调模型→执行工具→是否继续"的多轮推理循环。
    /// 仅处理 Working 模式。Express 模式由 Engine 直接调 Core，不走 Agent。
    /// </summary>
    internal class Agent
    {
        private readonly IAgentHost _host;
        private readonly AgentCore _core;
        private readonly AgentConfig _config;
        private Func<string, ITool?> _toolResolver;
        private readonly List<Message> _history = new();
        private int _consecutiveFailures;
        private DateTime? _backoffUntil;

        public AgentStopReason? StopReason { get; private set; }
        public List<Message> History => _history;
        public int TotalRounds { get; private set; }
        public bool IsInBackoff => _backoffUntil.HasValue && DateTime.Now < _backoffUntil.Value;
        public List<ToolCall>? LastRoundCalls { get; private set; }
        public List<ToolResult>? LastRoundResults { get; private set; }

        /// <summary>工具解析器，可在 InitAsync 之后更新为 loop 感知的版本。</summary>
        public Func<string, ITool?> ToolResolver
        {
            get => _toolResolver;
            set => _toolResolver = value ?? ToolRegistry.Get;
        }

        /// <summary>当前权限组（express/channel/task/review）。透传给内部 ToolExecutor 做执行期硬校验。</summary>
        public string? PermissionGroup { get; set; }

        /// <summary>工具执行完毕后的回调。Host 用此发布事件到总线。</summary>
        public Func<ToolCall, ToolResult, ITool?, Task>? OnToolExecuted { get; set; }

        /// <summary>每轮完成后的回调（tool_results 已写入 _history）。Host 用此持久化上下文。</summary>
        public Func<Task>? OnRoundCompleted { get; set; }

        /// <summary>对话内容在 history 中的起始偏移（跳过框架注入部分）。</summary>
        public int ConversationOffset { get; set; }

        public Agent(IAgentHost host, AgentCore core, AgentConfig config,
            Func<string, ITool?>? toolResolver = null)
        {
            _host = host;
            _core = core;
            _config = config;
            _toolResolver = toolResolver ?? ToolRegistry.Get;
        }

        /// <summary>
        /// 构建发送给模型的完整消息列表。若历史超限（MaxMessagesToSend > 0 且历史过多），
        /// 只保留最近 N 条 + 前缀摘要占位消息（提示模型早期内容已被截断），防止全量历史常驻内存和发费。
        /// 注意：仅影响发送给模型的副本，_history 完整保留用于持久化与后续轮次。
        /// </summary>
        private List<Message> BuildMessagesForModel(List<Message>? roundInject, out bool truncated)
        {
            truncated = false;
            var limit = _config.MaxMessagesToSend;
            if (limit <= 0 || _history.Count + (roundInject?.Count ?? 0) <= limit)
            {
                var full = new List<Message>(_history);
                if (roundInject != null) full.AddRange(roundInject);
                return full;
            }

            truncated = true;
            // 保留最近 N 条（去掉注入部分后，从历史尾部取）
            var injectCount = roundInject?.Count ?? 0;
            var keepFromHistory = Math.Max(0, limit - injectCount);
            var recent = _history.Skip(Math.Max(0, _history.Count - keepFromHistory)).ToList();

            // 前缀摘要占位（提示模型早期内容已被截断归档）
            var dropped = _history.Count - keepFromHistory;
            var summary = new Message
            {
                Role = "system",
                Content = $"[系统] 为控制上下文长度，较早的 {dropped} 条对话记录已归档（如需查阅历史请使用 search_chat_history 工具）。以下是最近的对话："
            };
            var result = new List<Message> { summary };
            result.AddRange(recent);
            if (roundInject != null) result.AddRange(roundInject);
            return result;
        }

        public async Task RunAsync(CancellationToken ct)
        {
            StopReason = null;
            _consecutiveFailures = 0;
            TotalRounds = 0;
            LastRoundCalls = null;
            LastRoundResults = null;

            // 启动注入（新消息、压缩产物等一次性内容）
            var startInject = await _host.BuildStartInjectAsync();
            if (startInject != null)
            {
                foreach (var m in startInject)
                    _history.Add(m);
            }
            // ConversationOffset 只跳过框架消息（prefix + summary），保留对话内容用于持久化
            ConversationOffset = _host.FrameworkMessageCount;

            for (int round = 0; round < _config.MaxRounds && !ct.IsCancellationRequested; round++)
            {
                if (IsInBackoff)
                {
                    var remaining = _backoffUntil!.Value - DateTime.Now;
                    if (remaining > TimeSpan.Zero)
                        await Task.Delay(remaining, ct);
                    _backoffUntil = null;
                }

                TotalRounds++;
                using var roundSpan = Signal.Open(LogGroup.Engine, $"agent:round R{round + 1}",
                    new { round = round + 1 });

                // 本轮注入
                var roundInject = await _host.BuildRoundInjectAsync();

                // 拼装消息：历史 + 本轮注入，同时写入 _history 确保持久化和跨轮可见
                var messages = BuildMessagesForModel(roundInject, out var truncated);
                if (roundInject != null)
                {
                    _history.AddRange(roundInject);
                }

                if (truncated)
                {
                    Signal.Debug(LogGroup.Engine, "历史截取（发送给模型限量）",
                        new { totalHistory = _history.Count, sentCount = messages.Count });
                }

                // 调模型（内层重试：同样 messages 最多尝试 MaxAttempts 次）
                ModelOutput output;
                bool modelSuccess = false;
                using (var modelSpan = Signal.Open(LogGroup.Model, $"模型调用 R{round + 1}",
                    new
                    {
                        round = round + 1,
                        messageCount = messages.Count,
                        messages = messages.Select(m => m.ContentParts != null
                            ? (object)new { m.Role, parts = m.ContentParts.Select(p => new { p.Type, p.Text, p.ToolName, p.ToolInput, p.ToolUseId, p.IsError }) }
                            : new { m.Role, content = m.Content })
                    }))
                {
                    output = default!;
                    Exception? lastEx = null;
                    for (int attempt = 0; attempt < _config.ModelCallMaxAttempts; attempt++)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            if (attempt > 0)
                            {
                                var retryDelay = _config.ModelCallRetryDelaySeconds[
                                    Math.Min(attempt - 1, _config.ModelCallRetryDelaySeconds.Length - 1)];
                                Signal.Warn(LogGroup.Model, $"模型调用重试 R{round + 1} #{attempt + 1}",
                                    new { round = round + 1, attempt = attempt + 1, delaySeconds = retryDelay, lastError = lastEx?.Message });
                                await Task.Delay(TimeSpan.FromSeconds(retryDelay), ct);
                            }
                            output = await _core.InvokeAsync(messages, EngineMode.Working);
                            modelSuccess = true;
                            _consecutiveFailures = 0;
                            _backoffUntil = null;
                            modelSpan.SetCloseDetail(new
                            {
                                responseText = output.Text,
                                thinking = output.Thinking,
                                toolCalls = output.ToolCalls?.Select(tc => new { tc.Tool, tc.Inputs, tc.ToolUseId }),
                                attempts = attempt + 1
                            });
                            break;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            lastEx = ex;
                        }
                    }

                    if (!modelSuccess)
                    {
                        _consecutiveFailures++;
                        modelSpan.SetCloseDetail(new
                        {
                            error = lastEx!.GetType().Name,
                            message = lastEx.Message,
                            attempts = _config.ModelCallMaxAttempts
                        });

                        if (_consecutiveFailures > _config.BackoffSeconds.Length)
                        {
                            StopReason = AgentStopReason.Error;
                            return;
                        }
                        var delay = _config.BackoffSeconds[
                            Math.Min(_consecutiveFailures - 1, _config.BackoffSeconds.Length - 1)];
                        _backoffUntil = DateTime.Now.AddSeconds(delay);
                        Signal.Warn(LogGroup.Engine, "agent退避",
                            new { consecutiveFailures = _consecutiveFailures, backoffSeconds = delay });
                        continue;
                    }
                }

                // 上报 token 消耗
                if (output.Usage != null)
                    await _host.OnTokensUsedAsync(output.Usage);

                // 追加 assistant 到历史
                _history.Add(NativeToolMessageFormatter.FormatAssistant(output, _core.UseNativeTools, _toolResolver));

                // 无工具 → 停止
                if (!output.HasToolCalls || output.ToolCalls == null || output.ToolCalls.Count == 0)
                {
                    StopReason = AgentStopReason.Completed;
                    if (OnRoundCompleted != null)
                        await OnRoundCompleted();
                    return;
                }

                // 执行工具
                List<ToolResult> results;
                var toolNames = string.Join(", ", output.ToolCalls.Select(c => c.Tool));
                using (var toolSpan = Signal.Open(LogGroup.Tool, $"工具: {toolNames}",
                    new
                    {
                        toolCount = output.ToolCalls.Count,
                        calls = output.ToolCalls.Select(c => new { c.Tool, c.Inputs, c.ToolUseId })
                    }))
                {
                    var executor = new ToolExecutor(_toolResolver) { PermissionGroup = PermissionGroup };
                    if (OnToolExecuted != null)
                    {
                        executor.OnToolExecuted = async (call, result) =>
                        {
                            var toolDef = _toolResolver(call.Tool);
                            await OnToolExecuted(call, result, toolDef);
                        };
                    }
                    results = await executor.ExecuteAsync(output.ToolCalls);
                    toolSpan.SetCloseDetail(new
                    {
                        results = output.ToolCalls.Zip(results, (c, r) => new
                        {
                            tool = c.Tool,
                            status = r.Status,
                            data = r.Data,
                            error = r.Error
                        })
                    });
                }

                LastRoundCalls = output.ToolCalls;
                LastRoundResults = results;

                // 工具结果加入历史（Claude API 要求 tool_use 后必须跟 tool_result）
                _history.Add(NativeToolMessageFormatter.FormatToolResults(output.ToolCalls, results, _core.UseNativeTools, !_core.DisableImageMessages));

                // 每轮持久化
                if (OnRoundCompleted != null)
                    await OnRoundCompleted();

                // 内联 wait（工具结果中的 RequestWait 标记，如 speak 含 [wait] 占位符）
                if (results.Any(r => r.RequestWait))
                {
                    StopReason = AgentStopReason.WaitRequested;
                    return;
                }

                // wait 工具 → 停止
                if (output.ToolCalls.Any(c => c.Tool == "wait"))
                {
                    StopReason = AgentStopReason.WaitRequested;
                    return;
                }
            }

            StopReason = AgentStopReason.MaxRounds;
        }

        public void ForceStop() => StopReason = AgentStopReason.ForceStopped;

        public void ClearHistory()
        {
            _history.Clear();
            LastRoundCalls = null;
            LastRoundResults = null;
        }

        public void AddToHistory(Message msg) => _history.Add(msg);
    }

    internal enum AgentStopReason
    {
        Completed,
        MaxRounds,
        WaitRequested,
        ForceStopped,
        Cancelled,
        Error
    }
}
