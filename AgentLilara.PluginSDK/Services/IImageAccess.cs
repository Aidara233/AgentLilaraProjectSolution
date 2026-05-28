using System.Threading.Tasks;

namespace AgentLilara.PluginSDK.Services
{
    /// <summary>
    /// 图片访问接口。桥接插件的读图/OCR需求到内部 ImageStorage + Vision/OCR 提供者。
    /// </summary>
    public interface IImageAccess
    {
        /// <summary>
        /// 读取/描述图片内容。优先返回缓存描述，缓存缺失时调视觉模型生成。
        /// </summary>
        /// <param name="identifier">图片标识：workspace 相对路径或 received 图片的数据库主键 ID</param>
        /// <param name="source">"workspace"（默认）或 "received"</param>
        /// <param name="forceRefresh">true 时忽略缓存，强制重新生成描述</param>
        /// <param name="contextHint">可选的上下文提示，引导视觉模型聚焦</param>
        Task<ImageReadResult> ReadImageAsync(
            string identifier,
            string source = "workspace",
            bool forceRefresh = false,
            string? contextHint = null);

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
    /// 图片读取结果。
    /// </summary>
    public class ImageReadResult
    {
        public bool Success { get; set; }
        public string? Description { get; set; }
        public string? Error { get; set; }
        public int? ImageId { get; set; }
        public string? ImageHash { get; set; }
        public string? Category { get; set; }
        public bool WasCached { get; set; }
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
