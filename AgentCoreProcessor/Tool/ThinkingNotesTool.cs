using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 思考笔记工具：按 key 写入或删除一条笔记，用于跨轮次保持思路。
    /// 纯信号工具——本身只做参数验证，实际的字典操作由 Agent 循环处理。
    /// </summary>
    internal class ThinkingNotesTool : ITool
    {
        public string Name => "思考笔记";
        public string Description => "写入或删除一条思考笔记（key-value），用于跨轮次保持思路";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("操作", "write 写入 / delete 删除", 0),
            new("键", "笔记的键名", 1),
            new("值", "笔记内容（delete 时可为空）", 2)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 2)
                return Task.FromResult(new ToolResult
                {
                    Status = "failed",
                    Error = "至少需要操作和键两个参数"
                });

            var action = resolvedInputs[0].Trim().ToLower();
            if (action != "write" && action != "delete")
                return Task.FromResult(new ToolResult
                {
                    Status = "failed",
                    Error = $"未知操作: {action}，支持 write 或 delete"
                });

            var key = resolvedInputs[1];
            var value = resolvedInputs.Count >= 3 ? resolvedInputs[2] : "";

            return Task.FromResult(new ToolResult
            {
                Status = "success",
                Data = $"{action}:{key}"
            });
        }
    }
}
