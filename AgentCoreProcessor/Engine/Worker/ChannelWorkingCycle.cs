using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Logging;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;
using AgentCoreProcessor.Tool.Core;
using AgentCoreProcessor.Engine.Modules;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// Working 模式：Agent 多轮推理循环、Agent 生命周期管理、上下文持久化。
    /// </summary>
    partial class ChannelEngine
    {
        /// <summary>Working 模式：Agent 多轮循环。</summary>
        private async Task ExecuteWorkingCycleAsync()
        {
            _roundImageHashes.Clear();

            _currentModeDef = ModeConfigLoader.GetMode(_currentModeId);
            if (_currentModeDef != null)
                agentConfig.MaxRounds = _currentModeDef.MaxRounds;

            EnsureAgent();

            // Lazy register compress tool
            if (compressionTierModule == null)
            {
                compressionTierModule = new CompressionTierModule(agentConfig,
                    () => agent?.History ?? new List<Message>(),
                    () =>
                    {
                        compressionTierModule!.CompressL3Async(
                            agent?.History ?? new List<Message>(),
                            (summary, retained) =>
                            {
                                contextSummary = summary;
                                agent?.ClearHistory();
                                agent?.AddToHistory(new Message { Role = "user", Content = fixedPrefix! });
                                if (!string.IsNullOrEmpty(summary))
                                    agent?.AddToHistory(new Message { Role = "user", Content = $"[上下文摘要]\n{summary}" });
                                if (agent != null)
                                    agent.ConversationOffset = agent.History.Count;
                                foreach (var m in retained) agent?.AddToHistory(m);
                                PersistCurrentContext();
                            }).GetAwaiter().GetResult();
                    });
                ToolRegistry.Register(new CompressTool(
                    compressionTierModule,
                    () => agent?.History ?? new List<Message>(),
                    (summary, retained) =>
                    {
                        // 暂存压缩结果，等 Agent.RunAsync 返回后再应用，
                        // 避免在工具回调中 ClearHistory 导致当前这轮的 tool_use 丢失
                        _pendingCompressionSummary = string.IsNullOrEmpty(summary) ? null : summary;
                        _pendingCompressionRetained = retained;
                        _pendingCompressionApply = true;
                    }), isNonComponent: true);
            }

            agentCore.EngineType = "channel";
            agentCore.CurrentModeId = _currentModeId;
            agentCore.AdditionalTools = componentHost!.GetVisibleTools().ToList();
            agentCore.GlobalComponentTools = ctx.GlobalComponentHost?.GetVisibleTools("channel").ToList();

            // Working 积压检查：游标后有大量未消费消息时，不进 Agent，直接回退 Express
            if (_lastConsumedMessageId > 0)
            {
                var checkMsgs = await ctx.Session.GetMessagesAfterIdAsync(channelId, _lastConsumedMessageId, 31);
                if (checkMsgs.Count > 30)
                {
                    _lastConsumedMessageId = checkMsgs.Max(m => m.Id);
                    Signal.Event(LogGroup.Engine, "自动回退",
                        new { channelId, from = "Working", to = "Express", pendingCount = checkMsgs.Count });
                    isWorkingMode = false;
                    _currentModeId = "express";
                    _currentModeDef = null;
                    persistence?.SaveContext(null, "express", new List<List<Message>>(),
                        _lastConsumedMessageId, null);
                    EndWorkingSession();
                    gate.Signal();
                    return;
                }
            }

            await agent!.RunAsync(CancellationToken.None);

            if (agent.StopReason == AgentStopReason.Error)
                throw new InvalidOperationException("Agent 连续模型调用失败");

            // 追踪连续输出轮次：无实际工作则累加，有工作则清零
            if (hadWorkThisRound)
                loopControlModule.ConsecutiveOutputOnly = 0;
            else
                loopControlModule.ConsecutiveOutputOnly++;

            // 推进游标到 StartInject 已消费的最大 ID
            if (_startInjectMaxId > _lastConsumedMessageId)
                _lastConsumedMessageId = _startInjectMaxId;

            // Persist after agent finishes
            PersistCurrentContext();

            // Post-processing
            impulseTracker.ApplyPostResponseUpdate();
            if (_lastSessionContext != null)
            {
                TrackMemoryExtraction(_lastSessionContext);
                await IncrementDailyProgressAsync(_lastSessionContext.Person);
            }

            // Handle agent stop reason
            if (agent.StopReason == AgentStopReason.WaitRequested)
            {
                EndWorkingSession();
            }
            else if (agent.StopReason == AgentStopReason.Deescalated)
            {
                var reason = agent.LastRoundCalls?
                    .FirstOrDefault(c => c.Tool == "deescalate")?.Inputs.FirstOrDefault();
                Signal.Event(LogGroup.Engine, "模式切换",
                    new { channelId, from = "Working", to = "Express", reason = reason ?? "工具调用" });
                isWorkingMode = false;
                _currentModeId = "express";
                _currentModeDef = null;

                // 清空 Working 上下文但保留游标
                persistence?.SaveContext(null, "express", new List<List<Message>>(),
                    _lastConsumedMessageId, null);

                EndWorkingSession();

                // 不主动唤醒：让 Express 等待真正的消息到来，避免连续两次空转 Express
            }
            else if (agent.StopReason == AgentStopReason.ModeSwitched)
            {
                // Working 子模式横向切换：保留上下文，只改工具列表
                // 数据格式：target|reason|asked_user_message|user_confirm_message_id
                var switchCall = agent.LastRoundCalls!.First(c => c.Tool == "switch_mode");
                var data = switchCall.Inputs.Count > 0 ? switchCall.Inputs[0] : "";
                var parts = data.Split('|', 4);
                var targetId = parts[0].Trim();
                var reason = parts.Length > 1 ? parts[1].Trim() : null;
                var askedMsg = parts.Length > 2 ? parts[2].Trim() : null;
                var confirmMsgId = parts.Length > 3 ? parts[3].Trim() : null;

                var targetDef = ModeConfigLoader.GetMode(targetId);
                if (targetDef != null && targetDef.MetaType == "Working")
                {
                    var oldModeId = _currentModeId;
                    _currentModeId = targetId;
                    _currentModeDef = targetDef;
                    agentConfig.MaxRounds = targetDef.MaxRounds;

                    // 注入模式切换提示
                    var confirmInfo = !string.IsNullOrEmpty(confirmMsgId)
                        ? $" 确认消息ID：{confirmMsgId}" : "";
                    agent!.AddToHistory(new Message { Role = "user",
                        Content = $"[模式切换] {oldModeId} → {targetId}。" +
                                  (!string.IsNullOrEmpty(reason) ? $" 原因：{reason}。" : "") +
                                  confirmInfo });

                    // 持久化当前上下文（含模式切换消息）
                    PersistCurrentContext();

                    Signal.Event(LogGroup.Engine, "子模式切换",
                        new { channelId, from = oldModeId, to = targetId, reason = reason ?? "",
                            askedUserMessage = askedMsg ?? "", confirmMessageId = confirmMsgId ?? "" });

                    // 强制重建 agent（下一轮用新模式工具列表），但保留历史轮次和上下文
                    agent = null;
                    isInWorkingSession = false;
                    gate.Signal();
                }
            }
            else if (agent.StopReason == AgentStopReason.MaxRounds)
            {
                loopControlModule.AdvanceRound();
                EndWorkingSession();
            }
            else
            {
                EndWorkingSession();
            }
        }

        private void EnsureAgent()
        {
            if (agent != null) return;

            // 新 Working 会话：清空 Express 遗留的图片去重状态，
            // 防止 BuildInterleavedContentParts 误将 Express 已见过的图片标记为重复
            _seenImageHashes.Clear();
            _injectedDescriptions.Clear();
            _injectedOcrTexts.Clear();

            fixedPrefix = BuildFixedPrefix();

            agent = new Agent(this, agentCore, agentConfig, componentHost!.TryGetTool);
            agent.OnToolExecuted = async (call, result, toolDef) =>
            {
                bus.Publish(new ToolExecutedEvent(call, result, toolDef));

                if (call.Tool == "refine_image")
                {
                    var data = result.Data ?? "";
                    var segs = data.Split('|', 2);
                    var imageRef = segs.Length > 0 ? segs[0] : "";
                    var focus = segs.Length > 1 ? segs[1] : "";
                    var hash = await ResolveImageRefAsync(imageRef);
                    if (hash != null)
                    {
                        var contextText = BuildSimpleVisionContext();
                        ctx.EventBus.PublishSignal("refine-image",
                            new { hash, targetPhase = 3, focus, contextText });
                        Signal.Event(LogGroup.Engine, "Vision精炼请求(Working)",
                            new { channelId, hash, focus });
                    }
                }
            };
            agent.OnRoundCompleted = () =>
            {
                // 延迟应用 compress 工具的压缩效果：
                // compress 工具的回调仅暂存摘要和保留列表，等本轮 tool_result 已加入历史后再执行，
                // 避免 tool_use/tool_result 配对断裂导致 Claude API 报 400
                if (_pendingCompressionApply)
                {
                    _pendingCompressionApply = false;
                    if (agent != null)
                    {
                        contextSummary = _pendingCompressionSummary;
                        agent.ClearHistory();
                        agent.AddToHistory(new Message { Role = "user", Content = fixedPrefix! });
                        if (!string.IsNullOrEmpty(contextSummary))
                            agent.AddToHistory(new Message { Role = "user", Content = $"[上下文摘要]\n{contextSummary}" });
                        agent.ConversationOffset = agent.History.Count;
                        if (_pendingCompressionRetained != null)
                            foreach (var m in _pendingCompressionRetained)
                                agent.AddToHistory(m);
                        _pendingCompressionSummary = null;
                        _pendingCompressionRetained = null;
                    }
                }

                PersistCurrentContext();
                loopControlModule.TrackSilentRound(hadSpeakThisRound);

                // 追踪连续 speak-only 轮次（仅有 speak，无实际工作）
                if (hadSpeakThisRound && !hadWorkThisRound)
                    _consecutiveSpeakRounds++;
                else
                    _consecutiveSpeakRounds = 0;

                if (_speakGuard != null)
                    _speakGuard.ConsecutiveSpeakRounds = _consecutiveSpeakRounds;

                hadSpeakThisRound = false;
                hadWorkThisRound = false;
                return Task.CompletedTask;
            };

            // Restore persisted context (loaded into _loadedConversation for BuildStartInjectAsync)
            if (persistence != null && _loadedConversation == null)
            {
                var (summary, mode, rounds, cursor, reason) = persistence.LoadContext();
                if (!string.IsNullOrEmpty(summary) && string.IsNullOrEmpty(contextSummary))
                    contextSummary = summary;
                if (cursor > _lastConsumedMessageId)
                    _lastConsumedMessageId = cursor;
                if (!string.IsNullOrEmpty(reason) && string.IsNullOrEmpty(_escalateReason))
                    _escalateReason = reason;
                if (rounds.Count > 0)
                {
                    _loadedConversation = new List<Message>();
                    foreach (var round in rounds)
                        foreach (var msg in round)
                            if (!IsEmptyMessage(msg))
                                _loadedConversation.Add(msg);
                }
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
    }
}
