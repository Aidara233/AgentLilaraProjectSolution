using System.Collections.Generic;
using System.Text;

namespace AgentCoreProcessor.Engine.Modules
{
    /// <summary>
    /// 任务列表模块。模型通过工具管理任务，每轮注入 prompt。
    /// </summary>
    internal class TaskListModule : EngineModule
    {
        public override string Name => "任务列表";
        public override int PromptPriority => 50;

        private readonly List<(string Description, bool Done)> tasks = new();

        public override void Attach(ILoopBus bus)
        {
            bus.Subscribe<ToolExecutedEvent>(e =>
            {
                if (e.Call.Tool != "任务管理" || !e.Result.IsSuccess) return;
                ApplyAction(e.Result.Data ?? "");
            });
        }

        private void ApplyAction(string data)
        {
            var sep = data.IndexOf(':');
            if (sep < 0) return;
            var action = data[..sep];
            var content = data[(sep + 1)..];

            switch (action)
            {
                case "add":
                    tasks.Add((content, false));
                    break;
                case "complete":
                    if (int.TryParse(content, out var ci) && ci >= 1 && ci <= tasks.Count)
                        tasks[ci - 1] = (tasks[ci - 1].Description, true);
                    break;
                case "remove":
                    if (int.TryParse(content, out var ri) && ri >= 1 && ri <= tasks.Count)
                        tasks.RemoveAt(ri - 1);
                    break;
            }
        }

        public override string? BuildPromptSection(EngineMode mode)
        {
            if (tasks.Count == 0) return null;
            var sb = new StringBuilder("[当前任务]\n");
            for (int i = 0; i < tasks.Count; i++)
            {
                var (desc, done) = tasks[i];
                var mark = done ? "\u2713" : " ";
                sb.AppendLine($"{i + 1}. [{mark}] {desc}");
            }
            return sb.ToString();
        }

        public override void Reset()
        {
            tasks.Clear();
        }
    }
}
