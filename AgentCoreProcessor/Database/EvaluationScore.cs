using System;
using SQLite;

namespace AgentCoreProcessor.Database
{
    [Table("EvaluationScores")]
    internal class EvaluationScore
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        /// <summary>person / channel</summary>
        [Indexed]
        public string TargetType { get; set; } = "";

        [Indexed]
        public int TargetId { get; set; }

        /// <summary>reliability / respect / value / stability</summary>
        public string Dimension { get; set; } = "";

        public float Value { get; set; }

        public DateTime LastEvaluatedAt { get; set; }
    }
}
