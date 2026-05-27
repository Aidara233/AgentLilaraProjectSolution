# Plugin.NetworkTools — 网络访问插件

## Summary

为 Agent 提供基础 HTTP 网络访问能力：同步请求、异步文件下载、下载进度管理。参照 ScheduledTasks 的双组件（Global+Loop）模式，下载完成后通过 WakeLoop + BuildPromptSection 通知 Agent。

## 工具清单

| 工具 | 说明 |
|---|---|
| `http_request` | 同步 HTTP 请求（GET/POST/PUT/DELETE/PATCH），返回文本，截断 20KB |
| `download_file` | 启动异步下载，立即返回 download_id，后台流式写磁盘 |
| `list_downloads` | 查看下载任务列表（可按状态筛选），含进度/速度/大小 |
| `cancel_download` | 取消指定下载，删除已写入的部分文件 |

## 组件结构

### NetworkToolsLoopComponent（Loop 组件）

- **适用循环**：Channel / System / SubAgent（默认全部启用）
- **职责**：注册四个工具 + 在 `OnBeforeInvokeAsync` 检查已完成下载 + `BuildPromptSection` 注入通知
- **每循环实例**：跟踪本循环发起的下载（通过 loopId），过滤出属于本循环的完成通知

生命周期：
```
OnInitAsync → 创建 DownloadStore(基于loopId) → 注册四个工具
OnBeforeInvokeAsync → DrainCompletedDownloads(loopId) → 转存到 _pendingNotifications
BuildPromptSection → 返回 notifications 文本 → 清空 _pendingNotifications
```

### NetworkToolsGlobalComponent（Global 组件）

- **职责**：管理后台下载任务、HttpClient 生命周期、安全配置加载
- **单实例**：`ConcurrentDictionary<downloadId, DownloadTask>` 存所有进行中的下载
- **后台下载**：每个 download 一个 `Task.Run`，流式写磁盘，完成时 `WakeLoop(loopId)`

生命周期：
```
OnInitAsync → 加载安全配置 → 初始化 HttpClient → 启动后台任务监控
OnShutdownAsync → 取消所有进行中的下载 → 清理临时文件 → 释放 HttpClient
```

## 工具详细定义

### http_request

```
参数:
  url        string  请求URL（必填）
  method     string  HTTP方法，默认 GET（可选）
  headers    string  JSON格式的请求头，如 {"Authorization":"Bearer xxx"}（可选）
  body       string  请求体文本（可选）
  timeout    number  超时秒数，默认取配置值（可选）

返回:
  status     HTTP状态码
  headers    响应头（JSON）
  body       响应体文本（截断500KB）
  size       原始响应体字节数
  truncated  是否被截断

超时或错误:
  status: "failed", error: 具体错误描述
```

### download_file

```
参数:
  url        string  下载URL（必填）
  save_path  string  保存路径，相对于 Workspace（必填）
  headers    string  JSON格式请求头（可选）

返回:
  download_id  唯一标识（GUID前8位）
  status        "started"
  message       提示信息（含预估大小如有Content-Length）

流程:
  1. 安全校验（域名 + 内网）
  2. HEAD请求获取Content-Length（可选，失败不影响）
  3. 注册DownloadTask→ConcurrentDictionary
  4. 返回download_id
  5. 后台Task.Run: 流式读→流式写→完成时WakeLoop

完成后通知格式:
  "[下载完成] {filename} ({size}) → {save_path}"
失败通知格式:
  "[下载失败] {filename}: {error}"
```

### list_downloads

```
参数:
  filter     string  筛选: "all"(默认) / "active" / "completed" / "failed"（可选）
  loop_only  bool    仅显示本循环的下载，默认 true（可选）

返回:
  downloads  数组，每项: { id, url, save_path, status, progress_pct, speed, total_size, error }
```

### cancel_download

```
参数:
  download_id  string  下载ID（必填）

返回:
  status   "cancelled" / "not_found" / "already_completed"
  message  详细信息

取消时删除已写入的部分文件。
```

## 安全模型

### 配置文件：`Storage/Plugin/NetworkTools.json`

```json
{
  "security": {
    "mode": "blacklist",
    "domains": [],
    "block_private_ips": true
  },
  "http": {
    "default_timeout_seconds": 30,
    "max_response_body_bytes": 20480,
    "user_agent": "AgentLilara-NetworkTools/1.0",
    "max_redirects": 5
  },
  "download": {
    "max_concurrent_downloads": 3,
    "chunk_size_bytes": 8192
  }
}
```

### 校验流程（每次请求前）

```
1. mode=none → 跳过域名检查
2. mode=blacklist → 提取URL host → 匹配domains列表 → 命中则拒绝
3. mode=whitelist → 提取URL host → 不在domains列表中 → 拒绝
4. block_private_ips=true → DNS解析 → 检查IP是否在私有/回环范围
5. 重定向到不同host时重新校验步骤2-4
```

### 私有IP范围

- `127.0.0.0/8`（回环）
- `10.0.0.0/8`（A类私有）
- `172.16.0.0/12`（B类私有）
- `192.168.0.0/16`（C类私有）
- `169.254.0.0/16`（链路本地）
- `::1`（IPv6回环）
- `fc00::/7`（IPv6唯一本地）

## 下载任务数据模型

```csharp
class DownloadTask
{
    string Id;           // GUID前8位
    string Url;
    string SavePath;     // 完整路径（Workspace + 相对路径）
    string LoopId;       // 发起下载的循环ID
    DownloadStatus Status; // Pending/Downloading/Completed/Failed/Cancelled
    long BytesDownloaded;
    long? TotalBytes;    // Content-Length，可能为null
    DateTime StartedAt;
    DateTime? CompletedAt;
    string? Error;
    string? FileName;    // 从URL或Content-Disposition提取
    CancellationTokenSource? Cts;
    int ProgressPct => TotalBytes > 0 ? (int)(BytesDownloaded * 100 / TotalBytes.Value) : -1;
    string? SpeedString; // 计算得出，如"1.2 MB/s"
}
```

## 错误处理

| 场景 | 处理 |
|---|---|
| URL格式无效 | 立即返回 failed |
| DNS解析失败 | 立即返回 failed |
| 域名未通过安全校验 | 立即返回 failed，附拒绝原因 |
| 连接超时 | 按配置超时，返回 failed |
| 下载中途断开 | 标记 Failed，删除部分文件，注入失败通知 |
| 磁盘空间不足 | 标记 Failed，删除部分文件 |
| 目标目录不存在 | download_file 自动创建父目录 |
| 最多并发下载数超限 | download_file 返回 failed，"已达到最大并发数(N)" |
| Shutdown | Global.OnShutdownAsync 取消所有下载，清理临时文件 |

## 需要的前置工作

无。纯新增插件，不修改现有代码。

## 文件清单

```
Plugins/Plugin.NetworkTools/
├── Plugin.NetworkTools.csproj
├── NetworkToolsGlobalComponent.cs   # Global组件：后台下载 + HttpClient管理
├── NetworkToolsLoopComponent.cs     # Loop组件：工具注册 + 通知注入
├── DownloadTask.cs                  # 下载任务模型
├── DownloadStore.cs                 # ConcurrentDictionary管理 + 持久化状态
├── SecurityConfig.cs                # 安全配置模型 + 加载/校验逻辑
├── HttpRequestTool.cs               # http_request工具
├── DownloadFileTool.cs              # download_file工具
├── ListDownloadsTool.cs             # list_downloads工具
└── CancelDownloadTool.cs            # cancel_download工具
```

## 不与现有模块冲突

- **不修改** AgentLilara.PluginSDK（现有接口足够）
- **不修改** 任何现有插件或引擎
- **不引入** 第三方 NuGet 依赖（仅用 `System.Net.Http` + `System.Text.Json`）
- 配置独立存放，不触碰现有配置文件
