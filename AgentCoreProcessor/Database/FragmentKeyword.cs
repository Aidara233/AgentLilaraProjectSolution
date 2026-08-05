using System;
using SQLite;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 关联片段类型。一个关键词可关联到 Claim 或 Journal。
    /// </summary>
    public enum FragmentOwnerType
    {
        Claim = 0,
        Journal = 1
    }

    /// <summary>
    /// 片段-关键词关联表。Claim/Journal 与 Keywords 的多对多关联。
    /// </summary>
    [Table("FragmentKeywords")]
    internal class FragmentKeyword
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        /// <summary>关联对象类型（Claim|Journal）</summary>
        public FragmentOwnerType OwnerType { get; set; }

        /// <summary>关联对象 Id（Claim.Id 或 Journal.Id）</summary>
        public int OwnerId { get; set; }

        /// <summary>关键词 Id（Keywords.Id）</summary>
        public int KeywordId { get; set; }
    }
}
