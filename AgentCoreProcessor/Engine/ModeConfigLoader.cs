using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentCoreProcessor.Config;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 模式配置加载器。从 Storage/Engine/ModeConfig.json 读取模式定义，
    /// 提供按模式查询工具启用状态的统一入口。
    /// </summary>
    internal static class ModeConfigLoader
    {
        private static readonly object _lock = new();
        private static ModeConfig? _config;
        private static string? _loadedPath;

        private static string StoragePath => Path.Combine(PathConfig.StoragePath, "Engine", "ModeConfig.json");
        private static string TemplatePath => Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "templates", "Engine", "ModeConfig.json");

        /// <summary>加载（或重新加载）模式配置。线程安全。</summary>
        public static ModeConfig Load()
        {
            lock (_lock)
            {
                if (_config != null && _loadedPath == StoragePath && File.Exists(StoragePath))
                    return _config;

                string sourcePath;
                if (File.Exists(StoragePath))
                {
                    sourcePath = StoragePath;
                }
                else if (File.Exists(TemplatePath))
                {
                    sourcePath = TemplatePath;
                }
                else
                {
                    // 没有配置文件，返回空配置
                    _config = new ModeConfig();
                    _loadedPath = null;
                    return _config;
                }

                var json = File.ReadAllText(sourcePath);
                var config = JsonConvert.DeserializeObject<ModeConfig>(json);
                _config = config ?? new ModeConfig();
                _loadedPath = sourcePath;
                return _config;
            }
        }

        /// <summary>获取指定 ID 的模式定义。未找到返回 null。</summary>
        public static ModeDefinition? GetMode(string modeId)
        {
            var config = Load();
            return config.Modes.FirstOrDefault(m =>
                string.Equals(m.Id, modeId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>获取指定 metaType 的所有模式。</summary>
        public static List<ModeDefinition> GetModesByMetaType(string metaType)
        {
            var config = Load();
            return config.Modes
                .Where(m => string.Equals(m.MetaType, metaType, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>获取所有模式。</summary>
        public static List<ModeDefinition> GetAllModes()
        {
            return Load().Modes;
        }

        /// <summary>检查工具在指定模式下是否启用。</summary>
        public static bool IsToolEnabled(string modeId, string toolName)
        {
            var mode = GetMode(modeId);
            if (mode == null) return false;

            // 显式配置优先
            if (mode.Tools != null &&
                mode.Tools.TryGetValue(toolName, out var explicitState))
            {
                return string.Equals(explicitState, "enabled", StringComparison.OrdinalIgnoreCase);
            }

            // 回退到 toolDefaults
            return string.Equals(mode.ToolDefaults, "enabled", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>获取工具在指定模式下的状态（"enabled"/"disabled"/"default"）。</summary>
        public static string GetToolState(string modeId, string toolName)
        {
            var mode = GetMode(modeId);
            if (mode == null) return "disabled";

            if (mode.Tools != null &&
                mode.Tools.TryGetValue(toolName, out var explicitState))
            {
                return explicitState.ToLowerInvariant();
            }

            return "default";
        }

        /// <summary>获取 escalate 的目标模式 ID。</summary>
        public static string GetEscalateTarget()
        {
            var config = Load();
            return string.IsNullOrEmpty(config.EscalateTarget) ? "plan" : config.EscalateTarget;
        }

        /// <summary>保存模式配置到 Storage。线程安全。</summary>
        public static void Save(ModeConfig config)
        {
            lock (_lock)
            {
                var dir = Path.GetDirectoryName(StoragePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonConvert.SerializeObject(config, Formatting.Indented);
                var tmpPath = StoragePath + ".tmp";
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, StoragePath, overwrite: true);

                _config = config;
                _loadedPath = StoragePath;
            }
        }

        /// <summary>重新加载（丢弃缓存）。</summary>
        public static ModeConfig Reload()
        {
            lock (_lock)
            {
                _config = null;
                _loadedPath = null;
            }
            return Load();
        }
    }

    internal class ModeConfig
    {
        [JsonProperty("version")]
        public int Version { get; set; } = 1;

        [JsonProperty("modes")]
        public List<ModeDefinition> Modes { get; set; } = new();

        [JsonProperty("escalateTarget")]
        public string EscalateTarget { get; set; } = "plan";
    }

    internal class ModeDefinition
    {
        [JsonProperty("id")]
        public string Id { get; set; } = "";

        [JsonProperty("displayName")]
        public string DisplayName { get; set; } = "";

        [JsonProperty("description")]
        public string Description { get; set; } = "";

        [JsonProperty("metaType")]
        public string MetaType { get; set; } = "Working";

        [JsonProperty("maxRounds")]
        public int MaxRounds { get; set; } = 10;

        [JsonProperty("toolDefaults")]
        public string ToolDefaults { get; set; } = "disabled";

        [JsonProperty("tools")]
        public Dictionary<string, string> Tools { get; set; } = new();
    }
}
