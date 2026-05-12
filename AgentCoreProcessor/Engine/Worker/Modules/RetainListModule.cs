using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using AgentCoreProcessor.Config;

namespace AgentCoreProcessor.Engine.Modules
{
    /// <summary>
    /// 缓存列表模块。从 Plugin.WorkingTools 的文件存储读取，注入 prompt。
    /// </summary>
    internal class RetainListModule : EngineModule
    {
        public override string Name => "缓存列表";
        public override int PromptPriority => 60;

        private static string FilePath =>
            Path.Combine(PathConfig.StoragePath, "PluginData", "_system", "retain", "items.json");

        public override void Attach(ILoopBus bus) { }

        public override string? BuildPromptSection(EngineMode mode)
        {
            if (mode == EngineMode.Express) return null;
            var items = LoadItems();
            if (items.Count == 0) return null;

            var sb = new StringBuilder("[缓存列表]\n");
            foreach (var (label, content) in items)
            {
                var preview = content.Length > 120 ? content[..120] + "..." : content;
                sb.AppendLine($"- [{label}] {preview}");
            }
            return sb.ToString();
        }

        public override void Reset() { }

        private static Dictionary<string, string> LoadItems()
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

    internal static class StringExtensions
    {
        public static string Truncate(this string s, int maxLen)
            => s.Length <= maxLen ? s : s[..maxLen] + "...";
    }
}
