using System;
using System.Collections.Generic;

namespace AgentLilara.PluginSDK.Services
{
    /// <summary>
    /// 工具执行历史查询接口。替代旧的 RetainList 概念。
    /// </summary>
    public interface IToolHistoryAccess
    {
        /// <summary>按调用 ID 获取执行记录。</summary>
        ToolExecutionRecord? GetById(string callId);

        /// <summary>获取最近的执行记录，可按工具名过滤。</summary>
        List<ToolExecutionRecord> GetRecent(string? toolName = null, int count = 10);
    }

    public class ToolExecutionRecord
    {
        public string CallId { get; set; } = "";
        public string ToolName { get; set; } = "";
        public List<string> Inputs { get; set; } = [];
        public string Status { get; set; } = "";
        public string? Data { get; set; }
        public string? Error { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
