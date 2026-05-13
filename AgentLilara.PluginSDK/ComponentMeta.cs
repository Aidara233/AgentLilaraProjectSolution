// AgentLilara.PluginSDK/ComponentMeta.cs
namespace AgentLilara.PluginSDK;

public class ComponentMeta
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public bool DefaultEnabled { get; init; } = true;
    public int PromptPriority { get; init; } = 50;
}
