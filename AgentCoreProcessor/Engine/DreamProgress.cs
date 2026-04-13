using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 单个复盘调查的进度存档。
    /// </summary>
    internal class ReviewInvestigation
    {
        public string Mode { get; set; } = "";
        public Dictionary<string, object> Target { get; set; } = new();
        public List<string> Findings { get; set; } = new();
        public List<string> NextSteps { get; set; } = new();
        public DateTime SavedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// 复盘进度存档。ReviewEngine 通过"保存进度"工具写入，
    /// 下次大睡启动时加载，决定是否继续未完成的调查。
    /// </summary>
    internal class DreamProgress
    {
        public List<ReviewInvestigation> ActiveInvestigations { get; set; } = new();

        public static DreamProgress Load(string path)
        {
            if (!File.Exists(path))
                return new DreamProgress();

            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<DreamProgress>(json) ?? new DreamProgress();
        }

        public void Save(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
    }
}
