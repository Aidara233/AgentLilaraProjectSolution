using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Tool.Core
{
    [ToolMeta(ContinueLoop = true, EngineTypes = new[] { "channel" }, ExpressAvailable = true)]
    internal class RefineImageTool : ITool
    {
        public string Name => "refine_image";
        public string Description => "请求重新仔细检查一张图片。当你需要看清图片中特定内容（文字、错误信息、数据、细节等）时调用。";
        public IReadOnlyList<ToolParameter> Parameters => [
            new("image_ref", "图片引用（hash 值或 [IMG:N] 索引）", 0),
            new("focus", "需要关注的具体内容（比如：错误堆栈第一行、按钮上的文字、表格第三列数据）", 0)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var imageRef = resolvedInputs.Count > 0 ? resolvedInputs[0] : "";
            var focus = resolvedInputs.Count > 1 ? resolvedInputs[1] : "";
            return Task.FromResult(new ToolResult { Status = "success", Data = $"{imageRef}|{focus}" });
        }
    }
}
