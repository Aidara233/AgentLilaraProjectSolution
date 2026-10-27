using System;
using SQLite;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 自然人实体。一个 Person 可关联多个 User（跨平台账号）。
    /// 信任等级挂在 Person 上，由模型在"做梦"时评估调整。
    /// </summary>
    [Table("Persons")]
    public class Person
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        /// <summary>主要称呼（跨平台统一名称，由做梦时选定）</summary>
        public string Name { get; set; } = "";

        /// <summary>别称列表（逗号分隔，用于识别同一人的不同称呼）</summary>
        public string Aliases { get; set; } = "";

        /// <summary>信任等级，影响框架前置路由行为</summary>
        public TrustLevel TrustLevel { get; set; } = TrustLevel.Unknown;

        /// <summary>全局好感度（可负），与硬性条件共同决定实际信任等级</summary>
        public float TrustProgress { get; set; } = 0f;

        /// <summary>快速记忆，一句话概括此人关键信息</summary>
        public string FastMemory { get; set; } = "";

        /// <summary>警报等级（0-4），递增惩罚</summary>
        public int AlertLevel { get; set; } = 0;

        /// <summary>上次警报触发时间，用于冷却恢复</summary>
        public DateTime? LastAlertTime { get; set; }

        /// <summary>创建时间</summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// 信任等级（Person 级）。
    /// 每级对应具体的框架行为，模型可在做梦时调整。
    /// </summary>
    public enum TrustLevel
    {
        Hostile = -2,       // 敌视，基本不想理，被@才敷衍回应
        Wary = -1,          // 警惕，态度冷淡但还是会回应
        Unknown = 0,        // 完全没定级/默认
        Stranger = 1,       // 出现过但不熟
        Understanding = 2,  // 有一定记忆，会根据记录回应
        Familiarity = 3,    // 互动频繁，会主动响应
        Trust = 4,          // 信任，模型评估通过
        AbsoluteTrust = 5,  // 绝对信任，管理员手动
    }
}
