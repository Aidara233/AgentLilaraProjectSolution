using System.Threading.Tasks;

namespace AgentLilara.PluginSDK.Services
{
    /// <summary>
    /// 复盘信标访问接口。频道循环工具通过此接口标记需要复盘关注的内容。
    /// </summary>
    public interface IBeaconAccess
    {
        Task CreateAsync(string reason, int? channelId = null, int? personId = null, int? messageId = null);
    }
}
