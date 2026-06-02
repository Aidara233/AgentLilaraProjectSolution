using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.GroupFileTools;

[ToolMeta(Group = null, ContinueLoop = true, ExpressAvailable = true)]
public class ListGroupFilesTool : ITool
{
    private readonly IAdapterAccess? _adapterAccess;
    private readonly string _adapterId = "";

    public ListGroupFilesTool() { }

    public ListGroupFilesTool(IAdapterAccess adapterAccess, string adapterId)
    {
        _adapterAccess = adapterAccess;
        _adapterId = adapterId;
    }

    public string Name => "list_group_files";
    public string Description => "查询群文件（群聊私聊均可用，提供 group_id 即可）。"
        + "拿到 file_id+busid 后用 download_group_file 下载。"
        + "folder_id 默认根目录。keyword 模糊匹配文件名和上传者。ext 按扩展名过滤（如 .c .pdf）。"
        + "sort_by: time/size/name，默认 time。limit 默认10。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("group_id", "群号", 0),
        new("folder_id", "文件夹ID（可选，默认根目录）", 1, isRequired: false),
        new("keyword", "文件名或上传者关键词（可选）", 2, isRequired: false),
        new("ext", "文件扩展名过滤，如 .c .pdf（可选）", 3, isRequired: false),
        new("sort_by", "排序方式: time/size/name（默认time）", 4, isRequired: false),
        new("limit", "返回条数（默认10，最大50）", 5, isRequired: false)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        if (_adapterAccess == null)
            return new ToolResult { Status = "failed", Error = "适配器服务不可用" };

        var groupId = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        if (string.IsNullOrEmpty(groupId))
            return new ToolResult { Status = "failed", Error = "group_id 不能为空" };

        var folderId = resolvedInputs.Count > 1 ? resolvedInputs[1]?.Trim() : null;
        var keyword = resolvedInputs.Count > 2 ? resolvedInputs[2]?.Trim() : null;
        var ext = resolvedInputs.Count > 3 ? resolvedInputs[3]?.Trim() : null;
        var sortBy = resolvedInputs.Count > 4 ? resolvedInputs[4]?.Trim() : "time";
        var limitStr = resolvedInputs.Count > 5 ? resolvedInputs[5]?.Trim() : "10";
        if (!int.TryParse(limitStr, out var limit) || limit < 1) limit = 10;
        if (limit > 50) limit = 50;

        var folderParam = string.IsNullOrEmpty(folderId) ? null : $",\"folder_id\":\"{folderId}\"";
        var paramJson = $"{{\"group_id\":\"{groupId}\"{folderParam}}}";

        var rawJson = await _adapterAccess.ExecuteActionAsync(_adapterId, "get_group_files", paramJson);
        if (rawJson == null)
            return new ToolResult { Status = "failed", Error = "获取群文件列表失败" };

        // rawJson 是摘要文本，再做客户端过滤/排序/截断
        var lines = rawJson.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return new ToolResult { Status = "success", Data = rawJson };

        // 第一行是统计: [群文件] 共 X 个文件夹, Y 个文件
        var header = lines[0];
        var items = lines.Skip(1).ToList();

        // 过滤 keyword（文件名或上传者）
        if (!string.IsNullOrEmpty(keyword))
            items = items.Where(l => l.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();

        // 过滤扩展名
        if (!string.IsNullOrEmpty(ext))
        {
            var exts = ext.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.StartsWith('.') ? e : $".{e}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            items = items.Where(l =>
            {
                var name = ExtractName(l);
                return exts.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
            }).ToList();
        }

        // 排序（简单按前缀类型分：文件夹[D]优先，然后按sort_by）
        var folders = items.Where(l => l.TrimStart().StartsWith("[D]")).ToList();
        var fileItems = items.Where(l => !l.TrimStart().StartsWith("[D]")).ToList();

        // 文件按 size 或 name 排序（默认 time 即原顺序）
        if (sortBy == "size")
            fileItems = fileItems.OrderByDescending(l => ExtractSize(l)).ToList();
        else if (sortBy == "name")
            fileItems = fileItems.OrderBy(l => ExtractName(l), StringComparer.OrdinalIgnoreCase).ToList();

        var result = folders.Concat(fileItems).Take(limit).ToList();
        var totalFiles = ExtractCount(header, "文件");
        var matched = !string.IsNullOrEmpty(keyword) ? $" 匹配 \"{keyword}\"" : "";
        var summary = $"[群文件] 显示 {result.Count}/{totalFiles} 个文件{matched}";
        if (!string.IsNullOrEmpty(folderId)) summary += $" (folder_id={folderId})";

        return new ToolResult { Status = "success", Data = summary + "\n" + string.Join("\n", result) };
    }

    private static string ExtractName(string line)
    {
        var trimmed = line.TrimStart();
        var prefixEnd = trimmed.IndexOf(' ') + 1;
        if (prefixEnd <= 0) return trimmed;
        var nameEnd = trimmed.IndexOf(" (", prefixEnd, StringComparison.Ordinal);
        return nameEnd > 0 ? trimmed[prefixEnd..nameEnd] : trimmed[prefixEnd..];
    }

    private static long ExtractSize(string line)
    {
        var match = System.Text.RegularExpressions.Regex.Match(line, @"\(([\d.]+)(MB|KB|B)\)");
        if (!match.Success) return 0;
        var num = double.TryParse(match.Groups[1].Value, out var n) ? n : 0;
        return match.Groups[2].Value switch
        {
            "MB" => (long)(n * 1_000_000),
            "KB" => (long)(n * 1_000),
            _ => (long)n
        };
    }

    private static int ExtractCount(string header, string label)
    {
        var match = System.Text.RegularExpressions.Regex.Match(header, $@"(\d+)\s*个{label}");
        return match.Success && int.TryParse(match.Groups[1].Value, out var n) ? n : 0;
    }
}
