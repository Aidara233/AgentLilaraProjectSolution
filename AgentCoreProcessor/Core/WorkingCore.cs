using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 工作核心。Agent 循环的中心——通过多轮工具调用完成任务。
    /// 模型通过工具调用控制框架行为：说话、思考、完成都是工具。
    /// </summary>
    internal class WorkingCore : CoreBase
    {
        /// <summary>安全上限，防止死循环。</summary>
        private const int MaxRounds = 10;

        // 特殊工具名称常量
        private const string CompletionToolName = "完成";
        private const string ThinkingNotesToolName = "思考笔记";
        private const string SpeakToolName = "说话";
        private const string MemoryToolName = "记忆";
        private const string DreamPermissionToolName = "睡眠许可";
        private const string ForceSleepToolName = "强制睡觉";
        private const string DreamConfigToolName = "修改睡眠配置";
        private const string SleepScoreToolName = "调整睡意";
        private const string RedAlertToolName = "触发红色警报";
        private const string ReviewHintToolName = "标记复盘";

        private readonly PromptBuilder promptBuilder = new();

        /// <summary>
        /// 说话回调。由 WorkerEngine 在调用 ProcessAsync 前设置。
        /// 签名：async (rawText) => { ExpressCore 润色 + Adapter 推送 }
        /// </summary>
        public Func<string, Task>? OnSpeak { get; set; }

        /// <summary>
        /// 记忆回调。由 WorkerEngine 在调用 ProcessAsync 前设置。
        /// 签名：async (content) => { MemoryService.StoreAsync }
        /// </summary>
        public Func<string, Task>? OnMemory { get; set; }

        /// <summary>
        /// 信号回调。由 WorkerEngine 在调用 ProcessAsync 前设置。
        /// 签名：async (signalName, payload) => { EventBus.PublishSignal }
        /// </summary>
        public Func<string, string?, Task>? OnSignal { get; set; }

        /// <summary>
        /// 复盘标记回调。由 WorkerEngine 在调用 ProcessAsync 前设置。
        /// 签名：async (content) => { ReviewHintRepository.CreateAsync }
        /// </summary>
        public Func<string, Task>? OnReviewHint { get; set; }

        /// <summary>
        /// 多轮 Agent 循环。模型反复调用工具直到调用"完成"工具。
        /// 返回完成摘要；若超限或异常则返回错误信息。
        /// </summary>
        public async Task<string> ProcessAsync(string userRequest, string? memoryContext = null)
        {
            // === 跨轮持久状态 ===
            var register = new Dictionary<string, string>();         // 寄存器：toolId → 输出数据
            var thinkingNotes = new Dictionary<string, string>();    // 思考笔记：key → value
            var retainedResults = new List<(ToolCall call, ToolResult result)>();  // retain=true 的历史结果

            // === 滚动状态（仅最近一轮）===
            List<ToolCall>? lastRoundCalls = null;
            List<ToolResult>? lastRoundResults = null;

            // 工具描述在循环期间不变（ToolRegistry 是静态的）
            var toolDescriptions = ToolRegistry.GenerateDescriptions();

            for (int round = 0; round < MaxRounds; round++)
            {
                // ── 1. 组装本轮提示词 ──
                var messages = promptBuilder.BuildRoundMessages(
                    toolDescriptions,
                    userRequest,
                    thinkingNotes,
                    lastRoundResults,
                    lastRoundCalls,
                    retainedResults,
                    memoryContext
                );
                processor.Client.ClearConversationHistory();
                processor.Client.SetConversationHistory(messages);

                // ── 2. 调用模型，通过 <over> break 解析工具调用 ──
                var toolCalls = await ParseToolCallsAsync();

                // ── 3. 空输出检查 ──
                if (toolCalls.Count == 0)
                {
                    // 首轮空输出 → 模型无法理解任务
                    if (round == 0)
                        return "[Agent] 未能生成有效的工具调用计划。";
                    // 后续轮空输出 → 视为隐式完成
                    break;
                }

                // ── 4. DAG 执行（寄存器跨轮共享）──
                var executor = new ToolExecutor(register);
                var results = await executor.ExecuteAsync(toolCalls);

                // ── 5. 处理特殊工具的副作用 ──
                bool shouldExit = false;
                string? completionSummary = null;

                for (int i = 0; i < toolCalls.Count; i++)
                {
                    var call = toolCalls[i];
                    var result = results[i];

                    switch (call.Tool)
                    {
                        case CompletionToolName:
                            if (result.IsSuccess)
                            {
                                shouldExit = true;
                                completionSummary = result.Data;
                            }
                            break;

                        case ThinkingNotesToolName:
                            if (result.IsSuccess)
                                ApplyThinkingNotes(call, thinkingNotes);
                            break;

                        case SpeakToolName:
                            if (result.IsSuccess && OnSpeak != null)
                                await OnSpeak(result.Data ?? "");
                            break;

                        case MemoryToolName:
                            if (result.IsSuccess && OnMemory != null)
                                await OnMemory(result.Data ?? "");
                            break;

                        case DreamPermissionToolName:
                            if (result.IsSuccess && OnSignal != null)
                                await OnSignal("dream-permission", null);
                            break;

                        case ForceSleepToolName:
                            if (result.IsSuccess && OnSignal != null)
                                await OnSignal("force-sleep", null);
                            break;

                        case DreamConfigToolName:
                            if (result.IsSuccess && OnSignal != null)
                                await OnSignal("dream-config", result.Data);
                            break;

                        case SleepScoreToolName:
                            if (result.IsSuccess && OnSignal != null)
                                await OnSignal("sleep-score-offset", result.Data);
                            break;

                        case RedAlertToolName:
                            if (result.IsSuccess && OnSignal != null)
                                await OnSignal("red-alert", null);
                            break;

                        case ReviewHintToolName:
                            if (result.IsSuccess && OnReviewHint != null)
                                await OnReviewHint(result.Data ?? "");
                            break;
                    }
                }

                // ── 6. 完成工具被调用 → 退出循环 ──
                if (shouldExit)
                    return completionSummary ?? "任务完成";

                // ── 7. 收集本轮 retain=true 的结果 ──
                for (int i = 0; i < toolCalls.Count; i++)
                {
                    if (toolCalls[i].Retain && results[i].IsSuccess)
                        retainedResults.Add((toolCalls[i], results[i]));
                }

                // ── 8. 更新滚动状态，进入下一轮 ──
                lastRoundCalls = toolCalls;
                lastRoundResults = results;
            }

            return "[Agent] 已达到最大执行轮次限制，任务未完成。";
        }

        /// <summary>
        /// 调用 GenerateAsync，通过 &lt;over&gt; break 逐个解析模型输出的 ToolCall JSON。
        /// 注意：不再自行 AddUserMessage——消息已由 Agent 循环通过 PromptBuilder 设置。
        /// </summary>
        private async Task<List<ToolCall>> ParseToolCallsAsync()
        {
            var toolCalls = new List<ToolCall>();

            await GenerateAsync(onBreak: (block) =>
            {
                var json = block.Content.Trim();
                if (string.IsNullOrEmpty(json))
                    return;

                try
                {
                    var call = ToolCall.FromJson(json);
                    var errors = call.Validate().ToList();
                    if (errors.Count == 0)
                        toolCalls.Add(call);
                }
                catch
                {
                    // 模型输出的 JSON 畸形，跳过该块
                }
            });

            return toolCalls;
        }

        /// <summary>
        /// 根据思考笔记工具的输入，更新笔记字典。
        /// 思考笔记的输入始终是 value 类型（不会用 ref），直接读 Inputs[i].Value。
        /// </summary>
        private static void ApplyThinkingNotes(ToolCall call, Dictionary<string, string> notes)
        {
            if (call.Inputs.Count < 2) return;

            var action = call.Inputs[0].Value?.Trim().ToLower();
            var key = call.Inputs[1].Value ?? "";

            if (action == "write" && call.Inputs.Count >= 3)
            {
                notes[key] = call.Inputs[2].Value ?? "";
            }
            else if (action == "delete")
            {
                notes.Remove(key);
            }
        }
    }
}
