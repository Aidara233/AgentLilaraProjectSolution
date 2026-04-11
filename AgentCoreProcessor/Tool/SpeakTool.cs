using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 说话工具：向用户发送一条消息（实时推送，不终止 Agent 循环）。
    /// 纯信号工具——返回消息文本，由 Agent 循环经 ExpressCore 润色后推送。
    /// </summary>
    internal class SpeakTool : ITool
    {
        public string Name => "说话";
        public string Description => "向用户发送一条消息（实时推送，不等待任务完成）";
        public IReadOnlyList<ToolParameter> Parameters =>
            [new("消息内容", "要发送给用户的文本内容", 0)];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 1 || string.IsNullOrWhiteSpace(resolvedInputs[0]))
                return Task.FromResult(new ToolResult
                {
                    Status = "failed",
                    Error = "消息内容不能为空"
                });

            return Task.FromResult(new ToolResult
            {
                Status = "success",
                Data = resolvedInputs[0]
            });
        }
    }
}
