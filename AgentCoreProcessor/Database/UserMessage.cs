using System;
using SQLite;

namespace AgentCoreProcessor.Database
{
    [Table("UserMessages")]
    internal class UserMessage
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }// 消息ID，唯一标识一条消息
        public int UserId { get; set; }// 用户ID，唯一标识一个用户
        public int ChannelId { get; set; }// 频道ID，标识消息所属的频道
        public int TopicId { get; set; }// 话题ID，标识消息所属的话题
        public string Content { get; set; } = "";// 消息内容
        /// <summary>发言人显示名（冗余存储，避免查询时 join）。Bot 消息为 "Lilara"。</summary>
        public string SenderName { get; set; } = "";
        public DateTime Time { get; set; }// 消息时间
        /// <summary>是否为 Lilara 的回复（区分用户消息和 bot 回复）</summary>
        public bool IsFromBot { get; set; } = false;
        /// <summary>平台侧消息ID（用于引用消息上下文查询）。可空，Console/File 不填。</summary>
        public string? PlatformMessageId { get; set; }
        /// <summary>消息中的图片数量（用于 XML 上下文标记）。</summary>
        public int ImageCount { get; set; } = 0;
    }
}
