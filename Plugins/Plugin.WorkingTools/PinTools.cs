// Plugins/Plugin.WorkingTools/PinTools.cs
using System.Linq;
using AgentLilara.PluginSDK;

namespace Plugin.WorkingTools;

/// <summary>
/// 静态访问器：PinboardComponent（Global）初始化后设置，pin_* 工具通过此访问共享 pins 目录与迁移状态。
/// </summary>
public static class PinToolsAccessor
{
    private static PinboardComponent? _component;

    public static void Configure(PinboardComponent component) => _component = component;
    public static void Clear() => _component = null;

    /// <summary>该组件注册的工具集合。PinboardComponent.Tools 返回此项。</summary>
    public static IEnumerable<ITool> Tools
    {
        get
        {
            yield return new PinListTool();
            yield return new PinReadTool();
            yield return new PinWriteTool();
            yield return new PinAppendTool();
            yield return new PinRemoveTool();
            yield return new PinSearchTool();
        }
    }

    internal static PinboardComponent? Component => _component;
    internal static string? PinsDir => _component?.PinsDir;
    internal static object SyncLock => _component?.SyncLock ?? new object();

    internal static Task<ToolResult> Ok(string data) =>
        Task.FromResult(new ToolResult { Status = "success", Data = data });
    internal static Task<ToolResult> Fail(string err) =>
        Task.FromResult(new ToolResult { Status = "failed", Error = err });

    internal static string? RequireComponent()
        => _component == null ? "pinboard 组件尚未初始化（Global 组件启动时装配）。" : null;
}

/// <summary>列出全部便签标题。</summary>
[ToolMeta(Group = null, ContinueLoop = true)]
public class PinListTool : ITool
{
    public string Name => "pin_list";
    public string Description => "列出系统记忆便签的全部标题（逐字文件档案的清单）。";
    public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (PinToolsAccessor.RequireComponent() is string err) return PinToolsAccessor.Fail(err);
        var comp = PinToolsAccessor.Component!;
        var titles = comp.ListTitles();
        if (titles.Count == 0) return PinToolsAccessor.Ok("(系统记忆为空)");
        return PinToolsAccessor.Ok("系统记忆便签：\n" + string.Join("\n", titles.Select(t => "- " + t)));
    }
}

/// <summary>读取一条便签的完整逐字内容。</summary>
[ToolMeta(Group = null, ContinueLoop = true)]
public class PinReadTool : ITool
{
    public string Name => "pin_read";
    public string Description => "读取系统记忆便签的完整内容（逐字返回）。参数 title 为便签标题。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("title", "便签标题", 0)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (PinToolsAccessor.RequireComponent() is string err) return PinToolsAccessor.Fail(err);
        if (inputs.Count == 0 || string.IsNullOrWhiteSpace(inputs[0]))
            return PinToolsAccessor.Fail("title 不能为空。");
        var content = PinToolsAccessor.Component!.ReadPin(inputs[0].Trim());
        return content == null
            ? PinToolsAccessor.Fail($"便签 [{inputs[0].Trim()}] 不存在（可用 pin_list 查看全部标题）。")
            : PinToolsAccessor.Ok(content);
    }
}

/// <summary>写入一条便签（新建或覆盖全文）。</summary>
[ToolMeta(Group = null, ContinueLoop = true)]
public class PinWriteTool : ITool
{
    public string Name => "pin_write";
    public string Description => "写入系统记忆便签：新建或整体覆盖。title 为标题，content 为逐字内容（原样保存）。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("title", "便签标题", 0),
        new("content", "便签内容（逐字保存）", 1)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (PinToolsAccessor.RequireComponent() is string err) return PinToolsAccessor.Fail(err);
        if (inputs.Count < 2 || string.IsNullOrWhiteSpace(inputs[0]))
            return PinToolsAccessor.Fail("title 和 content 均不能为空。");
        var ok = PinToolsAccessor.Component!.WritePin(inputs[0].Trim(), inputs[1]);
        return ok
            ? PinToolsAccessor.Ok($"已写入便签 [{inputs[0].Trim()}]")
            : PinToolsAccessor.Fail("便签标题不合法。");
    }
}

/// <summary>向一条便签追加内容（不改写已有内容）。</summary>
[ToolMeta(Group = null, ContinueLoop = true)]
public class PinAppendTool : ITool
{
    public string Name => "pin_append";
    public string Description => "向系统记忆便签追加一段内容（保留已有内容，仅续写）。title 为标题，content 为追加内容。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("title", "便签标题", 0),
        new("content", "追加内容", 1)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (PinToolsAccessor.RequireComponent() is string err) return PinToolsAccessor.Fail(err);
        if (inputs.Count < 2 || string.IsNullOrWhiteSpace(inputs[0]))
            return PinToolsAccessor.Fail("title 和 content 均不能为空。");
        var ok = PinToolsAccessor.Component!.AppendPin(inputs[0].Trim(), inputs[1]);
        return ok
            ? PinToolsAccessor.Ok($"已追加到便签 [{inputs[0].Trim()}]")
            : PinToolsAccessor.Fail("便签标题不合法。");
    }
}

/// <summary>删除一条便签。</summary>
[ToolMeta(Group = null, ContinueLoop = true)]
public class PinRemoveTool : ITool
{
    public string Name => "pin_remove";
    public string Description => "删除系统记忆便签（删除其 .md 文件）。title 为便签标题。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("title", "便签标题", 0)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (PinToolsAccessor.RequireComponent() is string err) return PinToolsAccessor.Fail(err);
        if (inputs.Count == 0 || string.IsNullOrWhiteSpace(inputs[0]))
            return PinToolsAccessor.Fail("title 不能为空。");
        var removed = PinToolsAccessor.Component!.RemovePin(inputs[0].Trim());
        return removed
            ? PinToolsAccessor.Ok($"已删除便签 [{inputs[0].Trim()}]")
            : PinToolsAccessor.Fail($"便签 [{inputs[0].Trim()}] 不存在。");
    }
}

/// <summary>在便签内容中全文搜索关键词，返回命中的标题。</summary>
[ToolMeta(Group = null, ContinueLoop = true)]
public class PinSearchTool : ITool
{
    public string Name => "pin_search";
    public string Description => "在系统记忆便签的全文内容中搜索关键词，返回命中标题列表（列表按标题排序）。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("query", "搜索关键词", 0)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (PinToolsAccessor.RequireComponent() is string err) return PinToolsAccessor.Fail(err);
        if (inputs.Count == 0 || string.IsNullOrWhiteSpace(inputs[0]))
            return PinToolsAccessor.Fail("query 不能为空。");
        var hits = PinToolsAccessor.Component!.SearchPins(inputs[0].Trim());
        if (hits.Count == 0) return PinToolsAccessor.Ok("(无命中)");
        return PinToolsAccessor.Ok("命中便签：\n" + string.Join("\n", hits.Select(t => "- " + t)));
    }
}