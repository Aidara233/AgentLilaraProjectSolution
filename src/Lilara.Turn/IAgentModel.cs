using AgentCoreProcessor.Models;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Core;

/// <summary>Session-facing model port; concrete clients and host observation stay behind it.</summary>
internal interface IAgentModel
{
    bool UseNativeTools { get; }
    bool DisableImageMessages { get; }
    string? SessionPrompt { set; }
    void ConfigureDefaults(string engineType, IReadOnlyList<ITool> tools);
    Task<ModelOutput> InvokeAsync(List<Message> messages, AgentInvokeRequest request);
}

internal sealed record AgentModelBinding(string ConfigurationName, string SessionId, string SessionType);
