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
