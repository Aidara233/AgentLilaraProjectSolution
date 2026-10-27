// AgentCoreProcessor/Engine/Session/SessionEngine.Scheduler.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Component;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine.Context;
using AgentCoreProcessor.Engine.Graph;
using AgentCoreProcessor.Engine.Messaging;
using AgentCoreProcessor.Logging;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine.Session;

/// <summary>
/// <see cref="SessionEngine"/> 的部件：调度与落盘（partial 文件，仅物理拆分，类型仍是一个）。
/// <para>含 ScheduledTask / TODO 的增查改、退出时全量持久化与 cron 下次触发时间计算。</para>
/// </summary>
internal partial class SessionEngine
{
    // ── ScheduledTask ──

    public void AddScheduledTask(ScheduledTask task)
    {
        lock (_scheduledTasks) _scheduledTasks.Add(task);
    }

    public IReadOnlyList<ScheduledTask> GetScheduledTasks()
    {
        lock (_scheduledTasks) return _scheduledTasks.ToList();
    }

    // ── TODO ──

    public void AddTodo(TodoItem todo)
    {
        lock (_todoItems) _todoItems.Add(todo);
    }

    public IReadOnlyList<TodoItem> GetPendingTodos(int? relatedPersonId = null)
    {
        lock (_todoItems)
        {
            return _todoItems
                .Where(t => t.Status == "pending")
                .Where(t => relatedPersonId == null || t.RelatedPersonId == relatedPersonId)
                .Where(t => t.NotBefore <= DateTime.UtcNow)
                .Where(t => _currency == null || _currency.Balance >= t.MinBalance)
                .ToList();
        }
    }

    public void CompleteTodo(string todoId)
    {
        lock (_todoItems)
        {
            var todo = _todoItems.FirstOrDefault(t => t.TodoId == todoId);
            if (todo != null) todo.Status = "done";
        }
    }

    private async Task PersistAllAsync()
    {
        foreach (var (_, session) in _sessions)
        {
            try { await session.PersistStateAsync(); }
            catch { /* 持久化失败不阻塞 */ }
        }
    }

    private DateTime CalculateNextCron(string cronExpression, DateTime from)
    {
        // 简化 cron 计算：目前只支持 Minutes（如 "*/30 * * * *"）
        // 生产环境可引入 Cronos 库
        try
        {
            var parts = cronExpression.Split(' ');
            if (parts.Length >= 2 && parts[0].StartsWith("*/"))
            {
                var minutes = int.Parse(parts[0].Substring(2));
                return from.AddMinutes(minutes);
            }
        }
        catch { }
        return from.AddHours(1); // fallback
    }
}
