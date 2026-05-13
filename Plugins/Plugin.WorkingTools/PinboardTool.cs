using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;

namespace Plugin.WorkingTools
{
    /// <summary>
    /// 便签板工具。全局共享的 key-value 存储，系统循环和频道循环都可读写。
    /// </summary>
    [ToolMeta(Group = null, ContinueLoop = true, CapabilitySummary = "便签板：持久化的笔记板，可跨轮次保留关键信息")]
    public class PinboardTool : ITool
    {
        private readonly string _filePath;
        private static readonly object _lock = new();

        public string Name => "pinboard";
        public string Description => "便签板：持久化的 key-value 存储，内容每轮自动注入上下文。action: set(写入) / get(读取) / delete(删除) / list(列出全部) / clear(清空)。set 时 key 和 value 必填，get/delete 时 key 必填。";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("action", "操作类型：get / set / delete / list / clear", 0),
            new("key", "便签键名（get/set/delete 时必填）", 1, isRequired: false),
            new("value", "便签内容（set 时必填）", 2, isRequired: false)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);

        public PinboardTool(IToolContext ctx)
        {
            _filePath = Path.Combine(ctx.Storage.GlobalDirectory, "pinboard.json");
        }

        /// <summary>Component 模式构造函数</summary>
        public PinboardTool(IPluginStorage storage)
        {
            _filePath = Path.Combine(storage.GlobalDirectory, "pinboard.json");
        }

        public string? BuildSection()
        {
            var board = LoadBoard();
            if (board.Count == 0) return null;
            var sb = new System.Text.StringBuilder("[便签板]\n");
            foreach (var (label, content) in board)
                sb.AppendLine($"- {label}: {content}");
            return sb.ToString();
        }

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var action = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim().ToLower() : "";
            var key = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";
            var value = resolvedInputs.Count > 2 ? resolvedInputs[2] : "";

            lock (_lock)
            {
                var board = LoadBoard();

                switch (action)
                {
                    case "get":
                        if (string.IsNullOrEmpty(key))
                            return Fail("key 不能为空");
                        return Ok(board.TryGetValue(key, out var v) ? v : "(空)");

                    case "set":
                        if (string.IsNullOrEmpty(key))
                            return Fail("key 不能为空");
                        board[key] = value;
                        SaveBoard(board);
                        return Ok($"已设置 [{key}]");

                    case "delete":
                        if (string.IsNullOrEmpty(key))
                            return Fail("key 不能为空");
                        board.Remove(key);
                        SaveBoard(board);
                        return Ok($"已删除 [{key}]");

                    case "list":
                        if (board.Count == 0)
                            return Ok("(便签板为空)");
                        var lines = new List<string>();
                        foreach (var kv in board)
                            lines.Add($"[{kv.Key}] {kv.Value}");
                        return Ok(string.Join("\n", lines));

                    case "clear":
                        board.Clear();
                        SaveBoard(board);
                        return Ok("便签板已清空");

                    default:
                        return Fail($"未知操作: {action}，支持 get/set/delete/list/clear");
                }
            }
        }

        private Dictionary<string, string> LoadBoard()
        {
            if (!File.Exists(_filePath))
                return new Dictionary<string, string>();
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>();
        }

        private void SaveBoard(Dictionary<string, string> board)
        {
            var dir = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(dir);
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(board, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _filePath, overwrite: true);
        }

        private static Task<ToolResult> Ok(string data) =>
            Task.FromResult(new ToolResult { Status = "success", Data = data });

        private static Task<ToolResult> Fail(string err) =>
            Task.FromResult(new ToolResult { Status = "failed", Error = err });
    }
}
