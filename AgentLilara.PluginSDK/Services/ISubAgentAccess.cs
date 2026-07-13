// AgentLilara.PluginSDK/Services/ISubAgentAccess.cs
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentLilara.PluginSDK.Services
{
    /// <summary>
    /// 子 agent（即子 session）管理接口。供父 session 通过工具创建/管理/通信子 session。
    /// 子 session 本质是一个 RuntimeSession，由 SessionEngine.CreateSession 创建；
    /// 本接口在 SubAgentAccessAdapter 中桥接父 session 与子 session 的双向通信。
    /// </summary>
    public interface ISubAgentAccess
    {
        /// <summary>创建并启动子 agent。返回子 agent 信息（含 sessionId 供父 session 后续操作）。</summary>
        SubAgentInfo Create(string instruction, SubAgentOptions? options = null);

        /// <summary>按 ID 查询子 agent。</summary>
        SubAgentInfo? Get(string sessionId);

        /// <summary>向运行中的子 agent 追加一条指令（写入 mailbox 端口，立即唤醒子 agent）。</summary>
        Task<bool> SendInstructionAsync(string sessionId, string instruction);

        /// <summary>请求停止子 agent。会先检查是否有未结束的子-子 agent。</summary>
        SubAgentStopResult RequestStop(string sessionId);

        /// <summary>列出活跃的子 agent。可按 parentLoopId 过滤仅返回本父 session 的子 agent。</summary>
        List<SubAgentInfo> List(string? parentLoopId = null);

        /// <summary>查询子 agent 详细状态（轮次、存活时间、最终输出预览）。</summary>
        SubAgentDetail? GetDetail(string sessionId);

        /// <summary>
        /// 子 agent 主动向父 session 回报进度或完成。由 report_to_parent 工具调用。
        /// content 会注入到父 session 的下一轮上下文（[子会话回报] 系统消息）。
        /// </summary>
        /// <param name="callerLoopId">调用方所在的 loopId（即子 agent 的 "session:xxx"）。</param>
        /// <param name="content">回报内容文本。</param>
        /// <param name="state">done=Done 完成 / progress=Progress 进行中 / failed=Failed 失败。</param>
        bool ReportToParent(string callerLoopId, string content, ReportState state);

        /// <summary>
        /// 阻塞等待指定子 agent 完成（或超时）。供 wait_sub 工具调用。
        /// </summary>
        Task<bool> WaitAsync(string sessionId, int timeoutMs);
    }

    /// <summary>子 agent 创建参数。</summary>
    public class SubAgentOptions
    {
        /// <summary>父 session 的 loopId（"channel:N" 或 "session:xxx"）。用于结果回传与唤醒。</summary>
        public string? ParentLoopId { get; set; }

        /// <summary>子 agent 启用的组件 ID 列表。为空时使用默认组件集。</summary>
        public string[]? ComponentIds { get; set; }

        /// <summary>覆盖初始状态文本（默认等于 instruction）。</summary>
        public string? InitialState { get; set; }
    }

    /// <summary>子 agent 回报状态枚举。</summary>
    public enum ReportState
    {
        Progress,
        Done,
        Failed
    }

    /// <summary>子 agent 摘要信息。</summary>
    public class SubAgentInfo
    {
        public string SessionId { get; set; } = "";
        public bool IsAlive { get; set; }
        /// <summary>父 loopId（谁创建的我）。</summary>
        public string? ParentLoopId { get; set; }
    }

    /// <summary>子 agent 详细状态。</summary>
    public class SubAgentDetail
    {
        public string SessionId { get; set; } = "";
        public bool IsAlive { get; set; }
        public string Status { get; set; } = "";
        public string? ParentLoopId { get; set; }
        public int RoundCount { get; set; }
        public System.DateTime CreatedAt { get; set; }
        public System.DateTime LastActivity { get; set; }
        public string? FinalOutput { get; set; }
    }

    /// <summary>停止子 agent 的结果。</summary>
    public class SubAgentStopResult
    {
        public bool Success { get; set; }
        public string? BlockReason { get; set; }
        public List<string>? BlockingChildren { get; set; }
    }
}