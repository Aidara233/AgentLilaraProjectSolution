using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Core;
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
    partial class ChannelEngine
    {
        /// <summary>Express 微循环：多轮模型调用（最多 ExpressMaxRounds 轮），工具结果回注供下一轮参考。</summary>
        private async Task ExecuteExpressCycleAsync()
        {
            _roundImageHashes.Clear();
            _seenImageHashes.Clear();
            _startInjectMaxId = 0;

            var messages = new List<Message>();

            var startInject = await ((IAgentHost)this).BuildStartInjectAsync();
            if (startInject != null) messages.AddRange(startInject);

            var roundInject = await ((IAgentHost)this).BuildRoundInjectAsync();
            if (roundInject != null) messages.AddRange(roundInject);

            if (messages.Count > HistoryMaxMessages)
            {
                var excess = messages.Count - HistoryMaxMessages;
                var maxRemovable = messages.Count - _frameworkMessageCount;
                if (excess > maxRemovable) excess = maxRemovable;
                if (excess > 0)
                    messages.RemoveRange(_frameworkMessageCount, excess);
            }

            agentCore.EngineType = "channel";
            agentCore.CurrentModeId = _currentModeId;
            agentCore.AdditionalTools = componentHost!.GetVisibleTools().ToList();
            agentCore.GlobalComponentTools = ctx.GlobalComponentHost?.GetVisibleTools("channel").ToList();

            if (_consecutiveExpressFailures > 0 && _consecutiveExpressFailures <= agentConfig.BackoffSeconds.Length)
            {
                var backoff = agentConfig.BackoffSeconds[_consecutiveExpressFailures - 1];
                await Task.Delay(TimeSpan.FromSeconds(backoff));
            }

            bool escalated = false;
            _currentModeDef ??= ModeConfigLoader.GetMode("express");
            var maxRounds = _currentModeDef?.MaxRounds ?? agentConfig.ExpressMaxRounds;
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
                    var executor = new ToolExecutor(componentHost.TryGetTool);
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
                for (int i = 0; i < output.ToolCalls.Count; i++)
                {
                    var call = output.ToolCalls[i];
                    var result = expressResults[i];
                    if (result.RequestWait)
                        inlineWaitRequested = true;
                    var toolDef = componentHost.TryGetTool(call.Tool);
                    bus.Publish(new ToolExecutedEvent(call, result, toolDef));
                }

                messages.Add(FormatResultsExpress(output.ToolCalls, expressResults));

                foreach (var call in output.ToolCalls)
                {
                    if (call.Tool == "escalate")
                    {
                        var reason = call.Inputs.Count > 0 ? call.Inputs[0] : null;
                        var targetModeId = ModeConfigLoader.GetEscalateTarget();
                        var targetDef = ModeConfigLoader.GetMode(targetModeId);
                        _signalBuffer.Enqueue(new ModeSwitchSignal(targetModeId, reason));
                        isWorkingMode = true;
                        _currentModeId = targetModeId;
                        _currentModeDef = targetDef;
                        isInWorkingSession = true;
                        _escalateReason = reason;
                        persistence?.SaveContext(null, targetModeId, new List<List<Message>>(),
                            _lastConsumedMessageId, reason);
                        Signal.Event(LogGroup.Engine, "模式切换",
                            new { channelId, from = "Express", to = targetModeId, reason = reason ?? "工具调用" });
                        gate.Signal();
                        escalated = true;
                        break;
                    }
                }
                if (escalated) break;

                if (inlineWaitRequested)
                    break;

                if (output.ToolCalls.Any(c => c.Tool == "wait"))
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
                TrackMemoryExtraction(_lastSessionContext);
                await IncrementDailyProgressAsync(_lastSessionContext.Person);
            }

            if (_startInjectMaxId > _lastConsumedMessageId)
                _lastConsumedMessageId = _startInjectMaxId;

            if (!escalated && _lastConsumedMessageId > 0)
                persistence?.SaveContext(contextSummary, _currentModeId, new List<List<Message>>(),
                    _lastConsumedMessageId, _escalateReason);
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
