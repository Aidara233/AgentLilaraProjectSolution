using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentLilara.PluginSDK.Services
{
    /// <summary>
    /// 适配器操作接口（平台交互）。
    /// </summary>
    public interface IAdapterAccess
    {
        /// <summary>获取 bot 在指定平台的 ID。</summary>
        string? GetBotPlatformId(string platform);

        /// <summary>执行适配器操作（如获取群列表、戳一戳等）。</summary>
        Task<string?> ExecuteActionAsync(string adapterId, string action, string? paramsJson = null);

        /// <summary>获取适配器支持的可用操作列表。</summary>
        List<AdapterActionInfo> GetAvailableActions(string adapterId);

        /// <summary>根据频道ID（如 group_123456）查找对应的适配器ID。</summary>
        string? GetAdapterIdForChannel(string channelId);

        /// <summary>按平台名查找对应的适配器ID（task/review 等无频道连接时会话定位适配器用）。</summary>
        string? GetAdapterIdByPlatform(string platform);

        /// <summary>获取 QQ 合并转发内容（含递归统计 + 全文渲染 + 子消息图片URL）。失败返回 null。</summary>
        Task<ForwardMessageResult?> GetForwardMessageAsync(string adapterId, string forwardId);
    }

    /// <summary>
    /// 合并转发查询结果：统计行 + 全文 + 图片URL。
    /// Summary 为入站自动统计用的紧凑统计行；FullText 为完整展开文本（含嵌套缩进）；
    /// ImageUrls 与 FullText 中的 [图片N] 标记一一对应，供工具下载后注入多模态。
    /// </summary>
    public class ForwardMessageResult
    {
        public string ForwardId { get; set; } = "";
        public string Summary { get; set; } = "";
        public string FullText { get; set; } = "";
        public List<string> ImageUrls { get; set; } = new();
        public bool Truncated { get; set; }
    }

    /// <summary>
    /// 适配器操作元数据。
    /// </summary>
    public class AdapterActionInfo
    {
        public string Name { get; init; } = "";
        public string Label { get; init; } = "";
        public string Description { get; init; } = "";
        public List<ActionParamInfo> Params { get; init; } = new();
    }

    /// <summary>
    /// 操作参数元数据。
    /// </summary>
    public class ActionParamInfo
    {
        public string Name { get; init; } = "";
        public string Label { get; init; } = "";
        public string Type { get; init; } = "text";
        public bool Required { get; init; } = true;
    }
}
