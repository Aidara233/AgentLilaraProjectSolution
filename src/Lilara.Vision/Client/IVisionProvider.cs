using System.Threading.Tasks;

namespace AgentCoreProcessor.Client
{
    /// <summary>
    /// 视觉模型接口。将图片转为文字描述。
    /// </summary>
    internal interface IVisionProvider
    {
        /// <summary>
        /// 描述图片内容。
        /// </summary>
        /// <param name="imagePath">本地图片文件路径</param>
        /// <param name="contextHint">对话上下文提示，帮助模型聚焦描述重点（可选）</param>
        /// <returns>图片的文字描述</returns>
        Task<string> DescribeImageAsync(string imagePath, string? contextHint = null);
    }
}
