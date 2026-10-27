using System;
using SQLite;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 冲突组成员实体。组与断言（Claims）的多对多关系表，
    /// 只存成员关系 + 加入时间戳，不做状态字段（状态全由数值派生）。
    /// </summary>
    [Table("ConflictGroupMembers")]
    internal class ConflictGroupMember
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        /// <summary>关联的冲突组（ConflictGroup.Id）</summary>
        public int GroupId { get; set; }

        /// <summary>关联的断言（Claims.Id）</summary>
        public int ClaimId { get; set; }

        /// <summary>加入该组的时间</summary>
        public DateTime JoinAt { get; set; } = DateTime.Now;
    }
}
