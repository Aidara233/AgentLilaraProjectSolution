namespace AgentLilara.SessionBrain;

/// <summary>
/// 会话模型面由谁执行。
/// <para>⚠️ 默认 <see cref="Off"/>：Legacy 路径**一个字节都不经过接缝**。
/// 这是切流前唯一安全的默认值——接缝本身在影子模式下才会被真正调用。</para>
/// </summary>
public enum SessionBrainMode
{
    /// <summary>不启用内核；Legacy 直接执行（默认）。</summary>
    Off = 0,

    /// <summary>Legacy 照常执行；内核只旁路构造请求并记录差异，不产生外部副作用。</summary>
    Shadow = 1,

    /// <summary>内核接管模型面（尚未实现；配置成它应当被拒绝而不是静默退回 Off）。</summary>
    Live = 2,
}

/// <summary>
/// 会话模型面的一次调用的接缝。
/// <para>⚠️ 实现方必须保证：<b>影子实现不得写 ALP 历史、不得执行有副作用的工具、不得外发消息</b>。</para>
/// </summary>
public interface ISessionBrain
{
    /// <summary>人类可读的引擎标识（"kernel" 等），用于日志与报告。</summary>
    string Engine { get; }

    Task<SessionBrainResult> InvokeAsync(SessionBrainRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 一次模型调用需要的全部输入，**已经由 ALP 侧转成中性形态**。
/// <para>⚠️ 它只描述「这一次请求」，不携带会话状态、不携带工具执行器。</para>
/// </summary>
public sealed record SessionBrainRequest
{
    public required string AlpSessionId { get; init; }

    /// <summary>ALP 会话类型（express / channel / task / review）。</summary>
    public required string EngineType { get; init; }

    public required string WorkingDirectory { get; init; }

    /// <summary>实际会作为系统提示发送的文本（Legacy 的 SessionPrompt ?? BasePrompt）。</summary>
    public required string SystemPrompt { get; init; }

    /// <summary>**不含**系统提示的消息序列；系统提示单独在 <see cref="SystemPrompt"/>。</summary>
    public required IReadOnlyList<SessionBrainMessage> Messages { get; init; }

    public required IReadOnlyList<SessionBrainTool> Tools { get; init; }

    public required SessionBrainProvider Provider { get; init; }
}

public sealed record SessionBrainMessage(
    string Role,
    IReadOnlyList<SessionBrainPart> Parts,
    string? Name = null);

public abstract record SessionBrainPart;

public sealed record SessionBrainText(string Text) : SessionBrainPart;

/// <summary>图片。⚠️ 路径与 base64 可能同时缺失——那是一条无法被内核表示的输入，必须显式报告。</summary>
public sealed record SessionBrainImage(string? Path, string? Base64, string? MediaType) : SessionBrainPart;

public sealed record SessionBrainToolUse(string ToolUseId, string ToolName, string InputJson) : SessionBrainPart;

public sealed record SessionBrainToolResult(string ToolUseId, string Content, bool IsError) : SessionBrainPart;

public sealed record SessionBrainTool(string Name, string Description, string ParametersJson);

/// <summary>
/// 提供商/模型参数。⚠️ 字段**按 ALP 配置的语义**命名，不按任何一家的 SDK 命名。
/// </summary>
public sealed record SessionBrainProvider
{
    /// <summary>"openai" | "claude"（ALP 的 provider 取值）。</summary>
    public required string Provider { get; init; }

    public required string Endpoint { get; init; }
    public required string ApiKey { get; init; }
    public required string Model { get; init; }
    public required double Temperature { get; init; }
    public int? MaxTokens { get; init; }

    public bool EnableCaching { get; init; }
    public bool UseNativeTools { get; init; }
    public bool ForceToolCall { get; init; }
    public bool DisableImageMessages { get; init; }

    /// <summary>ALP 透传到请求体的额外字段。内核没有对应能力时**必须报告**而不是丢弃。</summary>
    public IReadOnlyDictionary<string, string>? ExtraBody { get; init; }
}

public sealed record SessionBrainResult(
    string? Text,
    string? Thinking,
    IReadOnlyList<SessionBrainToolCall> ToolCalls,
    SessionBrainUsage? Usage);

public sealed record SessionBrainToolCall(
    string Tool,
    IReadOnlyList<string> Inputs,
    string? ToolUseId,
    string? RawInputJson);

public sealed record SessionBrainUsage(
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    int? CachedTokens);
