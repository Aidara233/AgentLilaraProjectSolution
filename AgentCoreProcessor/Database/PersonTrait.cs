using System;
using SQLite;

namespace AgentCoreProcessor.Database
{
    [Table("PersonTraits")]
    internal class PersonTrait
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        [Indexed]
        public int PersonId { get; set; }

        /// <summary>preference / habit / style / relationship / expertise</summary>
        public string Category { get; set; } = "";

        /// <summary>属性名，如"食物偏好"、"作息"、"沟通风格"</summary>
        public string Key { get; set; } = "";

        /// <summary>属性值</summary>
        public string Value { get; set; } = "";

        /// <summary>置信度 0-1，多源交叉验证可提高</summary>
        public float Confidence { get; set; } = 0.5f;

        /// <summary>来源线索（哪条消息/记忆得出的）</summary>
        public string SourceHint { get; set; } = "";

        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }
}
