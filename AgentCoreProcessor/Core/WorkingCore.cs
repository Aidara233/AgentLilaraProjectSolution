using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 工作核心。Agent 循环的中心——通过多轮工具调用完成任务。
    /// 支持运行时新消息感知。Phase 3 将重写为事件驱动 idle 模型。
    /// </summary>
    internal class WorkingCore : CoreBase
    {
        private const int MaxRounds = 15;

        // 特殊工具名称常量
        private const string ThinkingNotesToolName = "思考笔记";
        private const string SpeakToolName = "说话";
        private const string MemoryToolName = "记忆";
        private const string DreamPermissionToolName = "睡眠许可";
        private const string ForceSleepToolName = "强制睡觉";
        private const string DreamConfigToolName = "修改睡眠配置";
        private const string SleepScoreToolName = "调整睡意";
        private const string RedAlertToolName = "触发红色警报";
        private const string ReviewHintToolName = "标记复盘";
        private const string TaskToolName = "任务管理";
        private const string AlertButtonToolName = "报警";
        private const string ContinueToolName = "继续";

        private readonly PromptBuilder promptBuilder = new();

        // 回调
        public Func<string, Task>? OnSpeak { get; set; }
        public Func<string, Task>? OnMemory { get; set; }
        public Func<string, string?, Task>? OnSignal { get; set; }
        public Func<string, Task>? OnReviewHint { get; set; }
        public Func<string, Task>? OnAlert { get; set; }

        // 任务列表（跨轮保持）
        private readonly List<(string Description, bool Done)> taskList = new();

        // 运行时工具授权
        private readonly HashSet<string> authorizedTools = new();

        // 消息通道（由 WorkerEngine 注入）
        private ConcurrentQueue<IncomingMessage>? messageQueue;
        private SemaphoreSlim? messageSignal;

        /// <summary>注入消息通道，启用运行时消息感知。</summary>
        public void SetMessageChannel(ConcurrentQueue<IncomingMessage> queue, SemaphoreSlim signal)
        {
            this.messageQueue = queue;
            this.messageSignal = signal;
        }

        /// <summary>
        /// Agent 循环。Phase 3 将重写为事件驱动 idle 模型。
        /// </summary>
        public async Task<string> ProcessAsync(string userRequest, string? memoryContext = null,
            List<string>? imagePaths = null)
        {
            var thinkingNotes = new Dictionary<string, string>();
            List<ToolCall>? lastRoundCalls = null;
            List<ToolResult>? lastRoundResults = null;
            var pendingNewMessages = new List<string>();

            for (int round = 0; round < MaxRounds; round++)
            {
                var toolDescriptions = ToolRegistry.GenerateDescriptions(authorizedTools: authorizedTools);

                DrainNewMessages(pendingNewMessages);

                var messages = promptBuilder.BuildRoundMessages(
                    toolDescriptions, userRequest, thinkingNotes,
                    lastRoundResults, lastRoundCalls,
                    memoryContext,
                    imagePaths: round == 0 ? imagePaths : null,
                    newMessages: pendingNewMessages.Count > 0 ? pendingNewMessages : null,
                    taskList: taskList.Count > 0 ? taskList : null);

                processor.Client.ClearConversationHistory();
                processor.Client.SetConversationHistory(messages);

                pendingNewMessages.Clear();

                var toolCalls = await ParseToolCallsAsync();

                if (toolCalls.Count == 0)
                {
                    if (round == 0) return "[Agent] 未能生成有效的工具调用计划。";
                    break;
                }

                // 顺序执行
                var executor = new ToolExecutor(authorizedTools: authorizedTools);
                var allResults = await executor.ExecuteAsync(toolCalls);

                // 处理副作用：先 Speak
                for (int i = 0; i < toolCalls.Count; i++)
                {
                    if (toolCalls[i].Tool == SpeakToolName && allResults[i].IsSuccess && OnSpeak != null)
                        await OnSpeak(allResults[i].Data ?? "");
                }

                // 处理其余副作用
                for (int i = 0; i < toolCalls.Count; i++)
                {
                    var call = toolCalls[i];
                    var result = allResults[i];

                    switch (call.Tool)
                    {
                        case ThinkingNotesToolName:
                            if (result.IsSuccess) ApplyThinkingNotes(call, thinkingNotes);
                            break;
                        case SpeakToolName:
                            break; // 已处理
                        case MemoryToolName:
                            if (result.IsSuccess && OnMemory != null) await OnMemory(result.Data ?? "");
                            break;
                        case DreamPermissionToolName:
                            if (result.IsSuccess && OnSignal != null) await OnSignal("dream-permission", null);
                            break;
                        case ForceSleepToolName:
                            if (result.IsSuccess && OnSignal != null) await OnSignal("force-sleep", null);
                            break;
                        case DreamConfigToolName:
                            if (result.IsSuccess && OnSignal != null) await OnSignal("dream-config", result.Data);
                            break;
                        case SleepScoreToolName:
                            if (result.IsSuccess && OnSignal != null) await OnSignal("sleep-score-offset", result.Data);
                            break;
                        case RedAlertToolName:
                            if (result.IsSuccess && OnSignal != null) await OnSignal("red-alert", null);
                            break;
                        case ReviewHintToolName:
                            if (result.IsSuccess && OnReviewHint != null) await OnReviewHint(result.Data ?? "");
                            break;
                        case AlertButtonToolName:
                            if (result.IsSuccess && OnAlert != null) await OnAlert(result.Data ?? "");
                            break;
                        case TaskToolName:
                            if (result.IsSuccess) ApplyTaskAction(result.Data ?? "");
                            break;
                    }
                }

                // 判断是否继续：有 ContinueLoop 工具被调用 → 下一轮
                bool hasContinue = false;
                for (int i = 0; i < toolCalls.Count; i++)
                {
                    var tool = ToolRegistry.Get(toolCalls[i].Tool);
                    if (tool?.ContinueLoop == true)
                    {
                        hasContinue = true;
                        break;
                    }
                }

                lastRoundCalls = toolCalls;
                lastRoundResults = allResults;

                if (!hasContinue)
                    break;

                await WaitForEventsAsync();
            }

            return "idle";
        }

        // ---- 任务列表管理 ----

        private void ApplyTaskAction(string data)
        {
            var sep = data.IndexOf(':');
            if (sep < 0) return;
            var action = data[..sep];
            var content = data[(sep + 1)..];

            switch (action)
            {
                case "add":
                    taskList.Add((content, false));
                    break;
                case "complete":
                    if (int.TryParse(content, out var ci) && ci >= 1 && ci <= taskList.Count)
                        taskList[ci - 1] = (taskList[ci - 1].Description, true);
                    break;
                case "remove":
                    if (int.TryParse(content, out var ri) && ri >= 1 && ri <= taskList.Count)
                        taskList.RemoveAt(ri - 1);
                    break;
            }
        }

        // ---- 消息感知 ----

        private void DrainNewMessages(List<string> buffer)
        {
            if (messageQueue == null) return;
            while (messageQueue.TryDequeue(out var msg))
            {
                var sender = msg.DisplayName ?? msg.PlatformUserId;
                buffer.Add($"{sender}: {msg.Content}");
            }
        }

        private async Task WaitForEventsAsync()
        {
            if (messageSignal == null) return;
            await messageSignal.WaitAsync(TimeSpan.FromSeconds(30));
        }

        // ---- 工具调用解析 ----

        private async Task<List<ToolCall>> ParseToolCallsAsync()
        {
            var toolCalls = new List<ToolCall>();
            await GenerateAsync(onBreak: (block) =>
            {
                var json = block.Content.Trim();
                if (string.IsNullOrEmpty(json)) return;
                try
                {
                    var call = ToolCall.FromJson(json);
                    if (!call.Validate().Any())
                        toolCalls.Add(call);
                }
                catch { }
            });
            return toolCalls;
        }

        private static void ApplyThinkingNotes(ToolCall call, Dictionary<string, string> notes)
        {
            if (call.Inputs.Count < 2) return;
            var action = call.Inputs[0]?.Trim().ToLower();
            var key = call.Inputs[1] ?? "";
            if (action == "write" && call.Inputs.Count >= 3)
                notes[key] = call.Inputs[2] ?? "";
            else if (action == "delete")
                notes.Remove(key);
        }
    }
}
