using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 更新频道亲和度工具。由 ReviewEngine 在频道日报模式中使用，
    /// 根据频道活跃度和互动质量调整长期参与倾向。
    /// </summary>
    internal class ReviewUpdateAffinityTool : ITool
    {
        private readonly ISystemContext ctx;
        public ReviewUpdateAffinityTool(ISystemContext ctx) { this.ctx = ctx; }

        public string Name => "更新亲和度";
        public string Description => "调整频道亲和度（影响群聊回复倾向）。正值=更愿意参与，负值=减少参与";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("频道ID", "要调整的频道ID", 0),
            new("调整量", "亲和度变化量（-0.3 ~ +0.3）", 1)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(10);

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (!int.TryParse(resolvedInputs[0], out var channelId))
                return new ToolResult { Status = "failed", Error = "频道ID必须是整数" };

            if (!float.TryParse(resolvedInputs[1], out var delta))
                return new ToolResult { Status = "failed", Error = "调整量必须是数字" };

            // 限制单次调整幅度
            delta = Math.Clamp(delta, -0.3f, 0.3f);

            var channel = await ctx.Session.GetChannelByIdAsync(channelId);
            if (channel == null)
                return new ToolResult { Status = "failed", Error = $"频道 {channelId} 不存在" };

            var oldAffinity = channel.Affinity;
            channel.Affinity = Math.Clamp(channel.Affinity + delta, 0.1f, 3.0f);
            await ctx.Session.UpdateChannelAsync(channel);

            return new ToolResult
            {
                Status = "success",
                Data = $"频道 {channel.Name} 亲和度: {oldAffinity:F2} → {channel.Affinity:F2}"
            };
        }
    }
}
