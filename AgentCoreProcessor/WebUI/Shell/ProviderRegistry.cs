using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using AgentCoreProcessor.Engine;
using AgentLilara.PluginSDK.WebUI;

namespace AgentCoreProcessor.WebUI.Shell;

internal class ProviderRegistry
{
    private readonly ConcurrentDictionary<string, ProviderEntry> _providers = new();

    public event Action? OnChanged;

    public bool Register(IWebUIProvider provider, bool builtIn = false)
    {
        var entry = new ProviderEntry(provider, builtIn);
        if (!_providers.TryAdd(provider.Id, entry))
            return false;

        FrameworkLogger.Log("ProviderRegistry", $"已注册 Provider: {provider.Id} ({provider.Pages.Count} 页面)");
        OnChanged?.Invoke();
        return true;
    }

    public bool Unregister(string providerId)
    {
        if (!_providers.TryRemove(providerId, out var entry))
            return false;

        if (entry.BuiltIn)
        {
            _providers.TryAdd(providerId, entry);
            return false;
        }

        FrameworkLogger.Log("ProviderRegistry", $"已反注册 Provider: {providerId}");
        OnChanged?.Invoke();
        return true;
    }

    public PageDefinition? FindPage(string route)
    {
        foreach (var entry in _providers.Values)
        {
            foreach (var page in entry.Provider.Pages)
            {
                if (string.Equals(page.Route, route, StringComparison.OrdinalIgnoreCase))
                    return page;
            }
        }
        return null;
    }

    public List<NavGroup> BuildNavTree()
    {
        var groups = new Dictionary<string, NavGroup>();
        var topLevel = new List<PageDefinition>();

        foreach (var entry in _providers.Values)
        {
            foreach (var page in entry.Provider.Pages)
            {
                var group = page.Meta.Group;
                if (string.IsNullOrEmpty(group))
                {
                    topLevel.Add(page);
                }
                else
                {
                    if (!groups.TryGetValue(group, out var g))
                    {
                        g = new NavGroup { Name = group };
                        groups[group] = g;
                    }
                    g.Pages.Add(page);
                    if (page.Meta.DefaultCollapsed)
                        g.DefaultCollapsed = true;
                    if (page.Meta.Icon != null && g.Icon == null)
                        g.Icon = page.Meta.Icon;
                }
            }
        }

        foreach (var g in groups.Values)
            g.Pages.Sort((a, b) => a.Meta.Order.CompareTo(b.Meta.Order));

        var result = new List<NavGroup>();
        foreach (var p in topLevel.OrderBy(p => p.Meta.Order))
            result.Add(new NavGroup { Name = p.Meta.Title, Icon = p.Meta.Icon, Pages = { p }, IsSinglePage = true });

        foreach (var g in groups.Values.OrderBy(g => g.Pages.FirstOrDefault()?.Meta.Order ?? 999))
            result.Add(g);

        return result;
    }

    public IReadOnlyList<ProviderEntry> GetAll() => _providers.Values.ToList();
}

internal record ProviderEntry(IWebUIProvider Provider, bool BuiltIn);

internal class NavGroup
{
    public string Name { get; set; } = "";
    public string? Icon { get; set; }
    public bool DefaultCollapsed { get; set; }
    public bool IsSinglePage { get; set; }
    public List<PageDefinition> Pages { get; set; } = new();
}
