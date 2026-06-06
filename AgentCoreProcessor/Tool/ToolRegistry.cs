using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Logging;
using AgentLilara.PluginSDK;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 工具注册表。管理所有已注册工具（内置 + 插件），提供查询、禁用、描述生成。
    /// 不再硬编码工具列表——工具通过 PluginLoader 或启动时显式注册。
    /// </summary>
    internal static class ToolRegistry
    {
        private static readonly ConcurrentDictionary<string, ITool> _tools = new();
        private static readonly ConcurrentDictionary<string, ToolMetaAttribute> _metaCache = new();
        private static readonly ConcurrentDictionary<string, DisabledToolInfo> _disabledTools = new();
        private static readonly HashSet<string> _nonComponentTools = new();

        private static string ConfigPath => Path.Combine(PathConfig.StoragePath, "ToolConfig.json");

        internal class DisabledToolInfo
        {
            [JsonProperty("reason")]
            public string Reason { get; set; } = "";
            [JsonProperty("disabledAt")]
            public DateTime DisabledAt { get; set; }
        }

        // ---- 注册/查询 ----

        public static bool Register(ITool tool, bool isNonComponent = false)
        {
            if (!_tools.TryAdd(tool.Name, tool)) return false;
            // 缓存元数据
            var meta = Attribute.GetCustomAttribute(tool.GetType(), typeof(ToolMetaAttribute))
                as ToolMetaAttribute;
            if (meta != null) _metaCache[tool.Name] = meta;
            if (isNonComponent)
            {
                lock (_nonComponentTools) _nonComponentTools.Add(tool.Name);
            }
            return true;
        }

        public static bool Unregister(string toolName)
        {
            _metaCache.TryRemove(toolName, out _);
            lock (_nonComponentTools) _nonComponentTools.Remove(toolName);
            return _tools.TryRemove(toolName, out _);
        }

        public static ITool? Get(string toolName)
        {
            _tools.TryGetValue(toolName, out var tool);
            return tool;
        }

        public static ToolMetaAttribute? GetMeta(string toolName)
        {
            _metaCache.TryGetValue(toolName, out var meta);
            return meta;
        }

        public static IReadOnlyDictionary<string, ITool> All => _tools;

        /// <summary>非组件工具名称集合（核心工具 + MCP + 纯插件工具）。</summary>
        public static IReadOnlySet<string> NonComponentToolNames
        {
            get { lock (_nonComponentTools) return new HashSet<string>(_nonComponentTools); }
        }

        /// <summary>检查工具是否适用于指定引擎类型。null 表示所有引擎可用。</summary>
        public static bool IsApplicableToEngine(string toolName, string? engineType)
        {
            if (string.IsNullOrEmpty(engineType)) return true;
            var meta = GetMeta(toolName);
            if (meta?.EngineTypes == null) return true; // 未声明 = 全引擎可用
            return meta.EngineTypes.Contains(engineType);
        }

        // ---- 描述生成（JSON 降级路径用） ----

        public static string GenerateDescriptions(
            Func<ITool, bool>? filter = null,
            IEnumerable<ITool>? additionalTools = null)
        {
            var source = filter != null ? _tools.Values.Where(filter).ToList() : _tools.Values.ToList();
            if (additionalTools != null)
                source.AddRange(additionalTools);
            var sb = new StringBuilder();
            int i = 1;

            foreach (var tool in source)
            {
                if (IsDisabled(tool.Name)) continue;

                sb.AppendLine($"工具{i}：{tool.Name}");
                sb.AppendLine($"描述：{tool.Description}");
                if (tool.Parameters.Count > 0)
                {
                    var paramParts = tool.Parameters.Select(p => $"inputs[{p.Index}] = {p.Name}");
                    sb.AppendLine($"参数：{string.Join(", ", paramParts)}");
                }
                sb.AppendLine($"示例：{GenerateExample(tool)}<over>");
                sb.AppendLine();
                i++;
            }

            return sb.ToString().TrimEnd();
        }

        public static string GenerateCapabilitySummary(Func<ITool, bool>? filter = null)
        {
            var source = filter != null ? _tools.Values.Where(filter) : _tools.Values;
            var capabilities = source
                .Select(t => GetMeta(t.Name)?.CapabilitySummary)
                .Where(c => c != null)
                .Distinct()
                .ToList();

            if (capabilities.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendLine("你当前处于轻量对话模式。如果对话涉及以下任何能力，输出 [ESCALATE]你要做什么 切换到工作模式：");
            foreach (var cap in capabilities)
                sb.AppendLine($"- {cap}");
            sb.AppendLine("- 任何需要实际操作而非纯对话的场景");
            sb.AppendLine("注意：[ESCALATE]后面写上你打算做什么，例如 [ESCALATE]帮你查一下记忆里有没有相关内容");
            return sb.ToString().TrimEnd();
        }

        private static string GenerateExample(ITool tool)
        {
            var example = new
            {
                tool = tool.Name,
                inputs = tool.Parameters.Select(p => $"({p.Name})").ToArray()
            };
            return JsonConvert.SerializeObject(example, Formatting.None);
        }

        // ---- 禁用管理 ----

        public static List<ToolDefinition> GetExpressToolDefinitions(string? engineType = null)
        {
            return GetToolDefinitionsForMode("express", engineType);
        }

        public static List<ToolDefinition> GetToolDefinitionsForMode(string modeId, string? engineType = null)
        {
            return _tools.Values
                .Where(t => !IsDisabled(t.Name)
                            && ModeConfigLoader.IsToolEnabled(modeId, t.Name)
                            && IsApplicableToEngine(t.Name, engineType))
                .Select(t => new ToolDefinition
                {
                    Name = t.Name,
                    Description = t.Description,
                    Parameters = t.GetInputSchema()
                })
                .ToList();
        }

        public static bool IsDisabled(string toolName) => _disabledTools.ContainsKey(toolName);

        public static string? GetDisableReason(string toolName)
            => _disabledTools.TryGetValue(toolName, out var info) ? info.Reason : null;

        public static IReadOnlyDictionary<string, DisabledToolInfo> DisabledTools => _disabledTools;

        public static void DisableTool(string name, string reason)
        {
            _disabledTools[name] = new DisabledToolInfo { Reason = reason, DisabledAt = DateTime.Now };
            SaveConfig();
        }

        public static void EnableTool(string name)
        {
            _disabledTools.TryRemove(name, out _);
            SaveConfig();
        }

        public static void LoadConfig()
        {
            if (!File.Exists(ConfigPath)) return;
            try
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonConvert.DeserializeObject<ToolConfigFile>(json);
                if (cfg?.Disabled != null)
                {
                    foreach (var (name, info) in cfg.Disabled)
                        _disabledTools[name] = info;
                }
            }
            catch { Signal.Warn(LogGroup.Plugin, "工具配置加载失败"); }
        }

        private static void SaveConfig()
        {
            try
            {
                var cfg = new ToolConfigFile { Disabled = new Dictionary<string, DisabledToolInfo>(_disabledTools) };
                var json = JsonConvert.SerializeObject(cfg, Formatting.Indented);
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                File.WriteAllText(ConfigPath, json);
            }
            catch { Signal.Warn(LogGroup.Plugin, "工具配置保存失败"); }
        }

        private class ToolConfigFile
        {
            [JsonProperty("disabled")]
            public Dictionary<string, DisabledToolInfo> Disabled { get; set; } = new();
        }
    }
}
