using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;

namespace Plugin.ScheduledTasks;

[ToolMeta(Group = null, ContinueLoop = true, CapabilitySummary = "定时任务：在指定时间触发提醒")]
public class ScheduleTaskTool : ITool
{
    private readonly ScheduledTaskStore _store;

    public string Name => "schedule_task";
    public string Description =>
        "创建定时任务。expression支持: 'in 30m'/'in 2h'/'in 1d'(相对时间), " +
        "'YYYY-MM-DD HH:MM'(指定时间), '*-*-* 09:00'(每天9点), " +
        "'*-*-* 09:00 mon'(每周一9点), '*-*-01 10:00'(每月1号10点), " +
        "'*-12-25 08:00'(每年12月25日8点)。年/月/日可用*通配。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("expression", "时间表达式", 0),
        new("description", "任务描述（触发时AI会看到）", 1),
        new("requires_work", "是否需要工作模式处理（false=只提一嘴即可，true=需调用工具干活）。默认 false", 2, isRequired: false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public ScheduleTaskTool(ScheduledTaskStore store)
    {
        _store = store;
    }

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var expression = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        var description = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";
        var requiresWorkStr = resolvedInputs.Count > 2 ? resolvedInputs[2].Trim() : "false";
        bool requiresWork = requiresWorkStr.Equals("true", StringComparison.OrdinalIgnoreCase)
            || requiresWorkStr == "1" || requiresWorkStr == "yes";

        if (string.IsNullOrEmpty(expression))
            return Fail("expression 不能为空");
        if (string.IsNullOrEmpty(description))
            return Fail("description 不能为空");

        var result = TimeExpressionParser.Parse(expression, DateTime.Now);
        if (!result.Success)
            return Fail(result.Error!);

        var entry = _store.AddTask(description, expression, result.NextFireTime, result.IsRecurring, requiresWork);

        var fireTimeStr = entry.NextFireTime?.ToString("yyyy-MM-dd HH:mm:ss");
        var recurringLabel = entry.IsRecurring ? " (循环)" : "";
        var workLabel = requiresWork ? " (工作模式)" : "";
        return Ok($"已创建定时任务 [{entry.Id[..8]}]：{description}\n触发时间：{fireTimeStr}{recurringLabel}{workLabel}");
    }

    private static Task<ToolResult> Ok(string data) =>
        Task.FromResult(new ToolResult { Status = "success", Data = data });
    private static Task<ToolResult> Fail(string err) =>
        Task.FromResult(new ToolResult { Status = "failed", Error = err });
}
