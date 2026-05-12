using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 通知频道循环工具：系统循环向频道循环注入通知，由频道循环自行决定如何回应。
    /// 系统循环专用。不直接发送消息到适配器。
    /// </summary>
    internal class SendToChannelTool : ITool
    {
        public string Name => "notify_channel";
        public string Description => "向指定频道循环注入通知。频道循环醒来后会看到通知内容，自行决定是否回应用户以及如何措辞。";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("频道ID", "频道 ID（数字）", 0),
            new("通知内容", "要传达给频道循环的信息（如任务结果、提醒内容等）", 1)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);
        public bool AllowSubAgent => false;
        public bool ContinueLoop => true;

        private readonly ISystemContext ctx;

        public SendToChannelTool(ISystemContext ctx)
        {
            this.ctx = ctx;
        }

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 2 || string.IsNullOrWhiteSpace(resolvedInputs[0]) || string.IsNullOrWhiteSpace(resolvedInputs[1]))
            {
                return Task.FromResult(new ToolResult
                {
                    Status = "failed",
                    Error = "频道ID和通知内容不能为空"
                });
            }

            var channelIdStr = resolvedInputs[0].Trim();
            var content = resolvedInputs[1];

            if (!int.TryParse(channelIdStr, out var channelId))
            {
                return Task.FromResult(new ToolResult
                {
                    Status = "failed",
                    Error = "频道ID必须是数字"
                });
            }

            ctx.NotifyChannel(channelId, content);

            return Task.FromResult(new ToolResult
            {
                Status = "success",
                Data = $"通知已注入频道 {channelId}，频道循环将在下一轮处理"
            });
        }
    }
}
