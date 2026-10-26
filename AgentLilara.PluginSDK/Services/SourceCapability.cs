namespace AgentLilara.PluginSDK.Services;

/// <summary>一个信源能提供什么：给工具描述与错误提示用，让模型不用猜。</summary>
public sealed class SourceCapability
{
    /// <summary>信源类别（`mail` / `webhook`…），也就是订阅时要填的 `source`。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>该信源会提供的载荷字段。为空表示它没有声明（不参与键校验）。</summary>
    public IReadOnlyList<string> Fields { get; set; } = Array.Empty<string>();
}
