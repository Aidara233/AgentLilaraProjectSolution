using System.Threading.Tasks;

namespace AgentCoreProcessor.Tool.Contract.Services
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
    }
}
