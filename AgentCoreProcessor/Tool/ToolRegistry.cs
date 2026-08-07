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

        /// <summary>检查工具是否适用于指定会话类型（白/黑名单裁决）。type 为空 → 放行。</summary>
        public static bool IsApplicableToType(string toolName, string? type)
        {
            return Engine.Session.SessionTypeProfiles.IsAllowed(type, toolName);
        }

        /// <summary>
        /// 统一工具可见性判定。优先级：
        ///   1. 全局禁用（ToolConfig）→ false
        ///   2. 会话类型白/黑名单（Storage/SessionTypes/{type}.json）→ 放行/拒绝
        /// 会话类型文件即唯一权限源；type 为空时仅受全局禁用约束（放行）。
        /// </summary>
        public static bool IsVisible(string toolName, string? type)
        {
            if (IsDisabled(toolName)) return false;
            return Engine.Session.SessionTypeProfiles.IsAllowed(type, toolName);
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
