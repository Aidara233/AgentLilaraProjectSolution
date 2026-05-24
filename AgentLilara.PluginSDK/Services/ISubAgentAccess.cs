using System.Threading.Tasks;

namespace AgentLilara.PluginSDK.Services
{
    /// <summary>
    /// 子 agent 管理接口（系统循环用）。
    /// </summary>
    public interface ISubAgentAccess
    {
        /// <summary>创建并启动子 agent。</summary>
        SubAgentInfo Create(string instruction);

        /// <summary>创建带委托 ID 的子 agent。</summary>
        SubAgentInfo Create(string instruction, string? delegationId);

        /// <summary>按 ID 获取子 agent。</summary>
        SubAgentInfo? Get(string sessionId);

        /// <summary>向子 agent 追加指令。</summary>
        Task<bool> SendInstructionAsync(string sessionId, string instruction);

        /// <summary>请求停止子 agent。</summary>
        void RequestStop(string sessionId);

        /// <summary>列出所有活跃的子 agent。</summary>
        List<SubAgentInfo> List();
    }

    public class SubAgentInfo
    {
        public string SessionId { get; set; } = "";
        public bool IsAlive { get; set; }
    }
}
