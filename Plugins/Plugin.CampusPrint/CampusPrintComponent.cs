using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentLilara.PluginSDK;

namespace Plugin.CampusPrint;

[Component(Name = "campus-print", Scope = ComponentScope.Global)]
public class CampusPrintComponent : GlobalComponentBase
{
    private IGlobalComponentContext _ctx = null!;
    private CampusPrintConfig _config = null!;
    private CampusPrintClient? _client;

    private readonly List<ITool> _tools = new();

    public override ComponentMeta Meta => new()
    {
        Name = "campus-print",
        Description = "校园打印：上传文件、设置参数、计价下单（萌蚤云印）",
        DefaultEnabled = false,
        PromptPriority = 100
    };

    public override IEnumerable<ITool> Tools => _tools;

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        _ctx = context;
        _config = CampusPrintConfig.Load(context.Storage.GlobalDirectory);

        if (_config.HasCredentials)
        {
            _client = new CampusPrintClient(_config);
        }

        _tools.Add(new PrintSetTokenTool(this));
        _tools.Add(new PrintStoreInfoTool(this));
        _tools.Add(new PrintFileUploadTool(this, context.Storage.WorkspaceDirectory));
        _tools.Add(new PrintFileAddTool(this));
        _tools.Add(new PrintFileUpdateTool(this));
        _tools.Add(new PrintFileListTool(this));
        _tools.Add(new PrintFileDelTool(this));
        _tools.Add(new PrintPdfStatusTool(this));
        _tools.Add(new PrintGetPriceTool(this));
        _tools.Add(new PrintOrderCreateTool(this));

        return Task.CompletedTask;
    }

    public override string? BuildPromptSection(LoopInfo caller)
    {
        if (!_config.HasCredentials)
        {
            return """
                [校园打印] 未配置凭据。
                请用户从小程序 DevTools → Storage → cache_token / cache_appkey 提取，
                然后用 print_set_token 设置。门店默认 燕山大学(西校区) 印之友快印 (store_id=1440)。
                """;
        }

        return $"""
            [校园打印] 已配置 | 门店 store_id={_config.StoreId}
            标准流程（严格按顺序）：
              1. print_file_upload — 上传本地文件，得到 file_path
              2. print_file_add — 添加到打印列表，自动开始 PDF 转换
              3. print_pdf_status — 轮询直到 status=2（转换完成）
              4. print_file_update — （可选）修改打印设置：颜色/单双面/份数/缩放
              5. print_get_price — 计价，确认金额
              6. print_order_create — 下单预检，get_price_vip=1 为预检模式不支付
            中途可随时 print_file_list 查看列表、print_file_del 删除文件。
            每次会话开始建议先 print_store_info 刷新 appkey。
            """;
    }

    // =========================================================================
    // Internal helpers
    // =========================================================================

    internal CampusPrintConfig Config => _config;
    internal CampusPrintClient? Client => _client;

    internal void EnsureClient()
    {
        if (_client == null)
        {
            _client = new CampusPrintClient(_config);
        }
    }

    internal void SetCredentials(string token, string appkey)
    {
        _config.Token = token;
        _config.Appkey = appkey;
        _config.Save(_ctx.Storage.GlobalDirectory);
        _client = new CampusPrintClient(_config);
    }

    internal void UpdateStoreInfo(string appkey, string upUrl, int domainId)
    {
        _config.Appkey = appkey;
        _config.UpUrl = upUrl;
        _config.DomainId = domainId;
        _config.Save(_ctx.Storage.GlobalDirectory);
        _client = new CampusPrintClient(_config);
    }

    internal static string FormatJson(JsonNode? node)
    {
        if (node == null) return "(null)";
        try
        {
            return JsonSerializer.Serialize(node, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return node.ToJsonString();
        }
    }

    internal static int GetCode(JsonNode? resp)
    {
        return resp?["code"]?.GetValue<int>() ?? -999;
    }

    /// data 字段可能是 JSON 字符串（二次编码），也可能是对象或数组
    internal static JsonNode? ParseData(JsonNode? resp)
    {
        var data = resp?["data"];
        if (data == null) return null;
        if (data is JsonValue v && v.TryGetValue(out string? s) && s != null)
        {
            try { return JsonNode.Parse(s); }
            catch { return data; }
        }
        return data;
    }

    internal static bool IsTokenExpired(JsonNode? resp)
    {
        return GetCode(resp) == -6;
    }

    /// <summary>剥离模型经常额外包裹的引号</summary>
    internal static string Clean(string s) => s.Trim().Trim('"', '\'', '`');

    /// <summary>规范化路径：Git Bash /e/... → E:\...，/ → \</summary>
    internal static string NormalizePath(string path)
    {
        var p = Clean(path);
        // Git Bash: /e/foo → E:\foo, /c/Users → C:\Users
        if (p.Length >= 3 && p[0] == '/' && p[2] == '/' && char.IsLetter(p[1]))
            p = $"{char.ToUpper(p[1])}:{p[2..]}";
        p = p.Replace('/', '\\');
        // 去多余反斜杠
        while (p.Contains("\\\\"))
            p = p.Replace("\\\\", "\\");
        return p;
    }
}

// =============================================================================
// TOOLS
// =============================================================================

#region print_set_token

[ToolMeta(Group = "campus-print", ContinueLoop = true, AllowSubAgent = false)]
public class PrintSetTokenTool : ITool
{
    private readonly CampusPrintComponent _comp;
    public PrintSetTokenTool(CampusPrintComponent comp) => _comp = comp;

    public string Name => "print_set_token";
    public string Description => """
        设置或更新校园打印的认证凭据（token 和 appkey）。
        从微信小程序 DevTools → Storage 中提取：
        - cache_token：较长字符串，有效期数天到数周
        - cache_appkey：32位 hex + 时间戳后缀
        凭据会持久化保存，只需设置一次。设置后建议调用 print_store_info 验证。
        """;
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("token", "cache_token 值（从微信 Storage 提取）", 0),
        new("appkey", "cache_appkey 值（从微信 Storage 提取）", 1)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var token = CampusPrintComponent.Clean(inputs[0]);
        var appkey = CampusPrintComponent.Clean(inputs[1]);
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(appkey))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "token 和 appkey 都不能为空" });

        _comp.SetCredentials(token, appkey);
        return Task.FromResult(new ToolResult
        {
            Status = "success",
            Data = $"凭据已保存。token={token[..Math.Min(8, token.Length)]}... appkey={appkey[..Math.Min(16, appkey.Length)]}...\n下一步：调用 print_store_info 验证凭据并获取门店配置。"
        });
    }
}

#endregion

#region print_store_info

[ToolMeta(Group = "campus-print", ContinueLoop = true, AllowSubAgent = false)]
public class PrintStoreInfoTool : ITool
{
    private readonly CampusPrintComponent _comp;
    public PrintStoreInfoTool(CampusPrintComponent comp) => _comp = comp;

    public string Name => "print_store_info";
    public string Description => """
        获取打印门店配置并刷新 appkey。返回：纸张大小、价格规则、缩放/装订选项、VIP 信息。
        每次打印会话开始时建议调用一次，因为 appkey 带时间戳后缀，每次刷新都不同。
        如果返回 code=-6 表示 token 已过期，需手动从小程序重新提取。
        """;
    public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        _comp.EnsureClient();
        var client = _comp.Client!;
        if (!client.HasCredentials)
            return new ToolResult { Status = "failed", Error = "未配置凭据。请先用 print_set_token 设置 token 和 appkey。" };

        var resp = await client.GetStoreInfo();
        if (CampusPrintComponent.IsTokenExpired(resp))
            return new ToolResult { Status = "failed", Error = "Token 已过期（code=-6），需手动从微信小程序 DevTools 重新提取 cache_token。" };

        var code = CampusPrintComponent.GetCode(resp);
        if (code != 0)
            return new ToolResult { Status = "failed", Error = $"获取门店失败: code={code}, msg={resp?["msg"]?.GetValue<string>()}" };

        var data = CampusPrintComponent.ParseData(resp);
        var store = data?["store"];
        var user = data?["user"];
        var appkey = user?["appkey"]?.GetValue<string>() ?? "";
        var upUrl = store?["up_url"]?.GetValue<string>() ?? "";
        var domainId = store?["domain_id"]?.GetValue<int>() ?? 2;

        if (!string.IsNullOrWhiteSpace(appkey))
            _comp.UpdateStoreInfo(appkey, upUrl, domainId);

        var sb = new StringBuilder();
        sb.AppendLine($"门店: {store?["name"]?.GetValue<string>()} (id={store?["id"]})");
        sb.AppendLine($"appkey 已刷新: {appkey[..Math.Min(20, appkey.Length)]}...");
        sb.AppendLine($"domain_id: {domainId}");
        sb.AppendLine($"up_url: {upUrl}");

        var prices = data?["price"]?.AsArray();
        if (prices != null)
        {
            sb.AppendLine($"价格条目数: {prices.Count}");
            var sample = prices.Take(3).Select(p =>
                $"  {p!["page_size_type"]}: ¥{p["price"]}/{p["start_count"]}张起");
            foreach (var s in sample) sb.AppendLine(s);
        }

        return new ToolResult { Status = "success", Data = sb.ToString().TrimEnd() };
    }
}

#endregion

#region print_file_upload

[ToolMeta(Group = "campus-print", ContinueLoop = true, AllowSubAgent = false)]
public class PrintFileUploadTool : ITool
{
    private readonly CampusPrintComponent _comp;
    private readonly string _workspaceDir;

    public PrintFileUploadTool(CampusPrintComponent comp, string workspaceDir)
    {
        _comp = comp;
        _workspaceDir = Path.GetFullPath(workspaceDir);
    }

    public string Name => "print_file_upload";
    public string Description => """
        上传本地文件到打印服务器（步骤 1/5）。
        支持所有常见格式：doc/docx/pdf/ppt/pptx/xls/xlsx/jpg/png/txt 等。
        大文件自动分块上传，已存在的文件（相同 MD5）秒传跳过。
        返回 file_path，这是下一步 print_file_add 的必需参数。
        文件路径相对于共享工作目录（与 read_text / write_text 等工具相同），也支持绝对路径。
        """;
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("file_path", "要上传的文件名或相对路径（相对于工作目录），也支持绝对路径", 0)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(120);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var raw = inputs[0];
        var cleaned = CampusPrintComponent.Clean(raw);

        // 尝试多种解析策略
        string? resolved = null;
        var attempts = new List<string>();

        // 1) 原始输入（支持绝对路径如 E:\... 或 /e/...）
        var norm = CampusPrintComponent.NormalizePath(cleaned);
        attempts.Add(norm);
        if (File.Exists(norm)) resolved = norm;

        // 2) 相对于工作目录
        if (resolved == null)
        {
            var rel = cleaned.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var workspaceRoot = _workspaceDir.EndsWith(Path.DirectorySeparatorChar)
                ? _workspaceDir : _workspaceDir + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(_workspaceDir, rel));
            attempts.Add(full);
            if (full.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                resolved = full;
        }

        if (resolved == null)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"文件不存在。输入: \"{raw}\"");
            sb.AppendLine($"工作目录: {_workspaceDir}");
            sb.AppendLine("尝试的路径:");
            foreach (var a in attempts) sb.AppendLine($"  → {a}");
            return new ToolResult { Status = "failed", Error = sb.ToString().TrimEnd() };
        }

        _comp.EnsureClient();
        var client = _comp.Client!;
        if (!client.HasCredentials)
            return new ToolResult { Status = "failed", Error = "未配置凭据。请先用 print_set_token 设置 token 和 appkey。" };

        if (string.IsNullOrWhiteSpace(client.UpUrl))
            return new ToolResult { Status = "failed", Error = "未获取 up_url。请先调用 print_store_info。" };

        var fname = Path.GetFileName(resolved);
        var fsize = new FileInfo(resolved).Length;
        var ext = Path.GetExtension(resolved).TrimStart('.').ToLowerInvariant();

        var result = await client.UploadFile(resolved);
        if (result == null)
            return new ToolResult { Status = "failed", Error = "上传失败，可能是 token 过期或网络问题。检查 print_store_info 是否正常。" };

        var fp = result["file_path"]?.GetValue<string>() ?? "";
        var md5 = result["file_md5"]?.GetValue<string>() ?? "";

        return new ToolResult
        {
            Status = "success",
            Data = $"上传成功!\nfile_path: {fp}\nfile_name: {fname}\nfile_md5: {md5}\nfile_size: {fsize}\nformat: {ext}\n\n下一步：调用 print_file_add 将文件添加到打印列表。"
        };
    }
}

#endregion

#region print_file_add

[ToolMeta(Group = "campus-print", ContinueLoop = true, AllowSubAgent = false)]
public class PrintFileAddTool : ITool
{
    private readonly CampusPrintComponent _comp;
    public PrintFileAddTool(CampusPrintComponent comp) => _comp = comp;

    public string Name => "print_file_add";
    public string Description => """
        将已上传的文件添加到打印列表（步骤 2/5）。
        只需提供三个基本信息：file_path（上传返回的）、文件名、文件格式。
        如需设置纸张大小、单双面、份数等，添加后用 print_file_update 修改。
        添加后服务器自动开始 PDF 转换，需用 print_pdf_status 轮询等待完成。
        """;
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("file_path", "服务器端路径，由 print_file_upload 返回（如 /weapp/xxx.docx）", 0),
        new("file_name", "文件名含扩展名（如 报告.docx）", 1),
        new("file_format", "文件扩展名（如 docx、pdf、jpg）", 2)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        try
        {
            if (inputs.Count < 3)
                return new ToolResult { Status = "failed", Error = "缺少参数。需要 3 个参数：\n1) file_path — 由 print_file_upload 返回的服务器路径（如 /weapp/xxx.docx）\n2) file_name — 文件名（如 手册.docx）\n3) file_format — 扩展名（如 docx）\n请先调用 print_file_upload 上传文件。" };

            _comp.EnsureClient();
            var client = _comp.Client!;
            if (!client.HasCredentials)
                return new ToolResult { Status = "failed", Error = "未配置凭据。" };

            var fields = new Dictionary<string, object?>
            {
                ["file_index"] = "0s",
                ["file_path"] = CampusPrintComponent.Clean(inputs[0]),
                ["file_name"] = CampusPrintComponent.Clean(inputs[1]),
                ["file_format"] = CampusPrintComponent.Clean(inputs[2]).ToLowerInvariant(),
                ["domain_id"] = _comp.Config.DomainId
            };

            var resp = await client.FileAdd(fields);
            if (CampusPrintComponent.IsTokenExpired(resp))
                return new ToolResult { Status = "failed", Error = "Token 已过期（code=-6）。" };

            var code = CampusPrintComponent.GetCode(resp);
            if (code != 0)
                return new ToolResult { Status = "failed", Error = $"添加失败: code={code}, msg={resp?["msg"]?.GetValue<string>()}" };

            var data = CampusPrintComponent.ParseData(resp);
            var fileId = data?["id"]?.GetValue<int>() ?? 0;
            var status = data?["status"]?.GetValue<int>() ?? 0;

            return new ToolResult
            {
                Status = "success",
                Data = $"已添加到打印列表。file_id={fileId}, status={status}（0=等待, 1=转换中, 2=完成）\n下一步：调用 print_pdf_status 轮询等待 PDF 转换完成（status=2）。然后用 print_file_update 修改打印设置（如纸张大小、单双面、份数）。"
            };
        }
        catch (Exception ex)
        {
            return new ToolResult { Status = "failed", Error = $"添加失败: {ex.Message}" };
        }
    }
}

#endregion

#region print_file_update

[ToolMeta(Group = "campus-print", ContinueLoop = true, AllowSubAgent = false)]
public class PrintFileUpdateTool : ITool
{
    private readonly CampusPrintComponent _comp;
    public PrintFileUpdateTool(CampusPrintComponent comp) => _comp = comp;

    public string Name => "print_file_update";
    public string Description => """
        修改打印列表中某文件的打印设置（步骤 3/5，可选）。
        文件必须已处于 status=2（转换完成）状态才能修改。
        可修改的字段（至少传一个）：
        - is_color: 0=黑白, 1=彩色
        - page_size: 纸张大小，如 "A4", "A3"
        - page_orient: 0=自动, 1=纵向, 2=横向
        - page_way: 1=单面, 2=双面长边翻页, 3=双面短边翻页
        - print_count: 打印份数
        - print_page: 页码范围，如 "1-5" 或 "1,3,5,7"
        - color_pages: 彩色页指定，页码字符串
        - scale_type: 缩放类型 ID（从 store_info 的 scale_list 获取，1=不缩放）
        - is_saddle_stitch: 0=无装订, 1=骑马钉
        - page_way_binding: 装订方向 1=左, 2=上
        - is_print_note: 0/1 是否打印备注
        - is_lecture_note: 0/1 讲义模式（每张纸多页）
        修改后需重新调用 print_get_price 计价。
        """;
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("file_id", "文件 ID（print_file_add 返回的 file_id）", 0),
        new("settings_json", "要修改的设置，JSON 格式。例: {\"is_color\":1,\"print_count\":2,\"page_way\":2}", 1)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var fileIdStr = CampusPrintComponent.Clean(inputs[0]);
        var settingsJson = CampusPrintComponent.Clean(inputs[1]);

        if (!int.TryParse(fileIdStr, out var fileId))
            return new ToolResult { Status = "failed", Error = $"无效的 file_id: {fileIdStr}" };

        JsonNode? settings;
        try { settings = JsonNode.Parse(settingsJson); }
        catch { return new ToolResult { Status = "failed", Error = $"JSON 解析失败: {settingsJson}" }; }
        if (settings is not JsonObject jo)
            return new ToolResult { Status = "failed", Error = "settings_json 必须是一个 JSON 对象" };

        _comp.EnsureClient();
        var client = _comp.Client!;
        if (!client.HasCredentials)
            return new ToolResult { Status = "failed", Error = "未配置凭据。" };

        var fields = new Dictionary<string, object?> { ["id"] = fileId };
        foreach (var (k, v) in jo)
        {
            if (v is JsonValue jv)
            {
                if (jv.TryGetValue(out int n)) fields[k] = n;
                else if (jv.TryGetValue(out string? s) && s != null) fields[k] = s;
                else fields[k] = v.ToString();
            }
        }

        var resp = await client.FileAdd(fields);
        if (CampusPrintComponent.IsTokenExpired(resp))
            return new ToolResult { Status = "failed", Error = "Token 已过期（code=-6）。" };

        var code = CampusPrintComponent.GetCode(resp);
        if (code != 0)
            return new ToolResult { Status = "failed", Error = $"修改设置失败: code={code}, msg={resp?["msg"]?.GetValue<string>()}" };

        return new ToolResult
        {
            Status = "success",
            Data = $"打印设置已更新。file_id={fileId}\n修改的字段: {string.Join(", ", jo.Select(kv => kv.Key))}\n下一步：调用 print_get_price 重新计价。"
        };
    }
}

#endregion

#region print_file_list

[ToolMeta(Group = "campus-print", ContinueLoop = true, AllowSubAgent = false)]
public class PrintFileListTool : ITool
{
    private readonly CampusPrintComponent _comp;
    public PrintFileListTool(CampusPrintComponent comp) => _comp = comp;

    public string Name => "print_file_list";
    public string Description => """
        列出当前打印列表中的所有文件及其状态。
        返回每个文件的：id, file_name, page_count, status（0=等待 1=转换中 2=完成）, is_color, print_count, page_size, page_way 等。
        只有 status=2 的文件可以参与计价和下单。
        """;
    public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        _comp.EnsureClient();
        var client = _comp.Client!;
        if (!client.HasCredentials)
            return new ToolResult { Status = "failed", Error = "未配置凭据。" };

        var resp = await client.FileList();
        if (CampusPrintComponent.IsTokenExpired(resp))
            return new ToolResult { Status = "failed", Error = "Token 已过期（code=-6）。" };

        var code = CampusPrintComponent.GetCode(resp);
        if (code != 0)
            return new ToolResult { Status = "failed", Error = $"获取列表失败: code={code}" };

        var data = CampusPrintComponent.ParseData(resp);
        if (data is JsonArray arr && arr.Count == 0)
            return new ToolResult { Status = "success", Data = "打印列表为空。用 print_file_upload + print_file_add 添加文件。" };

        var sb = new StringBuilder();
        sb.AppendLine($"打印列表（{((data as JsonArray)?.Count ?? 0)} 个文件）：");

        if (data is JsonArray files)
        {
            foreach (var f in files)
            {
                var id = f!["id"]?.GetValue<int>() ?? 0;
                var name = f["file_name"]?.GetValue<string>() ?? "?";
                var pages = f["page_count"]?.GetValue<int>() ?? 0;
                var status = f["status"]?.GetValue<int>() ?? 0;
                var statusLabel = status switch { 0 => "等待", 1 => "转换中", 2 => "完成", _ => "?" };
                var color = (f["is_color"]?.GetValue<int>() ?? 0) == 1 ? "彩色" : "黑白";
                var copies = f["print_count"]?.GetValue<int>() ?? 1;
                var way = (f["page_way"]?.GetValue<int>() ?? 1) switch { 1 => "单面", 2 => "双面长边", 3 => "双面短边", _ => "?" };
                var size = f["page_size"]?.GetValue<string>() ?? "A4";
                var err = f["error_msg"]?.GetValue<string>();
                sb.AppendLine($"  [{id}] {name} | {pages}页 {color} {size} {way} x{copies}份 | 状态={statusLabel}");
                if (!string.IsNullOrWhiteSpace(err))
                    sb.AppendLine($"    错误: {err}");
            }
        }

        return new ToolResult { Status = "success", Data = sb.ToString().TrimEnd() };
    }
}

#endregion

#region print_file_del

[ToolMeta(Group = "campus-print", ContinueLoop = true, AllowSubAgent = false)]
public class PrintFileDelTool : ITool
{
    private readonly CampusPrintComponent _comp;
    public PrintFileDelTool(CampusPrintComponent comp) => _comp = comp;

    public string Name => "print_file_del";
    public string Description => "从打印列表中删除指定文件。参数 file_id 可以从 print_file_list 获取。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("file_id", "要删除的文件 ID", 0)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (!int.TryParse(CampusPrintComponent.Clean(inputs[0]), out var fileId))
            return new ToolResult { Status = "failed", Error = $"无效的 file_id: {inputs[0]}" };

        _comp.EnsureClient();
        var client = _comp.Client!;
        if (!client.HasCredentials)
            return new ToolResult { Status = "failed", Error = "未配置凭据。" };

        var resp = await client.FileDel(fileId);
        var code = CampusPrintComponent.GetCode(resp);
        if (code != 0)
            return new ToolResult { Status = "failed", Error = $"删除失败: code={code}, msg={resp?["msg"]?.GetValue<string>()}" };

        return new ToolResult { Status = "success", Data = $"文件 {fileId} 已删除。" };
    }
}

#endregion

#region print_pdf_status

[ToolMeta(Group = "campus-print", ContinueLoop = true, AllowSubAgent = false)]
public class PrintPdfStatusTool : ITool
{
    private readonly CampusPrintComponent _comp;
    public PrintPdfStatusTool(CampusPrintComponent comp) => _comp = comp;

    public string Name => "print_pdf_status";
    public string Description => """
        查询文件的 PDF 转换状态。通常在 print_file_add 之后调用。
        返回：status=0 等待中，status=1 转换中（需等待重试），status=2 完成（可以继续计价）。
        一般小文件秒转，大文件可能需要几秒到几十秒。
        如果返回 status=1，等待 2-3 秒后重试。
        """;
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("file_id", "文件 ID", 0)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (!int.TryParse(CampusPrintComponent.Clean(inputs[0]), out var fileId))
            return new ToolResult { Status = "failed", Error = $"无效的 file_id: {inputs[0]}" };

        _comp.EnsureClient();
        var client = _comp.Client!;
        if (!client.HasCredentials)
            return new ToolResult { Status = "failed", Error = "未配置凭据。" };

        var resp = await client.ToPdfCheck(fileId);
        if (CampusPrintComponent.IsTokenExpired(resp))
            return new ToolResult { Status = "failed", Error = "Token 已过期（code=-6）。" };

        var code = CampusPrintComponent.GetCode(resp);
        if (code == 10)
        {
            return new ToolResult { Status = "success", Data = "PDF 转换仍在进行中（code=10），请等待 2-3 秒后重试。" };
        }
        if (code != 0)
            return new ToolResult { Status = "failed", Error = $"查询失败: code={code}, msg={resp?["msg"]?.GetValue<string>()}" };

        var data = CampusPrintComponent.ParseData(resp);
        // to_pdf_check 返回数组
        JsonNode? fileInfo = data switch
        {
            JsonArray arr when arr.Count > 0 => arr[0],
            JsonObject obj => obj,
            _ => null
        };

        if (fileInfo == null)
            return new ToolResult { Status = "failed", Error = "未获取到文件信息。" };

        var status = fileInfo["status"]?.GetValue<int>() ?? 0;
        var pages = fileInfo["page_count"]?.GetValue<int>() ?? 0;
        var statusLabel = status switch { 0 => "等待中", 1 => "转换中", 2 => "已完成", _ => "未知" };

        var sb = new StringBuilder();
        sb.AppendLine($"file_id={fileId}: status={status}（{statusLabel}）, page_count={pages}");
        if (status == 2)
            sb.AppendLine("转换完成，可以调用 print_get_price 计价了。");
        else if (status == 1 || status == 0)
            sb.AppendLine("请等待 2-3 秒后重新调用此工具检查状态。");

        return new ToolResult { Status = "success", Data = sb.ToString().TrimEnd() };
    }
}

#endregion

#region print_get_price

[ToolMeta(Group = "campus-print", ContinueLoop = true, AllowSubAgent = false)]
public class PrintGetPriceTool : ITool
{
    private readonly CampusPrintComponent _comp;
    public PrintGetPriceTool(CampusPrintComponent comp) => _comp = comp;

    public string Name => "print_get_price";
    public string Description => """
        计算打印列表中所有文件的打印总价（步骤 4/5）。
        必须在所有文件 PDF 转换完成后（status=2）调用，否则未完成的文件不计入。
        返回总价、每个文件的单独价格、折扣/VIP 信息。
        A4 黑白通常 ¥0.6/页。
        """;
    public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        _comp.EnsureClient();
        var client = _comp.Client!;
        if (!client.HasCredentials)
            return new ToolResult { Status = "failed", Error = "未配置凭据。" };

        var resp = await client.GetPrice();
        if (CampusPrintComponent.IsTokenExpired(resp))
            return new ToolResult { Status = "failed", Error = "Token 已过期（code=-6）。" };

        var code = CampusPrintComponent.GetCode(resp);
        if (code != 0)
            return new ToolResult { Status = "failed", Error = $"计价失败: code={code}, msg={resp?["msg"]?.GetValue<string>()}" };

        var data = CampusPrintComponent.ParseData(resp);
        var price = data?["price"]?.GetValue<decimal>() ?? 0;
        var countFile = data?["count_file"]?.GetValue<int>() ?? 0;
        var disc = data?["list"]?["disc"];
        var priceRaw = data?["price_raw"];
        var priceReal = priceRaw?["price_real"]?.GetValue<decimal>() ?? 0;
        var msg = data?["msg"]?.GetValue<string>() ?? "";

        var sb = new StringBuilder();
        sb.AppendLine($"总价: ¥{price}");
        sb.AppendLine($"文件数: {countFile}");
        if (priceReal > 0) sb.AppendLine($"折后价: ¥{priceReal}");
        if (disc != null) sb.AppendLine($"折扣信息: {CampusPrintComponent.FormatJson(disc)}");
        if (!string.IsNullOrWhiteSpace(msg)) sb.AppendLine($"备注: {msg}");
        sb.AppendLine();
        sb.AppendLine("下一步：调用 print_order_create 下单（建议先用 get_price_vip=1 预检）。");

        return new ToolResult { Status = "success", Data = sb.ToString().TrimEnd() };
    }
}

#endregion

#region print_order_create

[ToolMeta(Group = "campus-print", ContinueLoop = true, AllowSubAgent = false)]
public class PrintOrderCreateTool : ITool
{
    private readonly CampusPrintComponent _comp;
    public PrintOrderCreateTool(CampusPrintComponent comp) => _comp = comp;

    public string Name => "print_order_create";
    public string Description => """
        创建打印订单（步骤 5/5）。
        - get_price_vip=1：预检模式，仅计算不实际下单，返回价格明细（推荐先用此模式）
        - get_price_vip=0：正式下单模式（⚠ 会实际创建订单，但不会自动支付）
        参数：
        - print_count: 固定填 1
        - is_delivery: 0=到店自取, 1=快递配送
        - pay_way: 支付方式，默认 0
        - get_price_vip: 1=预检, 0=正式下单
        """;
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("is_delivery", "0=到店自取 1=快递配送", 0, isRequired: false),
        new("get_price_vip", "1=预检（默认推荐） 0=正式下单", 1, isRequired: false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        _comp.EnsureClient();
        var client = _comp.Client!;
        if (!client.HasCredentials)
            return new ToolResult { Status = "failed", Error = "未配置凭据。" };

        var isDelivery = 0;
        var getPriceVip = 1;

        if (inputs.Count > 0 && !string.IsNullOrWhiteSpace(inputs[0]))
            int.TryParse(inputs[0], out isDelivery);
        if (inputs.Count > 1 && !string.IsNullOrWhiteSpace(inputs[1]))
            int.TryParse(inputs[1], out getPriceVip);

        var resp = await client.CreateOrder(
            printCount: 1,
            storeId: client.StoreId,
            isDelivery: isDelivery,
            payWay: 0,
            getPriceVip: getPriceVip
        );

        if (CampusPrintComponent.IsTokenExpired(resp))
            return new ToolResult { Status = "failed", Error = "Token 已过期（code=-6）。" };

        var code = CampusPrintComponent.GetCode(resp);
        if (code != 0)
            return new ToolResult { Status = "failed", Error = $"下单失败: code={code}, msg={resp?["msg"]?.GetValue<string>()}" };

        var data = CampusPrintComponent.ParseData(resp);
        var payPriceVip = data?["pay_price_vip"];

        if (getPriceVip == 1)
        {
            var wepay = payPriceVip?["wepay"]?.GetValue<decimal>() ?? 0;
            var vipPrice = payPriceVip?["vip"]?.GetValue<decimal>() ?? 0;
            var balance = payPriceVip?["balance"]?.GetValue<decimal>() ?? 0;
            var sb = new StringBuilder();
            sb.AppendLine("[预检模式] 订单价格明细：");
            sb.AppendLine($"  应付: ¥{wepay}");
            sb.AppendLine($"  VIP价: ¥{vipPrice}");
            sb.AppendLine($"  余额: ¥{balance}");
            sb.AppendLine("确认无误后，用 get_price_vip=0 正式下单。");
            return new ToolResult { Status = "success", Data = sb.ToString().TrimEnd() };
        }
        else
        {
            return new ToolResult
            {
                Status = "success",
                Data = $"订单已创建！\n{CampusPrintComponent.FormatJson(payPriceVip)}\n⚠ 订单未支付，需在小程序中完成支付。"
            };
        }
    }
}

#endregion
