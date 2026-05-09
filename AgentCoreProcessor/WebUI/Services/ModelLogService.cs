using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentCoreProcessor.Config;

namespace AgentCoreProcessor.WebUI.Services
{
    internal class ModelLogEntry
    {
        public string FileName { get; init; } = "";
        public DateTime Timestamp { get; init; }
        public string CoreName { get; init; } = "";
        public long FileSize { get; init; }
    }

    internal class ModelLogService
    {
        public List<ModelLogEntry> ListRecent(int count = 100, string? coreFilter = null)
        {
            var dir = Path.Combine(PathConfig.LogPath, "Model");
            if (!Directory.Exists(dir)) return new();

            var files = Directory.GetFiles(dir, "*.log")
                .OrderByDescending(f => Path.GetFileName(f))
                .AsEnumerable();

            if (!string.IsNullOrEmpty(coreFilter))
                files = files.Where(f => Path.GetFileName(f).Contains(coreFilter, StringComparison.OrdinalIgnoreCase));

            return files.Take(count).Select(f =>
            {
                var name = Path.GetFileName(f);
                return new ModelLogEntry
                {
                    FileName = name,
                    Timestamp = ParseTimestamp(name),
                    CoreName = ParseCoreName(name),
                    FileSize = new FileInfo(f).Length
                };
            }).ToList();
        }

        public string? ReadContent(string fileName)
        {
            if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
                return null;

            var path = Path.Combine(PathConfig.LogPath, "Model", fileName);
            if (!File.Exists(path)) return null;

            var info = new FileInfo(path);
            var content = File.ReadAllText(path);
            if (content.Length > 512 * 1024)
                return content[..(512 * 1024)] + "\n\n[... 截断，文件过大]";

            return content;
        }

        public List<string> GetCoreNames()
        {
            var dir = Path.Combine(PathConfig.LogPath, "Model");
            if (!Directory.Exists(dir)) return new();

            return Directory.GetFiles(dir, "*.log")
                .Select(f => ParseCoreName(Path.GetFileName(f)))
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .OrderBy(n => n)
                .ToList();
        }

        private static DateTime ParseTimestamp(string fileName)
        {
            if (fileName.Length < 19) return DateTime.MinValue;
            var parts = fileName[..19]; // yyyyMMdd_HHmmss_fff
            if (DateTime.TryParseExact(parts, "yyyyMMdd_HHmmss_fff",
                null, System.Globalization.DateTimeStyles.None, out var dt))
                return dt;
            return DateTime.MinValue;
        }

        private static string ParseCoreName(string fileName)
        {
            var withoutExt = Path.GetFileNameWithoutExtension(fileName);
            var lastUnderscore = withoutExt.LastIndexOf('_');
            if (lastUnderscore < 0) return withoutExt;
            // Format: yyyyMMdd_HHmmss_fff_CoreName
            // Find the 3rd underscore
            int count = 0, idx = 0;
            for (int i = 0; i < withoutExt.Length; i++)
            {
                if (withoutExt[i] == '_') { count++; if (count == 3) { idx = i; break; } }
            }
            return idx > 0 ? withoutExt[(idx + 1)..] : withoutExt;
        }
    }
}
