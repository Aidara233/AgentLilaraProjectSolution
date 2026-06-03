using System;
using SQLite;

namespace AgentCoreProcessor.Database
{
    [Table("ReviewSessions")]
    internal class ReviewSession
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        public string? SignalId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }

        /// <summary>beacon / random / resume</summary>
        public string SeedType { get; set; } = "";

        /// <summary>completed / budget / interrupted</summary>
        public string StopReason { get; set; } = "";

        public int TokensUsed { get; set; }
        public int RoundsExecuted { get; set; }

        /// <summary>JSON array of channel IDs</summary>
        public string ChannelsVisited { get; set; } = "[]";

        /// <summary>JSON array of person IDs</summary>
        public string PersonsEncountered { get; set; } = "[]";

        public string? ThinkingNotes { get; set; }
        public int EvaluationCount { get; set; }

        /// <summary>快照：complete 时的原始评价记录 JSON</summary>
        public string? RawEvaluations { get; set; }
    }

    [Table("ReviewActions")]
    internal class ReviewAction
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        [Indexed]
        public int SessionId { get; set; }

        public int SeqIndex { get; set; }
        public DateTime Time { get; set; }

        /// <summary>write_memory / update_person / evaluate / link_memory</summary>
        public string ActionType { get; set; } = "";

        public string Summary { get; set; } = "";

        /// <summary>JSON：具体参数和结果</summary>
        public string? Detail { get; set; }
    }
}
