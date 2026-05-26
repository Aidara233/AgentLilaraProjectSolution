# 模块10审计报告：适配器+基础设施 (Adapters & Infra)

审计时间：2026-05-26
文件数：~15 + 17个Command | 核心实现 ~3,200 行（含 OneBotAdapter 453行 + ImageStorage 490行 + Program.cs 427行）

---

## 发现问题

### 🔴 BUG — 中度

**1. FileAdapter.ParseJsonMessage 中用 .GetAwaiter().GetResult() 同步阻塞异步调用** (`FileAdapter.cs:210`)
```csharp
var (localPath, imgHash) = ImageStorage.CopyToStorageAsync(fa.Path).GetAwaiter().GetResult();
```
`ParseJsonMessage` 是同步方法（被同步的 `ProcessInputFiles` 调用），却用 `.GetAwaiter().GetResult()` 调用异步的 `CopyToStorageAsync`（含文件 I/O + DB 写入）。虽然不会死锁（无 SynchronizationContext），但阻塞线程池线程。输入文件中有大图时，FileAdapter 轮询循环被阻塞。

**2. AdapterManager.LoadFromConfig + MigrateLegacyConfig 裸 catch 吞异常** (`AdapterManager.cs:53-56, 82-84`) ✅ 已修复 2026-05-26
```csharp
// LoadFromConfig
try { ... RegisterAdapter(...); }
catch (Exception) { }

// MigrateLegacyConfig
catch (Exception) { }
```
配置文件损坏/格式错误时静默跳过。启动后发现适配器没加载，无任何日志说明是哪个文件出了问题。这在生产环境中是诊断噩梦——管理员改了 JSON 格式不小心写错，适配器就消失了。

**3. McpServerManager.ConnectAllAsync 裸 catch 吞异常** (`McpServerManager.cs:58-60`) ✅ 已修复 2026-05-26
```csharp
catch (Exception) { }
```
MCP 服务器连接失败（网络不通/命令找不到/认证失败）完全静默。Admin 开启 MCP 后看不到任何错误，工具列表为空，不知道是配置问题还是服务器问题。

### 🟡 BUG — 轻度

**4. OneBotAdapter.ReloadConfigAsync 检测到连接变更但不触发重连** (`OneBotAdapter.cs:140-152`)
```csharp
bool connectionChanged = newConfig.WsUrl != config.WsUrl || newConfig.Token != config.Token;
config = newConfig;
return Task.FromResult(connectionChanged);  // 返回 true 但不断开旧连接
```
检测到 WsUrl/Token 变更 → 返回 `true`，但 `CallApiAsync` 仍用旧的 `ws` 实例（连旧地址）。调用方 `AdapterManager.ReloadAdapterAsync` 只判断返回值，不执行 Stop+Start。配置重新加载后连接参数实际上不生效——需要手动 Disable + Enable。

**5. OneBotAdapter.StartAsync receiveTask 无外露健康状态** (`OneBotAdapter.cs:100`)
```csharp
receiveTask = RunReceiveLoopWithReconnectAsync(cts.Token);
// StartAsync 不等待也不检查 receiveTask
```
`RunReceiveLoopWithReconnectAsync` 不断重连，但如果重连一直失败（如 WsUrl 错误），`StartAsync` 返回成功（`connectionState = Connected` 在 catch 后设为 `Reconnecting`，但从外部看 adapter 是"已启动"状态）。`WaitAsync()` 暴露了 receiveTask 但从无调用方使用。

**6. Program.cs 优雅退出用 .GetAwaiter().GetResult() 阻塞线程** (`Program.cs:404-407`)
```csharp
adapterManager.StopAllAsync().GetAwaiter().GetResult();
engine.RequestStopAll();
var allStopped = engine.WaitAllStoppedAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
```
`ApplicationStopping.Register` 回调是同步的，内部调用 `WaitAllStoppedAsync(30s)` → 阻塞 30 秒直到超时或完成。这在容器化部署（如 Docker stop 有默认 10s 超时）中可能被 SIGKILL 杀死，导致引擎未正常退出。虽已用 `GetAwaiter().GetResult()` 避免死锁，但阻塞时间长。

**7. OneBotMessageParser.recentMessageIds 用整体 Clear 去重 ✅ 已修复 2026-05-26** (`OneBotMessageParser.cs:33-38`)
```csharp
lock (recentMessageIds) {
    if ((DateTime.Now - lastMessageIdCleanup).TotalSeconds > 60) {
        recentMessageIds.Clear();  // 瞬间清空，紧接着 Add → 放行重复消息
        lastMessageIdCleanup = DateTime.Now;
    }
    if (!recentMessageIds.Add(messageId)) return null;
}
```
每 60 秒清空整个去重集合。Clear 和 Add 虽然在同一 lock 内是安全的，但如果 NapCat 在这 60 秒窗口边缘重发了消息（TCP 重传/网络抖动），清空后消息 ID 丢失 → 重复处理同一条消息。概率极低但逻辑可用滑动窗口或时间戳替代。

**8. ImageStorage 全静态状态** (`ImageStorage.cs:20-21`)
```csharp
private static ImageRepository? _repo;
private static EventBus? _eventBus;
```
全局静态 `_repo` 和 `_eventBus`，测试隔离性差。如果将来支持多个存储路径或多实例，需要重构。当前单实例场景可接受。

### 🟠 设计问题 — 中度

**9. OneBotAdapter God Object 倾向** (OneBotAdapter.cs)
`OneBotAdapter` 暴露了 10+ 个 `internal` 成员供 `OneBotMessageParser` 和 `OneBotActions` 访问：`Config`, `SelfId`, `HttpClient`, `CallApiAsync()`, `IsSentMessage()`, `TrackSentMessage()`。三个类通过 `internal` 字段双向依赖——parser/actions 持有 adapter，adapter 持有 parser/actions。功能上正确，但 adapter 承担了连接管理、状态追踪、消息去重、API 调用、事件解析调度——职责过多。

更好的设计：Parser 做纯函数式消息转换（`JObject → IncomingMessage`），Actions 接收 `Func<string, JObject, Task<JObject?>>` 委托作为 API 通道，不依赖具体 Adapter 类型。

**10. FileAdapter.ProcessInputFiles 和 ProcessInputWithDelayAsync ~60% 重复** (`FileAdapter.cs:96-189`) ✅ 已修复 2026-05-26
两个方法有 ~35 行几乎相同的文件扫描/解析/删除/异常处理逻辑。区别仅在：一个是同步 `for` 循环，一个是带 `Task.Delay` 的异步 `foreach`。应提取公共的 `ProcessOneFile(string filePath)` → 两边复用。

**11. ImageStorage 门面过厚（~25 个方法，490 行）** (`ImageStorage.cs`)
`ImageStorage` 静态类包含了：图片下载/去重/存储/缩略图生成/格式检测、以及代理 `ImageRepository` 的全部方法（分页查询/OCR更新/Vision更新/批量清空/筛选计数）。职责混杂——既做文件系统操作（缩略图生成用 SkiaSharp），又做 Repository 门面，又做业务逻辑（分类标记/直传大小判断）。应将缩略图逻辑独立为 `ThumbnailService`，`ImageStorage` 只做下载+去重+存储。

**12. Program.cs Main 方法过长（~427 行），四种模式分支混杂** (`Program.cs:22-425`)
Main 包含：参数解析（20行）、日志系统初始化（10行）、适配器注册（10行）、MasterEngine 创建+Provider 注册（30行）、--test-send 分支（40行）、debug 分支（30行）、test 分支（60行）、正常 Web 模式（150行）。四种模式 if-else 交错。应将各模式提取为 `RunDebugModeAsync()`, `RunTestModeAsync()`, `RunWebServerAsync()` 独立方法。

### 🟢 ISSUE — 轻度

**13. OneBotMessageParser 图片下载阻塞消息处理** (`OneBotMessageParser.cs:110-141`)
```csharp
case "image":
    var (localPath, imgHash) = await ImageStorage.DownloadAndSaveAsync(imageUrl, adapter.HttpClient);
    // 设置 category、构建 attachment...
```
图片下载（HTTP，~200ms-2s）在 WebSocket 接收循环中 await。群聊一次发 3 张图 = 串行下载 3 次，后续消息被延迟处理。应改为 fire-and-forget 下载 + 先投递消息（标记图片 pending），后续异步补充 Attachment 信息。

**14. AdapterManager.CanHandleChannel 硬编码类型检查** (`AdapterManager.cs:236-247`)
```csharp
if (adapter is OneBotAdapter oneBot) { ... }
return true;
```
新增支持 channel 过滤的适配器类型（如 TelegramAdapter）需修改 `AdapterManager`。应将 `CanHandleChannel` 提升为 `IAdapter` 的默认接口方法，OneBotAdapter 覆盖实现。

**15. TextUtil.StripMarkdownCodeFence 仅处理首尾围栏** (`TextUtil.cs:9-18`) ✅ 已修复 2026-05-26
```csharp
if (lines[0].TrimStart().StartsWith("```") && lines[^1].Trim() == "```")
    return string.Join('\n', lines[1..^1]).Trim();
```
只匹配整个文本以 ``` 开始并结束的情况。模型常见输出格式：`Here is the result:\n\`\`\`json\n{...}\n\`\`\`\nDone.`（代码块不在首尾）无法正确提取。应改为查找中间的 ``` 块。

**16. OneBotActions.SendMessageAsync channelId 解析无异常处理 ✅ 已修复 2026-05-26** (`OneBotActions.cs:25-38`)
```csharp
if (message.ChannelId.StartsWith("group_"))
    p["group_id"] = long.Parse(message.ChannelId[6..]);  // 假设 channelId 格式正确
```
如果 channelId 格式异常（如 `group_abc`、非标 ID），`long.Parse` 抛异常 → 调用方收到未处理异常。应 `long.TryParse` + 返回 null 或记录错误。

**17. OneBotMessageParser.HandleEventAsync 图片段 catch 裸吞异常** (`OneBotMessageParser.cs:139-141`) ✅ 已修复 2026-05-26
```csharp
catch (Exception) { }
```
图片下载失败（URL 过期、网络不通）静默跳过。消息中的图片丢失，AI 只看到文本部分，无法知道原来有图片。

**18. VectorUtil.CosineSimilarity 无零向量保护** — `normA` 或 `normB` 为 0 时返回 0（正确）。但 `BytesToFloats` 假设 `bytes.Length` 是 4 的倍数——如果 BLOB 数据损坏（不完整的 float），会抛 `ArgumentException`。 ✅ 已修复 2026-05-26

---

## 正面发现

- **AdapterManager 生命周期管理完整**：ConcurrentDictionary + Add/Remove/Enable/Disable/Reload 全生命周期，StartAll/StopAll 批量操作
- **OneBotAdapter WebSocket 重连策略正确**：exponential backoff（1s→2s→4s→...→30s），重连成功后重置
- **OneBotAdapter API 调用模式可靠**：echo 机制 + `TaskCompletionSource` + 10 秒超时 + `pendingCalls` 清理
- **OneBotAdapter 发送消息去重**：`sentMessageIds` HashSet + 200 容量限制，防止回显自己的消息被当作新消息触发响应
- **OneBotMessageParser 消息分类完整**：支持 text/at/reply/image/record/video/file 所有 OneBot 消息段类型
- **OneBotMessageParser 群系统事件覆盖全面**：poke/group_ban/group_recall/group_upload 均有解析，且区分是否涉及 bot 自身
- **OneBotMessageParser 回复关联逻辑完整**：引用 bot 消息视为 @，已发送消息追踪去重
- **OneBotActions @ 解析支持内联和显式**：`atDelim`/`atPrefix` 方式支持文本内嵌 @，`Mentions` 列表支持显式 @
- **FileAdapter 双输入格式支持**：.json（完整元数据）+ .txt（纯文本缺省），适配不同测试场景
- **ImageStorage 魔数检测文件类型**：URL 扩展名不可信时，用 JPEG/PNG/GIF/BMP/WebP 文件头魔数识别，健壮
- **ImageStorage 缩略图生成质量好**：SkiaSharp 等比缩放 + JPEG 输出 + 小图跳过 + 已存在缩略图跳过
- **ImageStorage 文件丢失自动回源下载**：`ResolvePathsAsync` 发现本地文件缺失时从 `SourceUrl` 重新下载
- **Program.cs 图片服务端点防目录穿越**：`Path.GetFullPath` + `StartsWith` 检查，安全
- **SetupWizard 交互体验好**：7 步向导 + 预览 + 确认 + 模板释放，首次配置友好
- **AdapterInstanceConfig 设计灵活**：`JObject Settings` 存储适配器特定配置，`AutoStart`/`AutoStartDebug` 控制启动行为
- **McpServerConnection 双传输支持**：http（HttpClientTransport）+ stdio（StdioClientTransport），覆盖两种主流的 MCP 部署方式

---

## 判定

适配器层整体质量优秀——WebSocket 重连/去重/echo 超时/消息段解析都是成熟的消息平台适配经验。主要问题集中在**异常处理缺口**：`LoadFromConfig`/`MigrateLegacyConfig`/`McpServerManager` 的多处裸 catch 导致配置问题完全不可诊断。`FileAdapter` 同步阻塞异步调用是架构小缺陷。`OneBotAdapter` 内部耦合偏紧（parser/actions 深度依赖 adapter），但不影响功能。Program.cs 的 427 行 Main 方法需要拆分但功能完整。基础设施工具类（VectorUtil/TextUtil）极简干净。
