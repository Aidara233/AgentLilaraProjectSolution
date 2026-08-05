using System;
using System.IO;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Config
{
    /// <summary>
    /// 情绪状态配置（五轴情绪模型）。
    /// 对应 docs/memory-redesign/02-emotion-model.md，持久化到 EmotionState.json，
    /// 重启时加载作为情绪当前值基线。
    /// </summary>
    internal class EmotionStateConfig
    {
        /// <summary>格式版本号，用于未来格式迁移。</summary>
        public int FormatVersion { get; set; } = 1;

        /// <summary>兴奋（-1 到 1），默认 0。</summary>
        public float Excitement { get; set; } = 0;

        /// <summary>喜悦（-1 到 1），默认 0。</summary>
        public float Joy { get; set; } = 0;

        /// <summary>敏觉（-1 到 1），默认 0。</summary>
        public float Alertness { get; set; } = 0;

        /// <summary>满足（-1 到 1），默认 0。</summary>
        public float Satisfaction { get; set; } = 0;

        /// <summary>积极（-1 到 1），默认 0。</summary>
        public float Optimism { get; set; } = 0;

        /// <summary>最近一次更新时间。</summary>
        public DateTime LastUpdatedAt { get; set; } = DateTime.Now;

        /// <summary>
        /// 从指定路径加载情绪状态。
        /// 文件不存在或内容损坏时返回默认状态，不抛出异常。
        /// </summary>
        public static EmotionStateConfig Load(string path)
        {
            if (!File.Exists(path))
                return new EmotionStateConfig();

            try
            {
                var json = File.ReadAllText(path);
                return JsonConvert.DeserializeObject<EmotionStateConfig>(json) ?? new EmotionStateConfig();
            }
            catch
            {
                // 损坏回退默认，避免启动被脏文件阻断
                return new EmotionStateConfig();
            }
        }

        /// <summary>
        /// 保存情绪状态到指定路径。
        /// 先写 {path}.tmp 再原子移动覆盖，避免写一半留下损坏文件。
        /// </summary>
        public void Save(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null) Directory.CreateDirectory(dir);

            var tmp = path + ".tmp";
            var json = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
    }
}
