using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AgentLilara.SessionBrain;
using Lilara.Core.Kernel;
using Lilara.Core.Protocol;

namespace AgentLilara.AlcBridge;

/// <summary>协议无关的请求视图：只保留**会改变模型行为**的字段。</summary>
public sealed record WireView(
    string Model,
    double? Temperature,
    int? MaxTokens,
    string System,
    IReadOnlyList<string> Items,
    IReadOnlyList<WireTool> Tools);

public sealed record WireTool(string Name, string Description, string ParametersJson);

public sealed record WireComparison(
    IReadOnlyList<string> Differences,
    IReadOnlyList<string> Notes);

/// <summary>
/// 线上请求体比较：**Legacy 实际发出去的 JSON** vs **内核会构造的请求**。
///
/// <para>⚠️ 比的是语义字段，不逐字节比：`stream`、缓存标记、SDK 附加键这些
/// 不影响「模型看到什么」的差异只记进 Notes。逐字节比较会在协议层噪音里淹掉真问题。</para>
///
/// <para>⚠️ 没有捕获到 Legacy 请求体时**只记 Note**，不算差异——捕获是可选能力，
/// 不能因为没开就报「不等价」。</para>
/// </summary>
public static class WireComparer
{
    public static WireComparison Compare(SessionBrainRequest legacy, ModelRequest kernel)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(kernel);

        if (string.IsNullOrWhiteSpace(legacy.WireBody))
            return new([], ["wire：本次没有捕获到线上请求体（捕获未启用，或该协议没有钩子）"]);

        string protocol;
        try
        {
            protocol = ProtocolOf(legacy.Provider.Provider);
        }
        catch (NotSupportedException ex)
        {
            return new([], [$"wire：{ex.Message}"]);
        }

        WireView alp;
        try
        {
            alp = AlpWireReader.Read(legacy.WireBody!, protocol);
        }
        catch (Exception ex)
        {
            return new([], [$"wire：Legacy 请求体无法解析（{ex.GetType().Name}: {ex.Message}）"]);
        }

        var kernelView = KernelWireView.From(kernel);
        var differences = new List<string>();
        var notes = new List<string>();

        if (!string.Equals(alp.Model, kernelView.Model, StringComparison.Ordinal))
            differences.Add($"wire.model：Legacy '{alp.Model}' vs 内核 '{kernelView.Model}'");

        if (alp.Temperature != kernelView.Temperature)
            differences.Add($"wire.temperature：Legacy {alp.Temperature} vs 内核 {kernelView.Temperature}");

        if (alp.MaxTokens != kernelView.MaxTokens)
            differences.Add($"wire.maxTokens：Legacy {alp.MaxTokens} vs 内核 {kernelView.MaxTokens}");

        if (!string.Equals(alp.System, kernelView.System, StringComparison.Ordinal))
            differences.Add($"wire.system：Legacy {alp.System.Length} 字 vs 内核 {kernelView.System.Length} 字");

        CompareItems(alp.Items, kernelView.Items, differences);
        CompareTools(alp.Tools, kernelView.Tools, differences);

        if (protocol == Protocols.OpenAiChat && StreamFlag(legacy.WireBody!))
            notes.Add("wire：Legacy 以流式发送，内核当前非流式（已知差异，不在本比较范围内）");

        return new WireComparison(differences, notes);
    }

    private static bool StreamFlag(string body)
    {
        try
        {
            return JsonNode.Parse(body)?["stream"]?.GetValue<bool>() == true;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string ProtocolOf(string provider) => provider.ToLowerInvariant() switch
    {
        "claude" => Protocols.ClaudeMessages,
        "openai" => Protocols.OpenAiChat,
        _ => throw new NotSupportedException($"不知道 provider '{provider}' 的线上协议，跳过 wire 比较"),
    };

    private static void CompareItems(
        IReadOnlyList<string> alp, IReadOnlyList<string> kernel, List<string> differences)
    {
        if (alp.Count != kernel.Count)
            differences.Add($"wire.messages：Legacy {alp.Count} 项 vs 内核 {kernel.Count} 项");

        var shared = Math.Min(alp.Count, kernel.Count);
        for (var i = 0; i < shared; i++)
        {
            if (!string.Equals(alp[i], kernel[i], StringComparison.Ordinal))
                differences.Add($"wire.messages[{i}]：Legacy {alp[i]} vs 内核 {kernel[i]}");
        }
    }

    private static void CompareTools(
        IReadOnlyList<WireTool> alp, IReadOnlyList<WireTool> kernel, List<string> differences)
    {
        if (alp.Count != kernel.Count)
        {
            differences.Add($"wire.tools：Legacy {alp.Count} 个 vs 内核 {kernel.Count} 个");
            return;
        }

        for (var i = 0; i < alp.Count; i++)
        {
            if (!string.Equals(alp[i].Name, kernel[i].Name, StringComparison.Ordinal))
                differences.Add($"wire.tools[{i}].name：Legacy '{alp[i].Name}' vs 内核 '{kernel[i].Name}'");
            if (!string.Equals(alp[i].Description, kernel[i].Description, StringComparison.Ordinal))
                differences.Add($"wire.tools[{i}].description 不一致");
            if (!JsonEquals(alp[i].ParametersJson, kernel[i].ParametersJson))
                differences.Add($"wire.tools[{i}].parameters 不一致");
        }
    }

    private static bool JsonEquals(string a, string b)
    {
        try
        {
            return JsonNode.DeepEquals(JsonNode.Parse(a), JsonNode.Parse(b));
        }
        catch (System.Text.Json.JsonException)
        {
            return string.Equals(a, b, StringComparison.Ordinal);
        }
    }

    public static string ImageKeyFromDataUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "<empty>";

        const string marker = "base64,";
        var index = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return url;

        try
        {
            var bytes = Convert.FromBase64String(url[(index + marker.Length)..]);
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        catch (FormatException)
        {
            return "<invalid-base64>";
        }
    }
}

/// <summary>从 Legacy 实际发出的 JSON 提取 <see cref="WireView"/>。</summary>
internal static class AlpWireReader
{
    public static WireView Read(string body, string protocol) => protocol switch
    {
        Protocols.OpenAiChat => FromOpenAiChat(body),
        Protocols.ClaudeMessages => FromClaude(body),
        _ => throw new NotSupportedException($"wire：协议 '{protocol}' 还没有读取器"),
    };

    private static WireView FromOpenAiChat(string body)
    {
        var root = JsonNode.Parse(body)?.AsObject()
            ?? throw new FormatException("请求体不是 JSON 对象");

        var system = "";
        var items = new List<string>();
        var tools = new List<WireTool>();

        foreach (var message in root["messages"]?.AsArray() ?? [])
        {
            if (message is not JsonObject m) continue;
            var role = (string?)m["role"] ?? "";

            if (role == "system")
            {
                system += TextOf(m["content"]);
                continue;
            }

            var normalized = role == "assistant" ? "model" : "user";

            if (m["content"] is JsonArray parts)
            {
                foreach (var part in parts)
                {
                    if (part is not JsonObject p) continue;
                    var type = (string?)p["type"] ?? "";
                    if (type == "text")
                        items.Add($"{normalized}/text:{TextOf(p["text"])}");
                    else if (type == "image_url")
                        items.Add($"{normalized}/image:" +
                            WireComparer.ImageKeyFromDataUrl((string?)p["image_url"]?["url"]));
                }
            }
            else if (m["content"] is not null)
            {
                items.Add($"{normalized}/text:{TextOf(m["content"])}");
            }

            foreach (var call in m["tool_calls"]?.AsArray() ?? [])
            {
                if (call is not JsonObject c) continue;
                var id = (string?)c["id"] ?? "";
                var name = (string?)c["function"]?["name"] ?? "";
                var arguments = (string?)c["function"]?["arguments"] ?? "{}";
                items.Add($"model/tool_use:{id}:{name}:{arguments}");
            }

            if (role == "tool")
                items.Add($"user/tool_result:{(string?)m["tool_call_id"] ?? ""}:{TextOf(m["content"])}");
        }

        foreach (var tool in root["tools"]?.AsArray() ?? [])
        {
            if (tool is not JsonObject t) continue;
            var function = t["function"] as JsonObject;
            if (function is null) continue;
            tools.Add(new WireTool(
                (string?)function["name"] ?? "",
                (string?)function["description"] ?? "",
                function["parameters"]?.ToJsonString() ?? "{}"));
        }

        return new WireView(
            (string?)root["model"] ?? "",
            (double?)root["temperature"],
            (int?)root["max_tokens"],
            system,
            items,
            tools);
    }

    private static WireView FromClaude(string body)
    {
        var root = JsonNode.Parse(body)?.AsObject()
            ?? throw new FormatException("请求体不是 JSON 对象");

        var system = root["system"] is JsonArray systemBlocks
            ? string.Concat(systemBlocks.Select(p => TextOf(p?["text"])))
            : TextOf(root["system"]);

        var items = new List<string>();
        foreach (var message in root["messages"]?.AsArray() ?? [])
        {
            if (message is not JsonObject m) continue;
            var role = (string?)m["role"] ?? "";
            var normalized = role == "assistant" ? "model" : "user";

            if (m["content"] is JsonArray parts)
            {
                foreach (var part in parts)
                {
                    if (part is not JsonObject p) continue;
                    var type = (string?)p["type"] ?? "";
                    switch (type)
                    {
                        case "text":
                            items.Add($"{normalized}/text:{TextOf(p["text"])}");
                            break;
                        case "image":
                            var data = (string?)p["source"]?["data"] ?? "";
                            items.Add($"{normalized}/image:" + HashBase64(data));
                            break;
                        case "tool_use":
                            items.Add($"model/tool_use:{(string?)p["id"]}:{(string?)p["name"]}:"
                                + (p["input"]?.ToJsonString() ?? "{}"));
                            break;
                        case "tool_result":
                            items.Add($"user/tool_result:{(string?)p["tool_use_id"]}:"
                                + TextOf(p["content"]));
                            break;
                    }
                }
            }
            else if (m["content"] is not null)
            {
                items.Add($"{normalized}/text:{TextOf(m["content"])}");
            }
        }

        var tools = (root["tools"]?.AsArray() ?? [])
            .OfType<JsonObject>()
            .Select(t => new WireTool(
                (string?)t["name"] ?? "",
                (string?)t["description"] ?? "",
                t["input_schema"]?.ToJsonString() ?? "{}"))
            .ToList();

        return new WireView(
            (string?)root["model"] ?? "",
            (double?)root["temperature"],
            (int?)root["max_tokens"],
            system,
            items,
            tools);
    }

    private static string TextOf(JsonNode? node)
    {
        if (node is null) return "";

        if (node is JsonValue value)
        {
            // ⚠️ 用 GetValue<string>()：JsonValue.ToString() 给的是 JSON 表示（字符串会带引号），
            // 而这里要的是**原文**——带引号会让每一段文本都多两个假字符。
            try { return value.GetValue<string>() ?? ""; }
            catch (InvalidOperationException) { }
            catch (FormatException) { }
            return value.ToString();
        }

        return node.ToJsonString();
    }

    private static string HashBase64(string base64)
    {
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(base64)));
        }
        catch (FormatException)
        {
            return "<invalid-base64>";
        }
    }
}

/// <summary>从内核 <see cref="ModelRequest"/> 提取同一形状的视图。</summary>
internal static class KernelWireView
{
    public static WireView From(ModelRequest request)
    {
        var system = string.Concat(
            request.Slots.System.OfType<Block.TextBlock>().Select(b => b.Text));

        var items = new List<string>();
        foreach (var evt in request.Timeline)
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
                            _ => $"{role}/unknown",
                        });
                    }
                    break;

                case TimelineEvent.ToolResultEvent result:
                    var content = string.Concat(result.Content.OfType<Block.TextBlock>().Select(b => b.Text));
                    items.Add($"user/tool_result:{result.CallId}:{content}");
                    break;
            }
        }

        var tools = request.Tools
            .Select(t => new WireTool(t.Name, t.Description, t.ParametersSchemaJson))
            .ToList();

        return new WireView(
            request.ModelId,
            request.Temperature,
            request.MaxTokens,
            system,
            items,
            tools);
    }
}
