using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 引擎管理工具：查看、启停引擎。
    /// 系统循环专用。
    /// </summary>
    internal class EngineManagementTool : ITool
    {
        public string Name => "引擎管理";
        public string Description => "查看或管理引擎（list 列出 / stop 停止 / status 状态）";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("操作", "list（列出所有引擎）/ stop（停止引擎）/ status（查看状态）", 0),
            new("引擎类型", "可选：指定引擎类型（stop 操作需要，如 Worker / Dream）", 1)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);
        public bool AllowSubAgent => false;

        private readonly ISystemContext ctx;

        public EngineManagementTool(ISystemContext ctx)
        {
            this.ctx = ctx;
        }

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 1 || string.IsNullOrWhiteSpace(resolvedInputs[0]))
            {
                return Task.FromResult(new ToolResult
                {
                    Status = "failed",
                    Error = "操作不能为空"
                });
            }

            var action = resolvedInputs[0].ToLower().Trim();

            try
            {
                switch (action)
                {
                    case "list":
                        return Task.FromResult(ListEngines());

                    case "stop":
                        if (resolvedInputs.Count < 2 || string.IsNullOrWhiteSpace(resolvedInputs[1]))
                        {
                            return Task.FromResult(new ToolResult
                            {
                                Status = "failed",
                                Error = "stop 操作需要指定引擎类型"
                            });
                        }
                        return Task.FromResult(StopEngines(resolvedInputs[1]));

                    case "status":
                        return Task.FromResult(GetStatus());

                    default:
                        return Task.FromResult(new ToolResult
                        {
                            Status = "failed",
                            Error = $"无效的操作：{action}。有效值：list / stop / status"
                        });
                }
            }
            catch (Exception ex)
            {
                return Task.FromResult(new ToolResult
                {
                    Status = "failed",
                    Error = $"操作失败: {ex.Message}"
                });
            }
        }

        private ToolResult ListEngines()
        {
            var summary = ctx.GetActiveEngineSummary();
            var sb = new StringBuilder();
            sb.AppendLine("活跃引擎：");

            if (summary.Count == 0)
            {
                sb.AppendLine("（无）");
            }
            else
            {
                foreach (var (type, count) in summary)
                {
                    sb.AppendLine($"- {type}: {count} 个实例");
                }
            }

            return new ToolResult
            {
                Status = "success",
                Data = sb.ToString()
            };
        }

        private ToolResult StopEngines(string engineType)
        {
            var countBefore = ctx.GetActiveEngineCount(engineType);
            if (countBefore == 0)
            {
                return new ToolResult
                {
                    Status = "failed",
                    Error = $"没有活跃的 {engineType} 引擎"
                };
            }

            ctx.RequestStopEnginesByType(engineType);

            return new ToolResult
            {
                Status = "success",
                Data = $"已请求停止 {countBefore} 个 {engineType} 引擎"
            };
        }

        private ToolResult GetStatus()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"系统空闲: {(ctx.IsIdle ? "是" : "否")}");
            if (ctx.IsIdle)
            {
                sb.AppendLine($"空闲时长: {ctx.IdleDuration.TotalMinutes:F1} 分钟");
            }
            sb.AppendLine($"最后消息时间: {ctx.LastMessageTime:yyyy-MM-dd HH:mm:ss}");

            var summary = ctx.GetActiveEngineSummary();
            sb.AppendLine($"活跃引擎数: {summary.Sum(s => s.Count)}");

            return new ToolResult
            {
                Status = "success",
                Data = sb.ToString()
            };
        }
    }
}
