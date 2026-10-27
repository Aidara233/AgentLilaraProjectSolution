using System;
using SQLite;

namespace AgentCoreProcessor.Database
{
    [Table("ModelCallLogs")]
    internal class ModelCallLog
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        [Indexed]
        public DateTime Timestamp { get; set; }

        [Indexed]
        public string CoreName { get; set; } = "";

        public string? Model { get; set; }
        public string? Provider { get; set; }

        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int CacheCreationTokens { get; set; }
        public int CacheReadTokens { get; set; }

        /// <summary>DeepSeek 自动缓存命中</summary>
        public int CacheHitTokens { get; set; }

        /// <summary>调用是否失败（token 数据可能不完整）</summary>
        public bool IsError { get; set; }

        public string? LogFileName { get; set; }
    }
}
