using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentCoreProcessor.Config;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.WebUI.Services
{
    internal class ModelLogEntry
    {
        public string FileName { get; init; } = "";
        public DateTime Timestamp { get; init; }
        public string CoreName { get; init; } = "";
        public long FileSize { get; init; }
        public string? Model { get; init; }
        public int InputTokens { get; init; }
        public int OutputTokens { get; init; }
        public int CacheReadTokens { get; init; }
        public bool IsJson { get; init; }
    }

    internal class ModelLogService
    {
        public List<ModelLogEntry> ListRecent(int count = 100, string? coreFilter = null)
        {
            var dir = Path.Combine(PathConfig.LogPath, "Model");
            if (!Directory.Exists(dir)) return new();

            var files = Directory.GetFiles(dir, "*.*")
                .Where(f => f.EndsWith(".json") || f.EndsWith(".log"))
                .OrderByDescending(f => Path.GetFileName(f))
                .AsEnumerable();

            if (!string.IsNullOrEmpty(coreFilter))
                files = files.Where(f => Path.GetFileName(f).Contains(coreFilter, StringComparison.OrdinalIgnoreCase));

            return files.Take(count).Select(f => ParseEntry(f)).ToList();
        }

        public string? ReadContent(string fileName)
        {
            if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
                return null;

            var path = Path.Combine(PathConfig.LogPath, "Model", fileName);
            if (!File.Exists(path)) return null;

            var content = File.ReadAllText(path);
            if (content.Length > 512 * 1024)
                return content[..(512 * 1024)] + "\n\n[... 截断，文件过大]";

            return content;
        }

        public List<string> GetCoreNames()
        {
            var dir = Path.Combine(PathConfig.LogPath, "Model");
            if (!Directory.Exists(dir)) return new();

            return Directory.GetFiles(dir, "*.*")
                .Where(f => f.EndsWith(".json") || f.EndsWith(".log"))
                .Select(f => ParseCoreName(Path.GetFileName(f)))
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .OrderBy(n => n)
                .ToList();
        }

        private static ModelLogEntry ParseEntry(string fullPath)
        {
            var name = Path.GetFileName(fullPath);
            var isJson = name.EndsWith(".json");
            var entry = new ModelLogEntry
            {
                FileName = name,
                Timestamp = ParseTimestamp(name),
                CoreName = ParseCoreName(name),
                FileSize = new FileInfo(fullPath).Length,
                IsJson = isJson
            };

            if (isJson)
            {
                try
                {
                    using var reader = new StreamReader(fullPath);
                    var firstChars = new char[4096];
                    var read = reader.Read(firstChars, 0, firstChars.Length);
                    var partial = new string(firstChars, 0, read);

                    var obj = JObject.Parse(File.ReadAllText(fullPath));
                    var usage = obj["usage"];
                    if (usage != null)
                    {
                        return new ModelLogEntry
                        {
                            FileName = name,
                            Timestamp = entry.Timestamp,
                            CoreName = entry.CoreName,
                            FileSize = entry.FileSize,
                            IsJson = true,
                            Model = obj["model"]?.ToString(),
                            InputTokens = usage["inputTokens"]?.Value<int>() ?? 0,
                            OutputTokens = usage["outputTokens"]?.Value<int>() ?? 0,
                            CacheReadTokens = usage["cacheReadTokens"]?.Value<int>() ?? 0
                        };
                    }
                }
                catch { }
            }

            return entry;
        }

        private static DateTime ParseTimestamp(string fileName)
        {
            if (fileName.Length < 19) return DateTime.MinValue;
            var parts = fileName[..19];
            if (DateTime.TryParseExact(parts, "yyyyMMdd_HHmmss_fff",
                null, System.Globalization.DateTimeStyles.None, out var dt))
                return dt;
            return DateTime.MinValue;
        }

        private static string ParseCoreName(string fileName)
        {
            var withoutExt = Path.GetFileNameWithoutExtension(fileName);
            int count = 0, idx = 0;
            for (int i = 0; i < withoutExt.Length; i++)
            {
                if (withoutExt[i] == '_') { count++; if (count == 3) { idx = i; break; } }
            }
            return idx > 0 ? withoutExt[(idx + 1)..] : withoutExt;
        }
    }
}
