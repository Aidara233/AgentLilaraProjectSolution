using SQLite;
using System;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 用户实体，记录用户的基本信息和信任等级。
    /// </summary>
    [Table("Users")]
    internal class User
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        /// <summary>平台来源（如 "Console", "QQ", "Telegram"）</summary>
        public string Platform { get; set; } = "";

        /// <summary>平台侧用户ID，用于从 Adapter 层映射到内部用户</summary>
        public string PlatformId { get; set; } = "";

        /// <summary>信任等级，决定了用户可以执行哪些任务</summary>
        public TrustLevel TrustLevel { get; set; }

        /// <summary>快速记忆，记录用户的主要信息（姓名、兴趣等）</summary>
        public string FastMemory { get; set; } = "";
    }

    public enum TrustLevel
    {
        Blocked = -2,       // 阻止，完全不接收聊天消息，也不响应任何消息
        Caution = -1,       // 警告，不响应聊天，但可见其消息
        Unknown = 0,        // 未知，完全没定级/默认，只响应聊天
        Stranger = 1,       // 陌生，有记录但不多，同上
        Understanding = 2,  // 认识，有点记录，会根据记录回应
        Familiarity = 3,    // 熟悉，有很多记录，会主动响应，允许执行较复杂任务
        Trust = 4,          // 信任，允许执行存在风险任务，高风险任务会阻止
        AbsoluteTrust = 5,  // 绝对信任，允许执行高风险任务，有额外确认步骤
        Administrator = 6,  // 管理员，允许执行所有任务，有额外确认步骤
    }
}
