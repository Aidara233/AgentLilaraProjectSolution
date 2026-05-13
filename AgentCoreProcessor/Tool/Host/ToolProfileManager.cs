using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AgentCoreProcessor.Config;
using AgentLilara.PluginSDK;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Tool.Host
{
    /// <summary>
    /// 引擎工具配置管理。根据 profile 配置决定每个引擎可用的工具集。
    /// 支持链式继承、per-channel 映射、子 agent 工具池。
    /// </summary>
    internal class ToolProfileManager
    {
        private ToolProfileConfig config = new();
        private readonly Dictionary<string, HashSet<string>> activeGroups = new();
        private readonly Dictionary<string, ResolvedProfile> resolvedCache = new();

        private static string ConfigPath => Path.Combine(PathConfig.StoragePath, "Engine", "ToolProfiles.json");

        public void Load()
        {
            if (!File.Exists(ConfigPath))
                CreateDefaultConfig();

            try
            {
                var json = File.ReadAllText(ConfigPath);
                config = JsonConvert.DeserializeObject<ToolProfileConfig>(json) ?? new();
            }
            catch (Exception ex)
            {
                Engine.FrameworkLogger.Log("ToolProfileManager", $"加载配置失败: {ex.Message}，使用默认配置");
                config = GetDefaultConfig();
            }

            resolvedCache.Clear();
        }

        /// <summary>根据 channelId 获取对应的 profile 名。</summary>
        public string GetProfileForChannel(string channelId)
        {
            if (config.ChannelMapping.TryGetValue(channelId, out var profile))
                return profile;
            if (config.ChannelMapping.TryGetValue("_default", out var def))
                return def;
            return "_root";
        }

        /// <summary>获取解析后的基础工具集（沿继承链合并）。</summary>
        public HashSet<string> GetBaseTools(string profileName)
        {
            var resolved = Resolve(profileName);
            return new HashSet<string>(resolved.Base);
        }

        /// <summary>获取完整可用工具集（base + 已激活组的工具）。</summary>
        public HashSet<string> GetActiveTools(string profileName)
        {
            var resolved = Resolve(profileName);
            var result = new HashSet<string>(resolved.Base);

            if (activeGroups.TryGetValue(profileName, out var activated))
            {
                var allTools = ToolRegistry.All;
                foreach (var tool in allTools.Values)
                {
                    var meta = GetToolMeta(tool);
                    if (meta?.Group != null && activated.Contains(meta.Group))
                    {
                        if (!resolved.Blocked.Any(p => MatchesPattern(tool.Name, p)))
                            result.Add(tool.Name);
                    }
                }
            }

            return result;
        }

        /// <summary>判断工具是否被 profile 阻止。</summary>
        public bool IsBlocked(string profileName, string toolName)
        {
            var resolved = Resolve(profileName);
            return resolved.Blocked.Any(p => MatchesPattern(toolName, p));
        }

        /// <summary>判断工具组是否在 available 列表中。</summary>
        public bool IsGroupAvailable(string profileName, string groupName)
        {
            var resolved = Resolve(profileName);
            return resolved.Available.Any(p => MatchesPattern(groupName, p));
        }

        /// <summary>激活工具组（会话级）。</summary>
        public bool ActivateGroup(string profileName, string groupName)
        {
            if (!IsGroupAvailable(profileName, groupName))
                return false;
            if (!activeGroups.ContainsKey(profileName))
                activeGroups[profileName] = new HashSet<string>();
            activeGroups[profileName].Add(groupName);
            return true;
        }

        /// <summary>重置指定 profile 的已激活组。</summary>
        public void ResetActiveGroups(string profileName)
        {
            activeGroups.Remove(profileName);
        }

        /// <summary>获取可激活但尚未激活的工具组摘要。</summary>
        public List<ToolGroupSummary> GetAvailableGroupSummaries(string profileName)
        {
            var resolved = Resolve(profileName);
            var activated = activeGroups.TryGetValue(profileName, out var set) ? set : new HashSet<string>();
            var allTools = ToolRegistry.All;

            var groups = allTools.Values
                .Select(t => GetToolMeta(t)?.Group)
                .Where(g => g != null)
                .Distinct()
                .Where(g => resolved.Available.Any(p => MatchesPattern(g!, p)) && !activated.Contains(g!))
                .ToList();

            return groups.Select(g => new ToolGroupSummary
            {
                GroupName = g!,
                ToolNames = allTools.Values
                    .Where(t => GetToolMeta(t)?.Group == g
                        && !resolved.Blocked.Any(p => MatchesPattern(t.Name, p)))
                    .Select(t => t.Name)
                    .ToList()
            }).Where(s => s.ToolNames.Count > 0).ToList();
        }

        /// <summary>获取子 agent 可分配工具池。</summary>
        public HashSet<string> GetSubAgentPool()
        {
            var pool = new HashSet<string>(config.SubAgent.Pool);
            var allTools = ToolRegistry.All;
            foreach (var tool in allTools.Values)
            {
                if (pool.Contains(tool.Name)
                    && config.SubAgent.Blocked.Any(p => MatchesPattern(tool.Name, p)))
                    pool.Remove(tool.Name);
            }
            return pool;
        }

        /// <summary>获取子 agent 阻止列表。</summary>
        public HashSet<string> GetSubAgentBlocked()
        {
            return new HashSet<string>(config.SubAgent.Blocked);
        }

        /// <summary>生成原生工具定义列表。</summary>
        public List<ToolDefinition> GetToolDefinitions(string profileName)
        {
            var activeTools = GetActiveTools(profileName);
            return ToolRegistry.All.Values
                .Where(t => activeTools.Contains(t.Name) && !ToolRegistry.IsDisabled(t.Name))
                .Select(t => new ToolDefinition
                {
                    Name = t.Name,
                    Description = t.Description,
                    Parameters = t.GetInputSchema()
                })
                .ToList();
        }

        /// <summary>生成文本格式工具描述。</summary>
        public string GetToolDescriptions(string profileName)
        {
            var activeTools = GetActiveTools(profileName);
            return ToolRegistry.GenerateDescriptions(
                authorizedTools: activeTools);
        }

        /// <summary>生成 Express 模式能力摘要。</summary>
        public string GetCapabilitySummary(string profileName)
        {
            var activeTools = GetActiveTools(profileName);
            return ToolRegistry.GenerateCapabilitySummary(
                filter: t => activeTools.Contains(t.Name));
        }

        /// <summary>获取所有 profile 名称。</summary>
        public List<string> GetProfileNames() => config.Profiles.Keys.ToList();

        /// <summary>获取 channel mapping 配置。</summary>
        public Dictionary<string, string> GetChannelMapping() => new(config.ChannelMapping);

        // ---- 继承解析 ----

        private ResolvedProfile Resolve(string profileName)
        {
            if (resolvedCache.TryGetValue(profileName, out var cached))
                return cached;

            var chain = BuildInheritanceChain(profileName);
            var resolved = MergeChain(chain);
            resolvedCache[profileName] = resolved;
            return resolved;
        }

        private List<ToolProfile> BuildInheritanceChain(string profileName)
        {
            var chain = new List<ToolProfile>();
            var visited = new HashSet<string>();
            var current = profileName;

            while (!string.IsNullOrEmpty(current) && !visited.Contains(current))
            {
                visited.Add(current);
                if (config.Profiles.TryGetValue(current, out var profile))
                {
                    chain.Add(profile);
                    current = profile.Inherits;
                }
                else break;
            }

            return chain;
        }

        private static ResolvedProfile MergeChain(List<ToolProfile> chain)
        {
            var base_ = new HashSet<string>();
            var available = new HashSet<string>();
            var blocked = new HashSet<string>();

            // 从根（链尾）往叶（链头）合并
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                var p = chain[i];
                foreach (var t in p.Base) base_.Add(t);
                foreach (var t in p.Available) available.Add(t);
                foreach (var t in p.Blocked) blocked.Add(t);
                foreach (var t in p.RemoveBlock) blocked.Remove(t);
                foreach (var t in p.Remove) base_.Remove(t);
            }

            return new ResolvedProfile { Base = base_, Available = available, Blocked = blocked };
        }

        private static bool MatchesPattern(string value, string pattern)
        {
            if (pattern == "*") return true;
            if (pattern.Contains('*'))
            {
                var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
                return Regex.IsMatch(value, regex);
            }
            return value == pattern;
        }

        private static ToolMetaAttribute? GetToolMeta(ITool tool)
        {
            return Attribute.GetCustomAttribute(tool.GetType(), typeof(ToolMetaAttribute)) as ToolMetaAttribute;
        }

        // ---- 默认配置 ----

        private void CreateDefaultConfig()
        {
            config = GetDefaultConfig();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                var json = JsonConvert.SerializeObject(config, Formatting.Indented);
                File.WriteAllText(ConfigPath, json);
            }
            catch { }
        }

        private static ToolProfileConfig GetDefaultConfig()
        {
            return new ToolProfileConfig
            {
                Profiles = new Dictionary<string, ToolProfile>
                {
                    ["_root"] = new ToolProfile
                    {
                        Base = ["wait"],
                        Available = [],
                        Blocked = []
                    },
                    ["channel"] = new ToolProfile
                    {
                        Inherits = "_root",
                        Base = ["speak", "send_media", "memory", "pinboard",
                                "thinking_notes", "retain_list", "delegate_task",
                                "cancel_delegation", "alert",
                                "view_image", "get_image_text", "mark_review_hint",
                                "read_text", "write_text", "list_dir", "move_file",
                                "delete_file", "copy_file", "adapter_action"],
                        Available = ["file", "image", "planning", "meta"],
                        Blocked = ["dream_*", "evaluate_delegation", "complete_delegation",
                                   "create_sub_agent", "stop_sub_agent",
                                   "notify_channel", "set_watch_rule", "check_notifications",
                                   "continue_loop", "task_done"]
                    },
                    ["system"] = new ToolProfile
                    {
                        Inherits = "_root",
                        Base = ["continue_loop", "pinboard", "thinking_notes",
                                "evaluate_delegation", "complete_delegation",
                                "notify_channel", "create_sub_agent", "stop_sub_agent",
                                "check_notifications", "set_watch_rule", "memory",
                                "channel_info", "engine_management", "adapter_action",
                                "create_scheduled_task", "cancel_scheduled_task"],
                        Available = ["planning", "meta", "file"],
                        Blocked = ["speak", "send_media", "dream_*",
                                   "delegate_task", "cancel_delegation",
                                   "alert", "view_image", "get_image_text", "task_done"]
                    }
                },
                ChannelMapping = new Dictionary<string, string>
                {
                    ["_default"] = "channel"
                },
                SubAgent = new SubAgentPoolConfig
                {
                    Pool = ["memory", "read_text", "write_text", "list_dir",
                            "move_file", "delete_file", "copy_file",
                            "pinboard", "thinking_notes", "task_done",
                            "adapter_action"],
                    Blocked = ["create_sub_agent", "stop_sub_agent",
                               "evaluate_delegation", "complete_delegation",
                               "notify_channel", "set_watch_rule",
                               "delegate_task", "cancel_delegation"]
                }
            };
        }
    }

    // ---- 数据模型 ----

    internal class ToolProfileConfig
    {
        [JsonProperty("profiles")]
        public Dictionary<string, ToolProfile> Profiles { get; set; } = new();

        [JsonProperty("channelMapping")]
        public Dictionary<string, string> ChannelMapping { get; set; } = new();

        [JsonProperty("subAgent")]
        public SubAgentPoolConfig SubAgent { get; set; } = new();
    }

    internal class ToolProfile
    {
        [JsonProperty("inherits")]
        public string? Inherits { get; set; }

        [JsonProperty("base")]
        public List<string> Base { get; set; } = [];

        [JsonProperty("available")]
        public List<string> Available { get; set; } = [];

        [JsonProperty("blocked")]
        public List<string> Blocked { get; set; } = [];

        [JsonProperty("removeBlock")]
        public List<string> RemoveBlock { get; set; } = [];

        [JsonProperty("remove")]
        public List<string> Remove { get; set; } = [];
    }

    internal class SubAgentPoolConfig
    {
        [JsonProperty("pool")]
        public List<string> Pool { get; set; } = [];

        [JsonProperty("blocked")]
        public List<string> Blocked { get; set; } = [];
    }

    internal class ResolvedProfile
    {
        public HashSet<string> Base { get; set; } = new();
        public HashSet<string> Available { get; set; } = new();
        public HashSet<string> Blocked { get; set; } = new();
    }

    public class ToolGroupSummary
    {
        public string GroupName { get; set; } = "";
        public List<string> ToolNames { get; set; } = [];
    }
}
