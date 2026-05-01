using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 检查通知工具：读取轻量通知队列。
    /// 系统循环专用。
    /// </summary>
    internal class CheckNotificationsTool : ITool
    {
        public string Name => "检查通知";
        public string Description => "读取轻量通知队列（Notify / ProgressUpdate / WatchHit）";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("数量", "可选：读取数量（默认 10）", 0)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);
        public bool AllowSubAgent => false;

        private readonly ISystemContext ctx;

        public CheckNotificationsTool(ISystemContext ctx)
        {
            this.ctx = ctx;
        }

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var count = 10;
            if (resolvedInputs.Count > 0 && int.TryParse(resolvedInputs[0], out var parsedCount))
            {
                count = parsedCount;
            }

            try
            {
                var notifications = await ctx.TaskBridge.ReadNotificationsAsync(count, TimeSpan.FromSeconds(2));

                if (notifications.Count == 0)
                {
                    return new ToolResult
                    {
                        Status = "success",
                        Data = "没有新通知"
                    };
                }

                var sb = new StringBuilder();
                sb.AppendLine($"共 {notifications.Count} 条通知：");

                foreach (var notification in notifications)
                {
                    var time = notification.Timestamp.ToString("MM-dd HH:mm:ss");
                    sb.AppendLine($"[{time}] {notification.Type} from {notification.SourceId}");
                    sb.AppendLine($"  {notification.Summary}");
                }

                return new ToolResult
                {
                    Status = "success",
                    Data = sb.ToString()
                };
            }
            catch (Exception ex)
            {
                return new ToolResult
                {
                    Status = "failed",
                    Error = $"读取通知失败: {ex.Message}"
                };
            }
        }
    }
}
