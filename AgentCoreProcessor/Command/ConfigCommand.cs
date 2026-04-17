using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Database;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Command
{
    /// <summary>
    /// 运行时配置修改。通过 JObject 读写 JSON 文件。
    /// 用法: /config — 列出配置组
    ///        /config <组> — 列出 key 和当前值
    ///        /config <组> <key> <value> — 修改值
    /// </summary>
    internal class ConfigCommand : IInteractiveCommand
    {
        public string Name => "config";
        public string Description => "运行时配置 (查看/修改)";
        public PermissionLevel RequiredPermission => PermissionLevel.Admin;

        // 配置组 → 文件路径映射
        private static readonly Dictionary<string, string> Groups = new(StringComparer.OrdinalIgnoreCase)
        {
            ["base"] = Path.Combine(PathConfig.CoreConfigPath, "Base.json"),
            ["express"] = Path.Combine(PathConfig.CoreConfigPath, "ExpressCore.json"),
            ["working"] = Path.Combine(PathConfig.CoreConfigPath, "WorkingCore.json"),
            ["extraction"] = Path.Combine(PathConfig.CoreConfigPath, "MemoryExtractionCore.json"),
            ["consolidation"] = Path.Combine(PathConfig.CoreConfigPath, "ConsolidationCore.json"),
            ["weight"] = Path.Combine(PathConfig.CoreConfigPath, "WeightCore.json"),
            ["link"] = Path.Combine(PathConfig.CoreConfigPath, "LinkCore.json"),
            ["combine"] = Path.Combine(PathConfig.CoreConfigPath, "CombineCore.json"),
            ["review"] = Path.Combine(PathConfig.CoreConfigPath, "ReviewCore.json"),
            ["dream"] = Path.Combine(PathConfig.StoragePath, "Dream", "DreamConfig.json"),
            ["impulse"] = Path.Combine(PathConfig.StoragePath, "Engine", "ImpulseConfig.json"),
            ["trust"] = Path.Combine(PathConfig.StoragePath, "Engine", "TrustProgressionConfig.json"),
        };

        // ---- 交互式定义 ----
        public List<CommandStep> Steps => new()
        {
            new() { Key = "group", Prompt = "选择配置组:",
                     Options = Groups.Keys.OrderBy(k => k).ToList() },
            new() { Key = "action", Prompt = "输入 key value 修改，或直接回车查看全部:",
                     Validate = null }
        };

// PLACEHOLDER_REST

        /// <summary>有参数时一次性执行: /config group [key value]</summary>
        public Task<CommandResult> ExecuteAsync(string args, CommandContext context)
        {
            var parts = args.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return Task.FromResult(ListGroups());
            if (!Groups.TryGetValue(parts[0], out var path))
                return Task.FromResult(CommandResult.Fail($"未知配置组: {parts[0]}\n{FormatGroupList()}"));
            if (parts.Length == 1)
                return Task.FromResult(ShowGroup(parts[0], path));
            if (parts.Length == 2)
                return Task.FromResult(CommandResult.Fail($"缺少值。用法: /config {parts[0]} <key> <value>"));
            return Task.FromResult(SetValue(parts[0], path, parts[1], parts[2]));
        }

        /// <summary>交互完成后执行。</summary>
        public Task<CommandResult> ExecuteInteractiveAsync(
            Dictionary<string, string> data, CommandContext context)
        {
            var group = data["group"];
            var action = data.GetValueOrDefault("action", "").Trim();
            if (!Groups.TryGetValue(group, out var path))
                return Task.FromResult(CommandResult.Fail($"未知配置组: {group}"));

            if (string.IsNullOrEmpty(action))
                return Task.FromResult(ShowGroup(group, path));

            var actionParts = action.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (actionParts.Length < 2)
                return Task.FromResult(ShowGroup(group, path));

            return Task.FromResult(SetValue(group, path, actionParts[0], actionParts[1]));
        }

        // ---- 内部方法 ----

        private static CommandResult ListGroups()
        {
            return CommandResult.Ok($"可用配置组:\n{FormatGroupList()}");
        }

        private static string FormatGroupList()
        {
            var sb = new StringBuilder();
            foreach (var g in Groups.Keys.OrderBy(k => k))
                sb.AppendLine($"  {g}");
            return sb.ToString().TrimEnd();
        }

        private static readonly HashSet<string> SensitiveKeys =
            new(StringComparer.OrdinalIgnoreCase) { "apiKey", "apikey", "api_key" };

        private static CommandResult ShowGroup(string group, string path)
        {
            if (!File.Exists(path))
                return CommandResult.Fail($"配置文件不存在: {group}");
            var obj = JObject.Parse(File.ReadAllText(path));
            var sb = new StringBuilder();
            sb.AppendLine($"[{group}] 配置:");
            foreach (var prop in obj.Properties())
            {
                var val = prop.Value.Type switch
                {
                    JTokenType.Array => $"[...] ({((JArray)prop.Value).Count} 项)",
                    JTokenType.Object => "{...}",
                    JTokenType.Null => "null",
                    _ => SensitiveKeys.Contains(prop.Name)
                        ? MaskValue(prop.Value.ToString())
                        : prop.Value.ToString()
                };
                sb.AppendLine($"  {prop.Name} = {val}");
            }
            return CommandResult.Ok(sb.ToString().TrimEnd());
        }

        private static string MaskValue(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= 4)
                return "****";
            return new string('*', value.Length - 4) + value[^4..];
        }

        private static CommandResult SetValue(string group, string path, string key, string rawValue)
        {
            if (!File.Exists(path))
                return CommandResult.Fail($"配置文件不存在: {group}");
            var obj = JObject.Parse(File.ReadAllText(path));
            if (!obj.ContainsKey(key))
                return CommandResult.Fail($"[{group}] 不存在 key: {key}");

            var existing = obj[key]!;
            // 不允许改数组和对象类型
            if (existing.Type == JTokenType.Array || existing.Type == JTokenType.Object)
                return CommandResult.Fail($"[{group}].{key} 是复杂类型，不支持命令行修改。");

            // 类型推断写入
            JToken newVal;
            if (rawValue.Equals("null", StringComparison.OrdinalIgnoreCase))
                newVal = JValue.CreateNull();
            else if (rawValue.Equals("true", StringComparison.OrdinalIgnoreCase))
                newVal = true;
            else if (rawValue.Equals("false", StringComparison.OrdinalIgnoreCase))
                newVal = false;
            else if (int.TryParse(rawValue, out var intVal) && existing.Type == JTokenType.Integer)
                newVal = intVal;
            else if (float.TryParse(rawValue, System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture, out var floatVal)
                     && (existing.Type == JTokenType.Float || existing.Type == JTokenType.Integer))
                newVal = floatVal;
            else
                newVal = rawValue;

            var oldVal = existing.Type == JTokenType.Null ? "null" : existing.ToString();
            obj[key] = newVal;
            File.WriteAllText(path, obj.ToString(Newtonsoft.Json.Formatting.Indented));

            return CommandResult.Ok($"[{group}].{key}: {oldVal} → {rawValue}");
        }
    }
}
