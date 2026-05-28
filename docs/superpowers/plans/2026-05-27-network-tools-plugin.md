# Plugin.NetworkTools Implementation Plan

> **状态：已完成 (2026-05-27)** — 所有功能已实现

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a network access plugin providing HTTP request, async file download, download listing, and download cancellation tools.

**Architecture:** Two-component pattern (Global + Loop) referencing ScheduledTasks. Global component owns HttpClient lifecycle, background download execution, and WakeLoop dispatching. Loop component registers four ITool implementations and injects download-completion notifications via BuildPromptSection. A static DownloadStore singleton bridges the two components.

**Tech Stack:** .NET 8, C#, `System.Net.Http`, `System.Text.Json`, AgentLilara.PluginSDK (no third-party NuGet packages)

**Design spec:** `docs/superpowers/specs/2026-05-27-network-tools-plugin-design.md`

---

### Task 1: Project scaffold

**Files:**
- Create: `Plugins/Plugin.NetworkTools/Plugin.NetworkTools.csproj`

- [ ] **Step 1: Create project directory and .csproj**

```bash
mkdir -p Plugins/Plugin.NetworkTools
```

```xml
<!-- Plugins/Plugin.NetworkTools/Plugin.NetworkTools.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Plugin.NetworkTools</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\AgentLilara.PluginSDK\AgentLilara.PluginSDK.csproj">
      <Private>false</Private>
    </ProjectReference>
  </ItemGroup>

  <Target Name="CopyToHostPlugins" AfterTargets="Build">
    <Copy SourceFiles="$(OutputPath)Plugin.NetworkTools.dll"
          DestinationFolder="$(SolutionDir)AgentCoreProcessor\bin\$(Configuration)\net8.0\Plugins\"
          Condition="'$(SolutionDir)' != '' and '$(SolutionDir)' != '*Undefined*'" />
  </Target>

</Project>
```

- [ ] **Step 2: Add project to solution**

```bash
cd E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution
dotnet sln add Plugins/Plugin.NetworkTools/Plugin.NetworkTools.csproj --solution-folder Plugins
```

- [ ] **Step 3: Verify project structure**

```bash
dotnet build Plugins/Plugin.NetworkTools/
```

Expected: Build succeeds with 0 warnings (empty project, no .cs files yet).

- [ ] **Step 4: Commit scaffold**

```bash
git add Plugins/Plugin.NetworkTools/ AgentLilaraProjectSolution.sln
git commit -m "feat: scaffold Plugin.NetworkTools project"
```

---

### Task 2: DownloadTask data model

**Files:**
- Create: `Plugins/Plugin.NetworkTools/DownloadTask.cs`

- [ ] **Step 1: Write DownloadTask.cs**

```csharp
// Plugins/Plugin.NetworkTools/DownloadTask.cs
namespace Plugin.NetworkTools;

public enum DownloadStatus { Pending, Downloading, Completed, Failed, Cancelled }

public class DownloadTask
{
    public string Id { get; init; } = "";
    public string Url { get; init; } = "";
    public string SavePath { get; init; } = "";     // 完整磁盘路径
    public string RelativePath { get; init; } = "";  // Agent 指定的相对路径（通知用）
    public string LoopId { get; init; } = "";
    public DownloadStatus Status { get; set; } = DownloadStatus.Pending;
    public long BytesDownloaded { get; set; }
    public long? TotalBytes { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }
    public string? FileName { get; set; }            // 从URL或Content-Disposition提取
    public CancellationTokenSource? Cts { get; set; }

    public int ProgressPct => TotalBytes is > 0
        ? (int)(BytesDownloaded * 100 / TotalBytes.Value)
        : -1;

    public string? SpeedString
    {
        get
        {
            if (Status != DownloadStatus.Downloading || BytesDownloaded == 0)
                return null;
            var elapsed = (DateTime.UtcNow - StartedAt).TotalSeconds;
            if (elapsed < 0.5) return null;
            var bps = BytesDownloaded / elapsed;
            return bps switch
            {
                > 1_000_000 => $"{bps / 1_000_000:F1} MB/s",
                > 1_000 => $"{bps / 1_000:F1} KB/s",
                _ => $"{bps:F0} B/s"
            };
        }
    }

    public string SizeString => TotalBytes switch
    {
        > 1_000_000 => $"{TotalBytes / 1_000_000.0:F1} MB",
        > 1_000 => $"{TotalBytes / 1_000.0:F1} KB",
        _ => $"{TotalBytes} B"
    };
}
```

- [ ] **Step 2: Verify compilation**

```bash
dotnet build Plugins/Plugin.NetworkTools/
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add Plugins/Plugin.NetworkTools/DownloadTask.cs
git commit -m "feat: add DownloadTask data model"
```

---

### Task 3: SecurityConfig — configuration and validation

**Files:**
- Create: `Plugins/Plugin.NetworkTools/SecurityConfig.cs`

- [ ] **Step 1: Write SecurityConfig.cs**

```csharp
// Plugins/Plugin.NetworkTools/SecurityConfig.cs
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Plugin.NetworkTools;

public enum SecurityMode { Blacklist, Whitelist, None }

public class SecurityConfig
{
    public SecurityMode Mode { get; set; } = SecurityMode.Blacklist;
    public List<string> Domains { get; set; } = new();
    public bool BlockPrivateIps { get; set; } = true;

    public int DefaultTimeoutSeconds { get; set; } = 30;
    public int MaxResponseBodyBytes { get; set; } = 20480;
    public string UserAgent { get; set; } = "AgentLilara-NetworkTools/1.0";
    public int MaxRedirects { get; set; } = 5;

    public int MaxConcurrentDownloads { get; set; } = 3;
    public int ChunkSizeBytes { get; set; } = 8192;

    // ── Loading ──

    public static SecurityConfig Load(string configDir)
    {
        Directory.CreateDirectory(configDir);
        var path = Path.Combine(configDir, "NetworkTools.json");

        if (!File.Exists(path))
        {
            var defaults = new SecurityConfig();
            var json = JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SecurityConfig>(json) ?? new SecurityConfig();
        }
        catch
        {
            return new SecurityConfig();
        }
    }

    // ── Domain validation ──

    /// <summary>检查给定URL是否允许访问。返回null表示通过，否则返回拒绝原因。</summary>
    public string? ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "URL格式无效";

        if (uri.Scheme != "http" && uri.Scheme != "https")
            return "仅支持 http/https 协议";

        var host = uri.Host;

        // 域名检查
        switch (Mode)
        {
            case SecurityMode.Blacklist:
                if (Domains.Any(d => MatchDomain(host, d)))
                    return $"域名 {host} 在黑名单中";
                break;
            case SecurityMode.Whitelist:
                if (!Domains.Any(d => MatchDomain(host, d)))
                    return $"域名 {host} 不在白名单中";
                break;
        }

        return null; // 通过
    }

    /// <summary>检查IP是否为私有/内网地址。返回null表示通过（公网），否则返回拒绝原因。</summary>
    public string? ValidateIp(string host)
    {
        if (!BlockPrivateIps) return null;

        if (!IPAddress.TryParse(host, out var ip))
        {
            // 尝试DNS解析
            try
            {
                var addresses = Dns.GetHostAddresses(host);
                if (addresses.Length == 0)
                    return $"无法解析域名: {host}";
                ip = addresses[0];
            }
            catch
            {
                return $"DNS解析失败: {host}";
            }
        }

        if (IsPrivateIp(ip))
            return $"禁止访问内网地址: {ip}";

        return null;
    }

    /// <summary>检查重定向目标是否与原始host不同（需重新校验）。</summary>
    public bool HostChanged(string originalHost, string newUrl)
    {
        if (!Uri.TryCreate(newUrl, UriKind.Absolute, out var uri))
            return true;
        return !string.Equals(originalHost, uri.Host, StringComparison.OrdinalIgnoreCase);
    }

    // ── Private helpers ──

    private static bool MatchDomain(string host, string pattern)
    {
        if (pattern.StartsWith("*."))
        {
            var suffix = pattern[2..];
            return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && host.Length > suffix.Length;
        }
        return string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPrivateIp(IPAddress addr)
    {
        if (IPAddress.IsLoopback(addr)) return true;

        var bytes = addr.GetAddressBytes();

        if (addr.AddressFamily == AddressFamily.InterNetwork)
        {
            // 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // 169.254.0.0/16 (link-local)
            if (bytes[0] == 169 && bytes[1] == 254) return true;
        }
        else if (addr.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IPv6Loopback.Equals(addr)) return true;
            // fc00::/7
            if (bytes.Length >= 1 && bytes[0] >= 0xfc && bytes[0] <= 0xfd) return true;
        }

        return false;
    }
}
```

- [ ] **Step 2: Verify compilation**

```bash
dotnet build Plugins/Plugin.NetworkTools/
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add Plugins/Plugin.NetworkTools/SecurityConfig.cs
git commit -m "feat: add SecurityConfig with domain/IP validation"
```

---

### Task 4: DownloadStore — shared download registry

**Files:**
- Create: `Plugins/Plugin.NetworkTools/DownloadStore.cs`

- [ ] **Step 1: Write DownloadStore.cs**

```csharp
// Plugins/Plugin.NetworkTools/DownloadStore.cs
using System.Collections.Concurrent;

namespace Plugin.NetworkTools;

/// <summary>
/// 全局下载注册表（单例）。Global组件管理下载任务，Loop组件读取通知。
/// 通过 NetworkToolsNotifier 静态桥接实现 Global→Loop 唤醒通信。
/// </summary>
public class DownloadStore
{
    private readonly ConcurrentDictionary<string, DownloadTask> _tasks = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DownloadNotification>> _notifications = new();
    private int _activeCount;

    public int MaxConcurrent { get; set; } = 3;

    // ── Task management (called by Global component or tools) ──

    public bool TryAdd(DownloadTask task)
    {
        lock (_tasks)
        {
            if (_activeCount >= MaxConcurrent)
                return false;
            _activeCount++;
        }
        task.Status = DownloadStatus.Downloading;
        task.StartedAt = DateTime.UtcNow;
        return _tasks.TryAdd(task.Id, task);
    }

    public DownloadTask? Get(string id)
    {
        _tasks.TryGetValue(id, out var task);
        return task;
    }

    public List<DownloadTask> GetAll(string? filter, string? loopId)
    {
        var query = _tasks.Values.AsEnumerable();

        if (!string.IsNullOrEmpty(loopId))
            query = query.Where(t => t.LoopId == loopId);

        query = filter switch
        {
            "active" => query.Where(t => t.Status == DownloadStatus.Downloading || t.Status == DownloadStatus.Pending),
            "completed" => query.Where(t => t.Status == DownloadStatus.Completed),
            "failed" => query.Where(t => t.Status == DownloadStatus.Failed || t.Status == DownloadStatus.Cancelled),
            _ => query
        };

        return query.OrderByDescending(t => t.StartedAt).ToList();
    }

    public bool Cancel(string id)
    {
        if (!_tasks.TryGetValue(id, out var task))
            return false;

        if (task.Status is DownloadStatus.Completed or DownloadStatus.Cancelled)
            return false;

        task.Cts?.Cancel();
        task.Status = DownloadStatus.Cancelled;
        task.CompletedAt = DateTime.UtcNow;
        DecrementActive();

        EnqueueNotification(task, "cancelled", error: null);
        NetworkToolsNotifier.NotifyCompleted(task.LoopId);
        return true;
    }

    public void MarkCompleted(DownloadTask task, long totalBytes)
    {
        task.Status = DownloadStatus.Completed;
        task.BytesDownloaded = totalBytes;
        task.CompletedAt = DateTime.UtcNow;
        DecrementActive();

        EnqueueNotification(task, "completed", error: null);
        NetworkToolsNotifier.NotifyCompleted(task.LoopId);
    }

    public void MarkFailed(DownloadTask task, string error)
    {
        task.Status = DownloadStatus.Failed;
        task.Error = error;
        task.CompletedAt = DateTime.UtcNow;
        DecrementActive();

        // 删除部分下载的文件
        try { if (File.Exists(task.SavePath)) File.Delete(task.SavePath); }
        catch { /* best effort */ }

        EnqueueNotification(task, "failed", error: error);
        NetworkToolsNotifier.NotifyCompleted(task.LoopId);
    }

    // ── Notification drain (called by Loop component) ──

    public List<DownloadNotification> DrainNotifications(string loopId)
    {
        if (!_notifications.TryGetValue(loopId, out var queue))
            return new List<DownloadNotification>();

        var result = new List<DownloadNotification>();
        while (queue.TryDequeue(out var n))
            result.Add(n);
        return result;
    }

    public void Shutdown()
    {
        foreach (var task in _tasks.Values)
            task.Cts?.Cancel();
        _tasks.Clear();
        _activeCount = 0;
    }

    private void EnqueueNotification(DownloadTask task, string status, string? error)
    {
        var queue = _notifications.GetOrAdd(task.LoopId, _ => new ConcurrentQueue<DownloadNotification>());
        queue.Enqueue(new DownloadNotification
        {
            DownloadId = task.Id,
            LoopId = task.LoopId,
            FileName = task.FileName ?? Path.GetFileName(task.SavePath),
            RelativePath = task.RelativePath,
            Size = task.BytesDownloaded,
            Status = status,
            Error = error
        });
    }

    private void DecrementActive()
    {
        lock (_tasks) { _activeCount--; }
    }
}

public class DownloadNotification
{
    public string DownloadId { get; init; } = "";
    public string LoopId { get; init; } = "";
    public string FileName { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public long Size { get; init; }
    public string Status { get; init; } = "";
    public string? Error { get; init; }
}

/// <summary>
/// 静态桥接：Loop组件→Global组件唤醒信号。
/// </summary>
public static class NetworkToolsNotifier
{
    /// <summary>Global组件设置此回调：收到通知时调用WakeLoop。</summary>
    public static Action<string>? OnDownloadCompleted { get; set; }

    public static void NotifyCompleted(string loopId)
        => OnDownloadCompleted?.Invoke(loopId);
}
```

- [ ] **Step 2: Verify compilation**

```bash
dotnet build Plugins/Plugin.NetworkTools/
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add Plugins/Plugin.NetworkTools/DownloadStore.cs
git commit -m "feat: add DownloadStore with notification queue"
```

---

### Task 5: HttpRequestTool

**Files:**
- Create: `Plugins/Plugin.NetworkTools/HttpRequestTool.cs`

- [ ] **Step 1: Write HttpRequestTool.cs**

```csharp
// Plugins/Plugin.NetworkTools/HttpRequestTool.cs
using System.Net;
using System.Text;
using System.Text.Json;
using AgentLilara.PluginSDK;

namespace Plugin.NetworkTools;

[ToolMeta(Group = "network", ContinueLoop = true, CapabilitySummary = "发送HTTP请求获取网页/API响应")]
public class HttpRequestTool : ITool
{
    private readonly HttpClient _http;
    private readonly SecurityConfig _security;

    public string Name => "http_request";
    public string Description => "发送HTTP请求并返回响应文本。支持GET/POST/PUT/DELETE/PATCH。"
        + "响应体超过限制时会截断，大文件请用download_file。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("url", "请求URL", 0),
        new("method", "HTTP方法，默认GET（可选）", 1, isRequired: false),
        new("headers", "JSON格式请求头，如{\"Authorization\":\"Bearer xxx\"}（可选）", 2, isRequired: false),
        new("body", "请求体文本（可选）", 3, isRequired: false),
        new("timeout", "超时秒数，默认取配置值（可选）", 4, isRequired: false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(_security.DefaultTimeoutSeconds + 5);

    public HttpRequestTool(HttpClient http, SecurityConfig security)
    {
        _http = http;
        _security = security;
    }

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var url = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        var method = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim().ToUpper() : "GET";
        var headersJson = resolvedInputs.Count > 2 ? resolvedInputs[2].Trim() : "";
        var body = resolvedInputs.Count > 3 ? resolvedInputs[3] : "";
        var timeoutStr = resolvedInputs.Count > 4 ? resolvedInputs[4].Trim() : "";

        if (string.IsNullOrEmpty(url))
            return Fail("url不能为空");

        // 安全校验
        var reject = _security.ValidateUrl(url);
        if (reject != null) return Fail(reject);

        var host = new Uri(url).Host;
        reject = _security.ValidateIp(host);
        if (reject != null) return Fail(reject);

        // 构建请求
        var validMethods = new HashSet<string> { "GET", "POST", "PUT", "DELETE", "PATCH" };
        if (!validMethods.Contains(method))
            return Fail($"不支持的HTTP方法: {method}，支持: GET/POST/PUT/DELETE/PATCH");

        using var request = new HttpRequestMessage(new HttpMethod(method), url);

        if (!string.IsNullOrEmpty(body) && method is "POST" or "PUT" or "PATCH")
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        if (!string.IsNullOrEmpty(headersJson))
        {
            try
            {
                var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
                if (headers != null)
                {
                    foreach (var (key, value) in headers)
                        request.Headers.TryAddWithoutValidation(key, value);
                }
            }
            catch { return Fail("headers格式无效，需要JSON对象"); }
        }

        // 超时
        var timeoutSeconds = _security.DefaultTimeoutSeconds;
        if (int.TryParse(timeoutStr, out var t) && t > 0)
            timeoutSeconds = Math.Min(t, 120);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            using var response = await _http.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);

            var respHeaders = new Dictionary<string, string>();
            foreach (var h in response.Headers)
                respHeaders[h.Key] = string.Join(", ", h.Value);
            foreach (var h in response.Content.Headers)
                respHeaders[h.Key] = string.Join(", ", h.Value);

            var rawBytes = await response.Content.ReadAsByteArrayAsync(linkedCts.Token);
            var maxBytes = _security.MaxResponseBodyBytes;
            var truncated = rawBytes.Length > maxBytes;
            var display = Encoding.UTF8.GetString(rawBytes, 0, truncated ? maxBytes : rawBytes.Length);

            if (truncated)
                display += $"\n\n[已截断: {rawBytes.Length} bytes → {maxBytes} bytes，请用download_file获取完整内容]";

            var result = new Dictionary<string, object>
            {
                ["status_code"] = (int)response.StatusCode,
                ["headers"] = respHeaders,
                ["body"] = display,
                ["size"] = rawBytes.Length,
                ["truncated"] = truncated
            };

            return Ok(JsonSerializer.Serialize(result));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail($"请求超时（{timeoutSeconds}s）");
        }
        catch (HttpRequestException ex)
        {
            return Fail($"请求失败: {ex.Message}");
        }
    }

    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
}
```

- [ ] **Step 2: Verify compilation**

```bash
dotnet build Plugins/Plugin.NetworkTools/
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add Plugins/Plugin.NetworkTools/HttpRequestTool.cs
git commit -m "feat: add HttpRequestTool"
```

---

### Task 6: DownloadFileTool

**Files:**
- Create: `Plugins/Plugin.NetworkTools/DownloadFileTool.cs`

- [ ] **Step 1: Write DownloadFileTool.cs**

```csharp
// Plugins/Plugin.NetworkTools/DownloadFileTool.cs
using System.Text.Json;
using AgentLilara.PluginSDK;

namespace Plugin.NetworkTools;

[ToolMeta(Group = "network", ContinueLoop = true, CapabilitySummary = "异步下载文件到Workspace")]
public class DownloadFileTool : ITool
{
    private readonly HttpClient _http;
    private readonly SecurityConfig _security;
    private readonly DownloadStore _store;
    private readonly string _workspaceDir;
    private readonly string _loopId;

    public string Name => "download_file";
    public string Description => "启动异步文件下载，立即返回download_id。"
        + "文件保存到Agent指定的Workspace相对路径。下载完成后会通知Agent。"
        + "用list_downloads查看进度，cancel_download取消下载。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("url", "下载URL", 0),
        new("save_path", "保存路径，相对于Workspace目录", 1),
        new("headers", "JSON格式请求头（可选）", 2, isRequired: false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public DownloadFileTool(HttpClient http, SecurityConfig security, DownloadStore store,
        string workspaceDir, string loopId)
    {
        _http = http;
        _security = security;
        _store = store;
        _workspaceDir = workspaceDir;
        _loopId = loopId;
    }

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var url = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        var savePath = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";
        var headersJson = resolvedInputs.Count > 2 ? resolvedInputs[2].Trim() : "";

        if (string.IsNullOrEmpty(url))
            return Fail("url不能为空");
        if (string.IsNullOrEmpty(savePath))
            return Fail("save_path不能为空");

        // 安全校验
        var reject = _security.ValidateUrl(url);
        if (reject != null) return Fail(reject);

        var host = new Uri(url).Host;
        reject = _security.ValidateIp(host);
        if (reject != null) return Fail(reject);

        // 解析保存路径
        var fullPath = Path.GetFullPath(Path.Combine(_workspaceDir, savePath));
        if (!fullPath.StartsWith(_workspaceDir, StringComparison.OrdinalIgnoreCase))
            return Fail("路径不合法：不能访问Workspace目录之外");

        // 提取文件名
        var fileName = Path.GetFileName(savePath);
        if (string.IsNullOrEmpty(fileName))
            fileName = Path.GetFileName(new Uri(url).AbsolutePath);
        if (string.IsNullOrEmpty(fileName))
            fileName = "download";

        // 解析headers
        Dictionary<string, string>? headers = null;
        if (!string.IsNullOrEmpty(headersJson))
        {
            try { headers = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson); }
            catch { return Fail("headers格式无效"); }
        }

        // 注册下载任务
        var cts = new CancellationTokenSource();
        var task = new DownloadTask
        {
            Id = Guid.NewGuid().ToString()[..8],
            Url = url,
            SavePath = fullPath,
            RelativePath = savePath,
            LoopId = _loopId,
            FileName = fileName,
            Cts = cts
        };

        if (!_store.TryAdd(task))
        {
            cts.Dispose();
            return Fail($"已达到最大并发下载数({_store.MaxConcurrent})");
        }

        // 后台启动下载
        _ = DownloadAsync(task, headers, _http, _store, _security.ChunkSizeBytes);

        return Task.FromResult(Ok(JsonSerializer.Serialize(new
        {
            download_id = task.Id,
            status = "started",
            save_path = savePath,
            file_name = fileName
        })));
    }

    /// <summary>后台下载循环，不阻塞工具返回。</summary>
    private static async Task DownloadAsync(DownloadTask task, Dictionary<string, string>? headers,
        HttpClient http, DownloadStore store, int chunkSize)
    {
        try
        {
            var dir = Path.GetDirectoryName(task.SavePath)!;
            Directory.CreateDirectory(dir);

            using var request = new HttpRequestMessage(HttpMethod.Get, task.Url);
            if (headers != null)
            {
                foreach (var (key, value) in headers)
                    request.Headers.TryAddWithoutValidation(key, value);
            }

            using var response = await http.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, task.Cts!.Token);
            response.EnsureSuccessStatusCode();

            task.TotalBytes = response.Content.Headers.ContentLength;

            // 从Content-Disposition提取文件名（如果URL没有给出有意义的名字）
            var cd = response.Content.Headers.ContentDisposition;
            if (cd?.FileName != null)
                task.FileName = cd.FileName.Trim('"');

            await using var stream = await response.Content.ReadAsStreamAsync(task.Cts.Token);
            await using var fileStream = new FileStream(task.SavePath, FileMode.Create,
                FileAccess.Write, FileShare.None, chunkSize, useAsync: true);

            var buffer = new byte[chunkSize];
            int bytesRead;
            long total = 0;

            while ((bytesRead = await stream.ReadAsync(buffer, task.Cts.Token)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), task.Cts.Token);
                total += bytesRead;
                task.BytesDownloaded = total;
            }

            store.MarkCompleted(task, total);
        }
        catch (OperationCanceledException)
        {
            // 用户调用cancel_download，已在store.Cancel中处理
        }
        catch (Exception ex)
        {
            store.MarkFailed(task, ex.Message);
        }
        finally
        {
            task.Cts?.Dispose();
            task.Cts = null;
        }
    }

    private static Task<ToolResult> Fail(string err) =>
        Task.FromResult(new ToolResult { Status = "failed", Error = err });
    private static Task<ToolResult> Ok(string data) =>
        Task.FromResult(new ToolResult { Status = "success", Data = data });
}
```

- [ ] **Step 2: Verify compilation**

```bash
dotnet build Plugins/Plugin.NetworkTools/
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add Plugins/Plugin.NetworkTools/DownloadFileTool.cs
git commit -m "feat: add DownloadFileTool with async background download"
```

---

### Task 7: ListDownloadsTool

**Files:**
- Create: `Plugins/Plugin.NetworkTools/ListDownloadsTool.cs`

- [ ] **Step 1: Write ListDownloadsTool.cs**

```csharp
// Plugins/Plugin.NetworkTools/ListDownloadsTool.cs
using System.Text;
using System.Text.Json;
using AgentLilara.PluginSDK;

namespace Plugin.NetworkTools;

[ToolMeta(Group = "network", ContinueLoop = true)]
public class ListDownloadsTool : ITool
{
    private readonly DownloadStore _store;
    private readonly string _loopId;

    public string Name => "list_downloads";
    public string Description => "查看下载任务列表。可按状态筛选，默认仅显示本循环发起的下载。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("filter", "筛选: all(默认)/active/completed/failed（可选）", 0, isRequired: false),
        new("loop_only", "仅本循环的下载，默认true（可选）", 1, isRequired: false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public ListDownloadsTool(DownloadStore store, string loopId)
    {
        _store = store;
        _loopId = loopId;
    }

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var filter = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim().ToLower() : "all";
        var loopOnlyStr = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim().ToLower() : "true";
        var loopOnly = loopOnlyStr != "false";

        if (filter is not ("all" or "active" or "completed" or "failed"))
            filter = "all";

        var tasks = _store.GetAll(filter, loopOnly ? _loopId : null);

        if (tasks.Count == 0)
            return Ok("没有符合条件的下载任务");

        var sb = new StringBuilder();
        sb.AppendLine($"[下载任务] 共{ tasks.Count }个");

        foreach (var t in tasks)
        {
            var statusIcon = t.Status switch
            {
                DownloadStatus.Downloading => "↓",
                DownloadStatus.Completed => "✓",
                DownloadStatus.Failed => "✗",
                DownloadStatus.Cancelled => "⊘",
                _ => "·"
            };
            var progress = t.ProgressPct >= 0 ? $" {t.ProgressPct}%" : "";
            var speed = t.SpeedString != null ? $" {t.SpeedString}" : "";
            var size = t.TotalBytes.HasValue ? t.SizeString : "?";

            sb.Append($"  {statusIcon} [{t.Id}] {t.FileName}");
            if (t.Status == DownloadStatus.Downloading)
                sb.Append($" ({size}{progress}{speed})");
            else if (t.Status == DownloadStatus.Completed)
                sb.Append($" ({size}) → {t.RelativePath}");
            else if (t.Status == DownloadStatus.Failed)
                sb.Append($" ({t.Error})");

            sb.AppendLine();
        }

        return Ok(sb.ToString().TrimEnd());
    }

    private static Task<ToolResult> Ok(string data) =>
        Task.FromResult(new ToolResult { Status = "success", Data = data });
}
```

- [ ] **Step 2: Verify compilation**

```bash
dotnet build Plugins/Plugin.NetworkTools/
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add Plugins/Plugin.NetworkTools/ListDownloadsTool.cs
git commit -m "feat: add ListDownloadsTool"
```

---

### Task 8: CancelDownloadTool

**Files:**
- Create: `Plugins/Plugin.NetworkTools/CancelDownloadTool.cs`

- [ ] **Step 1: Write CancelDownloadTool.cs**

```csharp
// Plugins/Plugin.NetworkTools/CancelDownloadTool.cs
using AgentLilara.PluginSDK;

namespace Plugin.NetworkTools;

[ToolMeta(Group = "network", ContinueLoop = true)]
public class CancelDownloadTool : ITool
{
    private readonly DownloadStore _store;

    public string Name => "cancel_download";
    public string Description => "取消指定下载任务，删除已写入的部分文件。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("download_id", "下载ID（由download_file返回）", 0)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public CancelDownloadTool(DownloadStore store)
    {
        _store = store;
    }

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var id = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";

        if (string.IsNullOrEmpty(id))
            return Fail("download_id不能为空");

        var task = _store.Get(id);
        if (task == null)
            return Fail($"未找到下载任务: {id}（可能已完成或不存在）");

        if (task.Status == DownloadStatus.Completed)
            return Fail($"下载已完成，无法取消: {id}");

        var cancelled = _store.Cancel(id);
        return cancelled
            ? Ok($"已取消下载: {task.FileName}")
            : Fail($"取消失败: {id}");
    }

    private static Task<ToolResult> Fail(string err) =>
        Task.FromResult(new ToolResult { Status = "failed", Error = err });
    private static Task<ToolResult> Ok(string data) =>
        Task.FromResult(new ToolResult { Status = "success", Data = data });
}
```

- [ ] **Step 2: Verify compilation**

```bash
dotnet build Plugins/Plugin.NetworkTools/
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add Plugins/Plugin.NetworkTools/CancelDownloadTool.cs
git commit -m "feat: add CancelDownloadTool"
```

---

### Task 9: NetworkToolsGlobalComponent

**Files:**
- Create: `Plugins/Plugin.NetworkTools/NetworkToolsGlobalComponent.cs`

- [ ] **Step 1: Write NetworkToolsGlobalComponent.cs**

```csharp
// Plugins/Plugin.NetworkTools/NetworkToolsGlobalComponent.cs
using System.Net;
using AgentLilara.PluginSDK;

namespace Plugin.NetworkTools;

[Component(Name = "network-tools-global", Scope = ComponentScope.Global)]
public class NetworkToolsGlobalComponent : GlobalComponentBase
{
    private IGlobalComponentContext _ctx = null!;
    private SecurityConfig _security = null!;
    private HttpClient _http = null!;
    private DownloadStore? _store;

    public override ComponentMeta Meta => new()
    {
        Name = "network-tools-global",
        Description = "网络访问全局组件：HttpClient管理 + 后台下载",
        DefaultEnabled = true,
        PromptPriority = 99
    };

    public override IEnumerable<ITool> Tools => Array.Empty<ITool>();

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        _ctx = context;

        // 加载安全配置
        var configDir = Path.Combine(context.Storage.GlobalDirectory, "..");
        _security = SecurityConfig.Load(Path.GetFullPath(configDir));

        // 初始化 DownloadStore 单例
        _store = new DownloadStore { MaxConcurrent = _security.MaxConcurrentDownloads };

        // 初始化 HttpClient（不跟随重定向，手动控制以重新校验host）
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(_security.DefaultTimeoutSeconds)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(_security.UserAgent);

        // 注册唤醒回调：Global收到下载完成信号 → WakeLoop
        NetworkToolsNotifier.OnDownloadCompleted = loopId =>
        {
            _ctx.WakeLoop(loopId);
        };

        // 暴露静态引用供Loop组件工具使用
        NetworkToolsAccessor.Configure(_http, _security, _store);

        return Task.CompletedTask;
    }

    public override Task OnShutdownAsync(ShutdownReason reason)
    {
        NetworkToolsNotifier.OnDownloadCompleted = null;
        _store?.Shutdown();
        _http?.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>
/// 静态访问器：Global组件初始化后设置，Loop组件工具通过此访问共享资源。
/// </summary>
public static class NetworkToolsAccessor
{
    public static HttpClient? HttpClient { get; private set; }
    public static SecurityConfig? Security { get; private set; }
    public static DownloadStore? Store { get; private set; }

    public static void Configure(HttpClient http, SecurityConfig security, DownloadStore store)
    {
        HttpClient = http;
        Security = security;
        Store = store;
    }
}
```

- [ ] **Step 2: Verify compilation**

```bash
dotnet build Plugins/Plugin.NetworkTools/
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add Plugins/Plugin.NetworkTools/NetworkToolsGlobalComponent.cs
git commit -m "feat: add NetworkToolsGlobalComponent"
```

---

### Task 10: NetworkToolsLoopComponent

**Files:**
- Create: `Plugins/Plugin.NetworkTools/NetworkToolsLoopComponent.cs`

- [ ] **Step 1: Write NetworkToolsLoopComponent.cs**

```csharp
// Plugins/Plugin.NetworkTools/NetworkToolsLoopComponent.cs
using System.Text;
using AgentLilara.PluginSDK;

namespace Plugin.NetworkTools;

[Component(Name = "network-tools", Scope = ComponentScope.Loop)]
[LoopApplicability(Channel = Applicability.Enabled, System = Applicability.Enabled,
    Review = Applicability.Disabled, SubAgent = Applicability.Enabled)]
public class NetworkToolsLoopComponent : LoopComponentBase
{
    private ILoopComponentContext _ctx = null!;
    private string _workspaceDir = "";
    private string _loopId = "";
    private List<DownloadNotification> _pendingNotifications = new();

    private HttpRequestTool? _httpRequest;
    private DownloadFileTool? _downloadFile;
    private ListDownloadsTool? _listDownloads;
    private CancelDownloadTool? _cancelDownload;

    public override ComponentMeta Meta => new()
    {
        Name = "network-tools",
        Description = "网络访问：HTTP请求、文件下载",
        DefaultEnabled = true,
        PromptPriority = 40
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_httpRequest != null) yield return _httpRequest;
            if (_downloadFile != null) yield return _downloadFile;
            if (_listDownloads != null) yield return _listDownloads;
            if (_cancelDownload != null) yield return _cancelDownload;
        }
    }

    public override Task OnInitAsync(ILoopComponentContext context, InitReason reason)
    {
        _ctx = context;
        _loopId = context.LoopId;

        // Workspace路径：从插件存储目录推算
        _workspaceDir = Path.GetFullPath(Path.Combine(context.Storage.InstanceDirectory,
            "..", "..", "..", "Workspace"));
        Directory.CreateDirectory(_workspaceDir);

        var http = NetworkToolsAccessor.HttpClient
            ?? throw new InvalidOperationException("NetworkToolsGlobalComponent 未初始化");
        var security = NetworkToolsAccessor.Security
            ?? throw new InvalidOperationException("SecurityConfig 未加载");
        var store = NetworkToolsAccessor.Store
            ?? throw new InvalidOperationException("DownloadStore 未创建");

        _httpRequest = new HttpRequestTool(http, security);
        _downloadFile = new DownloadFileTool(http, security, store, _workspaceDir, _loopId);
        _listDownloads = new ListDownloadsTool(store, _loopId);
        _cancelDownload = new CancelDownloadTool(store);

        return Task.CompletedTask;
    }

    public override Task OnBeforeInvokeAsync()
    {
        var store = NetworkToolsAccessor.Store;
        if (store != null)
        {
            var notifications = store.DrainNotifications(_loopId);
            _pendingNotifications.AddRange(notifications);
        }
        return Task.CompletedTask;
    }

    public override string? BuildPromptSection()
    {
        if (_pendingNotifications.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("[网络下载通知]");

        foreach (var n in _pendingNotifications)
        {
            if (n.Status == "completed")
                sb.AppendLine($"- 下载完成: {n.FileName} ({FormatSize(n.Size)}) → {n.RelativePath}");
            else if (n.Status == "failed")
                sb.AppendLine($"- 下载失败: {n.FileName}: {n.Error}");
        }

        _pendingNotifications.Clear();
        return sb.ToString().TrimEnd();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        > 1_000_000 => $"{bytes / 1_000_000.0:F1} MB",
        > 1_000 => $"{bytes / 1_000.0:F1} KB",
        _ => $"{bytes} B"
    };
}
```

- [ ] **Step 2: Verify compilation**

```bash
dotnet build Plugins/Plugin.NetworkTools/
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add Plugins/Plugin.NetworkTools/NetworkToolsLoopComponent.cs
git commit -m "feat: add NetworkToolsLoopComponent with notification injection"
```

---

### Task 11: Full solution build and verification

- [ ] **Step 1: Clean and rebuild entire solution**

```bash
dotnet clean AgentLilaraProjectSolution.sln
dotnet build AgentLilaraProjectSolution.sln
```

Expected: Build succeeds with 0 errors.

- [ ] **Step 2: Verify DLL is copied to Plugins directory**

```bash
ls AgentCoreProcessor/bin/Debug/net8.0/Plugins/Plugin.NetworkTools.dll
```

Expected: File exists.

- [ ] **Step 3: Dry-run the host to check plugin loading**

```bash
dotnet run --project AgentCoreProcessor -- --help
```

Expected: Application starts without plugin-loading errors (check for "network-tools" in plugin list output if visible).

- [ ] **Step 4: Final commit**

```bash
git add -A
git commit -m "feat: complete Plugin.NetworkTools implementation

Four tools: http_request, download_file, list_downloads, cancel_download
Global component: HttpClient lifecycle, background downloads, WakeLoop
Loop component: tool registration, download-completion notification injection
Security: blacklist/whitelist modes, private IP blocking, redirect re-validation"
```

---

### Verification Checklist (post-build)

Run the host in `--test` mode and verify:

1. Plugin appears in WebUI `/p/plugins` as "network-tools" (enabled) and "network-tools-global" (enabled)
2. `Storage/Plugin/NetworkTools.json` auto-generated with defaults on first load
3. `http_request("https://httpbin.org/get")` returns JSON response
4. `http_request("http://127.0.0.1/")` returns "禁止访问内网地址"
5. `download_file("https://example.com/file.zip", "downloads/test.zip")` returns download_id
6. `list_downloads` shows the download with进度
7. `cancel_download(download_id)` cancels running download
8. Download completion injects notification into prompt section
