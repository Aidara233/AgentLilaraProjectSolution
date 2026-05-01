using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Engine;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 发送到频道工具：系统循环发消息到任意频道。
    /// 系统循环专用。
    /// </summary>
    internal class SendToChannelTool : ITool
    {
        public string Name => "发送到频道";
        public string Description => "向指定频道发送消息";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("频道标识", "频道 ID（数字）或 platform:channelId 格式", 0),
            new("消息内容", "要发送的消息文本", 1)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(10);
        public bool AllowSubAgent => false;

        private readonly ISystemContext ctx;

        public SendToChannelTool(ISystemContext ctx)
        {
            this.ctx = ctx;
        }

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 2 || string.IsNullOrWhiteSpace(resolvedInputs[0]) || string.IsNullOrWhiteSpace(resolvedInputs[1]))
            {
                return new ToolResult
                {
                    Status = "failed",
                    Error = "频道标识和消息内容不能为空"
                };
            }

            var channelIdentifier = resolvedInputs[0].Trim();
            var content = resolvedInputs[1];

            try
            {
                // 解析频道标识
                int channelId;
                if (int.TryParse(channelIdentifier, out channelId))
                {
                    // 数字 ID：从数据库查询
                    var channel = await ctx.Session.GetChannelByIdAsync(channelId);
                    if (channel == null)
                    {
                        return new ToolResult
                        {
                            Status = "failed",
                            Error = $"频道 ID {channelId} 不存在"
                        };
                    }

                    // Channel.Name 格式为 "platform:channelId"
                    var parts = channel.Name.Split(':', 2);
                    if (parts.Length != 2)
                    {
                        return new ToolResult
                        {
                            Status = "failed",
                            Error = $"频道 {channelId} 的名称格式无效: {channel.Name}"
                        };
                    }

                    var platform = parts[0];
                    var platformChannelId = parts[1];

                    // 发送消息
                    var sentId = await ctx.Adapters.SendMessageAsync(platform, new OutgoingMessage
                    {
                        ChannelId = platformChannelId,
                        Content = content
                    });

                    // 保存到数据库
                    await ctx.Session.SaveBotMessageAsync(channelId, content, sentId);

                    return new ToolResult
                    {
                        Status = "success",
                        Data = $"消息已发送到频道 {channelId}（{channel.Name}）"
                    };
                }
                else if (channelIdentifier.Contains(":"))
                {
                    // platform:channelId 格式
                    var parts = channelIdentifier.Split(':', 2);
                    var platform = parts[0];
                    var platformChannelId = parts[1];

                    // 发送消息
                    var sentId = await ctx.Adapters.SendMessageAsync(platform, new OutgoingMessage
                    {
                        ChannelId = platformChannelId,
                        Content = content
                    });

                    // 查询或创建频道记录
                    var channel = await ctx.Session.GetAllChannelsAsync();
                    var existingChannel = channel.FirstOrDefault(c => c.Name == channelIdentifier);
                    if (existingChannel != null)
                    {
                        await ctx.Session.SaveBotMessageAsync(existingChannel.Id, content, sentId);
                    }

                    return new ToolResult
                    {
                        Status = "success",
                        Data = $"消息已发送到频道 {platform}:{platformChannelId}"
                    };
                }
                else
                {
                    return new ToolResult
                    {
                        Status = "failed",
                        Error = "无效的频道标识格式。应为数字 ID 或 platform:channelId"
                    };
                }
            }
            catch (Exception ex)
            {
                return new ToolResult
                {
                    Status = "failed",
                    Error = $"发送消息失败: {ex.Message}"
                };
            }
        }
    }
}
