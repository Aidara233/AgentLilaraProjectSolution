using System.Collections.Generic;

namespace AgentCoreProcessor.WebUI.Navigation
{
    public class NavItem
    {
        public string Title { get; init; } = "";
        public string Icon { get; init; } = "";
        public string? Href { get; init; }
        public List<NavItem> Children { get; init; } = new();
    }
}
