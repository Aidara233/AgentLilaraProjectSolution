using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentCoreProcessor.Config;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace AgentCoreProcessor.Tool.Host
{
    public enum ComponentState
    {
        Enabled,
        Disabled,
        Unavailable
    }

    /// <summary>
    /// 引擎工具配置管理。以组件状态为核心，支持链式继承、per-channel 映射。
    /// </summary>
    internal class ToolProfileManager
    {
        private ToolProfileConfig config = new();
        private readonly Dictionary<string, ResolvedProfile> resolvedCache = new();
        private readonly Dictionary<string, HashSet<string>> sessionActivations = new();

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

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                var json = JsonConvert.SerializeObject(config, Formatting.Indented);
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                Engine.FrameworkLogger.LogError("ToolProfileManager", ex, "保存配置失败");
            }
        }

// PLACEHOLDER_METHODS

        // ---- 查询接口 ----

        /// <summary>根据 channelId 获取对应的 profile 名。</summary>
        public string GetProfileForChannel(string channelId)
        {
            if (config.ChannelMapping.TryGetValue(channelId, out var profile))
                return profile;
            if (config.ChannelMapping.TryGetValue("_default", out var def))
                return def;
            return "_root";
        }

        /// <summary>获取组件在指定 profile 中的解析状态。</summary>
        public ComponentState GetComponentState(string profileName, string componentName)
        {
            var resolved = Resolve(profileName);
            if (resolved.Components.TryGetValue(componentName, out var state))
                return state;
            return ComponentState.Unavailable;
        }

        /// <summary>获取指定 profile 中所有 enabled 或 disabled（可用）的组件名。</summary>
        public List<string> GetAvailableComponents(string profileName)
        {
            var resolved = Resolve(profileName);
            return resolved.Components
                .Where(kv => kv.Value != ComponentState.Unavailable)
                .Select(kv => kv.Key)
                .ToList();
        }

        /// <summary>获取指定 profile 中默认启用的组件名。</summary>
        public List<string> GetEnabledComponents(string profileName)
        {
            var resolved = Resolve(profileName);
            return resolved.Components
                .Where(kv => kv.Value == ComponentState.Enabled)
                .Select(kv => kv.Key)
                .ToList();
        }

        /// <summary>获取当前会话中实际激活的组件（enabled + 手动激活的 disabled）。</summary>
        public HashSet<string> GetActiveComponents(string profileName, string sessionId)
        {
            var resolved = Resolve(profileName);
            var active = new HashSet<string>(
                resolved.Components.Where(kv => kv.Value == ComponentState.Enabled).Select(kv => kv.Key));

            if (sessionActivations.TryGetValue(sessionId, out var activated))
            {
                foreach (var c in activated)
                {
                    if (resolved.Components.TryGetValue(c, out var s) && s == ComponentState.Disabled)
                        active.Add(c);
                }
            }

            return active;
        }

        /// <summary>判断工具是否被 profile 屏蔽。</summary>
        public bool IsToolBlocked(string profileName, string toolName)
        {
            var resolved = Resolve(profileName);
            return resolved.BlockedTools.Contains(toolName);
        }

        /// <summary>获取指定 profile 的完整可用工具名集合（活跃组件的工具 - 屏蔽列表）。</summary>
        public HashSet<string> GetActiveTools(string profileName, string? sessionId = null)
        {
            var resolved = Resolve(profileName);
            var activeComponents = sessionId != null
                ? GetActiveComponents(profileName, sessionId)
                : new HashSet<string>(resolved.Components
                    .Where(kv => kv.Value == ComponentState.Enabled).Select(kv => kv.Key));

            var result = new HashSet<string>();
            var allTools = ToolRegistry.All;

            foreach (var reg in Component.ComponentRegistry.GetAll())
            {
                var attr = AgentLilara.PluginSDK.ComponentAttribute.GetFrom(reg.Type);
                if (attr == null) continue;
                if (!activeComponents.Contains(attr.Name)) continue;

                // 获取该组件的工具（同 assembly 的工具属于该组件）
                foreach (var tool in allTools.Values)
                {
                    if (tool.GetType().Assembly == reg.SourceAssembly
                        && !resolved.BlockedTools.Contains(tool.Name))
                    {
                        result.Add(tool.Name);
                    }
                }
            }

            // 核心工具始终可用
            result.Add("wait");
            result.Add("manage_components");

            return result;
        }

        // ---- 会话级组件激活 ----

        /// <summary>激活一个 disabled 状态的组件（会话级）。</summary>
        public bool ActivateComponent(string profileName, string sessionId, string componentName)
        {
            var resolved = Resolve(profileName);
            if (!resolved.Components.TryGetValue(componentName, out var state))
                return false;
            if (state != ComponentState.Disabled)
                return false;

            if (!sessionActivations.ContainsKey(sessionId))
                sessionActivations[sessionId] = new HashSet<string>();
            sessionActivations[sessionId].Add(componentName);
            return true;
        }

        /// <summary>停用一个已激活的组件（会话级）。</summary>
        public bool DeactivateComponent(string profileName, string sessionId, string componentName)
        {
            if (!sessionActivations.TryGetValue(sessionId, out var set))
                return false;
            return set.Remove(componentName);
        }

        /// <summary>清除会话的所有激活状态。</summary>
        public void ClearSession(string sessionId)
        {
            sessionActivations.Remove(sessionId);
        }

        /// <summary>获取可激活但尚未激活的组件列表。</summary>
        public List<string> GetActivatableComponents(string profileName, string sessionId)
        {
            var resolved = Resolve(profileName);
            var activated = sessionActivations.TryGetValue(sessionId, out var set) ? set : new HashSet<string>();

            return resolved.Components
                .Where(kv => kv.Value == ComponentState.Disabled && !activated.Contains(kv.Key))
                .Select(kv => kv.Key)
                .ToList();
        }

        // ---- 配置管理 ----

        public List<string> GetProfileNames() => config.Profiles.Keys.ToList();
        public Dictionary<string, string> GetChannelMapping() => new(config.ChannelMapping);
        public ToolProfile? GetProfile(string name) =>
            config.Profiles.TryGetValue(name, out var p) ? p : null;
        public string GetDefaultProfile() =>
            config.ChannelMapping.TryGetValue("_default", out var d) ? d : "_root";

        public void SetChannelMapping(string channelId, string profileName)
        {
            config.ChannelMapping[channelId] = profileName;
            Save();
            resolvedCache.Clear();
        }

        public void SetDefaultProfile(string profileName)
        {
            config.ChannelMapping["_default"] = profileName;
            Save();
            resolvedCache.Clear();
        }

        /// <summary>生成原生工具定义列表（用于 API tool_use）。</summary>
        public List<AgentLilara.PluginSDK.ToolDefinition> GetToolDefinitions(string profileName, string? sessionId = null)
        {
            var activeTools = GetActiveTools(profileName, sessionId);
            return ToolRegistry.All.Values
                .Where(t => activeTools.Contains(t.Name) && !ToolRegistry.IsDisabled(t.Name))
                .Select(t => new AgentLilara.PluginSDK.ToolDefinition
                {
                    Name = t.Name,
                    Description = t.Description,
                    Parameters = t.GetInputSchema()
                })
                .ToList();
        }

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
            var components = new Dictionary<string, ComponentState>();
            var blocked = new HashSet<string>();
            var unblocked = new HashSet<string>();

            // 从根（链尾）往叶（链头）合并
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                var p = chain[i];

                // 组件状态：子覆盖父
                foreach (var kv in p.Components)
                {
                    if (Enum.TryParse<ComponentState>(kv.Value, true, out var state))
                        components[kv.Key] = state;
                }

                // 工具屏蔽：累积
                foreach (var t in p.BlockedTools) blocked.Add(t);
                // 工具解除：移除 block
                foreach (var t in p.UnblockedTools)
                {
                    if (blocked.Contains(t))
                        blocked.Remove(t);
                    else
                        unblocked.Add(t);
                }
            }

            // 同节点冲突检查（最终叶节点）
            if (chain.Count > 0)
            {
                var leaf = chain[0];
                var conflicts = leaf.BlockedTools.Intersect(leaf.UnblockedTools).ToList();
                foreach (var c in conflicts)
                {
                    Engine.FrameworkLogger.Log("ToolProfileManager",
                        $"警告: 工具 '{c}' 同时出现在 blockedTools 和 unblockedTools 中，block 优先");
                }
            }

            return new ResolvedProfile
            {
                Components = components,
                BlockedTools = blocked
            };
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
                        Description = "全局默认配置",
                        Components = new Dictionary<string, string>
                        {
                            ["basic-tools"] = "enabled",
                            ["memory-tools"] = "enabled",
                            ["file-tools"] = "disabled",
                            ["working-tools"] = "enabled",
                            ["delegation"] = "unavailable",
                            ["system-ops"] = "unavailable"
                        }
                    },
                    ["channel"] = new ToolProfile
                    {
                        Inherits = "_root",
                        Description = "频道循环默认",
                        Components = new Dictionary<string, string>
                        {
                            ["delegation"] = "enabled"
                        }
                    },
                    ["system"] = new ToolProfile
                    {
                        Inherits = "_root",
                        Description = "系统循环",
                        Components = new Dictionary<string, string>
                        {
                            ["working-tools"] = "unavailable",
                            ["basic-tools"] = "unavailable",
                            ["delegation"] = "unavailable",
                            ["system-ops"] = "enabled"
                        }
                    },
                    ["sub-agent"] = new ToolProfile
                    {
                        Inherits = "_root",
                        Description = "子agent默认",
                        Components = new Dictionary<string, string>
                        {
                            ["file-tools"] = "enabled"
                        },
                        BlockedTools = ["delegate_task", "cancel_delegation"]
                    }
                },
                ChannelMapping = new Dictionary<string, string>
                {
                    ["_default"] = "channel"
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
    }

    internal class ToolProfile
    {
        [JsonProperty("inherits")]
        public string? Inherits { get; set; }

        [JsonProperty("description")]
        public string? Description { get; set; }

        [JsonProperty("components")]
        public Dictionary<string, string> Components { get; set; } = new();

        [JsonProperty("blockedTools")]
        public List<string> BlockedTools { get; set; } = [];

        [JsonProperty("unblockedTools")]
        public List<string> UnblockedTools { get; set; } = [];
    }

    internal class ResolvedProfile
    {
        public Dictionary<string, ComponentState> Components { get; set; } = new();
        public HashSet<string> BlockedTools { get; set; } = new();
    }

    public class ToolGroupSummary
    {
        public string GroupName { get; set; } = "";
        public List<string> ToolNames { get; set; } = [];
    }
}
