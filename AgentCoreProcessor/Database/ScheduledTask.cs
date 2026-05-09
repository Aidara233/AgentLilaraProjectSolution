using System;
using SQLite;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 定时任务实体。支持一次性和重复执行。
    /// 时间表达式支持：
    /// - 相对时间: "30m", "2h", "1d"
    /// - 绝对时间: "09:00", "2026-05-10 14:00"
    /// - 重复: "every 1h", "every 30m", "daily 09:00", "weekly mon 09:00"
    /// </summary>
    [Table("ScheduledTask")]
    public class ScheduledTask
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        /// <summary>所有者类型: "system" 或 "channel"</summary>
        public string OwnerType { get; set; } = "system";

        /// <summary>所有者 ID: 系统循环为 "system"，频道循环为频道 ID</summary>
        public string OwnerId { get; set; } = "system";

        /// <summary>任务描述（人类可读）</summary>
        public string Description { get; set; } = "";

        /// <summary>下次触发时间</summary>
        public DateTime NextFireTime { get; set; }

        /// <summary>重复间隔（秒）。null 或 0 表示一次性</summary>
        public int RepeatIntervalSeconds { get; set; }

        /// <summary>Cron 风格的重复规则（用于复杂调度）。null 表示使用 RepeatIntervalSeconds</summary>
        public string? CronRule { get; set; }

        /// <summary>任务载荷（JSON 或纯文本，触发时注入 prompt）</summary>
        public string? Payload { get; set; }

        /// <summary>是否激活</summary>
        public bool IsActive { get; set; } = true;

        /// <summary>创建时间</summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>上次触发时间</summary>
        public DateTime? LastFiredAt { get; set; }

        /// <summary>已触发次数</summary>
        public int FireCount { get; set; }
    }
}
