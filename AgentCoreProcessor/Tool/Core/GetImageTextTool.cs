using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Tool.Core
{
    [ToolMeta(ContinueLoop = true, EngineTypes = new[] { "channel" })]
    internal class GetImageTextTool : ITool
    {
        public string Name => "get_image_text";
        public string Description => "获取图片的OCR文字识别完整结果。当图片文字在上下文中被截断时，使用此工具传入图片hash获取全部文字内容。";
        public IReadOnlyList<ToolParameter> Parameters => [
            new("image_ref", "图片的32位十六进制哈希值", 0)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var imageRef = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
            if (string.IsNullOrEmpty(imageRef))
                return new ToolResult { Status = "failed", Error = "image_ref 参数不能为空" };

            if (!Regex.IsMatch(imageRef, @"^[a-f0-9]{32}$"))
                return new ToolResult { Status = "failed", Error = $"'{imageRef}' 不是合法的图片hash" };

            var record = await ImageStorage.GetByHashAsync(imageRef);
            if (record == null)
                return new ToolResult { Status = "failed", Error = $"未找到hash为 {imageRef} 的图片" };

            if (string.IsNullOrEmpty(record.OcrText))
            {
                if (record.HasText == false)
                    return new ToolResult { Status = "success", Data = "(该图片经OCR检测不含文字)" };
                return new ToolResult { Status = "success", Data = "(该图片暂无OCR结果)" };
            }

            return new ToolResult { Status = "success", Data = record.OcrText };
        }
    }
}
