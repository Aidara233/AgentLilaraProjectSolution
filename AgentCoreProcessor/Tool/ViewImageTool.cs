using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Models;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 查看图片工具（Working 专用）。
    /// 输入图片 ID（ImageRecord 自增主键），返回原图作为 ContentPart 附件。
    /// </summary>
    internal class ViewImageTool : ITool
    {
        public string Name => "view_image";
        public string Description => "查看历史图片原图。输入图片ID（上下文中 <img id=\"N\"/> 的编号），返回图片内容供你仔细查看";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("图片ID", "要查看的图片编号", 0)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(10);
        public bool ContinueLoop => true;
        public bool RetainResult => false;
        public string? ToolGroup => null;
        public string? CapabilitySummary => "查看历史图片原图（需要仔细看图片内容时使用）";

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var idStr = resolvedInputs.ElementAtOrDefault(0) ?? "";
            if (!int.TryParse(idStr.Trim(), out var imageId))
                return new ToolResult { Status = "failed", Error = "请输入有效的图片ID数字" };

            var record = await ImageStorage.GetByIdAsync(imageId);
            if (record == null)
                return new ToolResult { Status = "failed", Error = $"图片 #{imageId} 不存在" };

            // 优先原图（查看图片的目的就是看清细节）
            var path = File.Exists(record.LocalPath) ? record.LocalPath : record.ThumbnailPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return new ToolResult { Status = "failed", Error = $"图片 #{imageId} 文件丢失" };

            var result = new ToolResult
            {
                Status = "success",
                Data = $"图片 #{imageId}" + (string.IsNullOrEmpty(record.Description) ? "" : $"（{record.Description}）"),
                Attachments = new List<ContentPart>
                {
                    ContentPart.FromImagePath(path)
                }
            };
            return result;
        }
    }
}
