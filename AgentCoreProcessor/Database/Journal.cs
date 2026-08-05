using System;
using SQLite;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 经历实体（正文式）。第一人称叙事，Lilara 为主体。
    /// 写入即历史，不可覆盖不可修正；不参与 Claims 的覆盖/同源逻辑。
    /// </summary>
    [Table("Journals")]
    internal class Journal
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        /// <summary>第一人称正文</summary>
        public string Content { get; set; } = "";

        /// <summary>经历类型：Experience(L2) | Reflection(L3)</summary>
        public string Kind { get; set; } = "Experience";

        /// <summary>来源：Framework | Session | UserCommand | System</summary>
        public string Source { get; set; } = "Session";

        /// <summary>上下文锚点：频道</summary>
        public int? ChannelId { get; set; }

        /// <summary>上下文锚点：会话</summary>
        public int? SessionId { get; set; }

        /// <summary>情绪信号 JSON：[{ Axis, Target, Reason }]</summary>
        public string? EmotionSignalsJson { get; set; }

        /// <summary>expect 参数原文（expect 链路待定）</summary>
        public string? ExpectRaw { get; set; }

        /// <summary>经历发生时间</summary>
        public DateTime OccurredAt { get; set; } = DateTime.Now;

        /// <summary>记录时间（写入库时间）</summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>处理状态：Raw|Tagged|Processed|Archived</summary>
        public string Status { get; set; } = "Raw";
    }
}
