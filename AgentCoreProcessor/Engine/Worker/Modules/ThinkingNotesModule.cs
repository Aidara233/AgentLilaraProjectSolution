using System.Collections.Generic;
using System.IO;
using AgentCoreProcessor.Config;

namespace AgentCoreProcessor.Engine.Modules
{
    /// <summary>
    /// 思考笔记模块。从 Plugin.WorkingTools 的文件存储读取对应 notebook，注入 prompt。
    /// </summary>
    internal class ThinkingNotesModule : EngineModule
    {
        public override string Name => "思考笔记";
        public override int PromptPriority => 45;

        private readonly string channelId;

        public ThinkingNotesModule(string channelId)
        {
            this.channelId = channelId;
        }

        private string NotebookPath
        {
            get
            {
                var safeName = SanitizeFileName(channelId);
                return Path.Combine(PathConfig.StoragePath, "PluginData", "_system", "notebooks", $"{safeName}.txt");
            }
        }

        /// <summary>读取当前笔记内容（供快照使用）。</summary>
        public Dictionary<string, string> Notes
        {
            get
            {
                var content = ReadContent();
                if (string.IsNullOrEmpty(content)) return new();
                return new Dictionary<string, string> { [channelId] = content };
            }
        }

        public override void Attach(ILoopBus bus) { }

        public override string? BuildPromptSection(EngineMode mode)
        {
            var content = ReadContent();
            if (string.IsNullOrWhiteSpace(content)) return null;
            return $"你的思考笔记（notebook={channelId}）：\n{content}";
        }

        public override void Reset() { }

        private string ReadContent()
        {
            if (!File.Exists(NotebookPath)) return "";
            try { return File.ReadAllText(NotebookPath); }
            catch { return ""; }
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            if (name.Length > 64) name = name[..64];
            return name;
        }
    }
}
