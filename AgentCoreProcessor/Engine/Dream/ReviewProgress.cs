using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// ReviewEngine 会话状态。save_progress 时序列化，下次启动时恢复。
    /// </summary>
    internal class ReviewProgress
    {
        public int? CursorMessageId { get; set; }
        public int? CursorChannelId { get; set; }

        public List<EvaluationBufferEntry> EvaluationBuffer { get; set; } = new();

        public string ThinkingNotes { get; set; } = "";

        public int TokensUsed { get; set; }
        public bool ReserveUsed { get; set; }

        public DateTime? SavedAt { get; set; }

        public static ReviewProgress Load(string path)
        {
            if (!File.Exists(path))
                return new ReviewProgress();

            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<ReviewProgress>(json) ?? new ReviewProgress();
        }

        public void Save(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
    }

    internal class EvaluationBufferEntry
    {
        public string TargetType { get; set; } = "";
        public int TargetId { get; set; }
        public string Dimension { get; set; } = "";
        public string Rating { get; set; } = "";
    }
}
