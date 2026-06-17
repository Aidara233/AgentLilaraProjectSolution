using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.BasicTools
{
    [ToolMeta(Group = null, ContinueLoop = false, OutputOnly = true)]
    public class SpeakTool : ITool
    {
        private static readonly Random _rng = new();
        private static readonly Regex _inlineWaitRegex = new(
            @"\[wait(?::([^\]]*))?\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly IChannelAccess? _channelAccess;
        private readonly int _channelId;
        private readonly ISpeakGuard? _speakGuard;

        public SpeakTool() { }

        public SpeakTool(IChannelAccess channelAccess, int channelId, ISpeakGuard? speakGuard = null)
        {
            _channelAccess = channelAccess;
            _channelId = channelId;
            _speakGuard = speakGuard;
        }

        public string Name => "speak";
        public string Description => "逐条发送消息到当前频道。content 为一个字符串，"
            + "多条消息单独空一行分隔，每条单独发送，建议常用分条功能以发送多条消息。"
            + "支持图文混排（<img work=\"rel/path\"/> 引用 Workspace 图片，"
            + "<img hash=\"xxx\"/> 引用图库图片）。可用标签：<at user=\"名字\"/> @提及、<reply id=\"消息ID\"/> 回复消息";
        public IReadOnlyList<ToolParameter> Parameters =>
            [new("content", "多条消息单独空一行分隔，每条单独发送，建议常用分条功能以发送多条消息。（可含 <img/> <at/> <reply/> 标签）", 0)];
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
                        ["type"] = "string",
                        ["description"] = "要发送的消息，多条消息单独空一行分隔，每条单独发送，建议常用分条功能以发送多条消息。（可含 <img/> <at/> <reply/> 标签）"
                    }
                },
                ["required"] = new JsonArray { "content" }
            };
        }

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 1 || string.IsNullOrWhiteSpace(resolvedInputs[0]))
                return new ToolResult { Status = "failed", Error = "消息内容不能为空" };

            // 防话唠阻断：连续多轮只说话不做实际工作时阻止 speak 调用
            if (_speakGuard?.IsBlocked == true)
                return new ToolResult { Status = "failed", Error = "消息发送失败：你已连续两轮只说话不做实际工作，本轮 speak 被阻断" };

            var messages = ParseArrayInput(resolvedInputs[0]);
            if (messages.Count == 0)
                return new ToolResult { Status = "failed", Error = "消息内容不能为空" };

            // 检测内联 [wait] / [wait:reason] 占位符，剥离后设 RequestWait
            var shouldWait = false;
            string? waitReason = null;
            var filteredMessages = new List<string>(messages.Count);
            foreach (var msg in messages)
            {
                var match = _inlineWaitRegex.Match(msg);
                if (match.Success)
                {
                    shouldWait = true;
                    if (waitReason == null && match.Groups[1].Success)
                        waitReason = match.Groups[1].Value.Trim();
                    var cleaned = _inlineWaitRegex.Replace(msg, "").Trim();
                    if (!string.IsNullOrWhiteSpace(cleaned))
                        filteredMessages.Add(cleaned);
                }
                else
                {
                    filteredMessages.Add(msg);
                }
            }

            if (_channelAccess == null)
            {
                var result = new ToolResult
                {
                    Status = "success",
                    Data = string.Join("\n\n", filteredMessages)
                };
                if (shouldWait) { result.RequestWait = true; result.WaitReason = waitReason; }
                return result;
            }

            if (filteredMessages.Count == 0 && !shouldWait)
                return new ToolResult { Status = "failed", Error = "消息内容不能为空" };

            var sentIds = new List<string>();
            for (int i = 0; i < filteredMessages.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var sentId = await _channelAccess.SendMessageAsync(_channelId, filteredMessages[i]);
                if (sentId != null)
                    sentIds.Add(sentId);

                if (i < filteredMessages.Count - 1)
                {
                    var delayMs = _rng.Next(1000, 2001);
                    await Task.Delay(delayMs, ct);
                }
            }

            var toolResult = sentIds.Count > 0
                ? new ToolResult { Status = "success", Data = string.Join(",", sentIds) }
                : new ToolResult { Status = "failed", Error = "消息发送失败" };
            if (shouldWait) { toolResult.RequestWait = true; toolResult.WaitReason = waitReason; }
            return toolResult;
        }

        private static List<string> ParseArrayInput(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return [];

            // 主路径：\n\n 分隔符切分多条消息
            var separator = "\n\n";
            if (raw.Contains(separator))
            {
                var parts = raw.Split(separator, StringSplitOptions.None);
                var list = new List<string>();
                foreach (var p in parts)
                {
                    var trimmed = p.Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed))
                        list.Add(trimmed);
                }
                if (list.Count > 0) return list;
            }

            // 最终 fallback：单条字符串
            return [raw];
        }
    }

    [ToolMeta(Group = null, ContinueLoop = false, OutputOnly = true)]
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

    [ToolMeta(Group = null, ContinueLoop = true, OutputOnly = false)]
    public class ThinkingTool : ITool
    {
        public string Name => "thinking";
        public string Description => "记录你的内部思考过程，不会发送消息。适合用于推理、分析、草稿等场景。";
        public IReadOnlyList<ToolParameter> Parameters =>
            [new("content", "思考内容", 0)];
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);

        public JsonNode GetInputSchema()
        {
            return new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["content"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "思考内容"
                    }
                },
                ["required"] = new JsonArray { "content" }
            };
        }

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 1 || string.IsNullOrWhiteSpace(resolvedInputs[0]))
                return Task.FromResult(new ToolResult { Status = "failed", Error = "思考内容不能为空" });

            return Task.FromResult(new ToolResult { Status = "success" });
        }
    }

    [ToolMeta(Group = null, ContinueLoop = false, OutputOnly = true)]
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
