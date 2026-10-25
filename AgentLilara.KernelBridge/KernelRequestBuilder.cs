using System.Security.Cryptography;
using AgentLilara.SessionBrain;
using Lilara.Core.Kernel;
using Lilara.Core.Protocol;
using Lilara.Core.Session;

namespace AgentLilara.KernelBridge;

/// <summary>一次转换的产物：内核请求 + 转换过程中发现的能力缺口。</summary>
public sealed record KernelBuild(ModelRequest Request, IReadOnlyList<string> Unsupported);

/// <summary>
/// 中性 DTO → ALC <see cref="ModelRequest"/>。
///
/// <para>⚠️ **只构造请求，不做任何 I/O 写操作**：图片按字节算哈希（只读），
/// 不建会话、不落盘、不调用模型、不执行工具。影子模式的安全性靠的就是这一条。</para>
/// </summary>
public static class KernelRequestBuilder
{
    public static KernelBuild Build(SessionBrainRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var unsupported = new List<string>();
        var protocol = ProtocolOf(request.Provider.Provider, unsupported);

        if (request.Provider.EnableCaching) unsupported.Add("provider.enableCaching（内核没有对应参数）");
        if (request.Provider.ForceToolCall) unsupported.Add("provider.forceToolCall（内核没有对应参数）");
        if (request.Provider.DisableImageMessages) unsupported.Add("provider.disableImageMessages（内核没有对应参数）");
        if (request.Provider.ExtraBody is { Count: > 0 }) unsupported.Add("provider.extraBody（内核没有透传通道）");
        if (request.Provider.MaxTokens is null) unsupported.Add("provider.maxTokens 未配置（Legacy 由客户端默认值决定）");

        var timeline = BuildTimeline(request.Messages, unsupported);
        var slots = string.IsNullOrEmpty(request.SystemPrompt)
            ? PromptSlots.Empty
            : new PromptSlots([new Block.TextBlock(request.SystemPrompt)], [], []);

        var tools = request.Tools
            .Select(t => new ToolSpec(t.Name, t.Description, t.ParametersJson))
            .ToList();

        var model = new ModelRequest
        {
            ModelId = request.Provider.Model,
            MaxTokens = request.Provider.MaxTokens ?? 0,
            Protocol = protocol,
            // ⚠️ ALP 没有 effort 这条轴。填 "default" 并在报告里注明——不能装作等价。
            Effort = "default",
            Temperature = request.Provider.Temperature,
            Slots = slots,
            Tools = tools,
            Timeline = timeline,
        };

        unsupported.Add("effort：ALP 没有对应配置，内核按 default 发出");
        return new KernelBuild(model, unsupported);
    }

    private static string ProtocolOf(string provider, List<string> unsupported) =>
        provider.ToLowerInvariant() switch
        {
            "claude" => Protocols.ClaudeMessages,
            "openai" => Protocols.OpenAiChat,
            _ => Unknown(provider, unsupported),
        };

    private static string Unknown(string provider, List<string> unsupported)
    {
        unsupported.Add($"provider.provider='{provider}'（内核不认识的协议，影子按 openai-chat 构造）");
        return Protocols.OpenAiChat;
    }

    private static List<TimelineEvent> BuildTimeline(
        IReadOnlyList<SessionBrainMessage> messages, List<string> unsupported)
    {
        var timeline = new List<TimelineEvent>();
        var at = DateTimeOffset.UtcNow;

        foreach (var message in messages)
        {
            var speaker = ToSpeaker(message.Role, unsupported);
            var pending = new List<Block>();

            void Flush()
            {
                if (pending.Count == 0) return;
                timeline.Add(new TimelineEvent.MessageEvent
                {
                    Id = Ids.New(),
                    At = at,
                    From = speaker,
                    Blocks = pending.ToList(),
                });
                pending.Clear();
            }

            foreach (var part in message.Parts)
            {
                switch (part)
                {
                    case SessionBrainText text:
                        pending.Add(new Block.TextBlock(text.Text));
                        break;

                    case SessionBrainImage image:
                        if (TryHashImage(image, out var sha, out var mediaType))
                            pending.Add(new Block.ImageBlock(sha, mediaType));
                        else
                            unsupported.Add("image：既没有 base64 也读不到路径，图片没有进入内核请求");
                        break;

                    case SessionBrainToolUse toolUse:
                        pending.Add(new Block.ToolUseBlock(
                            toolUse.ToolUseId, toolUse.ToolName, toolUse.InputJson));
                        break;

                    case SessionBrainToolResult result:
                        // ALC 的工具结果是**独立事件**，不是消息里的一块。
                        Flush();
                        timeline.Add(new TimelineEvent.ToolResultEvent
                        {
                            Id = Ids.New(),
                            At = at,
                            CallId = result.ToolUseId,
                            Content = [new Block.TextBlock(result.Content)],
                        });
                        if (result.IsError)
                            unsupported.Add($"tool_result {result.ToolUseId}：ALP 标了 IsError，内核没有对应字段");
                        break;
                }
            }

            Flush();
        }

        return timeline;
    }

    private static Speaker ToSpeaker(string role, List<string> unsupported) => role switch
    {
        "assistant" => Speaker.Model,
        "user" or "tool" => Speaker.User,
        // ⚠️ ALC 的时间线没有 system 发言者：把注入的系统消息降级成用户消息**是说谎**。
        // 影子请求仍要构造出来，所以这样落，但必须报告。
        "system" => SystemRole(unsupported),
        _ => UnknownRole(role, unsupported),
    };

    private static Speaker SystemRole(List<string> unsupported)
    {
        unsupported.Add("message.role='system'（内核时间线没有 system 发言者，已按 user 构造）");
        return Speaker.User;
    }

    private static Speaker UnknownRole(string role, List<string> unsupported)
    {
        unsupported.Add($"message.role='{role}'（未知角色，已按 user 构造）");
        return Speaker.User;
    }

    internal static bool TryHashImage(SessionBrainImage image, out string sha256, out string mediaType)
    {
        mediaType = image.MediaType ?? "image/png";

        byte[]? bytes = null;
        if (!string.IsNullOrEmpty(image.Base64))
        {
            try { bytes = Convert.FromBase64String(image.Base64); }
            catch (FormatException) { bytes = null; }
        }

        if (bytes is null && !string.IsNullOrEmpty(image.Path) && File.Exists(image.Path))
        {
            try { bytes = File.ReadAllBytes(image.Path); }
            catch (IOException) { bytes = null; }
            catch (UnauthorizedAccessException) { bytes = null; }
        }

        if (bytes is null)
        {
            sha256 = "";
            return false;
        }

        sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return true;
    }
}
