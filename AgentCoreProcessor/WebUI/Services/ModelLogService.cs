using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.WebUI.Services
{
    internal class ModelLogEntry
    {
        public string FileName { get; init; } = "";
        public DateTime Timestamp { get; init; }
        public string CoreName { get; init; } = "";
        public string? Caller { get; init; }
        public long FileSize { get; init; }
        public string? Model { get; init; }
        public int InputTokens { get; init; }
        public int OutputTokens { get; init; }
        public int CacheReadTokens { get; init; }
        public int CacheHitTokens { get; init; }
        public int CacheCreationTokens { get; init; }
        public bool IsError { get; init; }
        public bool IsJson { get; init; }
    }

    internal class ModelLogDetail
    {
        public string? CoreName { get; init; }
        public string? Model { get; init; }
        public string? Provider { get; init; }
        public string? Timestamp { get; init; }
        public string? Caller { get; init; }
        public string? SystemPrompt { get; init; }
        public string? SystemPromptHash { get; init; }
        public bool IsError { get; init; }
        public List<string>? Tools { get; init; }
        public List<ModelLogMessage> Messages { get; init; } = new();
        public string? Output { get; init; }
        public string? Thinking { get; init; }
        public ModelLogUsage? Usage { get; init; }
    }

    internal class ModelLogMessage
    {
        public string Role { get; init; } = "";
        public string Content { get; init; } = "";
        public List<ModelLogContentPart>? ContentParts { get; init; }
    }

    internal class ModelLogContentPart
    {
        public string Type { get; init; } = "";
        public string? Text { get; init; }
        public string? ToolUseId { get; init; }
        public string? ToolName { get; init; }
        public string? ToolInput { get; init; }
        public bool IsError { get; init; }
        public string? ImageMediaType { get; init; }
        public string? ImagePath { get; init; }
        public int? ImageBase64Length { get; init; }
    }

    internal class ModelLogUsage
    {
        public int InputTokens { get; init; }
        public int OutputTokens { get; init; }
        public int TotalTokens { get; init; }
        public int CacheCreationTokens { get; init; }
        public int CacheReadTokens { get; init; }
        public int CacheHitTokens { get; init; }
        public int CacheMissTokens { get; init; }
    }

    internal class ModelLogService
    {
        public List<ModelLogEntry> ListRecent(int count = 100, string? coreFilter = null, string? callerFilter = null)
        {
            var dir = Path.Combine(PathConfig.LogPath, "Model");
            if (!Directory.Exists(dir)) return new();

            var files = Directory.GetFiles(dir, "*.*")
                .Where(f => f.EndsWith(".json") || f.EndsWith(".log"))
                .OrderByDescending(f => Path.GetFileName(f))
                .AsEnumerable();

            if (!string.IsNullOrEmpty(coreFilter))
                files = files.Where(f => Path.GetFileName(f).Contains(coreFilter, StringComparison.OrdinalIgnoreCase));

            var entries = new List<ModelLogEntry>();
            foreach (var f in files)
            {
                var entry = ParseEntry(f);
                if (entry != null)
                {
                    // caller 过滤提前，避免 count 不足
                    if (!string.IsNullOrEmpty(callerFilter)
                        && (entry.Caller == null
                            || !entry.Caller.Contains(callerFilter, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    entries.Add(entry);
                    if (entries.Count >= count) break;
                }
            }

            return entries;
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

        public ModelLogDetail? ReadStructured(string fileName)
        {
            if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
                return null;

            var path = Path.Combine(PathConfig.LogPath, "Model", fileName);
            if (!File.Exists(path) || !fileName.EndsWith(".json")) return null;

            try
            {
                var obj = JObject.Parse(File.ReadAllText(path));
                var messages = new List<ModelLogMessage>();

                // 新格式: "messages" 数组
                if (obj["messages"] is JArray msgArr)
                {
                    foreach (var item in msgArr)
                    {
                        // 解析 contentParts（含 image 详情）
                        List<ModelLogContentPart>? parts = null;
                        if (item["contentParts"] is JArray partsArr)
                        {
                            parts = new List<ModelLogContentPart>();
                            foreach (var p in partsArr)
                            {
                                var img = p["image"];
                                parts.Add(new ModelLogContentPart
                                {
                                    Type = p["type"]?.ToString() ?? "",
                                    Text = p["text"]?.ToString(),
                                    ToolUseId = p["toolUseId"]?.ToString(),
                                    ToolName = p["toolName"]?.ToString(),
                                    ToolInput = p["toolInput"]?.ToString(),
                                    IsError = p["isError"]?.Value<bool>() ?? false,
                                    ImageMediaType = img?["mediaType"]?.ToString(),
                                    ImagePath = img?["path"]?.ToString(),
                                    ImageBase64Length = img?["base64Length"]?.Value<int>()
                                });
                            }
                        }

                        messages.Add(new ModelLogMessage
                        {
                            Role = item["role"]?.ToString() ?? "",
                            Content = item["content"]?.ToString() ?? "",
                            ContentParts = parts
                        });
                    }
                }

                // 工具列表
                List<string>? tools = null;
                if (obj["tools"] is JArray toolArr)
                    tools = toolArr.Select(t => t.ToString()).ToList();

                ModelLogUsage? usage = null;
                if (obj["usage"] is JObject u)
                {
                    usage = new ModelLogUsage
                    {
                        InputTokens = u["inputTokens"]?.Value<int>() ?? 0,
                        OutputTokens = u["outputTokens"]?.Value<int>() ?? 0,
                        TotalTokens = u["totalTokens"]?.Value<int>() ?? 0,
                        CacheCreationTokens = u["cacheCreationTokens"]?.Value<int>() ?? 0,
                        CacheReadTokens = u["cacheReadTokens"]?.Value<int>() ?? 0,
                        CacheHitTokens = u["cacheHitTokens"]?.Value<int>() ?? 0,
                        CacheMissTokens = u["cacheMissTokens"]?.Value<int>() ?? 0
                    };
                }

                return new ModelLogDetail
                {
                    CoreName = obj["coreName"]?.ToString(),
                    Model = obj["model"]?.ToString(),
                    Provider = obj["provider"]?.ToString(),
                    Timestamp = obj["timestamp"]?.ToString(),
                    Caller = obj["caller"]?.ToString(),
                    SystemPrompt = obj["systemPrompt"]?.ToString(),
                    SystemPromptHash = obj["systemPromptHash"]?.ToString(),
                    IsError = obj["error"]?.Value<bool>() ?? false,
                    Tools = tools,
                    Messages = messages,
                    Output = obj["output"]?.ToString(),
                    Thinking = obj["thinking"]?.ToString(),
                    Usage = usage
                };
            }
            catch
            {
                return null;
            }
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

        public List<string> GetCallers()
        {
            var dir = Path.Combine(PathConfig.LogPath, "Model");
            if (!Directory.Exists(dir)) return new();

            var callers = new HashSet<string>();
            var files = Directory.GetFiles(dir, "*.json")
                .OrderByDescending(f => Path.GetFileName(f))
                .Take(200);

            foreach (var f in files)
            {
                try
                {
                    var text = File.ReadAllText(f);
                    var obj = JObject.Parse(text);
                    var caller = obj["caller"]?.ToString();
                    if (!string.IsNullOrEmpty(caller))
                        callers.Add(caller);
                }
                catch { Signal.Warn(LogGroup.Engine, "模型日志解析失败", new { file = Path.GetFileName(f) }); }
            }

            return callers.OrderBy(c => c).ToList();
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
                    var obj = JObject.Parse(File.ReadAllText(fullPath));
                    var caller = obj["caller"]?.ToString();
                    var usage = obj["usage"];
                    var isError = obj["error"]?.Value<bool>() ?? name.Contains("_ERROR", StringComparison.OrdinalIgnoreCase);
                    if (usage != null)
                    {
                        return new ModelLogEntry
                        {
                            FileName = name,
                            Timestamp = entry.Timestamp,
                            CoreName = entry.CoreName,
                            Caller = caller,
                            FileSize = entry.FileSize,
                            IsJson = true,
                            IsError = isError,
                            Model = obj["model"]?.ToString(),
                            InputTokens = usage["inputTokens"]?.Value<int>() ?? 0,
                            OutputTokens = usage["outputTokens"]?.Value<int>() ?? 0,
                            CacheReadTokens = usage["cacheReadTokens"]?.Value<int>() ?? 0,
                            CacheHitTokens = usage["cacheHitTokens"]?.Value<int>() ?? 0,
                            CacheCreationTokens = usage["cacheCreationTokens"]?.Value<int>() ?? 0
                        };
                    }
                    else
                    {
                        return new ModelLogEntry
                        {
                            FileName = name,
                            Timestamp = entry.Timestamp,
                            CoreName = entry.CoreName,
                            Caller = caller,
                            FileSize = entry.FileSize,
                            IsJson = true,
                            IsError = isError,
                            Model = obj["model"]?.ToString()
                        };
                    }
                }
                catch { Signal.Warn(LogGroup.Engine, "模型日志解析失败", new { file = Path.GetFileName(fullPath) }); }
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
