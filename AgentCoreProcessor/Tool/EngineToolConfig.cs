using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentCoreProcessor.Config;
using AgentLilara.PluginSDK;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 引擎工具清单配置（EngineTools.json）。控制每个 LLM 引擎类型（express/session/review/task）的工具可用性。
    /// 这是工具可用性控制的第二层：ToolConfig.json（全局禁用）之后、ToolMetaAttribute.EngineTypes 声明之前。
    ///
    /// 两种模式：
    ///   - default=disabled（白名单）：仅允许 tools 列出的工具
    ///   - default=enabled（黑名单）：tools 列出的是禁用项
    ///
    /// 未在配置中出现的引擎类型 → 不限制（信任现有过滤）。
    /// </summary>
    internal class EngineToolConfig
    {
        private static string ConfigPath =>
            Path.Combine(PathConfig.StoragePath, "Engine", "EngineTools.json");

        private static EngineToolConfig? _cached;

        /// <summary>各引擎的工具清单。key 为引擎类型（express/session/review/task）。</summary>
        public Dictionary<string, EngineToolEntry> Engines { get; set; } = new();

        /// <summary>内置默认：Express 白名单（架构地图 5 类展开），其余引擎不限制。</summary>
        private static EngineToolConfig BuildDefaults()
        {
            return new EngineToolConfig
            {
                Engines = new Dictionary<string, EngineToolEntry>
                {
                    ["express"] = new EngineToolEntry
                    {
                        Default = "disabled",
                        Tools = new List<string>
                        {
                            "speak", "send_media", "escalate", "wait",
                            "memory_get", "memory_list", "memory_search", "memory_stats", "memory_link_get"
                        }
                    }
                }
            };
        }

        public static EngineToolConfig Load()
        {
            if (_cached != null) return _cached;

            if (!File.Exists(ConfigPath))
            {
                _cached = BuildDefaults();
                return _cached;
            }

            try
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonConvert.DeserializeObject<EngineToolConfig>(json);
                _cached = cfg ?? BuildDefaults();
            }
            catch (Exception ex)
            {
                Logging.Signal.Warn(Logging.LogGroup.Plugin, "引擎工具清单加载失败，使用默认", new { error = ex.Message });
                _cached = BuildDefaults();
            }
            return _cached;
        }

        public static void Invalidate() => _cached = null;

        /// <summary>检查工具是否对该引擎可见。引擎无配置时返回 true（不限制）。</summary>
        public bool IsVisible(string engineType, string toolName)
        {
            if (!Engines.TryGetValue(engineType, out var entry)) return true;
            var inList = entry.Tools.Contains(toolName, StringComparer.OrdinalIgnoreCase);
            return entry.Default == "disabled" ? inList : !inList;
        }

        /// <summary>持久化当前配置。合并到现有文件（保留其他字段）。</summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                var json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(ConfigPath, json);
                _cached = this;
            }
            catch (Exception ex)
            {
                Logging.Signal.Warn(Logging.LogGroup.Plugin, "引擎工具清单保存失败", new { error = ex.Message });
            }
        }
    }

    /// <summary>单个引擎的工具清单条目。</summary>
    internal class EngineToolEntry
    {
        /// <summary>"disabled" = 白名单模式；"enabled" = 黑名单模式。</summary>
        public string Default { get; set; } = "enabled";

        /// <summary>工具名列表。白名单=允许；黑名单=禁用。</summary>
        public List<string> Tools { get; set; } = new();

        public bool IsWhitelist => Default == "disabled";
    }
}
