using System.Linq;
using System.Text;
using AgentCoreProcessor.Tool;

namespace AgentCoreProcessor.Engine.Modules
{
    /// <summary>
    /// 工具状态模块。当有工具被禁用时，在动态区域注入提示让模型知道。
    /// </summary>
    internal class ToolStatusModule : EngineModule
    {
        public override string Name => "工具状态";
        public override int PromptPriority => 30;

        public override void Attach(ILoopBus bus) { }

        public override string? BuildPromptSection(EngineMode mode)
        {
            var disabled = ToolRegistry.DisabledTools;
            if (disabled.Count == 0) return null;

            var sb = new StringBuilder("[工具状态]\n以下工具当前不可用：\n");
            foreach (var (name, info) in disabled)
            {
                sb.AppendLine($"- {name}：{info.Reason}");
            }
            sb.Append("调用这些工具会失败，请用其他方式处理或告知用户。");
            return sb.ToString();
        }
    }
}
