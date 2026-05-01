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
    /// 频道信息工具：查询频道列表、详情、消息。
    /// 系统循环专用。
    /// </summary>
    internal class ChannelInfoTool : ITool
    {
        public string Name => "频道信息";
        public string Description => "查询频道列表、详情或消息历史";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("操作", "list（列出所有频道）/ detail（频道详情）/ messages（消息历史）", 0),
            new("频道ID", "可选：指定频道 ID（detail 和 messages 操作需要）", 1),
            new("数量", "可选：消息数量（messages 操作，默认 20）", 2)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);
        public bool AllowSubAgent => false;

        private readonly ISystemContext ctx;

        public ChannelInfoTool(ISystemContext ctx)
        {
            this.ctx = ctx;
        }

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 1 || string.IsNullOrWhiteSpace(resolvedInputs[0]))
            {
                return new ToolResult
                {
                    Status = "failed",
                    Error = "操作不能为空"
                };
            }

            var action = resolvedInputs[0].ToLower().Trim();

            try
            {
                switch (action)
                {
                    case "list":
                        return await ListChannelsAsync();

                    case "detail":
                        if (resolvedInputs.Count < 2 || !int.TryParse(resolvedInputs[1], out var detailChannelId))
                        {
                            return new ToolResult
                            {
                                Status = "failed",
                                Error = "detail 操作需要指定频道 ID"
                            };
                        }
                        return await GetChannelDetailAsync(detailChannelId);

                    case "messages":
                        if (resolvedInputs.Count < 2 || !int.TryParse(resolvedInputs[1], out var msgChannelId))
                        {
                            return new ToolResult
                            {
                                Status = "failed",
                                Error = "messages 操作需要指定频道 ID"
                            };
                        }
                        var limit = 20;
                        if (resolvedInputs.Count > 2 && int.TryParse(resolvedInputs[2], out var parsedLimit))
                        {
                            limit = parsedLimit;
                        }
                        return await GetChannelMessagesAsync(msgChannelId, limit);

                    default:
                        return new ToolResult
                        {
                            Status = "failed",
                            Error = $"无效的操作：{action}。有效值：list / detail / messages"
                        };
                }
            }
            catch (Exception ex)
            {
                return new ToolResult
                {
                    Status = "failed",
                    Error = $"查询失败: {ex.Message}"
                };
            }
        }

        private async Task<ToolResult> ListChannelsAsync()
        {
            var channels = await ctx.Session.GetAllChannelsAsync();
            var sb = new StringBuilder();
            sb.AppendLine($"共 {channels.Count} 个频道：");

            foreach (var channel in channels)
            {
                var hasActiveEngine = ctx.HasActiveEngine("Worker") &&
                                     ctx.GetActiveEngineCount("Worker") > 0; // 简化判断
                var status = hasActiveEngine ? "活跃" : "空闲";
                sb.AppendLine($"- ID {channel.Id}: {channel.Name} ({status})");
            }

            return new ToolResult
            {
                Status = "success",
                Data = sb.ToString()
            };
        }

        private async Task<ToolResult> GetChannelDetailAsync(int channelId)
        {
            var channel = await ctx.Session.GetChannelByIdAsync(channelId);
            if (channel == null)
            {
                return new ToolResult
                {
                    Status = "failed",
                    Error = $"频道 ID {channelId} 不存在"
                };
            }

            var sb = new StringBuilder();
            sb.AppendLine($"频道 ID: {channel.Id}");
            sb.AppendLine($"名称: {channel.Name}");
            sb.AppendLine($"亲和度: {channel.Affinity}");

            // 查询参与者（简化版，Phase 5 可以更详细）
            sb.AppendLine("参与者信息：（Phase 5 实现）");

            return new ToolResult
            {
                Status = "success",
                Data = sb.ToString()
            };
        }

        private async Task<ToolResult> GetChannelMessagesAsync(int channelId, int limit)
        {
            var messages = await ctx.Session.GetContextByChannelAsync(channelId, limit);
            var sb = new StringBuilder();
            sb.AppendLine($"频道 {channelId} 最近 {messages.Count} 条消息：");

            foreach (var msg in messages)
            {
                var sender = msg.IsFromBot ? "Bot" : $"User#{msg.UserId}";
                var time = msg.Time.ToString("MM-dd HH:mm");
                var preview = msg.Content.Length > 50 ? msg.Content.Substring(0, 50) + "..." : msg.Content;
                sb.AppendLine($"[{time}] {sender}: {preview}");
            }

            return new ToolResult
            {
                Status = "success",
                Data = sb.ToString()
            };
        }
    }
}
