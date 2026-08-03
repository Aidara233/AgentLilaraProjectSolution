// AgentLilara.PluginSDK/ComponentAttribute.cs
namespace AgentLilara.PluginSDK;

[AttributeUsage(AttributeTargets.Class)]
public class ComponentAttribute : Attribute
{
    public required string Name { get; set; }
    public ComponentScope Scope { get; set; } = ComponentScope.Global;

    public static ComponentAttribute? GetFrom(Type type)
        => Attribute.GetCustomAttribute(type, typeof(ComponentAttribute)) as ComponentAttribute;
}
