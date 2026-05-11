using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Models;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 获取图片文字工具（Working 专用）。
    /// 输入图片 ID，返回完整 OCR 识别文本。
    /// </summary>
    internal class GetImageTextTool : ITool
    {
        public string Name => "获取图片文字";
        public string Description => "获取图片中的完整文字内容（OCR结果）。输入图片ID（上下文中 <img id=\"N\"/> 的编号），返回识别到的所有文字";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("图片ID", "要获取文字的图片编号", 0)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(10);
        public bool ContinueLoop => true;
        public bool RetainResult => false;
        public string? ToolGroup => null;
        public string? CapabilitySummary => "获取图片中的完整文字（需要阅读图中文本时使用）";

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var idStr = resolvedInputs.ElementAtOrDefault(0) ?? "";
            if (!int.TryParse(idStr.Trim(), out var imageId))
                return new ToolResult { Status = "failed", Error = "请输入有效的图片ID数字" };

            var record = await ImageStorage.GetByIdAsync(imageId);
            if (record == null)
                return new ToolResult { Status = "failed", Error = $"图片 #{imageId} 不存在" };

            if (record.HasText == null)
                return new ToolResult { Status = "failed", Error = $"图片 #{imageId} 尚未完成文字识别" };

            if (record.HasText == false || string.IsNullOrEmpty(record.OcrText))
                return new ToolResult { Status = "success", Data = $"图片 #{imageId} 中没有识别到文字" };

            return new ToolResult
            {
                Status = "success",
                Data = $"图片 #{imageId} 文字内容:\n{record.OcrText}"
            };
        }
    }
}
