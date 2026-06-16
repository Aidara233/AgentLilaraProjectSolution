using System;
using System.Collections.Generic;
using System.IO;
using AgentCoreProcessor.Logging;
using AgentCoreProcessor.Models;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 系统循环上下文持久化（WAL 模式）。
    /// 每轮追加写入 context.jsonl，模块状态写入 state.json。
    /// v2: 支持 ContentParts（tool_use/tool_result），使用 Newtonsoft.Json 序列化。
    /// </summary>
    internal class ContextPersistence
    {
        private const int FormatVersion = 2;

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
        /// 追加一轮对话到 context.jsonl（v2 格式，含 ContentParts 支持）。
        /// </summary>
        public void AppendRound(List<Message> userMessages, List<Message> assistantMessages)
        {
            try
            {
                var round = new
                {
                    FormatVersion,
                    Timestamp = DateTime.Now,
                    User = userMessages,
                    Assistant = assistantMessages
                };

                var json = JsonConvert.SerializeObject(round, Formatting.None,
                    new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                File.AppendAllText(contextPath, json + "\n");
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "上下文轮次追加失败", new { error = ex.Message });
            }
        }

        /// <summary>
        /// 保存模块状态。
        /// </summary>
        public void SaveState(Dictionary<string, object> state)
        {
            try
            {
                var json = JsonConvert.SerializeObject(state, Formatting.Indented);
                File.WriteAllText(statePath, json);
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "模块状态保存失败", new { error = ex.Message });
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
                    Signal.Warn(LogGroup.Engine, "上下文摘要加载失败", new { error = ex.Message });
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

                        var wrapper = JsonConvert.DeserializeObject<dynamic>(line);
                        if (wrapper == null) continue;

                        var messages = new List<Message>();
                        if (wrapper.User != null)
                        {
                            var userJson = JsonConvert.SerializeObject(wrapper.User);
                            var userMsgs = JsonConvert.DeserializeObject<List<Message>>(userJson);
                            if (userMsgs != null) messages.AddRange(userMsgs);
                        }
                        if (wrapper.Assistant != null)
                        {
                            var asstJson = JsonConvert.SerializeObject(wrapper.Assistant);
                            var asstMsgs = JsonConvert.DeserializeObject<List<Message>>(asstJson);
                            if (asstMsgs != null) messages.AddRange(asstMsgs);
                        }
                        rounds.Add(messages);
                    }
                }
                catch (Exception ex)
                {
                    Signal.Warn(LogGroup.Engine, "上下文JSONL加载失败", new { error = ex.Message });
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
                return JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "模块状态加载失败", new { error = ex.Message });
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
                if (File.Exists(contextPath))
                    File.Delete(contextPath);
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "摘要保存+清理失败", new { error = ex.Message });
            }
        }
    }
}
