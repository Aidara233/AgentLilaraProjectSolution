using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Engine.Session;
using AgentCoreProcessor.Logging;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;
using AgentCoreProcessor.Tool.Core;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// Express 微循环：多轮模型调用，工具结果回注供下一轮参考。
    /// </summary>
    partial class ExpressEngine
    {
        /// <summary>Express 微循环：多轮模型调用（最多 ExpressMaxRounds 轮），工具结果回注供下一轮参考。</summary>
        private async Task ExecuteExpressCycleAsync()
        {
            // 每轮重置图片追踪状态（在 _ctxState 中）
            _ctxState.RoundImageHashes.Clear();
            _ctxState.InjectedDescriptions.Clear();
            _ctxState.InjectedOcrTexts.Clear();
            _startInjectMaxId = 0;

            var messages = new List<Message>();

            var startInject = await ((IAgentHost)this).BuildStartInjectAsync();
            if (startInject != null) messages.AddRange(startInject);

            var roundInject = await ((IAgentHost)this).BuildRoundInjectAsync();
            if (roundInject != null) messages.AddRange(roundInject);

            if (messages.Count > HistoryMaxMessages)
            {
                var excess = messages.Count - HistoryMaxMessages;
                if (excess > 0)
                    messages.RemoveRange(0, excess);
            }

            agentCore.EngineType = "express";
            // Loop 组件工具（speak 等）。核心工具（escalate/wait/continue_loop）由 AgentCore 内部通过
            // GetExpressToolDefinitions("express") 收集。这里不再 Concat(ToolRegistry.All.Values) 全量合并，
            // 否则会把 session 专用/全局工具无差别注入 Express 工具表。
            var componentTools = componentHost?.GetVisibleTools() ?? Enumerable.Empty<ITool>();
            agentCore.AdditionalTools = componentTools
                .Where(t => !ToolRegistry.IsDisabled(t.Name))
                .DistinctBy(t => t.Name)
                .ToList();
            agentCore.GlobalComponentTools = ctx.GlobalComponentHost?.GetVisibleTools("channel").ToList();

            // 诊断：输出当前可用的 Express 工具
            var allExpressDefs = ToolRegistry.GetExpressToolDefinitions("express");
            var additionalNames = agentCore.AdditionalTools?.Select(t => t.Name).ToList() ?? new();
            Console.Error.WriteLine($"[Express ch:{channelId}] ToolRegistry={allExpressDefs.Count} tools, AdditionalTools={additionalNames.Count} tools: {string.Join(", ", additionalNames)}");

            if (_consecutiveExpressFailures > 0 && _consecutiveExpressFailures <= agentConfig.BackoffSeconds.Length)
            {
                var backoff = agentConfig.BackoffSeconds[_consecutiveExpressFailures - 1];
                await Task.Delay(TimeSpan.FromSeconds(backoff));
            }

            var maxRounds = agentConfig.ExpressMaxRounds;
            for (int round = 0; round < maxRounds; round++)
            {
                ModelOutput output;
                var spanLabel = round == 0
                    ? $"Express模型调用 ch:{channelId}"
                    : $"Express模型调用 ch:{channelId} R{round + 1}";
                using (var modelSpan = Signal.Open(LogGroup.Model, spanLabel,
                    new
                    {
                        mode = "Express", channelId, round = round + 1,
                        messageCount = messages.Count,
                        messages = messages.Select(m => m.ContentParts != null
                            ? (object)new { m.Role, parts = m.ContentParts.Select(p => new { p.Type, p.Text, p.ToolName, p.ToolInput, p.ToolUseId, p.IsError }) }
                            : new { m.Role, content = m.Content })
                    }))
                {
                    output = default!;
                    Exception? lastEx = null;
                    bool success = false;
                    for (int attempt = 0; attempt < agentConfig.ModelCallMaxAttempts; attempt++)
                    {
                        try
                        {
                            if (attempt > 0)
                            {
                                var retryDelay = agentConfig.ModelCallRetryDelaySeconds[
                                    Math.Min(attempt - 1, agentConfig.ModelCallRetryDelaySeconds.Length - 1)];
                                Signal.Warn(LogGroup.Model, $"Express模型重试 ch:{channelId} R{round + 1} #{attempt + 1}",
                                    new { channelId, round = round + 1, attempt = attempt + 1, delaySeconds = retryDelay, lastError = lastEx?.Message });
                                await Task.Delay(TimeSpan.FromSeconds(retryDelay));
                            }
                            output = await agentCore.InvokeAsync(messages, EngineMode.Express);
                            success = true;
                            _consecutiveExpressFailures = 0;
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
                        catch (Exception ex) { lastEx = ex; }
                    }
                    if (!success)
                    {
                        _consecutiveExpressFailures++;
                        modelSpan.SetCloseDetail(new { error = lastEx!.GetType().Name, message = lastEx.Message, attempts = agentConfig.ModelCallMaxAttempts });
                        throw lastEx;
                    }
                }

                messages.Add(FormatAssistantExpress(output));

                if (!output.HasToolCalls || output.ToolCalls == null || output.ToolCalls.Count == 0)
                {
                    if (output.IsText)
                    {
                        Signal.Event(LogGroup.Engine, "Express文本已丢弃",
                            new { channelId, text = output.Text, reason = "模型未使用工具调用直接输出文本" });
                    }
                    break;
                }

                List<ToolResult> expressResults;
                using (var expressToolSpan = Signal.Open(LogGroup.Tool, $"Express工具 R{round + 1}: {string.Join(", ", output.ToolCalls.Select(c => c.Tool))}",
                    new { round = round + 1, calls = output.ToolCalls.Select(c => new { c.Tool, c.Inputs }) }))
                {
                    var executor = new ToolExecutor(name => componentHost?.TryGetTool(name) ?? ToolRegistry.Get(name))
                    { PermissionGroup = "express" };
                    expressResults = await executor.ExecuteAsync(output.ToolCalls);
                    expressToolSpan.SetCloseDetail(new
                    {
                        results = output.ToolCalls.Zip(expressResults, (c, r) => new
                        {
                            tool = c.Tool, status = r.Status, data = r.Data, error = r.Error
                        })
                    });
                }

                var inlineWaitRequested = false;
                var escalateTriggered = false;
                for (int i = 0; i < output.ToolCalls.Count; i++)
                {
                    var call = output.ToolCalls[i];
                    var result = expressResults[i];
                    if (result.RequestWait)
                        inlineWaitRequested = true;
                    var toolDef = componentHost?.TryGetTool(call.Tool) ?? ToolRegistry.Get(call.Tool);
                    bus.Publish(new ToolExecutedEvent(call, result, toolDef));
                }

                messages.Add(FormatResultsExpress(output.ToolCalls, expressResults));

                // escalate → 加载频道历史 + 通过 SessionEngine 创建 Working 会话
                for (int i = 0; i < output.ToolCalls.Count; i++)
                {
                    var call = output.ToolCalls[i];
                    if (call.Tool == "escalate")
                    {
                        Console.Error.WriteLine($"[Express ch:{channelId}] escalate 触发，创建 Working session...");
                        var reason = expressResults[i]?.Data ?? "";
                        var sessionEngine = ctx.GetActiveEnginesSnapshot()
                            .OfType<Session.SessionEngine>().FirstOrDefault();
                        if (sessionEngine != null)
                        {
                            // 频道占用 + 历史载入由 InitActions 的 connect_channel 在 session 启动时完成
                            sessionEngine.CreateSession(new CreateSessionSpec
                            {
                                Type = SessionType.Channel,
                                InitActions = new List<ToolCall>
                                {
                                    new() { Tool = "connect_channel", Inputs = new List<string> { channelId.ToString() } }
                                },
                                InitialState = $"用户请求切换到工作模式，原因：{reason}\n（保持在工作模式陪用户继续；只有确实不再需要工作模式时才用 end_session 退回轻量对话。需要等人回复时用 wait 挂起。）"
                            });
                            Console.Error.WriteLine($"[Express ch:{channelId}] Working session 已创建，频道已占用");
                            escalateTriggered = true;
                        }
                        else
                        {
                            Console.Error.WriteLine($"[Express ch:{channelId}] escalate 失败：SessionEngine 未找到");
                        }
                        break;
                    }
                }
                if (inlineWaitRequested)
                    break;

                if (output.ToolCalls.Any(c => c.Tool == "wait"))
                    break;

                if (escalateTriggered)
                    break;

                for (int i = 0; i < output.ToolCalls.Count; i++)
                {
                    var call = output.ToolCalls[i];
                    if (call.Tool == "refine_image")
                    {
                        var result = expressResults[i];
                        var data = result.Data ?? "";
                        var segs = data.Split('|', 2);
                        var imageRef = segs.Length > 0 ? segs[0] : "";
                        var focus = segs.Length > 1 ? segs[1] : "";
                        var hash = await ResolveImageRefAsync(imageRef);
                        if (hash != null)
                        {
                            ctx.EventBus.PublishSignal("refine-image",
                                new { hash, targetPhase = 3, focus, contextText = (string?)null });
                            Signal.Event(LogGroup.Engine, "Vision精炼请求(Express)",
                                new { channelId, hash, focus });
                        }
                    }
                }
            }

            impulseTracker.ApplyPostResponseUpdate();
            if (_lastSessionContext != null)
            {
                await IncrementDailyProgressAsync(_lastSessionContext.Person);
            }

            if (_startInjectMaxId > _lastConsumedMessageId)
                _lastConsumedMessageId = _startInjectMaxId;

            if (_lastConsumedMessageId > 0)
                persistence?.SaveContext(null, "express", new List<List<Message>>(),
                    _lastConsumedMessageId, null);
        }

        private static Message FormatAssistantExpress(ModelOutput output)
        {
            if (output.HasToolCalls && output.ToolCalls != null)
            {
                var parts = new List<ContentPart>();
                if (!string.IsNullOrEmpty(output.Thinking))
                    parts.Add(ContentPart.FromText(output.Thinking));
                foreach (var c in output.ToolCalls)
                {
                    if (c.ToolUseId != null)
                        parts.Add(ContentPart.FromToolUse(c.ToolUseId, c.Tool, c.RawInputJson ?? "{}"));
                }
                return new Message
                {
                    Role = "assistant",
                    Content = !string.IsNullOrEmpty(output.Thinking) ? output.Thinking : "[tool calls]",
                    ContentParts = parts
                };
            }
            return new Message { Role = "assistant", Content = output.Text ?? output.Thinking ?? "" };
        }

        private static Message FormatResultsExpress(List<ToolCall> calls, List<ToolResult> results)
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
    }
}
