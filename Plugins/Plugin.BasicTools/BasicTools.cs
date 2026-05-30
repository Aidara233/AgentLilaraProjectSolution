using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.BasicTools
{
    [ToolMeta(Group = null, ContinueLoop = false, ExpressAvailable = true, OutputOnly = true)]
    public class SpeakTool : ITool
    {
        private static readonly Random _rng = new();

        private readonly IChannelAccess? _channelAccess;
        private readonly int _channelId;

        public SpeakTool() { }

        public SpeakTool(IChannelAccess channelAccess, int channelId)
        {
            _channelAccess = channelAccess;
            _channelId = channelId;
        }

        public string Name => "speak";
        public string Description => "逐条发送消息到当前频道。content 为字符串数组，每条单独发送，"
            + "支持图文混排（<img work=\"rel/path\"/> 引用 Workspace 图片，"
            + "<img hash=\"xxx\"/> 引用图库图片）。可用标签：<at user=\"名字\"/> @提及、<reply id=\"消息ID\"/> 回复消息";
        public IReadOnlyList<ToolParameter> Parameters =>
            [new("content", "要发送的消息数组，每条为独立消息（可含 <img/> <at/> <reply/> 标签）", 0)];
        public TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public JsonNode GetInputSchema()
        {
            return new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["content"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "要发送的消息数组，每条为独立消息（可含 <img/> <at/> <reply/> 标签）"
                    }
                },
                ["required"] = new JsonArray { "content" }
            };
        }

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 1 || string.IsNullOrWhiteSpace(resolvedInputs[0]))
                return new ToolResult { Status = "failed", Error = "消息内容不能为空" };

            var messages = ParseArrayInput(resolvedInputs[0]);
            if (messages.Count == 0)
                return new ToolResult { Status = "failed", Error = "消息内容不能为空" };

            if (_channelAccess == null)
                return new ToolResult { Status = "success", Data = string.Join("\n---\n", messages) };

            var sentIds = new List<string>();
            for (int i = 0; i < messages.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var sentId = await _channelAccess.SendMessageAsync(_channelId, messages[i]);
                if (sentId != null)
                    sentIds.Add(sentId);

                if (i < messages.Count - 1)
                {
                    var delayMs = _rng.Next(1000, 2001);
                    await Task.Delay(delayMs, ct);
                }
            }

            return sentIds.Count > 0
                ? new ToolResult { Status = "success", Data = string.Join(",", sentIds) }
                : new ToolResult { Status = "failed", Error = "消息发送失败" };
        }

        private static List<string> ParseArrayInput(string raw)
        {
            try
            {
                var parsed = JsonNode.Parse(raw) as JsonArray;
                if (parsed == null) return [];
                var list = new List<string>();
                foreach (var item in parsed)
                {
                    if (item != null)
                    {
                        var s = item.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(s))
                            list.Add(s);
                    }
                }
                return list;
            }
            catch
            {
                // 兼容旧格式：单条字符串
                if (!string.IsNullOrWhiteSpace(raw))
                    return [raw];
                return [];
            }
        }
    }

    [ToolMeta(Group = null, ContinueLoop = false, ExpressAvailable = true, OutputOnly = true)]
    public class SendFileTool : ITool
    {
        private readonly IChannelAccess? _channelAccess;
        private readonly int _channelId;

        public SendFileTool() { }

        public SendFileTool(IChannelAccess channelAccess, int channelId)
        {
            _channelAccess = channelAccess;
            _channelId = channelId;
        }

        public string Name => "send_file";
        public string Description => "发送文件到当前频道。file_path: hash:xxx（图库）或 Workspace 相对路径。"
            + "file_name 可选，不填则使用原文件名。";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("file_path", "文件路径：hash:xxx（图库）或 Workspace 相对路径", 0),
            new("file_name", "可选，自定义显示文件名", 1)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(15);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 1 || string.IsNullOrWhiteSpace(resolvedInputs[0]))
                return Task.FromResult(new ToolResult { Status = "failed", Error = "文件路径不能为空" });

            var filePath = resolvedInputs[0].Trim();
            var fileName = resolvedInputs.Count > 1 ? resolvedInputs[1]?.Trim() : null;

            if (_channelAccess != null)
            {
                var channelAccess = _channelAccess;
                var channelId = _channelId;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var sentId = await channelAccess.SendFileAsync(channelId, filePath, fileName);
                        if (sentId != null)
                        {
                            var displayName = fileName ?? System.IO.Path.GetFileName(filePath);
                            await channelAccess.SendMessageAsync(channelId, $"文件 {displayName} 发送成功");
                        }
                    }
                    catch { /* 后台发送失败，适配器层已记日志 */ }
                }, CancellationToken.None);
                return Task.FromResult(new ToolResult { Status = "success", Data = $"[文件发送已提交] {filePath}" });
            }

            return Task.FromResult(new ToolResult { Status = "success", Data = filePath });
        }
    }

    [ToolMeta(Group = null, ContinueLoop = false, ExpressAvailable = true, OutputOnly = true)]
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
        public string Description => "发送独立媒体（语音/视频/贴纸），不适合和文字混排。图片请用 speak 的 <img/> 标签。"
            + "identifier: hash:xxx（图库）或 Workspace 相对路径。";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("type", "媒体类型：image / sticker / voice / video", 0),
            new("identifier", "hash:xxx（图库图片）或 Workspace 相对路径", 1)
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
