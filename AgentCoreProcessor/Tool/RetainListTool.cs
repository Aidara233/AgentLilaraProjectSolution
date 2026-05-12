using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 缓存管理工具：查看/清理 retain 列表中的条目。
    /// retain 列表由框架自动收集 RetainResult=true 的工具结果。
    /// 实际数据由 WorkingCore 维护，工具只做参数验证和信号传递。
    /// </summary>
    internal class RetainListTool : ITool
    {
        public string Name => "retain_list";
        public string Description => "管理缓存列表。view 查看指定序号的完整内容，remove 按序号移除，clear 清空全部";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("操作", "view / remove / clear", 0),
            new("序号", "view/remove 时必填（从1开始）", 1)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);
        public bool ContinueLoop => true;

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var action = (resolvedInputs.ElementAtOrDefault(0) ?? "").Trim().ToLower();
            var indexStr = (resolvedInputs.ElementAtOrDefault(1) ?? "").Trim();

            switch (action)
            {
                case "view":
                case "remove":
                    if (!int.TryParse(indexStr, out var index) || index < 1)
                        return Task.FromResult(new ToolResult { Status = "failed", Error = "序号必须是大于0的整数" });
                    return Task.FromResult(new ToolResult { Status = "success", Data = $"{action}:{index}" });

                case "clear":
                    return Task.FromResult(new ToolResult { Status = "success", Data = "clear" });

                default:
                    return Task.FromResult(new ToolResult { Status = "failed", Error = $"未知操作: {action}，支持 view / remove / clear" });
            }
        }
    }
}
