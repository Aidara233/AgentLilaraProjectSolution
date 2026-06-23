// AgentCoreProcessor/Engine/Messaging/MessageTypes.cs
namespace AgentCoreProcessor.Engine.Messaging;

/// <summary>
/// 引擎内部入向消息 DTO（区别于 Adapter 层的 IncomingMessage）。
/// </summary>
public class EngineIncomingMessage
{
    public int ChannelId { get; set; }
    public string MessageId { get; set; } = "";
    public string Content { get; set; } = "";
    public int SenderUserId { get; set; }
    public string Tag { get; set; } = "normal";  // normal / mention / command
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// 引擎内部出向消息 DTO。
/// </summary>
public class EngineOutgoingMessage
{
    public int ChannelId { get; set; }
    public string Content { get; set; } = "";
    public bool SentByBot { get; set; } = true;
    public bool InternalOnly { get; set; } = false;
}
