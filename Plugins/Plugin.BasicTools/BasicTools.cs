using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;

namespace Plugin.BasicTools
{
    [ToolMeta(Group = null, ContinueLoop = false, ExpressAvailable = true)]
    public class SpeakTool : ITool
    {
        public string Name => "speak";
        public string Description => "向用户发送一条消息（实时推送，不等待任务完成）";
        public IReadOnlyList<ToolParameter> Parameters =>
            [new("content", "要发送给用户的文本内容", 0)];
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 1 || string.IsNullOrWhiteSpace(resolvedInputs[0]))
                return Task.FromResult(new ToolResult { Status = "failed", Error = "消息内容不能为空" });

            return Task.FromResult(new ToolResult { Status = "success", Data = resolvedInputs[0] });
        }
    }

    [ToolMeta(Group = null, ContinueLoop = false, ExpressAvailable = true)]
    public class SendMediaTool : ITool
    {
        public string Name => "send_media";
        public string Description => "发送图片、表情包、语音或文件（支持本地路径或URL）";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("type", "媒体类型：image / sticker / voice / file", 0),
            new("path", "本地文件路径或网络URL", 1),
            new("text", "（可选）和媒体一起发送的文字内容", 2)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(10);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 2 || string.IsNullOrWhiteSpace(resolvedInputs[1]))
                return Task.FromResult(new ToolResult { Status = "failed", Error = "类型和路径不能为空" });

            var type = resolvedInputs[0].Trim().ToLower();
            var path = resolvedInputs[1].Trim();
            var text = resolvedInputs.Count > 2 ? resolvedInputs[2] : null;

            return Task.FromResult(new ToolResult
            {
                Status = "success",
                Data = $"{type}|{path}" + (text != null ? $"|{text}" : "")
            });
        }
    }
}
