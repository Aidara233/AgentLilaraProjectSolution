using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.BasicTools
{
    [ToolMeta(Group = null, ContinueLoop = false, ExpressAvailable = true)]
    public class SpeakTool : ITool
    {
        private readonly IChannelAccess? _channelAccess;
        private readonly int _channelId;

        public SpeakTool() { }

        public SpeakTool(IChannelAccess channelAccess, int channelId)
        {
            _channelAccess = channelAccess;
            _channelId = channelId;
        }

        public string Name => "speak";
        public string Description => "发送消息给当前频道，支持图文混排（在文本中用 <img path=\"...\"/> 标记图片位置）。"
            + "可用标签：<at user=\"名字\"/> @提及、<reply id=\"消息ID\"/> 回复消息";
        public IReadOnlyList<ToolParameter> Parameters =>
            [new("content", "要发送的文本内容（可含 <img/> <at/> <reply/> 标签）", 0)];
        public TimeSpan Timeout => TimeSpan.FromSeconds(10);

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 1 || string.IsNullOrWhiteSpace(resolvedInputs[0]))
                return new ToolResult { Status = "failed", Error = "消息内容不能为空" };

            var content = resolvedInputs[0];

            if (_channelAccess != null)
            {
                var sentId = await _channelAccess.SendMessageAsync(_channelId, content);
                return sentId != null
                    ? new ToolResult { Status = "success", Data = sentId }
                    : new ToolResult { Status = "failed", Error = "消息发送失败" };
            }

            return new ToolResult { Status = "success", Data = content };
        }
    }

    [ToolMeta(Group = null, ContinueLoop = false, ExpressAvailable = true)]
    public class SendMediaTool : ITool
    {
        private readonly IChannelAccess? _channelAccess;
        private readonly int _channelId;

        public SendMediaTool() { }

        public SendMediaTool(IChannelAccess channelAccess, int channelId)
        {
            _channelAccess = channelAccess;
            _channelId = channelId;
        }

        public string Name => "send_media";
        public string Description => "发送独立媒体（语音/视频/贴纸），不适合和文字混排。图片请用 speak 的 <img/> 标签。";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("type", "媒体类型：image / sticker / voice / video", 0),
            new("path", "本地文件路径或网络URL", 1)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(10);

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 2 || string.IsNullOrWhiteSpace(resolvedInputs[1]))
                return new ToolResult { Status = "failed", Error = "类型和路径不能为空" };

            var type = resolvedInputs[0].Trim().ToLower();
            var path = resolvedInputs[1].Trim();

            if (_channelAccess != null)
            {
                var sentId = await _channelAccess.SendMediaAsync(_channelId, type, path);
                return sentId != null
                    ? new ToolResult { Status = "success", Data = sentId }
                    : new ToolResult { Status = "failed", Error = "媒体发送失败" };
            }

            return new ToolResult { Status = "success", Data = $"{type}|{path}" };
        }
    }
}
