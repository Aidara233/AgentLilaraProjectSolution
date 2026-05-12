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
    /// 引擎工具配置管理。根据 profile 配置决定每个引擎可用的工具集，
    /// 替代原来散落在各引擎内部的硬编码白名单。
    /// </summary>
    internal class ToolProfileManager
    {
        private Dictionary<string, ToolProfile> profiles = new();
        private readonly Dictionary<string, HashSet<string>> activeGroups = new();

        private static string ConfigPath => Path.Combine(PathConfig.StoragePath, "Engine", "ToolProfiles.json");

        public void Load()
        {
            if (!File.Exists(ConfigPath))
            {
                CreateDefaultConfig();
            }

            try
            {
                var json = File.ReadAllText(ConfigPath);
                profiles = JsonConvert.DeserializeObject<Dictionary<string, ToolProfile>>(json)
                    ?? new Dictionary<string, ToolProfile>();
            }
            catch (Exception ex)
            {
                Engine.FrameworkLogger.Log("ToolProfileManager", $"加载配置失败: {ex.Message}，使用默认配置");
                profiles = GetDefaultProfiles();
            }
        }

        /// <summary>获取指定 profile 的基础工具名列表（始终包含在请求中）。</summary>
        public HashSet<string> GetBaseTools(string profileName)
        {
            if (!profiles.TryGetValue(profileName, out var profile))
                return new HashSet<string>();
            return new HashSet<string>(profile.Base);
        }

        /// <summary>获取指定 profile 的完整可用工具名集合（base + 已激活的 available 组）。</summary>
        public HashSet<string> GetActiveTools(string profileName)
        {
            var result = GetBaseTools(profileName);
            if (!profiles.TryGetValue(profileName, out var profile))
                return result;

            // 加入已激活组的工具
            if (activeGroups.TryGetValue(profileName, out var activated))
            {
                var allTools = ToolRegistry.All;
                foreach (var tool in allTools.Values)
                {
                    var meta = GetToolMeta(tool);
                    if (meta?.Group != null && activated.Contains(meta.Group))
                    {
                        if (!IsBlocked(profile, tool.Name))
                            result.Add(tool.Name);
                    }
                }
            }

            return result;
        }

        /// <summary>判断工具是否被 profile 阻止。</summary>
        public bool IsBlocked(string profileName, string toolName)
        {
            if (!profiles.TryGetValue(profileName, out var profile))
                return false;
            return IsBlocked(profile, toolName);
        }

        /// <summary>判断工具组是否在 available 列表中。</summary>
        public bool IsGroupAvailable(string profileName, string groupName)
        {
            if (!profiles.TryGetValue(profileName, out var profile))
                return false;
            return profile.Available.Any(pattern => MatchesPattern(groupName, pattern));
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

        /// <summary>获取可激活但尚未激活的工具组摘要（用于 prompt 注入）。</summary>
        public List<ToolGroupSummary> GetAvailableGroupSummaries(string profileName)
        {
            if (!profiles.TryGetValue(profileName, out var profile))
                return new List<ToolGroupSummary>();

            var activated = activeGroups.TryGetValue(profileName, out var set) ? set : new HashSet<string>();
            var allTools = ToolRegistry.All;

            var groups = allTools.Values
                .Select(t => GetToolMeta(t)?.Group)
                .Where(g => g != null)
                .Distinct()
                .Where(g => profile.Available.Any(p => MatchesPattern(g!, p)) && !activated.Contains(g!))
                .ToList();

            return groups.Select(g => new ToolGroupSummary
            {
                GroupName = g!,
                ToolNames = allTools.Values
                    .Where(t => GetToolMeta(t)?.Group == g && !IsBlocked(profile, t.Name))
                    .Select(t => t.Name)
                    .ToList()
            }).Where(s => s.ToolNames.Count > 0).ToList();
        }

        /// <summary>生成原生工具定义列表（只包含 base + 已激活组）。</summary>
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

        /// <summary>生成文本格式工具描述（JSON 降级路径用）。</summary>
        public string GetToolDescriptions(string profileName, HashSet<string>? authorizedTools = null)
        {
            var activeTools = GetActiveTools(profileName);
            return ToolRegistry.GenerateDescriptions(
                filter: t => activeTools.Contains(t.Name),
                authorizedTools: authorizedTools);
        }

        /// <summary>生成 Express 模式能力摘要。</summary>
        public string GetCapabilitySummary(string profileName)
        {
            var activeTools = GetActiveTools(profileName);
            return ToolRegistry.GenerateCapabilitySummary(
                filter: t => activeTools.Contains(t.Name));
        }

        private bool IsBlocked(ToolProfile profile, string toolName)
        {
            return profile.Blocked.Any(pattern => MatchesPattern(toolName, pattern));
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

        private void CreateDefaultConfig()
        {
            var defaults = GetDefaultProfiles();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                var json = JsonConvert.SerializeObject(defaults, Formatting.Indented);
                File.WriteAllText(ConfigPath, json);
            }
            catch { }
            profiles = defaults;
        }

        private static Dictionary<string, ToolProfile> GetDefaultProfiles()
        {
            return new Dictionary<string, ToolProfile>
            {
                ["channel"] = new ToolProfile
                {
                    Base = ["speak", "send_media", "wait", "memory", "pinboard",
                            "thinking_notes", "retain_list", "delegate_task", "alert",
                            "view_image", "get_image_text", "mark_review_hint", "task_management",
                            "read_file", "write_file", "adapter_action"],
                    Available = ["file", "image", "planning", "meta"],
                    Blocked = ["dream_*", "trigger_red_alert", "evaluate_delegation",
                               "create_sub_agent", "send_to_sub_agent", "stop_sub_agent",
                               "notify_channel", "set_watch_rule", "check_notifications",
                               "system_state", "continue_loop"]
                },
                ["system"] = new ToolProfile
                {
                    Base = ["wait", "continue_loop", "pinboard", "thinking_notes",
                            "evaluate_delegation", "notify_channel", "create_sub_agent",
                            "send_to_sub_agent", "stop_sub_agent", "check_notifications",
                            "set_watch_rule", "memory", "channel_info", "engine_management",
                            "adapter_action", "create_scheduled_task", "cancel_scheduled_task"],
                    Available = ["planning", "meta", "file"],
                    Blocked = ["speak", "send_media", "dream_*", "trigger_red_alert",
                               "delegate_task", "alert", "view_image", "get_image_text"]
                },
                ["sub_agent"] = new ToolProfile
                {
                    Base = ["continue_loop"],
                    Available = [],
                    Blocked = ["create_sub_agent", "send_to_sub_agent", "stop_sub_agent",
                               "evaluate_delegation", "notify_channel"]
                },
                ["review"] = new ToolProfile
                {
                    Base = ["search_memory", "view_links", "read_messages",
                            "update_affinity", "write_temp_memory", "thinking_notes",
                            "update_person_alias", "update_fast_memory", "adjust_trust",
                            "mark_review_hint", "request_reinforcement", "save_progress", "finish"],
                    Available = [],
                    Blocked = ["*"]
                }
            };
        }
    }

    internal class ToolProfile
    {
        [JsonProperty("base")]
        public List<string> Base { get; set; } = [];

        [JsonProperty("available")]
        public List<string> Available { get; set; } = [];

        [JsonProperty("blocked")]
        public List<string> Blocked { get; set; } = [];
    }

    public class ToolGroupSummary
    {
        public string GroupName { get; set; } = "";
        public List<string> ToolNames { get; set; } = [];
    }
}
