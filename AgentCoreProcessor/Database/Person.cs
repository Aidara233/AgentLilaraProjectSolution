using System;
using SQLite;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 自然人实体。一个 Person 可关联多个 User（跨平台账号）。
    /// 信任等级挂在 Person 上，由模型在"做梦"时评估调整。
    /// </summary>
    [Table("Persons")]
    internal class Person
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        /// <summary>信任等级，影响框架前置路由行为</summary>
        public TrustLevel TrustLevel { get; set; } = TrustLevel.Unknown;

        /// <summary>级内进度值 (0.0-1.0)，预留平滑用</summary>
        public float TrustProgress { get; set; } = 0f;

        /// <summary>创建时间</summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// 信任等级（Person 级）。
    /// 每级对应具体的框架行为，模型可在做梦时调整。
    /// </summary>
    public enum TrustLevel
    {
        Unknown = 0,        // 完全没定级/默认，只响应聊天
        Stranger = 1,       // 有记录但不多，同上
        Understanding = 2,  // 有点记录，会根据记录回应
        Familiarity = 3,    // 有很多记录，会主动响应
        Trust = 4,          // 信任，允许更深入的交互
        AbsoluteTrust = 5,  // 绝对信任
    }
}
