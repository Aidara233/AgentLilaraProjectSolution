using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 循环退出原因。
    /// </summary>
    internal enum LoopExitReason
    {
        Idle,
        NoToolCalls,
        MaxRounds,
    }

    /// <summary>
    /// 工作核心。事件驱动 Agent 循环——ContinueLoop 工具触发下一轮，否则自然 idle。
    /// </summary>
    internal class WorkingCore : CoreBase
    {
        private const int MaxRounds = 15;

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
        private const string PinboardToolName = "便签板";
        private const string RetainListToolName = "缓存管理";

        private readonly PromptBuilder promptBuilder = new();

        // 回调
        public Func<string, Task>? OnSpeak { get; set; }
        public Func<string, Task>? OnMemory { get; set; }
        public Func<string, string?, Task>? OnSignal { get; set; }
        public Func<string, Task>? OnReviewHint { get; set; }
        public Func<string, Task>? OnAlert { get; set; }
        public Func<string, PermissionLevel, Task<bool>>? OnAuthRequired { get; set; }

        // 跨轮状态
        private readonly List<(string Description, bool Done)> taskList = new();
        private readonly List<(string Summary, string FullContent)> retainList = new();
        private readonly HashSet<string> authorizedTools = new();

        // 便签板（由 WorkerEngine 注入，Express/Working 共享）
        private Dictionary<string, string>? pinboard;

        // 消息通道
        private ConcurrentQueue<IncomingMessage>? messageQueue;
        private SemaphoreSlim? messageSignal;

        public void SetMessageChannel(ConcurrentQueue<IncomingMessage> queue, SemaphoreSlim signal)
        {
            this.messageQueue = queue;
            this.messageSignal = signal;
        }

        public void SetPinboard(Dictionary<string, string> pinboard)
        {
            this.pinboard = pinboard;
        }

        /// <summary>
        /// 事件驱动 Agent 循环。ContinueLoop 工具触发下一轮，否则自然 idle。
        /// </summary>
        public async Task<LoopExitReason> ProcessAsync(string userRequest,
            string? memoryContext = null, List<string>? imagePaths = null)
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
                    taskList: taskList.Count > 0 ? taskList : null,
                    pinboard: pinboard?.Count > 0 ? pinboard : null,
                    retainList: retainList.Count > 0 ? retainList : null);

                processor.Client.ClearConversationHistory();
                processor.Client.SetConversationHistory(messages);
                pendingNewMessages.Clear();

                var toolCalls = await ParseToolCallsAsync();

                if (toolCalls.Count == 0)
                    return round == 0 ? LoopExitReason.NoToolCalls : LoopExitReason.Idle;

                // 顺序执行（说话副作用在每个工具执行后立即处理，不被后续授权阻塞）
                var executor = new ToolExecutor(authorizedTools: authorizedTools);
                executor.OnAuthRequired = OnAuthRequired;
                executor.OnToolExecuted = async (call, result) =>
                {
                    if (call.Tool == SpeakToolName && result.IsSuccess && OnSpeak != null)
                        await OnSpeak(result.Data ?? "");
                };
                var allResults = await executor.ExecuteAsync(toolCalls);

                // 副作用：其余
                bool hasContinue = false;
                for (int i = 0; i < toolCalls.Count; i++)
                {
                    var call = toolCalls[i];
                    var result = allResults[i];
                    var toolDef = ToolRegistry.Get(call.Tool);

                    // ContinueLoop 不论成功失败都要检查（失败时模型也需要看到错误）
                    if (toolDef?.ContinueLoop == true)
                        hasContinue = true;

                    if (!result.IsSuccess && call.Tool != SpeakToolName)
                    {
                        // 自动收集 RetainResult（即使失败也记录错误信息）
                        continue;
                    }

                    switch (call.Tool)
                    {
                        case ThinkingNotesToolName:
                            ApplyThinkingNotes(call, thinkingNotes); break;
                        case SpeakToolName: break;
                        case MemoryToolName:
                            if (OnMemory != null) await OnMemory(result.Data ?? ""); break;
                        case DreamPermissionToolName:
                            if (OnSignal != null) await OnSignal("dream-permission", null); break;
                        case ForceSleepToolName:
                            if (OnSignal != null) await OnSignal("force-sleep", null); break;
                        case DreamConfigToolName:
                            if (OnSignal != null) await OnSignal("dream-config", result.Data); break;
                        case SleepScoreToolName:
                            if (OnSignal != null) await OnSignal("sleep-score-offset", result.Data); break;
                        case RedAlertToolName:
                            if (OnSignal != null) await OnSignal("red-alert", null); break;
                        case ReviewHintToolName:
                            if (OnReviewHint != null) await OnReviewHint(result.Data ?? ""); break;
                        case AlertButtonToolName:
                            if (OnAlert != null) await OnAlert(result.Data ?? ""); break;
                        case TaskToolName:
                            ApplyTaskAction(result.Data ?? ""); break;
                        case PinboardToolName:
                            ApplyPinboardAction(result.Data ?? ""); break;
                        case RetainListToolName:
                            ApplyRetainAction(call, result); break;
                    }

                    // 自动收集 RetainResult
                    if (toolDef?.RetainResult == true && result.IsSuccess)
                    {
                        var summary = $"{call.Tool}: {string.Join(", ", call.Inputs).Truncate(50)}";
                        retainList.Add((summary, result.Data ?? ""));
                    }
                }

                lastRoundCalls = toolCalls;
                lastRoundResults = allResults;

                if (!hasContinue)
                    return LoopExitReason.Idle;

                await WaitForEventsAsync();
            }

            return LoopExitReason.MaxRounds;
        }

        // ---- 任务列表 ----

        private void ApplyTaskAction(string data)
        {
            var sep = data.IndexOf(':');
            if (sep < 0) return;
            var action = data[..sep];
            var content = data[(sep + 1)..];

            switch (action)
            {
                case "add":
                    taskList.Add((content, false)); break;
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

        // ---- 便签板 ----

        private void ApplyPinboardAction(string data)
        {
            if (pinboard == null) return;
            if (data.StartsWith("pin:"))
            {
                var rest = data[4..];
                var sep = rest.IndexOf(':');
                if (sep > 0)
                {
                    var label = rest[..sep];
                    var content = rest[(sep + 1)..];
                    pinboard[label] = content;
                }
            }
            else if (data.StartsWith("unpin:"))
            {
                var label = data[6..];
                pinboard.Remove(label);
            }
        }

        // ---- Retain 列表 ----

        private void ApplyRetainAction(ToolCall call, ToolResult result)
        {
            if (!result.IsSuccess) return;
            var data = result.Data ?? "";

            if (data.StartsWith("view:"))
            {
                if (int.TryParse(data[5..], out var idx) && idx >= 1 && idx <= retainList.Count)
                    result.Data = retainList[idx - 1].FullContent;
                else
                    result.Data = "序号超出范围";
            }
            else if (data.StartsWith("remove:"))
            {
                if (int.TryParse(data[7..], out var idx) && idx >= 1 && idx <= retainList.Count)
                {
                    retainList.RemoveAt(idx - 1);
                    result.Data = "已移除";
                }
            }
            else if (data == "clear")
            {
                retainList.Clear();
                result.Data = "已清空";
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

    internal static class StringExtensions
    {
        public static string Truncate(this string s, int maxLen)
            => s.Length <= maxLen ? s : s[..maxLen] + "...";
    }
}
