using SQLite;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 用户实体（账号级）。每个平台账号对应一条记录，通过 PersonId 关联到自然人。
    /// </summary>
    [Table("Users")]
    internal class User
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        /// <summary>关联的自然人ID（逻辑外键，指向 Person.Id）</summary>
        public int PersonId { get; set; }

        /// <summary>平台来源（如 "Console", "QQ", "Telegram"）</summary>
        public string Platform { get; set; } = "";

        /// <summary>平台侧用户ID，用于从 Adapter 层映射到内部用户</summary>
        public string PlatformId { get; set; } = "";

        /// <summary>权限等级，只能通过管理员指令修改</summary>
        public PermissionLevel PermissionLevel { get; set; } = PermissionLevel.Default;

        /// <summary>显示名（群名片优先，昵称兜底）。由适配器每次消息时更新。</summary>
        public string DisplayName { get; set; } = "";

        /// <summary>快速记忆，记录用户的主要信息（姓名、兴趣等）</summary>
        public string FastMemory { get; set; } = "";
    }
}
