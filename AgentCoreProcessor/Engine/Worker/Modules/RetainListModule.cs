using System.Collections.Generic;
using System.Linq;
using System.Text;
using AgentCoreProcessor.Tool;

namespace AgentCoreProcessor.Engine.Modules
{
    /// <summary>
    /// 缓存列表模块。自动收集 RetainResult 工具的输出，提供摘要注入和详情查看。
    /// </summary>
    internal class RetainListModule : EngineModule
    {
        public override string Name => "缓存列表";
        public override int PromptPriority => 60;

        private readonly List<(string Summary, string FullContent)> items = new();

        public override void Attach(ILoopBus bus)
        {
            bus.Subscribe<ToolExecutedEvent>(e =>
            {
                // 缓存管理工具自身的操作
                if (e.Call.Tool == "缓存管理" && e.Result.IsSuccess)
                {
                    ApplyAction(e.Call, e.Result);
                    return;
                }

                // 自动收集 RetainResult 工具的成功结果
                if (e.ToolDef?.RetainResult == true && e.Result.IsSuccess)
                {
                    var summary = $"{e.Call.Tool}: {string.Join(", ", e.Call.Inputs).Truncate(50)}";
                    items.Add((summary, e.Result.Data ?? ""));
                }
            });
        }

        private void ApplyAction(ToolCall call, ToolResult result)
        {
            var data = result.Data ?? "";

            if (data.StartsWith("view:"))
            {
                if (int.TryParse(data[5..], out var idx) && idx >= 1 && idx <= items.Count)
                    result.Data = items[idx - 1].FullContent;
                else
                    result.Data = "序号超出范围";
            }
            else if (data.StartsWith("remove:"))
            {
                if (int.TryParse(data[7..], out var idx) && idx >= 1 && idx <= items.Count)
                {
                    items.RemoveAt(idx - 1);
                    result.Data = "已移除";
                }
            }
            else if (data == "clear")
            {
                items.Clear();
                result.Data = "已清空";
            }
        }

        public override string? BuildPromptSection(EngineMode mode)
        {
            if (mode == EngineMode.Express || items.Count == 0) return null;
            var sb = new StringBuilder("[缓存列表]（使用「缓存管理」工具的 view 操作查看完整内容）\n");
            for (int i = 0; i < items.Count; i++)
                sb.AppendLine($"{i + 1}. {items[i].Summary}");
            return sb.ToString();
        }

        public override void Reset()
        {
            items.Clear();
        }
    }

    internal static class StringExtensions
    {
        public static string Truncate(this string s, int maxLen)
            => s.Length <= maxLen ? s : s[..maxLen] + "...";
    }
}
