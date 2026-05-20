using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;

namespace Plugin.WorkingTools
{
    /// <summary>
    /// 任务管理工具。创建和追踪待办任务，内容每轮自动注入上下文。
    /// </summary>
    [ToolMeta(Group = null, ContinueLoop = true, CapabilitySummary = "任务管理：创建和追踪待办任务")]
    public class TaskListTool : ITool, AgentCoreProcessor.Engine.IInjectProvider
    {
        private readonly string _filePath;
        private static readonly object _lock = new();
        private const int MaxTasks = 20;

        public string Name => "task_management";
        public string Description => "任务管理：创建和追踪待办任务。action: add(添加) / complete(完成) / remove(删除) / list(查看全部) / clear(清空)。add 时 value 填任务描述，complete/remove 时 value 填 1-based 数字。";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("action", "操作类型：add / complete / remove / list / clear", 0),
            new("value", "add 时填任务描述，complete/remove 时填 1-based 数字", 1, isRequired: false)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);

        public TaskListTool(IToolContext ctx)
        {
            _filePath = Path.Combine(ctx.Storage.GlobalDirectory, "task_list.json");
        }

        /// <summary>Component 模式构造函数</summary>
        public TaskListTool(IPluginStorage storage)
        {
            _filePath = Path.Combine(storage.GlobalDirectory, "task_list.json");
        }

        public string? BuildSection()
        {
            var tasks = LoadTasks();
            if (tasks.Count == 0) return null;
            var sb = new StringBuilder("[当前任务]\n");
            for (int i = 0; i < tasks.Count; i++)
            {
                var t = tasks[i];
                var mark = t.Done ? "v" : " ";
                sb.AppendLine($"{i + 1}. [{mark}] {t.Description}");
            }
            return sb.ToString();
        }

        // IInjectProvider
        public int InjectPriority => 50;
        public System.Threading.Tasks.Task<string?> BuildStartInjectAsync(AgentCoreProcessor.Engine.InjectContext ctx)
            => System.Threading.Tasks.Task.FromResult(BuildSection());
        public System.Threading.Tasks.Task<string?> BuildRoundInjectAsync(AgentCoreProcessor.Engine.InjectContext ctx)
            => System.Threading.Tasks.Task.FromResult<string?>(null);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var action = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim().ToLower() : "";
            var value = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";

            lock (_lock)
            {
                switch (action)
                {
                    case "add":
                        if (string.IsNullOrEmpty(value))
                            return Fail("value（任务描述）不能为空");
                        return Ok(Add(value));

                    case "complete":
                        if (!int.TryParse(value, out var ci) || ci < 1)
                            return Fail("value 必须是正整数（任务序号）");
                        return Ok(Complete(ci));

                    case "remove":
                        if (!int.TryParse(value, out var ri) || ri < 1)
                            return Fail("value 必须是正整数（任务序号）");
                        return Ok(Remove(ri));

                    case "list":
                        return Ok(List());

                    case "clear":
                        return Ok(Clear());

                    default:
                        return Fail($"未知操作: {action}，支持 add/complete/remove/list/clear");
                }
            }
        }

        private string Add(string description)
        {
            var tasks = LoadTasks();
            tasks.Add(new TaskItem { Description = description, Done = false });
            if (tasks.Count > MaxTasks)
                tasks.RemoveAt(0);
            SaveTasks(tasks);
            return $"已添加任务 #{tasks.Count}：{description}";
        }

        private string Complete(int index)
        {
            var tasks = LoadTasks();
            if (index < 1 || index > tasks.Count)
                return $"序号 {index} 超出范围（共 {tasks.Count} 条）";
            tasks[index - 1].Done = true;
            SaveTasks(tasks);
            return $"已完成任务 #{index}：{tasks[index - 1].Description}";
        }

        private string Remove(int index)
        {
            var tasks = LoadTasks();
            if (index < 1 || index > tasks.Count)
                return $"序号 {index} 超出范围（共 {tasks.Count} 条）";
            var removed = tasks[index - 1].Description;
            tasks.RemoveAt(index - 1);
            SaveTasks(tasks);
            return $"已移除任务 #{index}：{removed}";
        }

        private string List()
        {
            var tasks = LoadTasks();
            if (tasks.Count == 0) return "(任务列表为空)";
            var sb = new StringBuilder();
            for (int i = 0; i < tasks.Count; i++)
            {
                var t = tasks[i];
                var mark = t.Done ? "v" : " ";
                sb.AppendLine($"{i + 1}. [{mark}] {t.Description}");
            }
            return sb.ToString().TrimEnd();
        }

        private string Clear()
        {
            SaveTasks(new List<TaskItem>());
            return "任务列表已清空";
        }

        private List<TaskItem> LoadTasks()
        {
            if (!File.Exists(_filePath))
                return new List<TaskItem>();
            try
            {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<List<TaskItem>>(json) ?? new List<TaskItem>();
            }
            catch { return new List<TaskItem>(); }
        }

        private void SaveTasks(List<TaskItem> tasks)
        {
            var dir = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(dir);
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(tasks, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _filePath, overwrite: true);
        }

        private static Task<ToolResult> Ok(string data) =>
            Task.FromResult(new ToolResult { Status = "success", Data = data });

        private static Task<ToolResult> Fail(string err) =>
            Task.FromResult(new ToolResult { Status = "failed", Error = err });
    }

    internal class TaskItem
    {
        public string Description { get; set; } = "";
        public bool Done { get; set; }
    }
}
