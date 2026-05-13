// AgentLilara.PluginSDK/ShutdownResponse.cs
namespace AgentLilara.PluginSDK;

public record ShutdownResponse(bool Allow, string? Reason = null)
{
    public static ShutdownResponse Ok => new(true);
    public static ShutdownResponse NotReady(string reason) => new(false, reason);
}
