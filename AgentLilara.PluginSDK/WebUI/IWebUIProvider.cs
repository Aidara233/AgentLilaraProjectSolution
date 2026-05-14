using System.Collections.Generic;

namespace AgentLilara.PluginSDK.WebUI;

public interface IWebUIProvider
{
    string Id { get; }
    string DisplayName { get; }
    IReadOnlyList<PageDefinition> Pages { get; }
}

[AttributeUsage(AttributeTargets.Class)]
public class WebUIProviderAttribute : Attribute
{
    public bool BuiltIn { get; set; }
}
