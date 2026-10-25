using AgentCoreProcessor.Core;
using AgentCoreProcessor.Models;
using AgentLilara.SessionBrain;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Engine.Session.Brain;

/// <summary>
/// 把 **Legacy 真正交出去的那一份请求** 转成中性形态。
///
/// <para>⚠️ 判据是「<b>客户端里现在有什么</b>」，不是「<c>_history</c> 里有什么」：
/// `AgentCore.InvokeAsync` 会在客户端里 prepend 系统提示、`ApplyExtraMessages` 还会插预设消息。
/// 影子比较的对象必须是**实际会发出去的那一份**，否则比的是我们以为的请求。</para>
///
/// <para>⚠️ 本类只读快照，不改任何 Legacy 状态。</para>
/// </summary>
internal static class LegacyRequestMapper
{
    public static SessionBrainRequest Snapshot(
        AgentCore core, string sessionId, string engineType, string workingDirectory,
        string? wireBody = null)
    {
        var client = core.ClientForBrain;
        var systemPrompt = core.EffectiveSystemPrompt;
        var history = client.GetConversationHistory();

        var messages = new List<SessionBrainMessage>(history.Count);
        for (var i = 0; i < history.Count; i++)
        {
            var message = history[i];

            // ⚠️ 只剥**第一条**且与系统提示逐字相同的那条 system 消息。
            // 其余 system 消息（错误提示、注入通知）是对话的一部分，不能被折进系统提示。
            if (i == 0 && message.Role == "system" && string.Equals(message.Content, systemPrompt, StringComparison.Ordinal))
                continue;

            messages.Add(ToMessage(message));
        }

        var tools = (client.GetTools() ?? [])
            .Select(t => new SessionBrainTool(t.Name, t.Description, t.Parameters.ToJsonString()))
            .ToList();

        return new SessionBrainRequest
        {
            AlpSessionId = sessionId,
            EngineType = engineType,
            WorkingDirectory = workingDirectory,
            SystemPrompt = systemPrompt,
            Messages = messages,
            Tools = tools,
            Provider = ToProvider(client.Config),
            WireBody = wireBody,
        };
    }

    internal static SessionBrainMessage ToMessage(Message message)
    {
        var parts = message.ContentParts is { Count: > 0 }
            ? message.ContentParts.Select(ToPart).ToList()
            : [new SessionBrainText(message.Content)];

        return new SessionBrainMessage(message.Role, parts, message.Name);
    }

    internal static SessionBrainPart ToPart(ContentPart part) => part.Type switch
    {
        "text" => new SessionBrainText(part.Text ?? ""),
        "image" => new SessionBrainImage(part.ImagePath, part.ImageBase64, part.MediaType),
        "tool_use" => new SessionBrainToolUse(
            part.ToolUseId ?? "", part.ToolName ?? "", part.ToolInput ?? "{}"),
        "tool_result" => new SessionBrainToolResult(
            part.ToolUseId ?? "", part.Text ?? "", part.IsError == true),
        // ⚠️ 未知类型**保留原文**而不是静默丢弃：影子比较必须能看见「有东西丢了」。
        _ => new SessionBrainText($"[unsupported content part: {part.Type}]"),
    };

    internal static SessionBrainProvider ToProvider(Client.ApiClientCfg config) => new()
    {
        Provider = config.Provider,
        Endpoint = config.ApiEndpoint,
        ApiKey = config.ApiKey,
        Model = config.Model,
        Temperature = config.Temperature,
        MaxTokens = config.MaxTokens,
        EnableCaching = config.ShouldEnableCaching(),
        UseNativeTools = config.UseNativeTools,
        ForceToolCall = config.ForceToolCall,
        DisableImageMessages = config.DisableImageMessages,
        ExtraBody = config.ExtraBody?.ToDictionary(
            pair => pair.Key,
            pair => Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
            StringComparer.Ordinal),
    };
}
