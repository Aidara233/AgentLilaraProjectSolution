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
    /// 支持子 agent 异步委派和运行时新消息感知。
    /// </summary>
    internal class WorkingCore : CoreBase
    {
        private const int MaxRounds = 15;

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
        private const string DelegateToolName = "委派任务";
        private const string SubAgentDetailToolName = "查看子任务详情";
        private const string TaskToolName = "任务管理";
        private const string AlertButtonToolName = "报警";

        private readonly PromptBuilder promptBuilder = new();

        // 回调
        public Func<string, Task>? OnSpeak { get; set; }
        public Func<string, Task>? OnMemory { get; set; }
        public Func<string, string?, Task>? OnSignal { get; set; }
        public Func<string, Task>? OnReviewHint { get; set; }
        public Func<string, Task>? OnAlert { get; set; }

        // 任务列表（跨轮保持）
        private readonly List<(string Description, bool Done)> taskList = new();

        // 子 agent 管理
        private readonly Dictionary<string, SubAgentRecord> subAgentRecords = new();
        private int subAgentSeq = 0;

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
        /// 事件驱动 Agent 循环。支持子 agent 异步委派和运行时新消息感知。
        /// </summary>
        public async Task<string> ProcessAsync(string userRequest, string? memoryContext = null,
            List<string>? imagePaths = null)
        {
            // 跨轮持久状态
            var register = new Dictionary<string, string>();
            var thinkingNotes = new Dictionary<string, string>();
            var retainedResults = new List<(ToolCall call, ToolResult result)>();
            List<ToolCall>? lastRoundCalls = null;
            List<ToolResult>? lastRoundResults = null;

            // 新消息和子 agent 结果的累积缓冲
            var pendingNewMessages = new List<string>();
            var pendingSubResults = new List<(string id, string summary)>();

            var toolDescriptions = ToolRegistry.GenerateDescriptions();

            for (int round = 0; round < MaxRounds; round++)
            {
                // 1. 收集本轮新增内容
                DrainNewMessages(pendingNewMessages);
                CollectCompletedSubAgents(pendingSubResults);

                // 2. 组装本轮提示词
                var messages = promptBuilder.BuildRoundMessages(
                    toolDescriptions, userRequest, thinkingNotes,
                    lastRoundResults, lastRoundCalls, retainedResults,
                    memoryContext,
                    imagePaths: round == 0 ? imagePaths : null,
                    newMessages: pendingNewMessages.Count > 0 ? pendingNewMessages : null,
                    subAgentResults: pendingSubResults.Count > 0 ? pendingSubResults : null,
                    taskList: taskList.Count > 0 ? taskList : null);

                processor.Client.ClearConversationHistory();
                processor.Client.SetConversationHistory(messages);

                // 清空已注入的缓冲
                pendingNewMessages.Clear();
                pendingSubResults.Clear();

                // 3. 调用模型，解析工具调用
                var toolCalls = await ParseToolCallsAsync();

                if (toolCalls.Count == 0)
                {
                    if (round == 0) return "[Agent] 未能生成有效的工具调用计划。";
                    break;
                }

                // 4. 分拣：委派/详情 vs 同步工具
                var syncCalls = new List<ToolCall>();
                var delegateCalls = new List<ToolCall>();
                var detailCalls = new List<ToolCall>();

                foreach (var call in toolCalls)
                {
                    if (call.Tool == DelegateToolName) delegateCalls.Add(call);
                    else if (call.Tool == SubAgentDetailToolName) detailCalls.Add(call);
                    else syncCalls.Add(call);
                }

                // 5. 同步工具执行（含委派和详情的信号工具）
                var executor = new ToolExecutor(register);
                var allResults = await executor.ExecuteAsync(toolCalls);

                // 6. 处理副作用
                bool shouldExit = false;
                string? completionSummary = null;

                for (int i = 0; i < toolCalls.Count; i++)
                {
                    var call = toolCalls[i];
                    var result = allResults[i];

                    switch (call.Tool)
                    {
                        case CompletionToolName:
                            if (result.IsSuccess) { shouldExit = true; completionSummary = result.Data; }
                            break;
                        case ThinkingNotesToolName:
                            if (result.IsSuccess) ApplyThinkingNotes(call, thinkingNotes);
                            break;
                        case SpeakToolName:
                            if (result.IsSuccess && OnSpeak != null) await OnSpeak(result.Data ?? "");
                            break;
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

                        case DelegateToolName:
                            if (result.IsSuccess) LaunchSubAgent(result.Data ?? "", call.ToolId, register);
                            break;
                        case SubAgentDetailToolName:
                            if (result.IsSuccess) HandleSubAgentDetail(result.Data ?? "", call.ToolId, register);
                            break;
                    }
                }

                if (shouldExit)
                    return completionSummary ?? "任务完成";

                // 7. 收集 retain 结果
                for (int i = 0; i < toolCalls.Count; i++)
                {
                    if (toolCalls[i].Retain && allResults[i].IsSuccess)
                        retainedResults.Add((toolCalls[i], allResults[i]));
                }

                // 8. 更新滚动状态
                lastRoundCalls = toolCalls;
                lastRoundResults = allResults;

                // 9. 如果有挂起的子 agent → 等待事件
                await WaitForEventsAsync();
            }

            return "[Agent] 已达到最大执行轮次限制，任务未完成。";
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

        // ---- 子 agent 管理 ----

        private void LaunchSubAgent(string taskDescription, string toolId, Dictionary<string, string> register)
        {
            var id = $"sa_{++subAgentSeq:D2}";
            var tools = ToolRegistry.All.Values.Where(t => t.AllowSubAgent).ToList();
            var record = new SubAgentRecord { Id = id, TaskDescription = taskDescription };

            record.ExecutionTask = Task.Run(async () =>
            {
                try
                {
                    FrameworkLogger.Log("WorkingCore", $"子agent启动: {id}, 任务={Truncate(taskDescription, 100)}");
                    return await SubAgentRunner.RunAsync(taskDescription, tools, record);
                }
                catch (Exception ex)
                {
                    record.Status = "failed";
                    record.Summary = ex.Message;
                    record.Log.Add($"[异常] {ex.Message}");
                    FrameworkLogger.LogError("WorkingCore", ex, $"子agent {id} 执行异常");
                    return $"[子任务 {id} 异常] {ex.Message}";
                }
            });

            subAgentRecords[id] = record;
            // 覆盖信号工具的返回值，让模型看到分配的 ID
            register[toolId] = $"已派出子任务 {id}";
        }

        private void HandleSubAgentDetail(string subAgentId, string toolId, Dictionary<string, string> register)
        {
            if (!subAgentRecords.TryGetValue(subAgentId, out var record))
            {
                register[toolId] = $"未找到子任务: {subAgentId}";
                return;
            }

            var lines = new List<string>
            {
                $"子任务: {record.Id}",
                $"状态: {record.Status}",
                $"任务: {record.TaskDescription}"
            };
            if (record.Summary != null)
                lines.Add($"结果: {record.Summary}");
            lines.Add("--- 执行日志 ---");
            lines.AddRange(record.Log);

            register[toolId] = string.Join("\n", lines);
        }

        private void CollectCompletedSubAgents(List<(string id, string summary)> buffer)
        {
            foreach (var record in subAgentRecords.Values)
            {
                if (record.Status != "running") continue;
                if (record.ExecutionTask == null || !record.ExecutionTask.IsCompleted) continue;

                // 任务已完成但状态还没更新（可能是异常路径）
                if (record.Status == "running")
                {
                    record.Status = "completed";
                    record.Summary ??= record.ExecutionTask.Result;
                }
                buffer.Add((record.Id, record.Summary ?? "完成"));
                FrameworkLogger.Log("WorkingCore", $"子agent完成: {record.Id}, 结果={Truncate(record.Summary, 100)}");
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

        /// <summary>
        /// 等待事件：子 agent 完成 或 新消息到达。
        /// 没有挂起的子 agent 且没有消息通道时直接返回。
        /// </summary>
        private async Task WaitForEventsAsync()
        {
            var pendingSubs = subAgentRecords.Values
                .Where(r => r.Status == "running" && r.ExecutionTask != null)
                .Select(r => r.ExecutionTask!)
                .ToList();

            if (pendingSubs.Count == 0 && messageSignal == null)
                return;

            var waitTasks = new List<Task>(pendingSubs);
            if (messageSignal != null)
                waitTasks.Add(messageSignal.WaitAsync(TimeSpan.FromSeconds(30)));

            if (waitTasks.Count > 0)
                await Task.WhenAny(waitTasks);
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
            var action = call.Inputs[0].Value?.Trim().ToLower();
            var key = call.Inputs[1].Value ?? "";
            if (action == "write" && call.Inputs.Count >= 3)
                notes[key] = call.Inputs[2].Value ?? "";
            else if (action == "delete")
                notes.Remove(key);
        }

        private static string Truncate(string? s, int maxLen)
            => s == null ? "" : s.Length <= maxLen ? s : s[..maxLen] + "...";
    }
}
