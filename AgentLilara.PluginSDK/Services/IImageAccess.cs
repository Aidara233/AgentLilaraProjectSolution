using System.Threading.Tasks;

namespace AgentLilara.PluginSDK.Services
{
    /// <summary>
    /// 图片访问接口。桥接插件的读图/OCR需求到内部 ImageStorage。
    /// </summary>
    public interface IImageAccess
    {
        /// <summary>
        /// 解析图片标识符到本地路径。入库（如需要），不调任何外部API。
        /// </summary>
        /// <param name="identifier">图片标识：workspace 相对路径或 received 图片的数据库主键 ID</param>
        /// <param name="source">"workspace"（默认）或 "received"</param>
        Task<ImageResolveResult> ResolveImageAsync(string identifier, string source = "workspace");

        /// <summary>
        /// 对图片执行 OCR 文字识别。始终调用 API（忽略缓存结果），写回数据库。
        /// </summary>
        Task<ImageOcrResult> OcrImageAsync(string identifier, string source = "workspace");

        /// <summary>
        /// 获取图片的缓存 OCR 结果。不调用 API，无缓存时返回 null。
        /// </summary>
        Task<ImageOcrResult> GetOcrAsync(string identifier, string source = "workspace");
    }

    /// <summary>
    /// 图片解析结果。包含本地路径和元数据，供工具构造 ContentAttachment。
    /// </summary>
    public class ImageResolveResult
    {
        public bool Success { get; set; }
        public string? LocalPath { get; set; }
        public string? Error { get; set; }
        public int? ImageId { get; set; }
        public string? ImageHash { get; set; }
        public string? Category { get; set; }
        public string? DisplayName { get; set; }
    }

    /// <summary>
    /// 图片 OCR 结果。
    /// </summary>
    public class ImageOcrResult
    {
        public bool HasText { get; set; }
        public string? Text { get; set; }
        public bool Success { get; set; } = true;
        public string? Error { get; set; }
    }
}
