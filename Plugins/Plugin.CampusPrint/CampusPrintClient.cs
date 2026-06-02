using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;

namespace Plugin.CampusPrint;

public class CampusPrintClient
{
    private readonly CampusPrintConfig _config;
    private readonly HttpClient _http;
    private const string Version = "2.3.38";
    private const string BaseUrl = "https://store.mzyunyin.com/weapp";

    private static readonly JsonSerializerOptions JsonNoSpaces = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string Token => _config.Token;
    public string Appkey => _config.Appkey;
    public string UpUrl => _config.UpUrl;
    public int StoreId => _config.StoreId;
    public int DomainId => _config.DomainId;
    public bool HasCredentials => _config.HasCredentials;

    public CampusPrintClient(CampusPrintConfig config)
    {
        _config = config;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 5
        };
        _http = new HttpClient(handler);
        _http.DefaultRequestVersion = HttpVersion.Version11;
        _http.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        _http.DefaultRequestHeaders.ExpectContinue = false;
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        _http.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("zh-CN", 0.9));
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/107.0.0.0 Safari/537.36 MicroMessenger/7.0.20.1781(0x6700143B) NetType/WIFI MiniProgramEnv/Windows WindowsWechat/WMPF WindowsWechat(0x63090a13) XWEB/8555");
        _http.DefaultRequestHeaders.Add("xweb_xhr", "1");
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    // =========================================================================
    // SIGNING
    // =========================================================================

    private static string Md5(string s)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string HexRandom()
    {
        return Random.Shared.NextInt64(0, 1_000_000_000_000_000).ToString("x");
    }

    private static object? NormalizeValue(object? v)
    {
        return v switch
        {
            null => "",
            JsonObject jo => JsonSerializer.Serialize(jo, JsonNoSpaces),
            Dictionary<string, object?> d => JsonSerializer.Serialize(d, JsonNoSpaces),
            string s => s,
            int n when n == 0 => 0,
            long n when n == 0 => 0,
            bool b when !b => 0,
            _ => v
        };
    }

    private Dictionary<string, object?> SignPost(Dictionary<string, object?> apiFields)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var origJson = JsonSerializer.Serialize(apiFields, JsonNoSpaces);

        var data = new Dictionary<string, object?>();
        foreach (var (k, v) in apiFields)
            data[k] = NormalizeValue(v);

        data["version"] = Version;
        data["time"] = now;
        data["app"] = "wx";
        data["sign"] = "";
        data["randomstr"] = HexRandom() + _config.Token;

        var keysBefore = data.Keys.OrderBy(k => k).ToList();

        data["appkey"] = _config.Appkey;
        data["_uv"] = Version;
        data["_ut"] = now;
        data["_ur"] = HexRandom();
        data["_up"] = "pages/print/index";

        var usData = new Dictionary<string, string>();
        var signParts = new List<string>();
        foreach (var k in keysBefore)
        {
            if (data[k] is string s && s.Length <= 1000)
            {
                usData[k] = s;
                signParts.Add($"{k}={s}");
            }
        }
        data["sign"] = Md5(string.Join("&", signParts));
        data["_us"] = JsonSerializer.Serialize(usData, JsonNoSpaces);
        data["_uk"] = Md5(origJson);

        return data;
    }

    // =========================================================================
    // API CALLS
    // =========================================================================

    public async Task<JsonNode?> ApiGet(string path, Dictionary<string, string>? ps = null)
    {
        var url = $"{BaseUrl}{path}";
        if (ps is { Count: > 0 })
        {
            var qs = string.Join("&", ps.Select(kv => $"{HttpUtility.UrlEncode(kv.Key)}={HttpUtility.UrlEncode(kv.Value)}"));
            url += "?" + qs;
        }

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("token", _config.Token);
        var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        try { return JsonNode.Parse(body); }
        catch { return null; }
    }

    public async Task<JsonNode?> ApiPost(string path, Dictionary<string, object?>? apiFields = null)
    {
        apiFields ??= new();
        if (!string.IsNullOrWhiteSpace(_config.UpUrl))
            apiFields["up_url"] = _config.UpUrl;

        var signed = SignPost(apiFields);
        var json = JsonSerializer.Serialize(signed, JsonNoSpaces);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}{path}")
        {
            Content = content
        };
        req.Headers.TryAddWithoutValidation("token", _config.Token);

        var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        try { return JsonNode.Parse(body); }
        catch { return null; }
    }

    // =========================================================================
    // BUSINESS METHODS
    // =========================================================================

    /// GET /index/store — 获取店铺配置，刷新 appkey
    public async Task<JsonNode?> GetStoreInfo()
    {
        return await ApiGet("/index/store", new()
        {
            ["store_id"] = "",
            ["price"] = "1"
        });
    }

    /// POST /prints/file_list — 文件列表
    public async Task<JsonNode?> FileList()
    {
        return await ApiPost("/prints/file_list");
    }

    /// POST /prints/file_add — 添加/更新文件
    public async Task<JsonNode?> FileAdd(Dictionary<string, object?> fields)
    {
        return await ApiPost("/prints/file_add", fields);
    }

    /// POST /prints/file_save — 保存文件设置（原生设置面板用的就是这个）
    public async Task<JsonNode?> FileSave(Dictionary<string, object?> fields)
    {
        return await ApiPost("/prints/file_save", fields);
    }

    /// POST /prints/file_del — 删除文件
    public async Task<JsonNode?> FileDel(int fileId)
    {
        return await ApiPost("/prints/file_del", new()
        {
            ["id"] = fileId
        });
    }

    /// POST /prints/file_del — 批量删除（逗号分隔的 ID 字符串）
    public async Task<JsonNode?> FileClear(string ids)
    {
        return await ApiPost("/prints/file_del", new()
        {
            ["id"] = ids
        });
    }

    /// POST /prints/file_save_pass — 加密文件解锁
    public async Task<JsonNode?> FileSavePass(Dictionary<string, object?> fields)
    {
        return await ApiPost("/prints/file_save_pass", fields);
    }

    /// POST /prints/to_pdf_check — PDF 转换状态
    public async Task<JsonNode?> ToPdfCheck(int fileId)
    {
        return await ApiPost("/prints/to_pdf_check", new()
        {
            ["id"] = fileId.ToString()
        });
    }

    /// POST /prints/get_price — 计价
    public async Task<JsonNode?> GetPrice()
    {
        return await ApiPost("/prints/get_price");
    }

    /// POST /prints/pay — 下单（预检/正式）
    public async Task<JsonNode?> CreateOrder(int printCount, int storeId, int isDelivery, int payWay, int getPriceVip)
    {
        return await ApiPost("/prints/pay", new()
        {
            ["print_count"] = printCount,
            ["store_id"] = storeId,
            ["is_delivery"] = isDelivery,
            ["pay_way"] = payWay,
            ["get_price_vip"] = getPriceVip
        });
    }

    // =========================================================================
    // FILE UPLOAD
    // =========================================================================

    /// 上传文件到 up_url 服务器。返回 { file_path, file_md5, file_size } 或 null。
    public async Task<JsonNode?> UploadFile(string filePath)
    {
        var fname = Path.GetFileName(filePath);
        var fsize = new FileInfo(filePath).Length;

        // 计算 MD5
        string fileMd5;
        await using (var fs = File.OpenRead(filePath))
        {
            fileMd5 = Convert.ToHexString(await MD5.HashDataAsync(fs)).ToLowerInvariant();
        }

        // Step A: 秒传验证
        var verifyResult = await VerifyMd5(fname, fsize, fileMd5);
        if (verifyResult != null)
            return verifyResult; // 秒传成功

        // Step B: 分块上传
        return await ChunkedUpload(filePath, fname, fsize);
    }

    private async Task<JsonNode?> VerifyMd5(string fname, long fsize, string fileMd5)
    {
        var url = $"{_config.UpUrl}/weapp/verify.php";
        var formData = new Dictionary<string, string>
        {
            ["store_id"] = _config.StoreId.ToString(),
            ["token"] = _config.Token,
            ["appkey"] = _config.Appkey,
            ["dir"] = "weapp",
            ["file_md5"] = fileMd5,
            ["file_size"] = fsize.ToString(),
            ["file_name"] = fname
        };

        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(formData!)
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");

        var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(body);
        if (json?["code"]?.GetValue<int>() == 0)
        {
            var file = json["data"]?["file"]?.AsArray();
            if (file != null && file.Count > 0)
            {
                var f = file[0]!;
                return new JsonObject
                {
                    ["file_path"] = f["file_path"]!.GetValue<string>(),
                    ["file_md5"] = fileMd5,
                    ["file_size"] = fsize,
                    ["file_hash"] = f["file_hash"]?.GetValue<string>() ?? "",
                    ["file_id"] = f["id"]?.GetValue<int>() ?? 0,
                    ["file_name"] = f["file_name"]?.GetValue<string>() ?? fname,
                    ["file_format"] = f["file_format"]?.GetValue<string>() ?? Path.GetExtension(fname).TrimStart('.')
                };
            }
        }
        return null;
    }

    private async Task<JsonNode?> ChunkedUpload(string filePath, string fname, long fsize)
    {
        const int blockSize = 2 * 1024 * 1024; // 2MB
        var totalBlocks = (int)Math.Max(1, (fsize + blockSize - 1) / blockSize);
        var uploadPath = "";

        await using var fs = File.OpenRead(filePath);
        for (var blockIdx = 1; blockIdx <= totalBlocks; blockIdx++)
        {
            var buffer = new byte[blockSize];
            var read = await fs.ReadAsync(buffer, 0, blockSize);
            var actualBuffer = read < blockSize ? buffer[..read] : buffer;

            var formFields = new Dictionary<string, string>
            {
                ["store_id"] = _config.StoreId.ToString(),
                ["token"] = _config.Token,
                ["appkey"] = _config.Appkey,
                ["filename"] = fname,
                ["dir"] = "weapp",
                ["block"] = blockIdx.ToString(),
                ["amount"] = totalBlocks.ToString(),
                ["path"] = uploadPath
            };

            using var mpContent = new MultipartFormDataContent();
            foreach (var (k, v) in formFields)
                mpContent.Add(new StringContent(v), k);

            var fileContent = new ByteArrayContent(actualBuffer);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            mpContent.Add(fileContent, "uploadFile", fname);

            var req = new HttpRequestMessage(HttpMethod.Post, $"{_config.UpUrl}/weapp/upload.php")
            {
                Content = mpContent
            };

            var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            var json = JsonNode.Parse(body);

            if (json?["code"]?.GetValue<int>() == 0)
            {
                uploadPath = json["data"]?["file_path"]?.GetValue<string>() ?? "";
                if (blockIdx >= totalBlocks)
                {
                    var f = json["data"]?["file"]?.AsArray();
                    var finfo = (f != null && f.Count > 0) ? f[0]! : null;
                    return new JsonObject
                    {
                        ["file_path"] = uploadPath,
                        ["file_size"] = fsize,
                        ["file_id"] = finfo?["id"]?.GetValue<int>() ?? 0,
                        ["file_name"] = finfo?["file_name"]?.GetValue<string>() ?? fname,
                        ["file_format"] = finfo?["file_format"]?.GetValue<string>() ?? Path.GetExtension(fname).TrimStart('.'),
                        ["file_md5"] = finfo?["file_md5"]?.GetValue<string>() ?? ""
                    };
                }
            }
            else
            {
                return null;
            }
        }
        return null;
    }
}
