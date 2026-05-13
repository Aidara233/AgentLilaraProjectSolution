using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.MemoryTools
{
    /// <summary>
    /// 记忆工具。提供记忆的写入和搜索能力。
    /// 这是默认参考实现，高级开发者可替换为自己的检索策略。
    /// </summary>
    [ToolMeta(Group = null, ContinueLoop = true, CapabilitySummary = "记忆：主动记住信息或搜索已有记忆")]
    public class MemoryTool : ITool
    {
        private readonly IMemoryAccess? memory;

        public string Name => "memory";
        public string Description => "记忆系统：写入新记忆或搜索已有记忆。支持主记忆库和临时记忆库。";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("action", "操作：store / search / search_temp / store_temp / delete / info", 0),
            new("content", "记忆内容（store时）或搜索关键词（search时）", 1, isRequired: false),
            new("type", "（可选）记忆类型：knowledge/fact/feedback/inference/event", 2, isRequired: false),
            new("subject", "（可选）主题标签，便于后续检索", 3, isRequired: false)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public MemoryTool(IToolContext ctx)
        {
            memory = ctx.GetService<IMemoryAccess>();
        }

        /// <summary>Component 模式构造函数</summary>
        public MemoryTool(IMemoryAccess? memoryAccess)
        {
            memory = memoryAccess;
        }

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (memory == null)
                return Fail("记忆服务不可用");

            var action = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim().ToLower() : "";
            var content = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";
            var type = resolvedInputs.Count > 2 && !string.IsNullOrWhiteSpace(resolvedInputs[2])
                ? resolvedInputs[2].Trim() : null;
            var subject = resolvedInputs.Count > 3 && !string.IsNullOrWhiteSpace(resolvedInputs[3])
                ? resolvedInputs[3].Trim() : null;

            switch (action)
            {
                case "store":
                    return await StoreMemory(content, type, subject, persistent: true);

                case "store_temp":
                    return await StoreTempMemory(content, type, subject);

                case "search":
                    return await SearchMemory(content);

                case "search_temp":
                    return await SearchTempMemory(content);

                case "delete":
                    return await DeleteMemory(content);

                case "info":
                    return await GetInfo();

                default:
                    return Fail($"未知操作: {action}，支持 store/store_temp/search/search_temp/delete/info");
            }
        }

        private async Task<ToolResult> StoreMemory(string content, string? type, string? subject, bool persistent)
        {
            if (string.IsNullOrWhiteSpace(content))
                return Fail("记忆内容不能为空");

            var id = await memory!.StoreAsync(new MemoryWriteRequest
            {
                Content = content,
                Type = type,
                Subject = subject,
                IsPersistent = persistent
            });
            return Ok($"已存入主记忆库 (id={id})");
        }

        private async Task<ToolResult> StoreTempMemory(string content, string? type, string? subject)
        {
            if (string.IsNullOrWhiteSpace(content))
                return Fail("记忆内容不能为空");

            var id = await memory!.StoreTempAsync(new TempMemoryWriteRequest
            {
                Content = content,
                Type = type,
                Subject = subject
            });
            return Ok($"已存入临时记忆库 (id={id})");
        }

        private async Task<ToolResult> SearchMemory(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return Fail("搜索关键词不能为空");

            var results = await memory!.SemanticSearchAsync(query, limit: 10);
            if (results.Count == 0)
                return Ok("未找到相关记忆");

            var sb = new StringBuilder();
            foreach (var m in results)
            {
                sb.AppendLine($"[{m.Id}] (score={m.Score:F3}, type={m.Type}) {m.Content}");
                if (m.Subject != null) sb.AppendLine($"    subject: {m.Subject}");
            }
            return Ok(sb.ToString().TrimEnd());
        }

        private async Task<ToolResult> SearchTempMemory(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return Fail("搜索关键词不能为空");

            var results = await memory!.SearchTempAsync(query, limit: 10);
            if (results.Count == 0)
                return Ok("临时记忆库中未找到相关记忆");

            var sb = new StringBuilder();
            foreach (var m in results)
            {
                sb.AppendLine($"[temp:{m.Id}] (score={m.Score:F3}) {m.Content}");
            }
            return Ok(sb.ToString().TrimEnd());
        }

        private async Task<ToolResult> DeleteMemory(string idStr)
        {
            if (!int.TryParse(idStr, out var id))
                return Fail("请提供有效的记忆 ID");

            await memory!.DeleteAsync(id);
            return Ok($"已删除记忆 id={id}");
        }

        private async Task<ToolResult> GetInfo()
        {
            var mainCount = await memory!.CountAsync();
            var tempCount = await memory!.CountTempAsync();
            return Ok($"主记忆库: {mainCount} 条\n临时记忆库: {tempCount} 条");
        }

        private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
        private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
    }
}
