using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Tool
{
    internal class SendMediaTool : ITool
    {
        public string Name => "发送媒体";
        public string Description => "发送图片、表情包、语音或文件（支持本地路径或URL）";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("类型", "媒体类型：image / sticker / voice / file", 0),
            new("路径", "本地文件路径或网络URL", 1),
            new("附带文字", "（可选）和媒体一起发送的文字内容", 2)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);
        public bool AllowSubAgent => false;
        public string? CapabilitySummary => "发送图片、表情包、语音、文件";

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var type = resolvedInputs.Count > 0 ? resolvedInputs[0]?.Trim().ToLowerInvariant() : null;
            var path = resolvedInputs.Count > 1 ? resolvedInputs[1]?.Trim() : null;
            var text = resolvedInputs.Count > 2 ? resolvedInputs[2]?.Trim() : null;

            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(path))
                return Task.FromResult(new ToolResult
                {
                    Status = "failed",
                    Error = "必须提供类型和路径"
                });

            var validTypes = new HashSet<string> { "image", "sticker", "voice", "file" };
            if (!validTypes.Contains(type))
                return Task.FromResult(new ToolResult
                {
                    Status = "failed",
                    Error = $"无效类型 \"{type}\"，可用：image, sticker, voice, file"
                });

            var payload = JsonConvert.SerializeObject(new { type, path, text });
            return Task.FromResult(new ToolResult
            {
                Status = "success",
                Data = payload
            });
        }
    }
}
