using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Tool.Contract.Services
{
    /// <summary>
    /// 定时任务调度接口。
    /// </summary>
    public interface ISchedulingAccess
    {
        /// <summary>创建定时任务。</summary>
        Task<ScheduledTaskInfo> CreateAsync(string timeExpression, string description, string? payload = null, string? owner = null);

        /// <summary>取消定时任务。</summary>
        Task<bool> CancelAsync(string taskId);

        /// <summary>获取活跃的定时任务列表。</summary>
        Task<List<ScheduledTaskInfo>> GetActiveAsync();
    }

    public class ScheduledTaskInfo
    {
        public string Id { get; set; } = "";
        public string Description { get; set; } = "";
        public DateTime? NextFireTime { get; set; }
        public string Status { get; set; } = "";
    }
}
