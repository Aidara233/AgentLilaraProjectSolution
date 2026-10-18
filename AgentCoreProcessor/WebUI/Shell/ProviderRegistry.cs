using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Logging;
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
        {
            var existing = GetById(provider.Id)?.Provider;
            Signal.Warn(LogGroup.Plugin, "WebUI Provider ID 冲突，后注册者被忽略",
                new { providerId = provider.Id, existing = existing?.GetType().FullName, incoming = provider.GetType().FullName });
            return false;
        }

        // 页面路由冲突诊断：精确路由重复或模板形状相同的页面会导致某个 Provider 被静默遮蔽。
        var duplicates = provider.Pages
            .GroupBy(p => p.Route, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        foreach (var dup in duplicates)
        {
            Signal.Warn(LogGroup.Plugin, "WebUI 页面路由在 Provider 内部重复",
                new { providerId = provider.Id, route = dup });
        }

        var existingRoutes = _providers.Values
            .Where(e => !ReferenceEquals(e.Provider, provider))
            .SelectMany(e => e.Provider.Pages.Select(p => (e.Provider.Id, p.Route)))
            .Where(x => x.Route.Contains('{') == false)
            .ToDictionary(x => x.Route, x => x.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var page in provider.Pages.Where(p => !p.Route.Contains('{')))
        {
            if (existingRoutes.TryGetValue(page.Route, out var ownerId) && ownerId != provider.Id)
            {
                Signal.Warn(LogGroup.Plugin, "WebUI 页面路由冲突",
                    new { route = page.Route, providerId = provider.Id, existingProvider = ownerId });
            }
        }

        OnChanged?.Invoke();
        return true;
    }

    private ProviderEntry? GetById(string providerId)
        => _providers.TryGetValue(providerId, out var entry) ? entry : null;

    public bool Unregister(string providerId)
    {
        if (!_providers.TryGetValue(providerId, out var entry))
            return false;

        if (entry.BuiltIn)
            return false;

        if (!_providers.TryRemove(providerId, out _))
            return false;

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
        route = NormalizeRoute(route);

        // 第一遍：精确匹配优先（先按规范化路由比较，避免尾斜杠造成漏匹配）
        foreach (var entry in _providers.Values)
        {
            foreach (var page in entry.Provider.Pages)
            {
                if (string.Equals(page.Route, route, StringComparison.OrdinalIgnoreCase))
                    return page;
            }
        }

        // 第二遍：模板匹配（{param} 段）。按“字面段更多、参数段更少”排序，
        // 避免依赖 ConcurrentDictionary 的枚举顺序。
        var candidates = _providers.Values
            .SelectMany(e => e.Provider.Pages)
            .Where(p => p.Route.Contains('{'))
            .Select(p => (Page: p, Rank: TemplateRank(p.Route)))
            .OrderByDescending(x => x.Rank.LiteralSegments)
            .ThenBy(x => x.Rank.ParameterSegments)
            .ThenBy(x => x.Page.Route, StringComparer.OrdinalIgnoreCase);

        foreach (var (page, _) in candidates)
        {
            if (TryMatchTemplate(page.Route, route, out var extracted))
            {
                routeParams = extracted;
                return page;
            }
        }

        return null;
    }

    private static string NormalizeRoute(string route)
    {
        // 去掉前后斜杠与重复斜杠；保留段内内容。
        var trimmed = route.Trim('/');
        var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('/', parts);
    }

    private static (int LiteralSegments, int ParameterSegments) TemplateRank(string route)
    {
        var parts = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var literal = parts.Count(p => !(p.StartsWith('{') && p.EndsWith('}')));
        return (literal, parts.Length - literal);
    }

    private static bool TryMatchTemplate(string template, string actual, out Dictionary<string, string> parameters)
    {
        parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tParts = NormalizeRoute(template).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var aParts = NormalizeRoute(actual).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (tParts.Length != aParts.Length) return false;

        for (int i = 0; i < tParts.Length; i++)
        {
            if (tParts[i].StartsWith('{') && tParts[i].EndsWith('}'))
            {
                var paramName = tParts[i][1..^1];
                if (string.IsNullOrEmpty(paramName)) return false;
                parameters[paramName] = Uri.UnescapeDataString(aParts[i]);
            }
            else if (!string.Equals(tParts[i], Uri.UnescapeDataString(aParts[i]), StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// 当前路径所属的菜单分组名。隐藏详情页（ShowInNav=false）不在导航树里，
    /// 但可以通过模板前缀（如 channels/{id} → channels）找到所属分组，用于自动展开。
    /// </summary>
    public string? FindNavGroupForPath(string path)
    {
        var normalized = NormalizeRoute(path);
        if (string.IsNullOrEmpty(normalized)) return null;

        // 精确可见页
        foreach (var entry in _providers.Values)
        {
            foreach (var page in entry.Provider.Pages)
            {
                if (!page.Meta.ShowInNav) continue;
                if (string.Equals(NormalizeRoute(page.Route), normalized, StringComparison.OrdinalIgnoreCase))
                    return page.Meta.Group;
            }
        }

        // 隐藏详情页：模板前缀归属
        var candidates = _providers.Values
            .SelectMany(e => e.Provider.Pages)
            .Where(p => !p.Meta.ShowInNav && p.Route.Contains('{'))
            .Select(p => (Page: p, Prefix: NormalizeRoute(p.Route.Split('{')[0]).TrimEnd('/')))
            .Where(x => !string.IsNullOrEmpty(x.Prefix)
                        && (normalized.Equals(x.Prefix, StringComparison.OrdinalIgnoreCase)
                            || normalized.StartsWith(x.Prefix + "/", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x => x.Prefix.Length);

        return candidates.FirstOrDefault().Page?.Meta.Group;
    }
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
