using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 复盘引擎。由 DreamEngine 在大睡 Phase 2 孵化，实现独立的 Agent 循环进行深度分析。
    /// 不注册 SpawnCheck——通过 ISystemContext.StartEngine() 直接启动。
    /// </summary>
    internal class ReviewEngine : ISubEngine
    {
        public string EngineType => "Review";
        public bool IsAlive { get; private set; } = true;

        private readonly ISystemContext ctx;
        private readonly ReviewMode mode;
        private readonly string preInjectedContext;
        private readonly int baseBudget;
        private readonly int reserveBudget;
        private readonly DreamProgress progress;

        private readonly ReviewCore reviewCore = new();
        private readonly Dictionary<string, ITool> tools;
        private readonly string toolDescriptions;

        private volatile bool shouldWake = false;
        private volatile bool shouldStop = false;

        private int totalTokens = 0;
        private int effectiveBudget;
        private bool reserveUsed = false;

        private static string DreamProgressPath =>
            Path.Combine(PathConfig.StoragePath, "Dream", "DreamProgress.json");

        public ReviewEngine(ISystemContext ctx, ReviewMode mode, string preInjectedContext,
            int baseBudget, int reserveBudget, DreamProgress progress)
        {
            this.ctx = ctx;
            this.mode = mode;
            this.preInjectedContext = preInjectedContext;
            this.baseBudget = baseBudget;
            this.reserveBudget = reserveBudget;
            this.progress = progress;
            this.effectiveBudget = baseBudget;

            // 初始化工具集（不注册到全局 ToolRegistry）
            tools = BuildToolSet();
            toolDescriptions = GenerateToolDescriptions();
        }

        public async Task RunAsync()
        {
            FrameworkLogger.Log("ReviewEngine",
                $"复盘开始: mode={mode} budget={baseBudget} reserve={reserveBudget}");

            try
            {
                await RunAgentLoopAsync();
            }
            catch (Exception ex)
            {
                FrameworkLogger.Log("ReviewEngine", $"复盘异常: {ex.Message}");
            }
            finally
            {
                // 标记已处理的 ReviewHint
                try
                {
                    var hints = await ctx.ReviewHints.GetUnprocessedAsync();
                    foreach (var hint in hints)
                        await ctx.ReviewHints.MarkProcessedAsync(hint.Id);
                }
                catch { }

                FrameworkLogger.Log("ReviewEngine",
                    $"复盘结束: mode={mode} tokens={totalTokens}/{effectiveBudget}");
                IsAlive = false;
            }
        }

        public void OnEvent(EngineEvent e)
        {
            if (e is MessageEvent) shouldWake = true;
        }

        public void RequestStop() => shouldStop = true;

        // ---- Agent 循环 ----

        private async Task RunAgentLoopAsync()
        {
            var thinkingNotes = new Dictionary<string, string>();
            var retainedResults = new List<(ToolCall call, ToolResult result)>();
            List<ToolCall>? lastRoundCalls = null;
            List<ToolResult>? lastRoundResults = null;

            int round = 0;
            while (!shouldStop && totalTokens < effectiveBudget)
            {
                if (shouldWake)
                {
                    FrameworkLogger.Log("ReviewEngine", "被叫醒，完成当前轮后停止");
                    shouldStop = true; // 完成本轮后退出
                }

                // 1. 构建提示词
                var messages = BuildRoundMessages(
                    round, thinkingNotes, lastRoundResults, lastRoundCalls, retainedResults);

                reviewCore.ResetProcessor();
                reviewCore.SetConversation(messages);

                // 2. 调用模型，解析工具调用
                var toolCalls = new List<ToolCall>();
                var usage = await reviewCore.GenerateAsync(onBreak: (block) =>
                {
                    var json = block.Content.Trim();
                    if (string.IsNullOrEmpty(json)) return;
                    try
                    {
                        var call = ToolCall.FromJson(json);
                        if (call.Validate().Count() == 0)
                            toolCalls.Add(call);
                    }
                    catch { }
                });

                // 累加 token
                totalTokens += usage.TotalTokens;

                // 3. 空输出检查
                if (toolCalls.Count == 0)
                {
                    if (round == 0)
                    {
                        FrameworkLogger.Log("ReviewEngine", "首轮无工具调用，终止");
                        break;
                    }
                    break; // 隐式完成
                }

                // 4. 执行（使用自有工具集）
                Func<string, ITool?> resolver = name =>
                    tools.TryGetValue(name, out var t) ? t : null;
                var executor = new ToolExecutor(resolver);
                var results = await executor.ExecuteAsync(toolCalls);

                // 5. 处理特殊工具
                for (int i = 0; i < toolCalls.Count; i++)
                {
                    var call = toolCalls[i];
                    var result = results[i];
                    if (!result.IsSuccess) continue;

                    switch (call.Tool)
                    {
                        case "完成":
                            shouldStop = true;
                            FrameworkLogger.Log("ReviewEngine",
                                $"复盘完成: {result.Data}");
                            break;

                        case "思考笔记":
                            ApplyThinkingNotes(call, thinkingNotes);
                            break;

                        case "写入临时记忆":
                            try
                            {
                                await ctx.MemorySvc.StoreAsync(result.Data ?? "");
                            }
                            catch (Exception ex)
                            {
                                FrameworkLogger.Log("ReviewEngine",
                                    $"写入临时记忆失败: {ex.Message}");
                            }
                            break;

                        case "标记复盘":
                            try
                            {
                                await ctx.ReviewHints.CreateAsync(result.Data ?? "");
                            }
                            catch { }
                            break;

                        case "请求增援":
                            if (!reserveUsed)
                            {
                                reserveUsed = true;
                                effectiveBudget += reserveBudget;
                                FrameworkLogger.Log("ReviewEngine",
                                    $"备用预算已启用，总预算: {effectiveBudget}");
                            }
                            break;

                        case "保存进度":
                            try
                            {
                                var investigation = JsonConvert.DeserializeObject<ReviewInvestigation>(
                                    result.Data ?? "{}") ?? new ReviewInvestigation();
                                investigation.Mode = mode.ToString();
                                investigation.SavedAt = DateTime.Now;
                                progress.ActiveInvestigations.Clear();
                                progress.ActiveInvestigations.Add(investigation);
                                progress.Save(DreamProgressPath);
                                FrameworkLogger.Log("ReviewEngine", "进度已保存");
                            }
                            catch (Exception ex)
                            {
                                FrameworkLogger.Log("ReviewEngine",
                                    $"保存进度失败: {ex.Message}");
                            }
                            break;
                    }
                }

                if (shouldStop) break;

                // 6. 收集 retain 结果
                for (int i = 0; i < toolCalls.Count; i++)
                {
                    var tool = tools.TryGetValue(toolCalls[i].Tool, out var t) ? t : null;
                    if (tool?.RetainResult == true && results[i].IsSuccess)
                        retainedResults.Add((toolCalls[i], results[i]));
                }

                // 7. 更新滚动状态
                lastRoundCalls = toolCalls;
                lastRoundResults = results;
                round++;
            }

            // 预算外收尾：如果是预算耗尽退出（非主动完成），给一轮总结机会
            if (!shouldStop && totalTokens >= effectiveBudget)
            {
                FrameworkLogger.Log("ReviewEngine", "预算耗尽，执行收尾轮");
                await RunFinalRound(thinkingNotes, retainedResults,
                    lastRoundCalls, lastRoundResults, round);
            }
        }

        private async Task RunFinalRound(
            Dictionary<string, string> thinkingNotes,
            List<(ToolCall, ToolResult)> retainedResults,
            List<ToolCall>? lastCalls, List<ToolResult>? lastResults, int round)
        {
            try
            {
                var messages = BuildRoundMessages(
                    round, thinkingNotes, lastResults, lastCalls, retainedResults,
                    extraNote: "【预算已耗尽】这是最后一轮。请立即保存进度或写入总结，然后调用「完成」。");

                reviewCore.ResetProcessor();
                reviewCore.SetConversation(messages);

                var toolCalls = new List<ToolCall>();
                await reviewCore.GenerateAsync(onBreak: (block) =>
                {
                    var json = block.Content.Trim();
                    if (string.IsNullOrEmpty(json)) return;
                    try
                    {
                        var call = ToolCall.FromJson(json);
                        if (call.Validate().Count() == 0) toolCalls.Add(call);
                    }
                    catch { }
                });

                if (toolCalls.Count > 0)
                {
                    Func<string, ITool?> resolver = name =>
                        tools.TryGetValue(name, out var t) ? t : null;
                    var executor = new ToolExecutor(resolver);
                    var results = await executor.ExecuteAsync(toolCalls);

                    for (int i = 0; i < toolCalls.Count; i++)
                    {
                        var call = toolCalls[i];
                        var result = results[i];
                        if (!result.IsSuccess) continue;

                        if (call.Tool == "写入临时记忆")
                            try { await ctx.MemorySvc.StoreAsync(result.Data ?? ""); } catch { }
                        else if (call.Tool == "保存进度")
                        {
                            try
                            {
                                var inv = JsonConvert.DeserializeObject<ReviewInvestigation>(
                                    result.Data ?? "{}") ?? new ReviewInvestigation();
                                inv.Mode = mode.ToString();
                                inv.SavedAt = DateTime.Now;
                                progress.ActiveInvestigations.Clear();
                                progress.ActiveInvestigations.Add(inv);
                                progress.Save(DreamProgressPath);
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                FrameworkLogger.Log("ReviewEngine", $"收尾轮异常: {ex.Message}");
            }
        }

        // ---- 提示词构建 ----

        private List<Message> BuildRoundMessages(
            int round,
            Dictionary<string, string> thinkingNotes,
            List<ToolResult>? lastResults,
            List<ToolCall>? lastCalls,
            List<(ToolCall, ToolResult)> retainedResults,
            string? extraNote = null)
        {
            var messages = new List<Message>();

            // 工具描述
            messages.Add(new Message { Role = "user", Content = toolDescriptions });

            // 预注入上下文（首轮）
            if (round == 0)
                messages.Add(new Message { Role = "user", Content = preInjectedContext });

            // 预算进度
            var budgetInfo = new StringBuilder();
            budgetInfo.Append($"[复盘资源] 累计消耗: {totalTokens} / {effectiveBudget}");
            if (!reserveUsed)
                budgetInfo.Append(" | 备用预算: 可用");
            else
                budgetInfo.Append(" | 备用预算: 已使用");
            if (totalTokens > effectiveBudget * 0.8f && !shouldStop)
                budgetInfo.Append(" | ⚠ 预算即将耗尽，考虑收尾或请求增援");
            messages.Add(new Message { Role = "user", Content = budgetInfo.ToString() });

            // 额外提示（如收尾轮）
            if (extraNote != null)
                messages.Add(new Message { Role = "user", Content = extraNote });

            // 思考笔记
            if (thinkingNotes.Count > 0)
            {
                var sb = new StringBuilder("你的思考笔记：\n");
                foreach (var (key, value) in thinkingNotes)
                    sb.AppendLine($"- {key}: {value}");
                messages.Add(new Message { Role = "user", Content = sb.ToString() });
            }

            // 保留结果
            if (retainedResults.Count > 0)
            {
                var sb = new StringBuilder("历史轮次保留的工具结果：\n");
                foreach (var (call, result) in retainedResults)
                    FormatResult(sb, call, result);
                messages.Add(new Message { Role = "user", Content = sb.ToString() });
            }

            // 上一轮结果
            if (lastResults != null && lastCalls != null && lastResults.Count > 0)
            {
                var sb = new StringBuilder("上一轮工具执行结果：\n");
                for (int i = 0; i < lastCalls.Count && i < lastResults.Count; i++)
                    FormatResult(sb, lastCalls[i], lastResults[i]);
                messages.Add(new Message { Role = "user", Content = sb.ToString() });
            }

            return messages;
        }

        private static void FormatResult(StringBuilder sb, ToolCall call, ToolResult result)
        {
            if (result.IsSuccess)
                sb.AppendLine($"[{call.Tool}]: 成功，返回值：{result.Data}");
            else
                sb.AppendLine($"[{call.Tool}]: {result.Status}" +
                    (result.Error != null ? $" - {result.Error}" : ""));
        }

        // ---- 工具管理 ----

        private Dictionary<string, ITool> BuildToolSet()
        {
            var toolList = new ITool[]
            {
                new ReviewSearchMemoryTool(ctx),
                new ReviewViewLinksTool(ctx),
                new ReviewReadMessagesTool(ctx),
                new ReviewUpdateAffinityTool(ctx),
                new ReviewUpdateFastMemoryTool(ctx),
                new ReviewUpdateTrustProgressTool(ctx),
                new ReviewWriteTempMemoryTool(),
                new ReviewThinkingNotesTool(),
                new ReviewMarkHintTool(),
                new ReviewRequestReinforcementTool(),
                new ReviewSaveProgressTool(),
                new ReviewCompletionTool()
            };
            return toolList.ToDictionary(t => t.Name);
        }

        private string GenerateToolDescriptions()
        {
            var sb = new StringBuilder();
            int i = 1;
            foreach (var tool in tools.Values)
            {
                sb.AppendLine($"工具{i}：{tool.Name}");
                sb.AppendLine($"描述：{tool.Description}");
                if (tool.Parameters.Count > 0)
                {
                    var paramParts = tool.Parameters
                        .Select(p => $"inputs[{p.Index}] = {p.Name}");
                    sb.AppendLine($"参数：{string.Join(", ", paramParts)}");
                }
                var example = new
                {
                    tool = tool.Name,
                    inputs = tool.Parameters.Select(p => $"({p.Name})").ToArray()
                };
                sb.AppendLine($"示例：{JsonConvert.SerializeObject(example, Formatting.None)}<over>");
                sb.AppendLine();
                i++;
            }
            return sb.ToString().TrimEnd();
        }

        // ---- 辅助 ----

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
