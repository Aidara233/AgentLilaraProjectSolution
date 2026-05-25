using System;
using System.Collections.Generic;
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
        private readonly HashSet<string> _authorizedTools;
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

        /// <summary>工具执行完毕后的回调。Host 用此发布事件到总线。</summary>
        public Func<ToolCall, ToolResult, ITool?, Task>? OnToolExecuted { get; set; }

        /// <summary>对话内容在 history 中的起始偏移（跳过框架注入部分）。</summary>
        public int ConversationOffset { get; set; }

        public Agent(IAgentHost host, AgentCore core, AgentConfig config, HashSet<string> authorizedTools,
            Func<string, ITool?>? toolResolver = null)
        {
            _host = host;
            _core = core;
            _config = config;
            _authorizedTools = authorizedTools ?? new HashSet<string>();
            _toolResolver = toolResolver ?? ToolRegistry.Get;
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

                // 拼装消息：历史 + 本轮注入
                var messages = new List<Message>(_history);
                if (roundInject != null)
                    messages.AddRange(roundInject);

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

                // 追加 assistant 到历史
                _history.Add(FormatAssistant(output));

                // 无工具 → 停止
                if (!output.HasToolCalls || output.ToolCalls == null || output.ToolCalls.Count == 0)
                {
                    StopReason = AgentStopReason.Completed;
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
                    var executor = new ToolExecutor(_toolResolver, _authorizedTools);
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
                _history.Add(FormatToolResults(output.ToolCalls, results));

                // wait 工具 → 停止
                if (output.ToolCalls.Any(c => c.Tool == "wait"))
                {
                    StopReason = AgentStopReason.WaitRequested;
                    return;
                }

                // deescalate 工具 → 降级到 Express
                if (output.ToolCalls.Any(c => c.Tool == "deescalate"))
                {
                    StopReason = AgentStopReason.Deescalated;
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

        // ── 格式化 ──

        private Message FormatAssistant(ModelOutput output)
        {
            if (output.HasToolCalls && output.ToolCalls != null)
            {
                if (_core.UseNativeTools)
                {
                    var parts = new List<ContentPart>();
                    if (!string.IsNullOrEmpty(output.Thinking))
                        parts.Add(ContentPart.FromText(output.Thinking));
                    foreach (var c in output.ToolCalls)
                    {
                        if (c.ToolUseId != null)
                        {
                            var json = BuildToolInputJson(c);
                            parts.Add(ContentPart.FromToolUse(c.ToolUseId, c.Tool, json));
                        }
                    }
                    return new Message
                    {
                        Role = "assistant",
                        Content = !string.IsNullOrEmpty(output.Thinking) ? output.Thinking : "[tool calls]",
                        ContentParts = parts
                    };
                }
                else
                {
                    var sb = new System.Text.StringBuilder();
                    if (!string.IsNullOrEmpty(output.Thinking))
                        sb.AppendLine(output.Thinking);
                    foreach (var c in output.ToolCalls)
                        sb.AppendLine($"{c.Tool}({string.Join(", ", c.Inputs).Truncate(100)})");
                    return new Message { Role = "assistant", Content = sb.ToString() };
                }
            }
            var content = output.Text ?? output.Thinking;
            if (string.IsNullOrEmpty(content))
                content = "[无输出]";
            return new Message { Role = "assistant", Content = content };
        }

        private Message FormatToolResults(List<ToolCall> calls, List<ToolResult> results)
        {
            if (_core.UseNativeTools)
            {
                var parts = new List<ContentPart>();
                for (int i = 0; i < calls.Count && i < results.Count; i++)
                {
                    if (calls[i].ToolUseId != null)
                    {
                        var data = results[i].IsSuccess
                            ? (results[i].Data ?? "成功")
                            : $"失败: {results[i].Error ?? results[i].Status}";
                        parts.Add(ContentPart.FromToolResult(calls[i].ToolUseId!, data, !results[i].IsSuccess));
                    }
                }
                return new Message { Role = "user", Content = "[tool results]", ContentParts = parts };
            }
            else
            {
                var sb = new System.Text.StringBuilder("[上一轮工具执行结果]\n");
                for (int i = 0; i < calls.Count && i < results.Count; i++)
                {
                    if (results[i].IsSuccess)
                        sb.AppendLine($"[{calls[i].Tool}]: 成功");
                    else
                        sb.AppendLine($"[{calls[i].Tool}]: {results[i].Status} - {results[i].Error}");
                }
                return new Message { Role = "user", Content = sb.ToString() };
            }
        }

        /// <summary>
        /// 确保 tool_use 的 input 始终是 JSON 对象（非数组）。
        /// Anthropic API 要求 tool_use.input 必须是 JSON object，数组格式会导致 400。
        /// </summary>
        private string BuildToolInputJson(ToolCall call)
        {
            // 优先用 RawInputJson，但验证它是对象
            if (!string.IsNullOrEmpty(call.RawInputJson))
            {
                try
                {
                    var node = Newtonsoft.Json.Linq.JToken.Parse(call.RawInputJson);
                    if (node is Newtonsoft.Json.Linq.JObject)
                        return call.RawInputJson;
                }
                catch { }
            }

            // 从工具 schema 重建具名 JSON 对象
            var tool = _toolResolver(call.Tool);
            if (tool != null)
            {
                try
                {
                    var schemaNode = tool.GetInputSchema();
                    var schemaStr = schemaNode?.ToJsonString();
                    if (!string.IsNullOrEmpty(schemaStr))
                    {
                        var schema = Newtonsoft.Json.Linq.JObject.Parse(schemaStr);
                        if (schema["properties"] is Newtonsoft.Json.Linq.JObject props)
                        {
                            var obj = new Newtonsoft.Json.Linq.JObject();
                            int i = 0;
                            foreach (var prop in props.Properties())
                            {
                                obj[prop.Name] = i < call.Inputs.Count ? call.Inputs[i] : "";
                                i++;
                            }
                            return obj.ToString(Newtonsoft.Json.Formatting.None);
                        }
                    }
                }
                catch { }
            }

            // 最后兜底：空对象
            return "{}";
        }
    }

    internal enum AgentStopReason
    {
        Completed,
        MaxRounds,
        WaitRequested,
        Deescalated,
        ForceStopped,
        Cancelled,
        Error
    }
}
