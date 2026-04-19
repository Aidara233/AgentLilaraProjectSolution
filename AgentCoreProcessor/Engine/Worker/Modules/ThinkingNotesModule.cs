using System.Collections.Generic;
using System.Text;

namespace AgentCoreProcessor.Engine.Modules
{
    /// <summary>
    /// 思考笔记模块。模型通过工具写入/删除笔记，每轮注入 prompt。
    /// </summary>
    internal class ThinkingNotesModule : EngineModule
    {
        public override string Name => "思考笔记";
        public override int PromptPriority => 45;

        private readonly Dictionary<string, string> notes = new();

        public override void Attach(ILoopBus bus)
        {
            bus.Subscribe<ToolExecutedEvent>(e =>
            {
                if (e.Call.Tool != "思考笔记" || !e.Result.IsSuccess) return;
                if (e.Call.Inputs.Count < 2) return;

                var action = e.Call.Inputs[0]?.Trim().ToLower();
                var key = e.Call.Inputs[1] ?? "";

                if (action == "write" && e.Call.Inputs.Count >= 3)
                    notes[key] = e.Call.Inputs[2] ?? "";
                else if (action == "delete")
                    notes.Remove(key);
            });
        }

        public override string? BuildPromptSection(EngineMode mode)
        {
            if (notes.Count == 0) return null;
            var sb = new StringBuilder("你的思考笔记：\n");
            foreach (var (key, value) in notes)
                sb.AppendLine($"- {key}: {value}");
            return sb.ToString();
        }

        public override void Reset()
        {
            notes.Clear();
        }
    }
}
