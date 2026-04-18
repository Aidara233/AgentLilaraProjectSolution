using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 便签板工具：会话级上下文注入，Express/Working 共享。
    /// 内容全量展示在 prompt 中，不像 retain 只显示摘要。
    /// 实际数据由 WorkerEngine 维护，工具只做参数验证和信号传递。
    /// </summary>
    internal class PinboardTool : ITool
    {
        public string Name => "便签板";
        public string Description => "管理便签板。pin 钉一条内容（跨轮可见），unpin 按标签移除，list 查看所有标签";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("操作", "pin / unpin / list", 0),
            new("标签", "pin/unpin 时必填，用于标识条目", 1),
            new("内容", "pin 时必填，要钉住的内容", 2)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var action = (resolvedInputs.ElementAtOrDefault(0) ?? "").Trim().ToLower();
            var label = (resolvedInputs.ElementAtOrDefault(1) ?? "").Trim();
            var content = resolvedInputs.ElementAtOrDefault(2) ?? "";

            switch (action)
            {
                case "pin":
                    if (string.IsNullOrEmpty(label))
                        return Task.FromResult(new ToolResult { Status = "failed", Error = "标签不能为空" });
                    if (string.IsNullOrEmpty(content))
                        return Task.FromResult(new ToolResult { Status = "failed", Error = "内容不能为空" });
                    return Task.FromResult(new ToolResult { Status = "success", Data = $"pin:{label}:{content}" });

                case "unpin":
                    if (string.IsNullOrEmpty(label))
                        return Task.FromResult(new ToolResult { Status = "failed", Error = "标签不能为空" });
                    return Task.FromResult(new ToolResult { Status = "success", Data = $"unpin:{label}" });

                case "list":
                    return Task.FromResult(new ToolResult { Status = "success", Data = "list" });

                default:
                    return Task.FromResult(new ToolResult { Status = "failed", Error = $"未知操作: {action}，支持 pin / unpin / list" });
            }
        }
    }
}
