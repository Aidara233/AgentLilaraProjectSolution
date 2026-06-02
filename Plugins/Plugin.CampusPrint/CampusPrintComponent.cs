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
            _client = new CampusPrintClient(_config);

        _tools.Add(new PrintSetTokenTool(this));
        _tools.Add(new PrintStoreInfoTool(this));
        _tools.Add(new PrintFileUploadTool(this, context.Storage.WorkspaceDirectory));
        _tools.Add(new PrintFileAddTool(this));
        _tools.Add(new PrintFileUpdateTool(this));
        _tools.Add(new PrintFileListTool(this));
        _tools.Add(new PrintFileDelTool(this));
        _tools.Add(new PrintFileClearTool(this));
        _tools.Add(new PrintPdfStatusTool(this));
        _tools.Add(new PrintGetPriceTool(this));
        _tools.Add(new PrintOrderCreateTool(this));
        return Task.CompletedTask;
    }

    public override string? BuildPromptSection(LoopInfo caller)
    {
        if (!_config.HasCredentials)
            return "[校园打印] 未配置凭据。请用户从小程序提取 cache_token/cache_appkey，用 print_set_token 设置。";

        return $"[校园打印] 已配置 store_id={_config.StoreId} | 流程:\n  1.upload(自动注册) 2.pdf_status 3.update(改设置) 4.price 5.order\n  upload已自动注册文件(file_id>0)，勿再调add。用print_store_info查纸张价格。参数均string。";
    }

    internal CampusPrintConfig Config => _config;
    internal CampusPrintClient? Client => _client;

    internal void EnsureClient() { if (_client == null) _client = new CampusPrintClient(_config); }

    internal void SetCredentials(string token, string appkey)
    {
        _config.Token = token; _config.Appkey = appkey;
        _config.Save(_ctx.Storage.GlobalDirectory); _client = new CampusPrintClient(_config);
    }

    internal void UpdateStoreInfo(string appkey, string upUrl, int domainId)
    {
        _config.Appkey = appkey; _config.UpUrl = upUrl; _config.DomainId = domainId;
        _config.Save(_ctx.Storage.GlobalDirectory); _client = new CampusPrintClient(_config);
    }

    internal static string FormatJson(JsonNode? node)
    {
        if (node == null) return "(null)";
        try { return JsonSerializer.Serialize(node, new JsonSerializerOptions { WriteIndented = true }); }
        catch { return node.ToJsonString(); }
    }

    internal static int GetCode(JsonNode? resp)
    {
        if (resp == null) return -999;
        var n = resp["code"]; if (n == null) return -999;
        try { return n.GetValue<int>(); } catch { return -999; }
    }

    internal static decimal? SafeDecimal(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue(out decimal d)) return d;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out string s) && decimal.TryParse(s, out var sd)) return sd;
        return null;
    }

    internal static int SafeInt(JsonNode? node, int fallback = 0)
    {
        if (node is not JsonValue v) return fallback;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out string s) && int.TryParse(s, out var si)) return si;
        return fallback;
    }

    internal static JsonNode? ParseData(JsonNode? resp)
    {
        var data = resp?["data"]; if (data == null) return null;
        if (data is JsonValue v && v.TryGetValue(out string? s) && s != null)
        { try { return JsonNode.Parse(s); } catch { return data; } }
        return data;
    }

    internal static bool IsTokenExpired(JsonNode? resp) => GetCode(resp) == -6;
    internal static string Clean(string s) => s.Trim().Trim('"', '\'', '`');

    internal static string NormalizePath(string path)
    {
        var p = Clean(path);
        if (p.Length >= 3 && p[0] == '/' && p[2] == '/' && char.IsLetter(p[1]))
            p = $"{char.ToUpper(p[1])}:{p[2..]}";
        p = p.Replace('/', '\\');
        while (p.Contains("\\\\")) p = p.Replace("\\\\", "\\");
        return p;
    }
}

// ═══════════════════════════════════════════════════════════════ TOOLS

#region print_set_token
[ToolMeta(Group = "campus-print", ContinueLoop = true, AllowSubAgent = false)]
public class PrintSetTokenTool : ITool
{
    private readonly CampusPrintComponent _c;
    public PrintSetTokenTool(CampusPrintComponent c) => _c = c;
    public string Name => "print_set_token";
    public string Description => "设置认证凭据。token 和 appkey 从小程序 DevTools→Storage 提取。类型均为 string。";
    public IReadOnlyList<ToolParameter> Parameters => [new("token", "cache_token（string）", 0), new("appkey", "cache_appkey（string）", 1)];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var token = CampusPrintComponent.Clean(inputs[0]);
        var appkey = CampusPrintComponent.Clean(inputs[1]);
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(appkey))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "token 和 appkey 不能为空" });
        _c.SetCredentials(token, appkey);
        return Task.FromResult(new ToolResult { Status = "success", Data = $"已保存。下一步: print_store_info 验证。" });
    }
}
#endregion

#region print_store_info
[ToolMeta(Group = "campus-print", ContinueLoop = true, AllowSubAgent = false)]
public class PrintStoreInfoTool : ITool
{
    private readonly CampusPrintComponent _c;
    public PrintStoreInfoTool(CampusPrintComponent c) => _c = c;
    public string Name => "print_store_info";
    public string Description => "获取门店配置：纸张、价格、缩放、装订。同时刷新 appkey。无参数。";
    public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        _c.EnsureClient(); var cl = _c.Client!;
        if (!cl.HasCredentials) return new ToolResult { Status = "failed", Error = "未配置凭据，先 print_set_token。" };
        var resp = await cl.GetStoreInfo();
        if (CampusPrintComponent.IsTokenExpired(resp)) return new ToolResult { Status = "failed", Error = "Token 过期(code=-6)，需重新从小程序提取。" };
        var code = CampusPrintComponent.GetCode(resp);
        if (code != 0) return new ToolResult { Status = "failed", Error = $"失败: code={code} msg={resp?["msg"]?.GetValue<string>()}" };
        var data = CampusPrintComponent.ParseData(resp);
        var store = data?["store"]; var user = data?["user"];
        var appkey = user?["appkey"]?.GetValue<string>() ?? "";
        var upUrl = store?["up_url"]?.GetValue<string>() ?? "";
        var domainId = CampusPrintComponent.SafeInt(store?["domain_id"], 2);
        if (!string.IsNullOrWhiteSpace(appkey)) _c.UpdateStoreInfo(appkey, upUrl, domainId);

        var sb = new StringBuilder();
        sb.AppendLine($"门店: {store?["name"]?.GetValue<string>()} (id={CampusPrintComponent.SafeInt(store?["id"])})");
        var ps = data?["page_size"]?.AsArray();
        if (ps != null) sb.AppendLine($"纸张: {string.Join(", ", ps.Select(p => p!.GetValue<string>()))}");
        var prices = data?["price"]?.AsArray();
        if (prices != null)
        {
            var g = prices.GroupBy(p => p!["page_size_type"]?.GetValue<string>() ?? "?")
                .Select(g => $"{g.Key} ¥{g.Min(p => decimal.TryParse(p!["price"]?.ToString(), out var v) ? v : 0m)}/页起");
            sb.AppendLine($"价格({prices.Count}条): {string.Join(", ", g)}");
        }
        return new ToolResult { Status = "success", Data = sb.ToString().TrimEnd() };
    }
}
#endregion

#region print_file_upload
[ToolMeta(Group = "campus-print", ContinueLoop = true, AllowSubAgent = false)]
public class PrintFileUploadTool : ITool
{
    private readonly CampusPrintComponent _c;
    private readonly string _ws;
    public PrintFileUploadTool(CampusPrintComponent c, string ws) { _c = c; _ws = Path.GetFullPath(ws); }
    public string Name => "print_file_upload";
    public string Description => "上传本地文件到打印服务器（步骤1/5）。路径相对于工作目录或绝对路径。返回 server file_path。参数均为 string。";
    public IReadOnlyList<ToolParameter> Parameters => [new("file_path", "本地文件路径（string），相对于工作目录或如 E:\\... 的绝对路径", 0)];
    public TimeSpan Timeout => TimeSpan.FromSeconds(120);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var raw = inputs[0]; var cleaned = CampusPrintComponent.Clean(raw);
        string? resolved = null; var attempts = new List<string>();
        var norm = CampusPrintComponent.NormalizePath(cleaned); attempts.Add(norm);
        if (File.Exists(norm)) resolved = norm;
        if (resolved == null)
        {
            var rel = cleaned.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(_ws, rel));
            var root = _ws.EndsWith(Path.DirectorySeparatorChar) ? _ws : _ws + Path.DirectorySeparatorChar;
            attempts.Add(full);
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full)) resolved = full;
        }
        if (resolved == null)
        {
            var sb = new StringBuilder(); sb.AppendLine($"找不到文件。输入:\"{raw}\" 工作目录:{_ws}"); sb.AppendLine("尝试:"); foreach (var a in attempts) sb.AppendLine($"  {a}");
            return new ToolResult { Status = "failed", Error = sb.ToString().TrimEnd() };
        }
        _c.EnsureClient(); var cl = _c.Client!;
        if (!cl.HasCredentials) return new ToolResult { Status = "failed", Error = "未配置凭据。" };
        if (string.IsNullOrWhiteSpace(cl.UpUrl)) return new ToolResult { Status = "failed", Error = "未获取 up_url，先 print_store_info。" };
        var fn = Path.GetFileName(resolved); var fz = new FileInfo(resolved).Length; var ex = Path.GetExtension(resolved).TrimStart('.').ToLowerInvariant();
        var result = await cl.UploadFile(resolved);
        if (result == null) return new ToolResult { Status = "failed", Error = "上传失败，检查 token 是否过期。" };
        var fp = result["file_path"]?.GetValue<string>() ?? "";
        var md = result["file_md5"]?.GetValue<string>() ?? "";
        var fid = CampusPrintComponent.SafeInt(result["file_id"]);
        var ufn = result["file_name"]?.GetValue<string>() ?? fn;
        var uex = result["file_format"]?.GetValue<string>() ?? ex;

        var next = fid > 0
            ? $"文件已自动注册，file_id={fid}。下一步: print_pdf_status {fid} 等待转换；如需改设置用 print_file_update {fid} '{{\"page_size\":\"B5\",\"is_color\":\"0\"}}'"
            : $"下一步: print_file_add server_path=\"{fp}\" file_name=\"{ufn}\" file_format=\"{uex}\"";

        return new ToolResult { Status = "success", Data = $"上传成功!\nfile_path: {fp}\nfile_id: {fid}\nfile_name: {ufn}\nfile_md5: {md}\nfile_size: {fz}\nformat: {uex}\n{next}" };
    }
}
#endregion

#region print_file_add
[ToolMeta(Group = "campus-print", ContinueLoop = true, AllowSubAgent = false)]
public class PrintFileAddTool : ITool
{
    private readonly CampusPrintComponent _c;
    public PrintFileAddTool(CampusPrintComponent c) => _c = c;
    public string Name => "print_file_add";
    public string Description => "添加上传完成的文件到打印列表。注意：print_file_upload 通常已自动注册文件（返回 file_id>0），此时跳过此步直接用 print_file_update 改设置。仅在 file_id=0 时才需调用此工具。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("server_path",  "必填：上传返回的 file_path（string）", 0),
        new("file_name",    "必填：文件名含扩展名（string）", 1),
        new("file_format",  "必填：扩展名（string），如 docx/pdf/jpg", 2),
        new("is_color",     "可选：0=黑白 1=彩色（string，默认\"0\"）", 3, isRequired: false),
        new("print_count",  "可选：份数（string，默认\"1\"）", 4, isRequired: false),
        new("page_size",    "可选：纸张（string，默认\"A4\"，用 print_store_info 查）", 5, isRequired: false),
        new("page_way",     "可选：1=单面 2=双面长边 3=双面短边（string，默认\"1\"）", 6, isRequired: false),
        new("scale_type",   "可选：缩放（string，默认\"1\"=不缩放）", 7, isRequired: false),
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        try
        {
            if (inputs.Count < 3) return new ToolResult { Status = "failed", Error = "参数不足。需 (1)server_path (2)file_name (3)file_format。server_path 来自 print_file_upload 的返回值。" };
            _c.EnsureClient(); var cl = _c.Client!;
            if (!cl.HasCredentials) return new ToolResult { Status = "failed", Error = "未配置凭据。" };
            var fields = new Dictionary<string, object?>
            {
                ["file_index"] = "0s", ["file_path"] = CampusPrintComponent.Clean(inputs[0]),
                ["file_name"] = CampusPrintComponent.Clean(inputs[1]), ["file_format"] = CampusPrintComponent.Clean(inputs[2]).ToLowerInvariant(),
                ["domain_id"] = _c.Config.DomainId
            };
            if (inputs.Count > 3 && int.TryParse(CampusPrintComponent.Clean(inputs[3]), out var ic) && (ic == 0 || ic == 1)) fields["is_color"] = ic;
            if (inputs.Count > 4 && int.TryParse(CampusPrintComponent.Clean(inputs[4]), out var ip) && ip > 0) fields["print_count"] = ip;
            if (inputs.Count > 5) { var s = CampusPrintComponent.Clean(inputs[5]); if (!string.IsNullOrWhiteSpace(s)) fields["page_size"] = s; }
            if (inputs.Count > 6 && int.TryParse(CampusPrintComponent.Clean(inputs[6]), out var iw) && iw >= 1 && iw <= 3) fields["page_way"] = iw;
            if (inputs.Count > 7 && int.TryParse(CampusPrintComponent.Clean(inputs[7]), out var isc) && isc >= 1) fields["scale_type"] = isc;

            var resp = await cl.FileAdd(fields);
            if (resp == null) return new ToolResult { Status = "failed", Error = "服务器无响应，确认 server_path 是上传返回的路径而非本地文件名。" };
            if (CampusPrintComponent.IsTokenExpired(resp)) return new ToolResult { Status = "failed", Error = "Token 过期。" };
            var code = CampusPrintComponent.GetCode(resp);
            if (code != 0) return new ToolResult { Status = "failed", Error = $"添加失败: code={code} msg={resp?["msg"]?.GetValue<string>()}" };

            var data = CampusPrintComponent.ParseData(resp);
            var fid = CampusPrintComponent.SafeInt(data?["id"]); var st = CampusPrintComponent.SafeInt(data?["status"]);
            var ap = new List<string>();
            if (fields.ContainsKey("is_color")) ap.Add((int)fields["is_color"]! == 1 ? "彩色" : "黑白");
            if (fields.ContainsKey("print_count")) ap.Add($"份数={fields["print_count"]}");
            if (fields.ContainsKey("page_size")) ap.Add($"纸={fields["page_size"]}");
            if (fields.ContainsKey("page_way")) { var wl = (int)fields["page_way"]! switch { 1 => "单面", 2 => "双面长边", 3 => "双面短边", _ => "?" }; ap.Add(wl); }
            if (fields.ContainsKey("scale_type")) ap.Add($"缩放={fields["scale_type"]}");
            var aps = ap.Count > 0 ? $"\n  设置: {string.Join("，", ap)}" : "";
            return new ToolResult { Status = "success", Data = $"已添加。file_id={fid} status={st}{aps}\n下一步: print_pdf_status {fid} 等待转换，然后 print_get_price 计价。" };
        }
        catch (Exception ex) { return new ToolResult { Status = "failed", Error = $"异常[{ex.GetType().Name}]: {ex.Message}" }; }
    }
}
#endregion

#region print_file_update
[ToolMeta(Group = "campus-print", ContinueLoop = true, AllowSubAgent = false)]
public class PrintFileUpdateTool : ITool
{
    private readonly CampusPrintComponent _c;
    public PrintFileUpdateTool(CampusPrintComponent c) => _c = c;
    public string Name => "print_file_update";
    public string Description => "修改已有文件的打印设置（步骤3可选）。参数均为 string。例: {\"is_color\":\"1\",\"page_size\":\"B5\"}";
    public IReadOnlyList<ToolParameter> Parameters => [new("file_id", "文件 ID（string）", 0), new("settings_json", "要修改的字段，JSON对象格式（string）", 1)];
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);
    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var fidStr = CampusPrintComponent.Clean(inputs[0]); var sjson = CampusPrintComponent.Clean(inputs[1]);
        if (!int.TryParse(fidStr, out var fid)) return new ToolResult { Status = "failed", Error = $"无效 file_id: {fidStr}" };
        JsonNode? st; try { st = JsonNode.Parse(sjson); } catch { return new ToolResult { Status = "failed", Error = $"JSON 解析失败: {sjson}" }; }
        if (st is not JsonObject jo) return new ToolResult { Status = "failed", Error = "settings_json 须为 JSON 对象" };
        _c.EnsureClient(); var cl = _c.Client!;
        if (!cl.HasCredentials) return new ToolResult { Status = "failed", Error = "未配置凭据。" };
        var fields = new Dictionary<string, object?> { ["id"] = fid };
        foreach (var (k, v) in jo) { if (v is JsonValue jv) { if (jv.TryGetValue(out int n)) fields[k] = n; else if (jv.TryGetValue(out string? s) && s != null) fields[k] = CampusPrintComponent.Clean(s); else fields[k] = v.ToString(); } }
        var resp = await cl.FileAdd(fields);
        if (CampusPrintComponent.IsTokenExpired(resp)) return new ToolResult { Status = "failed", Error = "Token 过期。" };
        var code = CampusPrintComponent.GetCode(resp);
        if (code != 0) return new ToolResult { Status = "failed", Error = $"修改失败: code={code} msg={resp?["msg"]?.GetValue<string>()}" };
        return new ToolResult { Status = "success", Data = $"已更新 file_id={fid}。改动: {string.Join(",", jo.Select(kv => kv.Key))}。下一步: print_get_price。" };
    }
}
#endregion

#region print_file_list
[ToolMeta(Group = "campus-print", ContinueLoop = true, AllowSubAgent = false)]
public class PrintFileListTool : ITool
{
    private readonly CampusPrintComponent _c;
    public PrintFileListTool(CampusPrintComponent c) => _c = c;
    public string Name => "print_file_list";
    public string Description => "列出打印列表中所有文件及状态。无参数。";
    public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);
    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        _c.EnsureClient(); var cl = _c.Client!;
        if (!cl.HasCredentials) return new ToolResult { Status = "failed", Error = "未配置凭据。" };
        var resp = await cl.FileList();
        if (CampusPrintComponent.IsTokenExpired(resp)) return new ToolResult { Status = "failed", Error = "Token 过期。" };
        if (CampusPrintComponent.GetCode(resp) != 0) return new ToolResult { Status = "failed", Error = "获取列表失败。" };
        var data = CampusPrintComponent.ParseData(resp);
        if (data is JsonArray arr && arr.Count == 0) return new ToolResult { Status = "success", Data = "列表为空。" };
        if (data is not JsonArray files) return new ToolResult { Status = "failed", Error = "数据格式异常。" };
        var sb = new StringBuilder(); sb.AppendLine($"{files.Count} 个文件:");
        foreach (var f in files)
        {
            var id = CampusPrintComponent.SafeInt(f!["id"]); var nm = f["file_name"]?.GetValue<string>() ?? "?";
            var pg = CampusPrintComponent.SafeInt(f["page_count"]); var st = CampusPrintComponent.SafeInt(f["status"]);
            var sl = st switch { 0 => "等待", 1 => "转换中", 2 => "完成", _ => "?" };
            var clr = CampusPrintComponent.SafeInt(f["is_color"]) == 1 ? "彩" : "黑白";
            var cp = CampusPrintComponent.SafeInt(f["print_count"], 1);
            var wy = CampusPrintComponent.SafeInt(f["page_way"], 1) switch { 1 => "单面", 2 => "双面长", 3 => "双面短", _ => "?" };
            var sz = f["page_size"]?.GetValue<string>() ?? "A4"; var er = f["error_msg"]?.GetValue<string>();
            sb.AppendLine($"  [{id}] {nm} {pg}页 {clr} {sz} {wy} x{cp} {sl}");
            if (!string.IsNullOrWhiteSpace(er)) sb.AppendLine($"    错误: {er}");
        }
        return new ToolResult { Status = "success", Data = sb.ToString().TrimEnd() };
    }
}
#endregion

#region print_file_del
[ToolMeta(Group = "campus-print", ContinueLoop = true, AllowSubAgent = false)]
public class PrintFileDelTool : ITool
{
    private readonly CampusPrintComponent _c;
    public PrintFileDelTool(CampusPrintComponent c) => _c = c;
    public string Name => "print_file_del";
    public string Description => "删除文件。参数 file_id 为 string。";
    public IReadOnlyList<ToolParameter> Parameters => [new("file_id", "文件 ID（string）", 0)];
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);
    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (!int.TryParse(CampusPrintComponent.Clean(inputs[0]), out var fid)) return new ToolResult { Status = "failed", Error = $"无效 file_id: {inputs[0]}" };
        _c.EnsureClient(); var cl = _c.Client!;
        if (!cl.HasCredentials) return new ToolResult { Status = "failed", Error = "未配置凭据。" };
        var resp = await cl.FileDel(fid);
        if (CampusPrintComponent.GetCode(resp) != 0) return new ToolResult { Status = "failed", Error = $"删除失败: {resp?["msg"]?.GetValue<string>()}" };
        return new ToolResult { Status = "success", Data = $"文件 {fid} 已删除。" };
    }
}
#endregion

#region print_file_clear
[ToolMeta(Group = "campus-print", ContinueLoop = true, AllowSubAgent = false)]
public class PrintFileClearTool : ITool
{
    private readonly CampusPrintComponent _c;
    public PrintFileClearTool(CampusPrintComponent c) => _c = c;
    public string Name => "print_file_clear";
    public string Description => "清空打印列表中所有文件。无参数，一键删除全部。";
    public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);
    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        _c.EnsureClient(); var cl = _c.Client!;
        if (!cl.HasCredentials) return new ToolResult { Status = "failed", Error = "未配置凭据。" };
        var resp = await cl.FileList();
        if (CampusPrintComponent.GetCode(resp) != 0) return new ToolResult { Status = "failed", Error = "获取列表失败。" };
        var data = CampusPrintComponent.ParseData(resp);
        if (data is not JsonArray files || files.Count == 0) return new ToolResult { Status = "success", Data = "列表已为空。" };
        var ids = string.Join(",", files.Select(f => CampusPrintComponent.SafeInt(f!["id"]).ToString()));
        var del = await cl.FileClear(ids);
        if (CampusPrintComponent.GetCode(del) != 0) return new ToolResult { Status = "failed", Error = $"清空失败: {del?["msg"]?.GetValue<string>()}" };
        return new ToolResult { Status = "success", Data = $"已清空 {files.Count} 个文件。" };
    }
}
#endregion

#region print_pdf_status
[ToolMeta(Group = "campus-print", ContinueLoop = true, AllowSubAgent = false)]
public class PrintPdfStatusTool : ITool
{
    private readonly CampusPrintComponent _c;
    public PrintPdfStatusTool(CampusPrintComponent c) => _c = c;
    public string Name => "print_pdf_status";
    public string Description => "查询 PDF 转换状态。file_id 为 string。status: 0=等待 1=转换中 2=完成。";
    public IReadOnlyList<ToolParameter> Parameters => [new("file_id", "文件 ID（string）", 0)];
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);
    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (!int.TryParse(CampusPrintComponent.Clean(inputs[0]), out var fid)) return new ToolResult { Status = "failed", Error = $"无效 file_id: {inputs[0]}" };
        _c.EnsureClient(); var cl = _c.Client!;
        if (!cl.HasCredentials) return new ToolResult { Status = "failed", Error = "未配置凭据。" };
        var resp = await cl.ToPdfCheck(fid);
        if (CampusPrintComponent.IsTokenExpired(resp)) return new ToolResult { Status = "failed", Error = "Token 过期。" };
        var code = CampusPrintComponent.GetCode(resp);
        if (code == 10) return new ToolResult { Status = "success", Data = "转换中(code=10)，2-3秒后重试。" };
        if (code != 0) return new ToolResult { Status = "failed", Error = $"查询失败: code={code}" };
        var data = CampusPrintComponent.ParseData(resp);
        JsonNode? fi = data switch { JsonArray a when a.Count > 0 => a[0], JsonObject o => o, _ => null };
        if (fi == null) return new ToolResult { Status = "failed", Error = "未获取到文件信息。" };
        var st = CampusPrintComponent.SafeInt(fi["status"]); var pg = CampusPrintComponent.SafeInt(fi["page_count"]);
        var lb = st switch { 0 => "等待", 1 => "转换中", 2 => "完成", _ => "?" };
        return new ToolResult { Status = "success", Data = $"file_id={fid} status={st}({lb}) pages={pg}\n{(st == 2 ? "完成，可 print_get_price 计价。" : "等待2-3秒后重试。")}" };
    }
}
#endregion

#region print_get_price
[ToolMeta(Group = "campus-print", ContinueLoop = true, AllowSubAgent = false)]
public class PrintGetPriceTool : ITool
{
    private readonly CampusPrintComponent _c;
    public PrintGetPriceTool(CampusPrintComponent c) => _c = c;
    public string Name => "print_get_price";
    public string Description => "计算打印总价（步骤4/5）。所有文件 status=2 后调用。无参数。";
    public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);
    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        _c.EnsureClient(); var cl = _c.Client!;
        if (!cl.HasCredentials) return new ToolResult { Status = "failed", Error = "未配置凭据。" };
        var resp = await cl.GetPrice();
        if (CampusPrintComponent.IsTokenExpired(resp)) return new ToolResult { Status = "failed", Error = "Token 过期。" };
        if (CampusPrintComponent.GetCode(resp) != 0) return new ToolResult { Status = "failed", Error = $"计价失败: {resp?["msg"]?.GetValue<string>()}" };
        var d = CampusPrintComponent.ParseData(resp);
        var pr = CampusPrintComponent.SafeDecimal(d?["price"]) ?? 0m;
        var cf = CampusPrintComponent.SafeInt(d?["count_file"]);
        var rp = CampusPrintComponent.SafeDecimal(d?["price_raw"]?["price_real"]) ?? 0m;
        return new ToolResult { Status = "success", Data = $"总价:¥{pr} 文件数:{cf}{(rp > 0 ? $" 折后:¥{rp}" : "")}\n下一步: print_order_create 下单（先 get_price_vip=\"1\" 预检）。" };
    }
}
#endregion

#region print_order_create
[ToolMeta(Group = "campus-print", ContinueLoop = true, AllowSubAgent = false)]
public class PrintOrderCreateTool : ITool
{
    private readonly CampusPrintComponent _c;
    public PrintOrderCreateTool(CampusPrintComponent c) => _c = c;
    public string Name => "print_order_create";
    public string Description => "创建订单（步骤5/5）。参数均为 string。get_price_vip: \"1\"=预检 \"0\"=正式。is_delivery: \"0\"=自取 \"1\"=快递。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [new("get_price_vip", "预检或下单（string，默认\"1\"）", 0, isRequired: false), new("is_delivery", "自取或快递（string，默认\"0\"）", 1, isRequired: false)];
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);
    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        _c.EnsureClient(); var cl = _c.Client!;
        if (!cl.HasCredentials) return new ToolResult { Status = "failed", Error = "未配置凭据。" };
        int.TryParse(CampusPrintComponent.Clean(inputs.ElementAtOrDefault(0) ?? "1"), out var gv); if (gv != 0) gv = 1;
        int.TryParse(CampusPrintComponent.Clean(inputs.ElementAtOrDefault(1) ?? "0"), out var idv);
        var resp = await cl.CreateOrder(1, cl.StoreId, idv, 0, gv);
        if (CampusPrintComponent.IsTokenExpired(resp)) return new ToolResult { Status = "failed", Error = "Token 过期。" };
        if (CampusPrintComponent.GetCode(resp) != 0) return new ToolResult { Status = "failed", Error = $"下单失败: {resp?["msg"]?.GetValue<string>()}" };
        var d = CampusPrintComponent.ParseData(resp); var ppv = d?["pay_price_vip"];
        if (gv == 1)
        {
            var wp = CampusPrintComponent.SafeDecimal(ppv?["wepay"]) ?? 0m;
            var vp = CampusPrintComponent.SafeDecimal(ppv?["vip"]) ?? 0m;
            var bl = CampusPrintComponent.SafeDecimal(ppv?["balance"]) ?? 0m;
            return new ToolResult { Status = "success", Data = $"[预检] 应付:¥{wp} VIP:¥{vp} 余额:¥{bl}\n确认后 get_price_vip=\"0\" 正式下单。" };
        }
        return new ToolResult { Status = "success", Data = $"订单已创建。\n{CampusPrintComponent.FormatJson(ppv)}\n⚠ 未支付，需小程序内完成。" };
    }
}
#endregion
