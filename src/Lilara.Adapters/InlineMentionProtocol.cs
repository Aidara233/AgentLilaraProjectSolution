namespace AgentCoreProcessor.Adapter;

/// <summary>兼容文本发送路径中的内联 @ 占位符。</summary>
public static class InlineMentionProtocol
{
    public const char Delimiter = '\x01';
    public const string Prefix = "AT:";
}
