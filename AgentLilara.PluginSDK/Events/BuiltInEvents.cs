// AgentLilara.PluginSDK/Events/BuiltInEvents.cs
namespace AgentLilara.PluginSDK.Events;

public record MessageReceived(string LoopId, string SenderId, string Content);
public record LoopActivated(string LoopId, string Reason);
public record LoopPausing(string LoopId);
public record TaskArrived(string TaskId, string Description);
public record ComponentStateChanged(string ComponentName, bool IsEnabled, string LoopId);
