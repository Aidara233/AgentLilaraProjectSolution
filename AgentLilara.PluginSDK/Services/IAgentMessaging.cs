using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentLilara.PluginSDK.Services
{
    /// <summary>
    /// 统一 agent 间通信接口。所有跨循环请求和通知的唯一入口。
    /// </summary>
    public interface IAgentMessaging
    {
        /// <summary>提交请求并等待首个回应（评估/超时）。保留用于特殊场景。</summary>
        Task<CrossRequestResult> SubmitAndWaitAsync(
            string? targetId, string title, string content,
            Dictionary<string, string>? metadata = null,
            TimeSpan? timeout = null);

        /// <summary>Fire-and-forget 提交请求。状态变更通过通知队列送达。</summary>
        string SubmitFireAndForget(string? targetId, string title, string content);

        /// <summary>读取当前循环的待处理请求。</summary>
        List<CrossRequestInfo> Receive(int maxCount = 10);

        /// <summary>当前循环回应某请求。返回是否成功。</summary>
        bool Respond(string requestId, CrossRequestResponseType type, string content);

        /// <summary>排出待通知的委托状态变更（由 inject 模块消费）。</summary>
        List<DelegationNotificationInfo> DrainNotifications();

        /// <summary>查询活跃请求。</summary>
        List<CrossRequestInfo> GetActiveRequests();

        /// <summary>查询已完成/失败的请求。</summary>
        List<CrossRequestInfo> GetCompletedRequests();

        /// <summary>查询已归档的请求。</summary>
        List<CrossRequestInfo> GetArchivedRequests();

        /// <summary>归档请求（所有接受者收到通知）。</summary>
        void Archive(string requestId);

        /// <summary>忽略广播请求（仅广播可用）。</summary>
        void Ignore(string requestId);

        /// <summary>获取单个请求详情。</summary>
        CrossRequestInfo? Get(string requestId);

        /// <summary>获取所有活跃循环的 ID 列表。</summary>
        List<string> GetActiveLoopIds();
    }

    // ═══════ DTO ═══════

    public enum CrossRequestResponseType
    {
        Accept,
        Reject,
        Progress,
        Complete,
        Failed,
        Ignore
    }

    public class CrossRequestResult
    {
        public string RequestId { get; set; } = "";
        public bool Success { get; set; }
        public bool TimedOut { get; set; }
        public string? Verdict { get; set; }
        public string? Result { get; set; }
    }

    public class CrossRequestInfo
    {
        public string Id { get; set; } = "";
        public string InitiatorId { get; set; } = "";
        public string? TargetId { get; set; }
        public string Title { get; set; } = "";
        public string Content { get; set; } = "";
        public string State { get; set; } = "";
        public DateTime SubmittedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public List<CrossRequestResponseInfo> Responses { get; set; } = new();
    }

    public class CrossRequestResponseInfo
    {
        public int Seq { get; set; }
        public string ResponderId { get; set; } = "";
        public string Type { get; set; } = "";
        public string Content { get; set; } = "";
        public DateTime Timestamp { get; set; }
    }

    public class DelegationNotificationInfo
    {
        public string RequestId { get; set; } = "";
        public string Title { get; set; } = "";
        public string NewState { get; set; } = "";
        public string ResponseType { get; set; } = "";
        public string? ResponderId { get; set; }
        public string? Content { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>旧版 AgentMessage，保留兼容。</summary>
    public class AgentMessage
    {
        public string Type { get; set; } = "notification";
        public string Content { get; set; } = "";
        public string? SourceSessionId { get; set; }
        public Dictionary<string, string>? Metadata { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }
}
