using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using AgentCoreProcessor.Models;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 系统循环上下文持久化（WAL 模式）。
    /// 每轮追加写入 context.jsonl，模块状态写入 state.json。
    /// </summary>
    internal class ContextPersistence
    {
        private readonly string contextPath;
        private readonly string summaryPath;
        private readonly string statePath;

        public ContextPersistence(string systemLoopDir)
        {
            contextPath = Path.Combine(systemLoopDir, "context.jsonl");
            summaryPath = Path.Combine(systemLoopDir, "context_summary.json");
            statePath = Path.Combine(systemLoopDir, "state.json");
        }

        /// <summary>
        /// 追加一轮对话到 context.jsonl。
        /// </summary>
        public void AppendRound(List<Message> userMessages, List<Message> assistantMessages)
        {
            try
            {
                var round = new
                {
                    Timestamp = DateTime.Now,
                    User = userMessages,
                    Assistant = assistantMessages
                };

                var json = JsonSerializer.Serialize(round);
                File.AppendAllText(contextPath, json + "\n");
            }
            catch (Exception ex)
            {
                FrameworkLogger.Log("ContextPersistence", $"追加上下文失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 保存模块状态。
        /// </summary>
        public void SaveState(Dictionary<string, object> state)
        {
            try
            {
                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(statePath, json);
            }
            catch (Exception ex)
            {
                FrameworkLogger.Log("ContextPersistence", $"保存状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 加载完整上下文（summary + JSONL）。
        /// </summary>
        public (string? Summary, List<List<Message>> Rounds) LoadContext()
        {
            string? summary = null;
            var rounds = new List<List<Message>>();

            // 加载摘要
            if (File.Exists(summaryPath))
            {
                try
                {
                    summary = File.ReadAllText(summaryPath);
                }
                catch (Exception ex)
                {
                    FrameworkLogger.Log("ContextPersistence", $"加载摘要失败: {ex.Message}");
                }
            }

            // 加载 JSONL
            if (File.Exists(contextPath))
            {
                try
                {
                    var lines = File.ReadAllLines(contextPath);
                    foreach (var line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var round = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line);
                        if (round != null)
                        {
                            var messages = new List<Message>();
                            if (round.ContainsKey("User"))
                            {
                                var userMsgs = JsonSerializer.Deserialize<List<Message>>(round["User"].GetRawText());
                                if (userMsgs != null) messages.AddRange(userMsgs);
                            }
                            if (round.ContainsKey("Assistant"))
                            {
                                var assistantMsgs = JsonSerializer.Deserialize<List<Message>>(round["Assistant"].GetRawText());
                                if (assistantMsgs != null) messages.AddRange(assistantMsgs);
                            }
                            rounds.Add(messages);
                        }
                    }
                }
                catch (Exception ex)
                {
                    FrameworkLogger.Log("ContextPersistence", $"加载上下文失败: {ex.Message}");
                }
            }

            return (summary, rounds);
        }

        /// <summary>
        /// 加载模块状态。
        /// </summary>
        public Dictionary<string, object>? LoadState()
        {
            if (!File.Exists(statePath)) return null;

            try
            {
                var json = File.ReadAllText(statePath);
                return JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            }
            catch (Exception ex)
            {
                FrameworkLogger.Log("ContextPersistence", $"加载状态失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 压缩后保存摘要，清空 JSONL。
        /// </summary>
        public void SaveSummaryAndClearContext(string summary)
        {
            try
            {
                File.WriteAllText(summaryPath, summary);
                File.Delete(contextPath);
                FrameworkLogger.Log("ContextPersistence", "上下文已压缩");
            }
            catch (Exception ex)
            {
                FrameworkLogger.Log("ContextPersistence", $"压缩失败: {ex.Message}");
            }
        }
    }
}
