using System;
using SQLite;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 断言实体（三元组）。遵循《断言存储系统设计决定.md》：
    /// subject/predicate/object 为槽位 JSON {type, index, ref?}，index 存表面词。
    /// 无 polarity/confidence，否定靠 strength 负数，可靠性靠来源链派生。
    /// </summary>
    [Table("Claims")]
    internal class Claim
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        /// <summary>主体槽位 JSON：{ type, index, ref? }。entity 只存 index 不归并。</summary>
        public string SubjectJson { get; set; } = "{}";

        /// <summary>谓词槽位 JSON：{ type, index, ref? }。relation|attribute。</summary>
        public string PredicateJson { get; set; } = "{}";

        /// <summary>客体槽位 JSON：{ type, index, ref? }。entity|keyword|attribute|literal|assertion。</summary>
        public string ObjectJson { get; set; } = "{}";

        /// <summary>拼好的整句原文（召回时直接返回）</summary>
        public string Raw { get; set; } = "";

        /// <summary>来源 JSON：{ who, ref { kind, id } }。谁说的 + 哪条消息/经历（承担引用职责）。</summary>
        public string SourceJson { get; set; } = "{}";

        /// <summary>同源分组。一句话拆多条断言的共同出身，防重复计分。</summary>
        public string? UtteranceId { get; set; }

        /// <summary>程度值 [-1,1]。态度方向强度（爱+0.9 / 喜欢+0.4 / 不喜欢-0.8），或支持边强度。与 Credibility 正交。</summary>
        public float? Strength { get; set; }

        /// <summary>可信度锚点。写入者定初值（stated≈1.0 / inferred=推断确定度），Review 可改。</summary>
        public float Credibility { get; set; } = 1.0f;

        /// <summary>推断标记。跨等级碾压（stated 压 inferred）+ 召回带"推断"标签。</summary>
        public bool IsInference { get; set; } = false;

        public DateTime? ValidFrom { get; set; }
        public DateTime? ValidUntil { get; set; }

        /// <summary>记录时间（写入库时间）</summary>
        public DateTime RecordedAt { get; set; } = DateTime.Now;

        /// <summary>经历发生时间</summary>
        public DateTime? OccurredAt { get; set; }

        /// <summary>处理状态：Raw|Tagged|Processed|Archived</summary>
        public string Status { get; set; } = "Raw";
    }
}
