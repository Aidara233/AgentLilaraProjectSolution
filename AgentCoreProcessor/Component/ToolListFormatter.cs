// AgentCoreProcessor/Component/ToolListFormatter.cs
using System.Text;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Component;

internal record ToolGroup(
    string ComponentName,
    string Description,
    ComponentScope Scope,
    bool IsEnabled,
    IReadOnlyList<ITool> Tools);

internal static class ToolListFormatter
{
    public static List<ToolGroup> CollectGroups(
        ComponentHost? loopHost,
        GlobalComponentHost? globalHost)
    {
        var groups = new List<ToolGroup>();

        if (globalHost != null)
        {
            foreach (var inst in globalHost.Instances)
            {
                groups.Add(new ToolGroup(
                    inst.Component.Meta.Name,
                    inst.Component.Meta.Description,
                    ComponentScope.Global,
                    inst.Context.IsEnabled,
                    inst.Component.Tools.ToList()));
            }
        }

        if (loopHost != null)
        {
            foreach (var inst in loopHost.Instances)
            {
                groups.Add(new ToolGroup(
                    inst.Component.Meta.Name,
                    inst.Component.Meta.Description,
                    ComponentScope.Loop,
                    inst.Context.IsEnabled,
                    inst.Component.Tools.ToList()));
            }
        }

        return groups;
    }

    /// <summary>
    /// 构建组件目录注入 prompt。启用的列工具名，禁用的折叠为摘要。
    /// 工具描述不重复（由 API tool schema 提供）。
    /// modeId 不为空时，仅列出当前模式启用的工具。
    /// </summary>
    public static string? BuildToolOverviewSection(List<ToolGroup> groups, string? modeId = null)
    {
        if (groups.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("[组件目录]");

        foreach (var g in groups)
        {
            var scope = g.Scope == ComponentScope.Global ? "全局" : "循环";

            if (g.IsEnabled)
            {
                var tools = modeId != null
                    ? g.Tools.Where(t => Engine.ModeConfigLoader.IsToolEnabled(modeId, t.Name)).ToList()
                    : g.Tools.ToList();
                if (tools.Count == 0) continue;
                var toolNames = string.Join(", ", tools.Select(t => t.Name));
                sb.AppendLine($"▸ {g.ComponentName}（{scope}）: {toolNames}");
            }
            else
            {
                sb.AppendLine($"▹ {g.ComponentName}（{scope} · 已禁用）: {g.Tools.Count}个工具，enable_component(\"{g.ComponentName}\") 启用");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 收集传给 API 的工具定义（仅启用组件的工具）。
    /// </summary>
    public static List<ITool> CollectVisibleTools(List<ToolGroup> groups)
    {
        var tools = new List<ITool>();
        foreach (var g in groups)
        {
            if (!g.IsEnabled) continue;
            tools.AddRange(g.Tools);
        }
        return tools;
    }
}
