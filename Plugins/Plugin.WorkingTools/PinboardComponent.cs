// Plugins/Plugin.WorkingTools/PinboardComponent.cs
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentLilara.PluginSDK;

namespace Plugin.WorkingTools;

/// <summary>
/// 便签板 Global 组件（文件式系统性记忆）。
/// 每个条目 = 一个 &lt;标题&gt;.md 文件，内容逐字保存、零结构化处理（不适用于断言系统/Review 的内容放这里）。
/// 存储目录：Storage/Workspace/pins/（即工作目录内，file-tools 的 read_text/write_text/list_dir/grep_files 可原生操作）。
/// 会话开始时（BuildStartPromptSection）向所有会话类型注入仅标题清单，需要时用 pin_read 取全文。
/// 首次启动把旧 pinboard.json 迁移拆分成 &lt;key&gt;.md（原 json 改名保留兜底，可逆）。
/// 工具经 PinToolsAccessor 静态桥接访问（Static Accessor Bridge 模式）。
/// </summary>
[Component(Name = "pinboard", Scope = ComponentScope.Global)]
public class PinboardComponent : GlobalComponentBase
{
    private IGlobalComponentContext _ctx = null!;
    private string _pinsDir = null!;
    private bool _migrationDone;
    private readonly object _lock = new();

    public override ComponentMeta Meta => new()
    {
        Name = "pinboard",
        Description = "系统记忆（文件式逐字档案）：每个条目一个 &lt;标题&gt;.md，会话开始时注入仅标题清单",
        DefaultEnabled = true,
        PromptPriority = 50
    };

    public override IEnumerable<ITool> Tools => PinToolsAccessor.Tools;

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        _ctx = context;
        // pins 目录放工作目录内（Storage/Workspace/pins/），file-tools 可原生操作
        _pinsDir = Path.Combine(context.Storage.WorkspaceDirectory, "pins");
        Directory.CreateDirectory(_pinsDir);

        lock (_lock)
        {
            if (!_migrationDone)
            {
                // 旧 pinboard.json 可能在新组件目录（pinboard/）或旧 Loop 组件目录（working-tools/）两处
                var ownDir = context.Storage.GlobalDirectory;
                var legacyWorkingTools = Path.Combine(
                    Path.GetDirectoryName(ownDir) ?? "", "working-tools", "pinboard.json");
                MigrateLegacyJson(Path.Combine(ownDir, "pinboard.json"));
                MigrateLegacyJson(legacyWorkingTools);
                _migrationDone = true;
            }
        }

        PinToolsAccessor.Configure(this);
        return Task.CompletedTask;
    }

    /// <summary>会话开始时一次性注入仅标题清单（静态参考段，随首轮持久化；恢复会话跳过）。</summary>
    public override string? BuildStartPromptSection(LoopInfo caller)
    {
        var titles = ListTitles();
        if (titles.Count == 0) return null;

        var lines = titles.Select(t => $"- {t}");
        return "[系统记忆] 逐字文件档案，需要全文时用 pin_read 取：\n" + string.Join("\n", lines);
    }

    // ── 供 pin_* 工具访问 ──
    public string PinsDir => _pinsDir;
    public string WorkspaceDir => _ctx.Storage.WorkspaceDirectory;
    public object SyncLock => _lock;

    public List<string> ListTitles()
    {
        lock (_lock)
        {
            return Directory.Exists(_pinsDir)
                ? Directory.GetFiles(_pinsDir, "*.md")
                    .Select(f => Path.GetFileNameWithoutExtension(f))
                    .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();
        }
    }

    public string? ReadPin(string title)
    {
        lock (_lock)
        {
            var path = PinPath(title);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
    }

    public bool WritePin(string title, string content)
    {
        lock (_lock)
        {
            var name = SanitizeFileName(title);
            if (string.IsNullOrWhiteSpace(name)) return false;
            var path = Path.Combine(_pinsDir, name + ".md");
            WriteAtomic(path, content);
            return true;
        }
    }

    public bool AppendPin(string title, string content)
    {
        lock (_lock)
        {
            var name = SanitizeFileName(title);
            if (string.IsNullOrWhiteSpace(name)) return false;
            var path = Path.Combine(_pinsDir, name + ".md");
            var existing = File.Exists(path) ? File.ReadAllText(path) : "";
            var merged = string.IsNullOrWhiteSpace(existing)
                ? content
                : existing.TrimEnd() + "\n\n" + content;
            WriteAtomic(path, merged);
            return true;
        }
    }

    public bool RemovePin(string title)
    {
        lock (_lock)
        {
            var path = PinPath(title);
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
    }

    public List<string> SearchPins(string query)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(query) || !Directory.Exists(_pinsDir))
                return new List<string>();
            return Directory.GetFiles(_pinsDir, "*.md")
                .Where(f =>
                {
                    try { return File.ReadAllText(f).Contains(query, StringComparison.OrdinalIgnoreCase); }
                    catch { return false; }
                })
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private string PinPath(string title) => Path.Combine(_pinsDir, SanitizeFileName(title) + ".md");

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim();
        if (name.Length > 64) name = name[..64];
        return name;
    }

    /// <summary>首次启动把旧 pinboard.json（key-value）迁移为 &lt;key&gt;.md 文件。原 json 改名保留兜底（可逆）。</summary>
    private void MigrateLegacyJson(string legacy)
    {
        if (string.IsNullOrWhiteSpace(legacy) || !File.Exists(legacy)) return;

        Dictionary<string, string>? entries = null;
        try
        {
            var json = File.ReadAllText(legacy);
            entries = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch { entries = null; }

        if (entries == null || entries.Count == 0) return;

        var written = 0;
        foreach (var (key, value) in entries)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) continue;
            if (WritePin(key, value)) written++;
        }

        if (written > 0)
        {
            var backup = legacy + ".migrated-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            try { File.Move(legacy, backup); } catch { /* 改名失败则保留原文件 */ }
        }
    }
}