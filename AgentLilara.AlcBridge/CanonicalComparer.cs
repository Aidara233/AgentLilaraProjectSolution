using AgentLilara.SessionBrain;
using Lilara.Core.Kernel;
using Lilara.Core.Protocol;

namespace AgentLilara.AlcBridge;

/// <summary>
/// Legacy 请求 vs 内核请求的**规范化比较**。
///
/// <para>⚠️ 当前比较的是「请求内容」而不是线上字节：线上等价还要经过协议适配器，
/// 那属于 S3 的金样测试。这里先回答最重要的一条——<b>转换有没有丢东西</b>。</para>
///
/// <para>⚠️ 扁平化只保留**顺序**，不比较消息边界：ALP 把 N 个 tool_result 装进一条消息，
/// ALC 落成 N 个事件。边界差异在协议适配上通常不可见，硬比会制造噪音。</para>
/// </summary>
public static class CanonicalComparer
{
    public static ComparisonResult Compare(
        SessionBrainRequest legacy, ModelRequest kernel, IReadOnlyList<string> buildNotes)
    {
        var differences = new List<string>();
        var unsupported = new List<string>(buildNotes);

        CompareSystemPrompt(legacy, kernel, differences);
        CompareProvider(legacy, kernel, differences);
        CompareTools(legacy, kernel, differences);
        CompareTimeline(legacy, kernel, differences);

        return new ComparisonResult(differences, unsupported);
    }

    private static void CompareSystemPrompt(
        SessionBrainRequest legacy, ModelRequest kernel, List<string> differences)
    {
        var kernelText = string.Concat(kernel.Slots.System.OfType<Block.TextBlock>().Select(b => b.Text));
        if (!string.Equals(legacy.SystemPrompt, kernelText, StringComparison.Ordinal))
            differences.Add($"system：Legacy 有 {legacy.SystemPrompt.Length} 字，内核有 {kernelText.Length} 字");
    }

    private static void CompareProvider(
        SessionBrainRequest legacy, ModelRequest kernel, List<string> differences)
    {
        if (!string.Equals(legacy.Provider.Model, kernel.ModelId, StringComparison.Ordinal))
            differences.Add($"model：Legacy '{legacy.Provider.Model}' vs 内核 '{kernel.ModelId}'");

        var expectedProtocol = legacy.Provider.Provider.ToLowerInvariant() switch
        {
            "claude" => Protocols.ClaudeMessages,
            "openai" => Protocols.OpenAiChat,
            _ => kernel.Protocol,
        };
        if (!string.Equals(expectedProtocol, kernel.Protocol, StringComparison.Ordinal))
            differences.Add($"protocol：期望 '{expectedProtocol}' vs 内核 '{kernel.Protocol}'");

        if (!legacy.Provider.Temperature.Equals(kernel.Temperature))
            differences.Add($"temperature：Legacy {legacy.Provider.Temperature} vs 内核 {kernel.Temperature}");

        if (legacy.Provider.MaxTokens is { } max && max != kernel.MaxTokens)
            differences.Add($"maxTokens：Legacy {max} vs 内核 {kernel.MaxTokens}");
    }

    private static void CompareTools(
        SessionBrainRequest legacy, ModelRequest kernel, List<string> differences)
    {
        if (legacy.Tools.Count != kernel.Tools.Count)
        {
            differences.Add($"tools：Legacy {legacy.Tools.Count} 个 vs 内核 {kernel.Tools.Count} 个");
            return;
        }

        for (var i = 0; i < legacy.Tools.Count; i++)
        {
            var a = legacy.Tools[i];
            var b = kernel.Tools[i];
            if (a.Name != b.Name)
                differences.Add($"tools[{i}].name：Legacy '{a.Name}' vs 内核 '{b.Name}'");
            if (a.Description != b.Description)
                differences.Add($"tools[{i}].description 不一致");
            if (a.ParametersJson != b.ParametersSchemaJson)
                differences.Add($"tools[{i}].parameters 不一致");
        }
    }

    private static void CompareTimeline(
        SessionBrainRequest legacy, ModelRequest kernel, List<string> differences)
    {
        var legacyItems = FlattenLegacy(legacy.Messages);
        var kernelItems = FlattenKernel(kernel.Timeline);

        if (legacyItems.Count != kernelItems.Count)
            differences.Add($"timeline：Legacy {legacyItems.Count} 项 vs 内核 {kernelItems.Count} 项");

        var shared = Math.Min(legacyItems.Count, kernelItems.Count);
        for (var i = 0; i < shared; i++)
        {
            if (legacyItems[i] != kernelItems[i])
                differences.Add($"timeline[{i}]：Legacy {legacyItems[i]} vs 内核 {kernelItems[i]}");
        }
    }

    private static List<string> FlattenLegacy(IReadOnlyList<SessionBrainMessage> messages)
    {
        var items = new List<string>();
        foreach (var message in messages)
        {
            var role = NormalizeLegacyRole(message.Role);
            foreach (var part in message.Parts)
            {
                items.Add(part switch
                {
                    SessionBrainText text => $"{role}/text:{text.Text}",
                    SessionBrainImage image => $"{role}/image:{ImageKey(image)}",
                    SessionBrainToolUse use =>
                        $"{role}/tool_use:{use.ToolUseId}:{use.ToolName}:{use.InputJson}",
                    SessionBrainToolResult result =>
                        $"{role}/tool_result:{result.ToolUseId}:{result.Content}"
                        + (result.IsError ? ":error" : ""),
                    _ => $"{role}/unknown:{part.GetType().Name}",
                });
            }
        }
        return items;
    }

    private static List<string> FlattenKernel(IReadOnlyList<TimelineEvent> timeline)
    {
        var items = new List<string>();
        foreach (var evt in timeline)
        {
            switch (evt)
            {
                case TimelineEvent.MessageEvent message:
                    var role = message.From == Speaker.Model ? "model" : "user";
                    foreach (var block in message.Blocks)
                    {
                        items.Add(block switch
                        {
                            Block.TextBlock text => $"{role}/text:{text.Text}",
                            Block.ImageBlock image => $"{role}/image:{image.Sha256}",
                            Block.ToolUseBlock use =>
                                $"{role}/tool_use:{use.CallId}:{use.Name}:{use.Arguments}",
                            Block.ReasoningBlock => $"{role}/reasoning",
                            Block.StoreBlock store => $"{role}/store:{store.Sha256}",
                            _ => $"{role}/unknown:{block.GetType().Name}",
                        });
                    }
                    break;

                case TimelineEvent.ToolResultEvent result:
                    var content = string.Concat(result.Content.OfType<Block.TextBlock>().Select(b => b.Text));
                    items.Add($"user/tool_result:{result.CallId}:{content}");
                    break;
            }
        }
        return items;
    }

    private static string NormalizeLegacyRole(string role) => role switch
    {
        "assistant" => "model",
        "user" or "tool" => "user",
        _ => role,
    };

    private static string ImageKey(SessionBrainImage image)
    {
        if (!KernelRequestBuilder.TryHashImage(image, out var sha, out _))
            return "<unavailable>";
        return sha;
    }
}

public sealed record ComparisonResult(
    IReadOnlyList<string> Differences,
    IReadOnlyList<string> Unsupported);
