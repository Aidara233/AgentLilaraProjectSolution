using SQLite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AgentCoreProcesser.Engine
{
    [Table("Users")]
    internal class User
    {
        [PrimaryKey,AutoIncrement]
        public int Id { get; set; }// 用户ID，唯一标识一个用户
        public TrustLevel TrustLevel { get; set; }// 信任等级，决定了用户可以执行哪些任务
        public required string FastMemory { get; set; }//快速记忆，记录用户的主要内容，例如姓名、兴趣等
    }

    public enum TrustLevel
    {
        Blocked = -2, // 阻止，完全不接收聊天消息，也不响应任何消息
        Caution = -1, // 警告，不响应聊天，但可见其消息
        Unknown = 0,// 未知，完全没定级/默认，只响应聊天
        Stranger = 1, // 陌生，有记录但不多，同上
        Understanding = 2, // 认识，有点记录，会根据记录回应响应
        Familiarity = 3,// 熟悉，有很多记录，会主动响应，允许执行较复杂任务
        Trust = 4,// 信任，允许执行存在风险任务，高风险任务会阻止；允许执行复杂任务
        AbsoluteTrust = 5,// 绝对信任，允许执行高风险任务，高风险任务会有额外的确认步骤，但无法执行可能自毁的任务
        Administrator = 6,// 管理员，允许执行所有任务，包括极高风险的任务，但会有额外的确认步骤
    }
}
