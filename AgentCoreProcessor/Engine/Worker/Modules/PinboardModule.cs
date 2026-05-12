using System.Collections.Generic;
using System.Text;

namespace AgentCoreProcessor.Engine.Modules
{
    /// <summary>
    /// 便签板模块。会话级持久化，Express/Working 共享。
    /// </summary>
    internal class PinboardModule : EngineModule
    {
        public override string Name => "便签板";
        public override int PromptPriority => 55;

        private readonly Dictionary<string, string> pinboard = new();

        /// <summary>获取便签板引用（供 Express 模式直接读取）。</summary>
        public Dictionary<string, string> Entries => pinboard;

        public override void Attach(ILoopBus bus)
        {
            bus.Subscribe<ToolExecutedEvent>(e =>
            {
                if (e.Call.Tool != "pinboard" || !e.Result.IsSuccess) return;
                ApplyAction(e.Result.Data ?? "");
            });
        }

        private void ApplyAction(string data)
        {
            if (data.StartsWith("pin:"))
            {
                var rest = data[4..];
                var sep = rest.IndexOf(':');
                if (sep > 0)
                {
                    var label = rest[..sep];
                    var content = rest[(sep + 1)..];
                    pinboard[label] = content;
                }
            }
            else if (data.StartsWith("unpin:"))
            {
                var label = data[6..];
                pinboard.Remove(label);
            }
        }

        public override string? BuildPromptSection(EngineMode mode)
        {
            if (pinboard.Count == 0) return null;
            var sb = new StringBuilder("[便签板]\n");
            foreach (var (label, content) in pinboard)
                sb.AppendLine($"- {label}: {content}");
            return sb.ToString();
        }

        public override void Reset()
        {
            pinboard.Clear();
        }
    }
}
