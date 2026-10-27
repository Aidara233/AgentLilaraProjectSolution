using System;
using System.Collections.Generic;

namespace AgentCoreProcessor.Adapter
{
    /// <summary>POST /api/send 请求体。</summary>
    internal class SendRequest
    {
        /// <summary>频道名（缺省用 defaultChannel）。</summary>
        public string Channel { get; set; } = "";
        public string Content { get; set; } = "";
        public string? User { get; set; }
        public string? DisplayName { get; set; }
        public bool? IsPrivate { get; set; }
        public bool? IsMentioned { get; set; }
        public string? ReplyTo { get; set; }

        /// <summary>true=阻塞等突发结束统一返回；false=立即返回 accepted。</summary>
        public bool Wait { get; set; } = true;
        public int? TimeoutMs { get; set; }
    }

    /// <summary>出向消息快照（speak 输出），WS 推送与 burst 收集共用。</summary>
    internal class OutgoingSnapshot
    {
        public string Channel { get; init; } = "";
        public string Content { get; init; } = "";
        public List<MessageSegment>? Segments { get; init; }
        public string? ReplyTo { get; init; }
        public List<string>? Mentions { get; init; }
        public long Seq { get; init; }
        public DateTime Time { get; init; }
    }
}
