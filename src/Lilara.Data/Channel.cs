using SQLite;
using System;

namespace AgentCoreProcessor.Database
{
    [Table ("Channels")]
    public class Channel
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public float Affinity { get; set; } = 1.0f;
        public int LastExtractedMessageId { get; set; } = 0;
    }
}
