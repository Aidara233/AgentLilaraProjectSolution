using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;

namespace Plugin.WorkingTools
{
    /// <summary>
    /// 思考笔记工具。每个 notebook 独立存储，互不干扰。
    /// notebook 参数由调用方指定（系统循环用 "system"，频道循环用 channelId）。
    /// </summary>
    [ToolMeta(Group = null, ContinueLoop = true, CapabilitySummary = "思考笔记：记录推理过程和待办事项")]
    public class ThinkingNotesTool : ITool, AgentCoreProcessor.Engine.IInjectProvider
    {
        private readonly string _baseDir;
        private static readonly object _lock = new();
        private const int MaxLines = 50;

        public string Name => "thinking_notes";
        public string Description => "思考笔记：你的私人草稿本，内容每轮自动注入上下文。action: append(追加) / read(读取) / replace(替换全部) / clear(清空)。notebook 参数传当前频道ID（见循环状态提示）。append/replace 时 content 必填。";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("action", "操作类型：append / read / clear / replace", 0),
            new("notebook", "笔记本标识（系统循环用 system，频道循环用频道ID）", 1),
            new("content", "笔记内容（append/replace 时必填）", 2, isRequired: false)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);

        private readonly string? _defaultNotebook;

        public ThinkingNotesTool(IToolContext ctx)
        {
            _baseDir = Path.Combine(ctx.Storage.GlobalDirectory, "notebooks");
            _defaultNotebook = null;
            Directory.CreateDirectory(_baseDir);
        }

        /// <summary>Component 模式构造函数</summary>
        public ThinkingNotesTool(IPluginStorage storage, string loopId)
        {
            _baseDir = Path.Combine(storage.InstanceDirectory, "notebooks");
            _defaultNotebook = loopId;
            Directory.CreateDirectory(_baseDir);
        }

        public string? BuildSection()
        {
            if (_defaultNotebook == null) return null;
            var path = Path.Combine(_baseDir, $"{SanitizeFileName(_defaultNotebook)}.txt");
            if (!File.Exists(path)) return null;
            var content = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(content)) return null;
            return $"你的思考笔记（notebook={_defaultNotebook}）：\n{content}";
        }

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var action = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim().ToLower() : "";
            var notebook = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "default";
            var content = resolvedInputs.Count > 2 ? resolvedInputs[2] : "";

            if (string.IsNullOrEmpty(notebook))
                notebook = "default";

            var safeName = SanitizeFileName(notebook);
            var filePath = Path.Combine(_baseDir, $"{safeName}.txt");

            lock (_lock)
            {
                switch (action)
                {
                    case "append":
                        if (string.IsNullOrEmpty(content))
                            return Fail("content 不能为空");
                        AppendAndTruncate(filePath, content);
                        return Ok("已追加");

                    case "read":
                        if (!File.Exists(filePath))
                            return Ok("(笔记为空)");
                        return Ok(File.ReadAllText(filePath));

                    case "clear":
                        if (File.Exists(filePath))
                            File.Delete(filePath);
                        return Ok("笔记已清空");

                    case "replace":
                        if (string.IsNullOrEmpty(content))
                            return Fail("content 不能为空");
                        WriteAtomic(filePath, content);
                        return Ok("笔记已替换");

                    default:
                        return Fail($"未知操作: {action}，支持 append/read/clear/replace");
                }
            }
        }

        private static void AppendAndTruncate(string filePath, string content)
        {
            var lines = File.Exists(filePath)
                ? new List<string>(File.ReadAllLines(filePath))
                : new List<string>();

            lines.Add($"[{DateTime.Now:HH:mm}] {content}");

            if (lines.Count > MaxLines)
                lines.RemoveRange(0, lines.Count - MaxLines);

            WriteAtomic(filePath, string.Join("\n", lines));
        }

        private static void WriteAtomic(string filePath, string content)
        {
            var dir = Path.GetDirectoryName(filePath)!;
            Directory.CreateDirectory(dir);
            var tmp = filePath + ".tmp";
            File.WriteAllText(tmp, content);
            File.Move(tmp, filePath, overwrite: true);
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            if (name.Length > 64) name = name[..64];
            return name;
        }

        // IInjectProvider
        public int InjectPriority => 45;
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
