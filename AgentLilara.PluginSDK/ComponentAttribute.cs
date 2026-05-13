// AgentLilara.PluginSDK/ComponentAttribute.cs
namespace AgentLilara.PluginSDK;

[AttributeUsage(AttributeTargets.Class)]
public class ComponentAttribute : Attribute
{
    public required string Name { get; set; }
    public ComponentScope Scope { get; set; } = ComponentScope.Global;
}

[AttributeUsage(AttributeTargets.Class)]
public class LoopApplicabilityAttribute : Attribute
{
    public Applicability Channel { get; set; } = Applicability.Enabled;
    public Applicability System { get; set; } = Applicability.Enabled;
}

[AttributeUsage(AttributeTargets.Class)]
public class ToolVisibilityAttribute : Attribute
{
    public Visibility Default { get; set; } = Visibility.FollowState;
}
