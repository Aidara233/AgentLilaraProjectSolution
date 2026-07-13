using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Tool.Core
{
    // 引擎不限：频道 working session 与任务 session 都用它收尾。
    // 频道会话"退回轻量模式"的语义在创建时由 InitialState 注入，不写进工具描述（保持中性）。
    [ToolMeta(ContinueLoop = false, EngineTypes = new[] { "session" })]
    internal class EndSessionTool : ITool
    {
        public string Name => "end_session";
        public string Description => "彻底结束当前工作会话、退回轻量对话模式。仅在确实不再需要工作模式时调用（如用户明确表示结束、或一项长任务全部完成且无后续）。日常说完话只想等用户回应，请用 wait 而不是这个——用错会把自己踢出工作模式。";

        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("reason", "结束原因（简述完成了什么，可选）", 0)
        ];

        public TimeSpan Timeout => TimeSpan.FromSeconds(1);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var reason = resolvedInputs.Count > 0 ? resolvedInputs[0] : "";
            return Task.FromResult(new ToolResult { Status = "success", Data = reason });
        }
    }
}
