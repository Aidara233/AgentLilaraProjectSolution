using System;
using SQLite;

namespace AgentCoreProcessor.Database
{
    [Table("ImageRecords")]
    internal class ImageRecord
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        [Indexed(Unique = true)]
        public string Hash { get; set; } = "";
        public string LocalPath { get; set; } = "";
        public string? SourceUrl { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
