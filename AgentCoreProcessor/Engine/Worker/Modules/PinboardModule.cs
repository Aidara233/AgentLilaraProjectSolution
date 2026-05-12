using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using AgentCoreProcessor.Config;

namespace AgentCoreProcessor.Engine.Modules
{
    /// <summary>
    /// 便签板模块。从 Plugin.WorkingTools 的文件存储读取，注入 prompt。
    /// </summary>
    internal class PinboardModule : EngineModule
    {
        public override string Name => "便签板";
        public override int PromptPriority => 55;

        private static string FilePath =>
            Path.Combine(PathConfig.StoragePath, "PluginData", "_system", "pinboard.json");

        /// <summary>读取当前便签板内容（供快照使用）。</summary>
        public Dictionary<string, string> Entries => LoadBoard();

        public override void Attach(ILoopBus bus) { }

        public override string? BuildPromptSection(EngineMode mode)
        {
            var board = LoadBoard();
            if (board.Count == 0) return null;

            var sb = new StringBuilder("[便签板]\n");
            foreach (var (label, content) in board)
                sb.AppendLine($"- {label}: {content}");
            return sb.ToString();
        }

        public override void Reset() { }

        private static Dictionary<string, string> LoadBoard()
        {
            if (!File.Exists(FilePath)) return new();
            try
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            }
            catch { return new(); }
        }
    }
}
