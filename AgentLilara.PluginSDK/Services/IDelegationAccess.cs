using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentLilara.PluginSDK.Services
{
    /// <summary>
    /// 委托系统访问接口（频道循环提交委托、系统循环评估委托）。
    /// </summary>
    public interface IDelegationAccess
    {
        /// <summary>提交委托并等待评估结果。</summary>
        Task<DelegationSubmitResult> SubmitAndWaitAsync(
            string description, string? context, int channelId, int personId,
            System.TimeSpan timeout);

        /// <summary>解决委托评估（系统循环用）。</summary>
        bool ResolveEvaluation(string delegationId, string verdict, string reason);

        /// <summary>获取待评估的委托列表。</summary>
        List<DelegationInfo> GetPendingForEvaluation();

        /// <summary>标记委托开始执行。</summary>
        void MarkExecuting(string delegationId);

        /// <summary>标记委托完成。</summary>
        void MarkCompleted(string delegationId, string result);

        /// <summary>标记委托失败。</summary>
        void MarkFailed(string delegationId, string error);

        /// <summary>获取委托详情。</summary>
        DelegationInfo? Get(string delegationId);

        /// <summary>获取频道已完成（含失败）且未消费的委托。</summary>
        List<DelegationInfo> GetCompletedForChannel(int channelId);

        /// <summary>获取频道进行中的委托（Accepted/Queued/Executing）。</summary>
        List<DelegationInfo> GetActiveForChannel(int channelId);

        /// <summary>标记委托结果已被频道消费。</summary>
        void ConsumeCompleted(string delegationId);

        /// <summary>取消/移除委托（任何状态均可）。</summary>
        bool Cancel(string delegationId);

        /// <summary>检查委托是否还能重试。</summary>
        bool CanRetry(string delegationId);

        /// <summary>递增重试计数并将状态重置为 Accepted（准备重新执行）。</summary>
        void IncrementRetry(string delegationId);

        /// <summary>获取等待重试决策的委托列表。</summary>
        List<DelegationInfo> GetRetryPending();
    }

    public class DelegationSubmitResult
    {
        public bool Success { get; set; }
        public string? Verdict { get; set; }
        public string? Reason { get; set; }
        public bool TimedOut { get; set; }
    }

    public class DelegationInfo
    {
        public string Id { get; set; } = "";
        public string Description { get; set; } = "";
        public string? Context { get; set; }
        public int ChannelId { get; set; }
        public string Status { get; set; } = "";
        public string? Result { get; set; }
        public int RetryCount { get; set; }
    }
}
