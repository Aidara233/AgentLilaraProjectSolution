// AgentLilara.PluginSDK/Events/BuiltInEvents.cs
namespace AgentLilara.PluginSDK.Events;

public record MessageReceived(string SessionId, string SenderId, string Content);
public record LoopActivated(string SessionId, string Reason);
public record LoopPausing(string SessionId);
public record TaskArrived(string TaskId, string Description);
public record ComponentStateChanged(string ComponentName, bool IsEnabled, string SessionId);
