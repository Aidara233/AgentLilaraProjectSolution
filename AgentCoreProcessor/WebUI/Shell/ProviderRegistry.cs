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

        OnChanged?.Invoke();
        return true;
    }

    public PageDefinition? FindPage(string route)
    {
        return FindPage(route, out _);
    }

    public PageDefinition? FindPage(string route, out Dictionary<string, string>? routeParams)
    {
        routeParams = null;

        // 第一遍：精确匹配优先
        foreach (var entry in _providers.Values)
        {
            foreach (var page in entry.Provider.Pages)
            {
                if (string.Equals(page.Route, route, StringComparison.OrdinalIgnoreCase))
                    return page;
            }
        }

        // 第二遍：模板匹配（{param} 段）
        foreach (var entry in _providers.Values)
        {
            foreach (var page in entry.Provider.Pages)
            {
                if (page.Route.Contains('{') && TryMatchTemplate(page.Route, route, out var extracted))
                {
                    routeParams = extracted;
                    return page;
                }
            }
        }

        return null;
    }

    private static bool TryMatchTemplate(string template, string actual, out Dictionary<string, string> parameters)
    {
        parameters = new();
        var tParts = template.Split('/');
        var aParts = actual.Split('/');
        if (tParts.Length != aParts.Length) return false;

        for (int i = 0; i < tParts.Length; i++)
        {
            if (tParts[i].StartsWith('{') && tParts[i].EndsWith('}'))
            {
                var paramName = tParts[i][1..^1];
                parameters[paramName] = aParts[i];
            }
            else if (!string.Equals(tParts[i], aParts[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    public List<NavGroup> BuildNavTree()
    {
        var groups = new Dictionary<string, NavGroup>();
        var topLevel = new List<PageDefinition>();

        foreach (var entry in _providers.Values)
        {
            foreach (var page in entry.Provider.Pages)
            {
                if (!page.Meta.ShowInNav) continue;
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
