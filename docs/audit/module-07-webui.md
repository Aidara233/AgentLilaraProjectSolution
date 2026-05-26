# 模块7审计报告：WebUI

审计时间：2026-05-26
文件数：~50（含 .razor + .cs） | 核心 C# 实现 ~2,200 行

---

## 发现问题

### 🔴 BUG — 中度

**1. LogStreamService 是空壳 — buffer 永不被写入** (`LogStreamService.cs:16-31`)
```csharp
private readonly ConcurrentQueue<LogEntry> buffer = new();
// ... 没有任何 Enqueue/Push 方法！
public void Dispose() { }  // 空实现
```
`buffer` 声明了 `ConcurrentQueue<LogEntry>` 但整个类没有任何入队方法。`GetRecent()` 永远返回空列表。`Dispose()` 为空。这是一个从未实现的占位类——实时日志流功能完全不可用。

**2. SystemMonitor 双重构建快照对象** (`SystemMonitor.cs:57-82`)
```csharp
var snapshot = new SystemSnapshot { ...  // 构建第一个对象
};
Current = new SystemSnapshot {            // 立即构建第二个完全相同的对象
    IsIdle = snapshot.IsIdle,
    // ... 逐字段复制 ...
    Alerts = alertService.CollectAlerts(snapshot),  // 仅多了这一行
};
```
第一个 `snapshot` 对象构建后立即被丢弃，仅用于传给 `alertService.CollectAlerts(snapshot)`，然后所有字段逐一手动复制到第二个对象赋值给 `Current`。第一个对象是多余的 GC 压力。应直接在第一个对象上设置 `Alerts`。

### 🟡 BUG — 轻度

**3. DataSourceManager.FetchAsync 第二个 catch 块不可达** (`DataSourceManager.cs:53-76`)
```csharp
catch (Exception) when (attempt < 3) { ... retry ... }
catch (Exception ex) { state.Error = ex.Message; return null; }
```
`for (int attempt = 0; attempt < 3; attempt++)` 确保循环体内 `attempt` 始终 < 3，因此 `when (attempt < 3)` 永真，第二个 `catch (Exception ex)` 永远不可达。最后一次重试失败时错误信息丢失（`state.Error` 不会被设置），UI 显示空白错误。

**4. EngineSummarySource.Subscribe 每次订阅创建独立 Timer** (`EnginesProvider.cs:91-95`)
```csharp
public IDisposable? Subscribe(Action<JsonNode?> callback)
{
    var timer = new System.Threading.Timer(_ => callback(null), null, 5000, 5000);
    return new TimerDisposable(timer);
}
```
每次 Blazor 组件渲染/订阅都创建新的 5 秒定时器。如果组件因状态变更多次重渲染，会累积多个定时器同时运行。`DataSourceManager.SubscribeAll()` 在 `Initialize()` 后仅调用一次，风险可控，但如果 `Initialize` 被多次调用则泄漏。

**5. NavConfig.Items 死代码** (`NavConfig.cs:5-9`)
```csharp
internal static class NavConfig
{
    public static List<NavItem> Items { get; } = new();
}
```
永远为空的静态列表。真正的导航构建在 `ProviderRegistry.BuildNavTree()` 中。`NavConfig` 和 `NavItem` 类看起来是旧导航系统的残余。

### 🟠 设计问题 — 中度

**6. SystemMonitor.CollectSnapshot 空 catch 吞异常** (`SystemMonitor.cs:86-88`) ✅ 已修复 2026-05-26
```csharp
catch (Exception) { }
```
`CollectSnapshot` 是 Timer 回调（非 UI 线程）。如果快照收集过程中的任何步骤抛异常（如 `GetSpawnCheck` 返回 null 后访问 null 成员），异常被完全吞没。`Current` 保持上个周期的旧值，UI 显示过时数据且无任何错误迹象。应至少 `Signal.Warn` 记录。

**7. AlertService.providers 无线程安全** (`AlertService.cs:9`)
```csharp
private readonly List<IAlertProvider> providers = new();
```
`Register` 和 `CollectAlerts` 都访问此列表。`Register` 在启动时调用（主线程），`CollectAlerts` 在 Timer 回调中调用（线程池）。虽然当前注册只在启动阶段（早于 Timer 启动）完成，但无防护的 `List<T>` 在并发场景下会崩溃。建议 `ConcurrentBag` 或在 `SystemMonitor` 构造完成后冻结列表。

**8. WebAuthService 密码哈希无盐** (`WebConfig.cs:66-70`)
```csharp
public static string HashPassword(string password)
{
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password));
    return Convert.ToHexString(bytes).ToLowerInvariant();
}
```
SHA256 不加盐。虽然是本地管理面板（localhost:5000），但配置文件 `WebConfig.json` 中的密码哈希对于相同密码产生相同哈希值。如果用户使用弱密码且配置文件泄露，存在彩虹表攻击风险。建议至少加一个配置中的盐值。

### 🟢 ISSUE — 轻度

**9. ProviderRegistry.Unregister 中 builtIn 保护的逻辑有误** (`ProviderRegistry.cs:27-35`)
```csharp
if (entry.BuiltIn)
{
    _providers.TryAdd(providerId, entry);  // 重新加回
    return false;
}
```
当尝试卸载 BuiltIn Provider 时，先 `TryRemove` 成功，再 `TryAdd` 回去。但 `TryAdd` 可能失败（如果另一个线程刚好注册了同 ID 的 Provider），导致 BuiltIn Provider 永久丢失。虽然概率极低（Provider 卸载是 WebUI 管理操作），但逻辑有缺陷。

**10. ModelLogService.ListRecent 先 Take(count*3) 再 callerFilter 再 Take(count)** (`ModelLogService.cs:79-98`)
先取 `count * 3` 条文件，再按 caller 过滤，再 `Take(count)`。如果 caller 过滤后结果很少（如 callerFilter 匹配率 10%），最终返回可能不足 count 条。应：取所有匹配 coreFilter 的文件 → 解析 → caller 过滤 → Take(count)。当前实现中如果 callerFilter 命中率低，前端显示的分页会不准确。

**11. LogStreamService 订阅模式但无实际数据源** — 整个 WebUI 没有统一的日志写入桥接。Signal 系统的 `LogWriter` 批量写入 SQLite，但 `LogStreamService`（内存队列）没有任何数据流入。如果想做实时日志流，需要从 `LogWriter` 的回调桥接到 `LogStreamService`。

**12. DashboardStatusSource.SubmitAsync 中 trigger-vision 直接访问 internal API** (`DashboardProvider.cs:158-169`)
```csharp
case "trigger-vision":
    var visionCheck = _engine.GetSpawnCheck<VisionEngineSpawnCheck>();
    if (visionCheck?.ActiveInstance != null)
    {
        visionCheck.ActiveInstance.SignalGate();
```
WebUI DataSource 直接访问 `VisionEngineSpawnCheck.ActiveInstance` 内部属性并调用 `SignalGate()`。跨层耦合——WebUI → Engine 内部。应通过 EventBus 发信号解耦。

---

## 正面发现

- **Shell 架构分层清晰**：Provider → Page → Card → DataSource，每层职责明确
- **ProviderRegistry 路由匹配正确**：精确匹配优先 + 模板 `{param}` 回退，实现无歧义
- **DataSourceManager 重试+退避**合理：`FetchAsync` 最多 3 次重试，指数退避 1s/2s/4s
- **EngineAlertProvider 告警覆盖全面**：SystemEngine 未运行、API 错误、睡觉审批、任务积压、频道错误皆有对应告警
- **ModelLogService 格式兼容好**：支持新 JSON 格式（messages/contentParts）、旧格式（input 数组）、dynamicInput 三种格式
- **Provider 模式一致**：Dashboard/Engines/Channel/Dream/Review 等 Provider 遵循相同结构，易于新增
- **CardSchema 类型丰富**：Status/Table/Form/Stream/Chat/Action/Tree/Detail/PropertyEditor，满足多种数据展示需求
- **DashboardProvider 操作接口完整**：mute/unmute/trigger-vision/trigger-dream 四个管理操作，一键触发

---

## 判定

核心功能正常，未发现崩溃级 bug。最大问题是 **LogStreamService 是空壳**——实时日志流功能完全不可用（如果 UI 上有对应组件，展示永远为空）。SystemMonitor 的双重快照构建浪费 CPU。NavConfig 死代码和 AlertService 线程安全问题是遗留设计债务。整体架构分层好，Provider 模式一致性高，ModelLogService 格式兼容做得仔细。
