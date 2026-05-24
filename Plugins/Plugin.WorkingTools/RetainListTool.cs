using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;

namespace Plugin.WorkingTools
{
    /// <summary>
    /// 缓存列表工具。钉住工具返回的长文本结果，使其在后续轮次持续可见。
    /// 每个 notebook（频道/引擎）独立存储。
    /// </summary>
    [ToolMeta(Group = null, ContinueLoop = true, CapabilitySummary = "缓存列表：钉住重要的工具结果，避免滚出上下文")]
    public class RetainListTool : ITool, AgentCoreProcessor.Engine.IInjectProvider
    {
        private readonly string _baseDir;
        private static readonly object _lock = new();
        private const int MaxItems = 10;

        public string Name => "retain_list";
        public string Description => "缓存列表：将重要的工具结果或长文本钉住，使其在后续轮次持续可见。action: add(添加) / remove(移除) / list(查看全部) / clear(清空)。add 时 label 和 content 必填。";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("action", "操作类型：add / remove / list / clear", 0),
            new("label", "条目标签（add/remove 时必填）", 1, isRequired: false),
            new("content", "要缓存的内容（add 时必填）", 2, isRequired: false)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);

        public RetainListTool(IToolContext ctx)
        {
            _baseDir = Path.Combine(ctx.Storage.InstanceDirectory, "retain");
            Directory.CreateDirectory(_baseDir);
        }

        /// <summary>Component 模式构造函数</summary>
        public RetainListTool(IPluginStorage storage)
        {
            _baseDir = Path.Combine(storage.InstanceDirectory, "retain");
            Directory.CreateDirectory(_baseDir);
        }

        public string? BuildSection()
        {
            var items = LoadItems();
            if (items.Count == 0) return null;
            var sb = new StringBuilder("[缓存列表]\n");
            foreach (var (label, content) in items)
            {
                var preview = content.Length > 120 ? content[..120] + "..." : content;
                sb.AppendLine($"- [{label}] {preview}");
            }
            return sb.ToString();
        }

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var action = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim().ToLower() : "";
            var label = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";
            var content = resolvedInputs.Count > 2 ? resolvedInputs[2] : "";

            lock (_lock)
            {
                switch (action)
                {
                    case "add":
                        if (string.IsNullOrEmpty(label)) return Fail("label 不能为空");
                        if (string.IsNullOrEmpty(content)) return Fail("content 不能为空");
                        return Ok(Add(label, content));

                    case "remove":
                        if (string.IsNullOrEmpty(label)) return Fail("label 不能为空");
                        return Ok(Remove(label));

                    case "list":
                        return Ok(List());

                    case "clear":
                        return Ok(Clear());

                    default:
                        return Fail($"未知操作: {action}，支持 add/remove/list/clear");
                }
            }
        }

        private string Add(string label, string content)
        {
            var items = LoadItems();
            items[label] = content;
            if (items.Count > MaxItems)
            {
                var oldest = new List<string>(items.Keys)[0];
                items.Remove(oldest);
            }
            SaveItems(items);
            return $"已缓存 [{label}]（共 {items.Count} 条）";
        }

        private string Remove(string label)
        {
            var items = LoadItems();
            if (!items.Remove(label)) return $"[{label}] 不存在";
            SaveItems(items);
            return $"已移除 [{label}]";
        }

        private string List()
        {
            var items = LoadItems();
            if (items.Count == 0) return "(缓存列表为空)";
            var sb = new StringBuilder();
            foreach (var (lbl, val) in items)
            {
                var preview = val.Length > 80 ? val[..80] + "..." : val;
                sb.AppendLine($"[{lbl}] {preview}");
            }
            return sb.ToString().TrimEnd();
        }

        private string Clear()
        {
            SaveItems(new Dictionary<string, string>());
            return "缓存列表已清空";
        }

        private string FilePath => Path.Combine(_baseDir, "items.json");

        private Dictionary<string, string> LoadItems()
        {
            if (!File.Exists(FilePath)) return new();
            try
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            }
            catch { return new(); }
        }

        private void SaveItems(Dictionary<string, string> items)
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }

        // IInjectProvider
        public int InjectPriority => 60;
        public System.Threading.Tasks.Task<string?> BuildStartInjectAsync(AgentCoreProcessor.Engine.InjectContext ctx)
            => System.Threading.Tasks.Task.FromResult(BuildSection());
        public System.Threading.Tasks.Task<string?> BuildRoundInjectAsync(AgentCoreProcessor.Engine.InjectContext ctx)
            => System.Threading.Tasks.Task.FromResult<string?>(null);

        private static Task<ToolResult> Ok(string data) =>
            Task.FromResult(new ToolResult { Status = "success", Data = data });

        private static Task<ToolResult> Fail(string err) =>
            Task.FromResult(new ToolResult { Status = "failed", Error = err });
    }
}
