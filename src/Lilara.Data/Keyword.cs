using System;
using SQLite;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 关键词库实体。区分两类：
    /// attribute（开放属性名，供断言侧使用，可标记 IsExclusive 驱动冲突检测）
    /// 与 keyword（普通关键词，附带 Person/Topic/Behavior/Place/Event 分类）。
    /// </summary>
    [Table("Keywords")]
    internal class Keyword
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        /// <summary>关键词名</summary>
        public string Name { get; set; } = "";

        /// <summary>别名 JSON（数组，形如 ["别名1","别名2"]），可为 null</summary>
        public string? AliasesJson { get; set; }

        /// <summary>角色：attribute（开放属性名/断言侧）| keyword（普通关键词）</summary>
        public string Role { get; set; } = "keyword";

        /// <summary>keyword 用分类：Person / Topic / Behavior / Place / Event；attribute 时为空</summary>
        public string? Category { get; set; }

        /// <summary>排他属性标记（属性关键词用），驱动冲突检测</summary>
        public bool IsExclusive { get; set; } = false;

        /// <summary>被使用次数</summary>
        public int UseCount { get; set; } = 0;

        /// <summary>创建时间</summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>上次被使用时间</summary>
        public DateTime? LastUsedAt { get; set; }
    }
}