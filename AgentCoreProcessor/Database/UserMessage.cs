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
        public DateTime Time { get; set; }// 消息时间
    }
}
