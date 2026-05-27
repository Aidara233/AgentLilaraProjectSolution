# 模块6审计报告：插件+工具系统 (Plugins & Tools)

审计时间：2026-05-26
文件数：~55（含 PluginSDK 接口 + 6 个插件项目 + Tool/Host/Core） | 核心实现 ~2,800 行

---

## 发现问题

### 🔴 BUG — 中度

**1. PluginLoader.ReloadAll 非原子操作** (`PluginLoader.cs:66-70`)
```csharp
public void ReloadAll()
{
    UnloadAll();
    LoadAll();
}
```
`UnloadAll()` 清除所有已加载插件后，如果 `LoadAll()` 中任一 DLL 加载失败（损坏的 DLL、缺失依赖），所有插件全部丢失且无法回滚。热重载场景下系统会失去全部插件功能直到手动重启。应改为：先加载新插件到临时列表 → 成功后替换旧列表 → 失败则保留旧列表。

**2. GlobalComponentHost empty catch 吞异常** (`GlobalComponentHost.cs:92-106`) ✅ 已修复 2026-05-26
```csharp
try { await inst.Component.OnEnabledAsync(); }
catch (Exception) { }     // EnableComponentAsync
...
catch (Exception) { }     // DisableComponentAsync
```
`EnableComponentAsync` / `DisableComponentAsync` 的 catch 块完全为空，不记录任何日志。对比 `ComponentHost` 同一模式有 `LogError` 调用。此处异常静默丢弃导致运行时组件状态异常无法诊断。

### 🟡 BUG — 轻度

**3. PluginLoader 空 catch 块** (`PluginLoader.cs:136-138`) ✅ 已修复 2026-05-26
```csharp
if (ToolRegistry.Register(tool))
    entry.ToolNames.Add(tool.Name);
else
{ }  // 注册失败静默丢弃
```
工具注册失败（同名冲突等）完全静默。一个 DLL 中的工具可能与已注册工具同名而无声失败。应至少 Console.Error 写入警告。

**5. PluginLoader.LoadAll 中空 DLL 目录只创建目录** (`PluginLoader.cs:46-49`) ✅ 已修复 2026-05-26
如果 Plugins 目录存在但无 DLL，直接 return。但如果之前有 DLL 被删除（手动清理），系统不报任何信息。对运维不友好。

### 🟠 设计问题 — 中度

**6. ToolRegistry 全静态导致不可测试** (`ToolRegistry.cs`)
所有方法均为 `static`，字典为 `static readonly`。无法在测试中隔离 ToolRegistry 状态——所有测试共享同一个全局 registry。`ConcurrentDictionary` 确保了线程安全，但牺牲了可测试性。如果将来允许多个 MasterEngine 实例（多租户），全局静态会成为障碍。

**7. ComponentHost 和 GlobalComponentHost ~70% 代码重复** (`ComponentHost.cs` & `GlobalComponentHost.cs`)
两者的 Init/Shutdown/CreateInstance/Enable/Disable/RegisterTools 逻辑几乎相同，区别仅在于：
- Component 类型：`ILoopComponent` vs `IGlobalComponent`
- 查找方式：`GetLoopComponents(loopType)` vs `GetGlobals()`
- Prompt 接口：`BuildPromptSection()` vs `BuildPromptSection(caller)`

可提取泛型基类减少重复，但考虑到差异点分散且各自仍在演进，适度重复可接受。

**8. PluginLoader 硬编码可注入类型** (`PluginLoader.cs:254-260`)
```csharp
private static readonly Dictionary<Type, Func<IServiceProvider, object?>> s_injectableResolvers = new()
{
    [typeof(EventBus)] = sp => sp.GetService(typeof(EventBus)),
    [typeof(ModuleBus)] = sp => sp.GetService(typeof(ModuleBus)),
    // ...
};
```
新增可注入类型（如新增的 `ILogAccess`、`IDelegationAccess`）需要修改 PluginLoader 代码。应改为从 IServiceProvider 直接解析（当前 `services.GetService(pType)` 在 line 281 已有 fallback），去掉此硬编码字典。

**9. 所有工具各自实现参数验证** — 缺乏框架级输入校验
每个工具 ExecuteAsync 的开头都是手动检查 `resolvedInputs.Count`。框架已通过 `ToolParameter` 提供了 `IsRequired` 元数据，但 `ToolExecutor` 不执行任何校验。导致：
- 必填参数缺失的错误信息不一致（有的返回中文、有的英文）
- 每个工具都重复相同的验证模板
- 无法在框架层统一处理（如返回结构化的参数缺失错误）

### 🟢 ISSUE — 轻度

**10. PluginLoader.InstantiateWithInjection 构造失败吞异常** (`PluginLoader.cs:291`)
```csharp
try { return Activator.CreateInstance(type, args); }
catch { Signal.Warn(LogGroup.Plugin, "插件类型构造失败", ...); continue; }
```
这里吞掉了具体异常类型（MissingMethodException vs TargetInvocationException），Signal.Warn 只记录 type.FullName。如果参数解析有问题，无法从日志定位根因。

**11. ToolExecutor.ExecuteAsync 顺序执行无并行** (`ToolExecutor.cs:30-41`)
工具调用是顺序执行的（`foreach` 逐个 await）。如果模型一次返回 5 个工具调用，它们是串行执行的。对于独立的工具调用（如同时 speak + pinboard），并行执行可减少延迟。

**12. EscalateTool / DeescalateTool / CompressTool 等 Core 工具不注册到 ToolRegistry** — 这些工具由 ChannelEngine 直接创建和使用，不走 PluginLoader/ToolRegistry。它们的行为正确但查找路径不透明——无法通过 ToolRegistry 统一查询所有可用工具。

**13. 插件 Component 工具懒初始化但 Tools 属性每次调用** (`CrossLoopComponent.cs:33-49`)
`Tools` 属性是 `get` 访问器，每次调用都重新 yield 非 null 的工具。虽然开销小（仅 null 检查 + yield），但每次 API 调用都会遍历此属性多次（ComponentHost 中 `GetVisibleTools`、`RegisterTools` 等）——缓存到列表可避免重复枚举。

---

## 正面发现

- **PluginLoader AssemblyLoadContext 隔离设计正确**：插件 DLL 通过 `PluginLoadContext` 独立加载，`isCollectible: true` 支持卸载。契约程序集（AgentCoreProcessor、PluginSDK）正确地从主进程加载
- **ComponentHost 两层工具解析链清晰**：local Loop 工具 → global 组件工具 → ToolRegistry，优先级递减
- **AgentMessagingImpl 事件通知机制设计好**：序列号去重 + `_notifications` 队列 + `DrainNotifications` 消费模式，避免通知丢失
- **ToolListFormatter 收集+格式化分离**：`CollectGroups` 负责数据收集，`BuildToolOverviewSection` 负责渲染，职责单一
- **Component 配置化的启用/禁用**：`ComponentConfig` + `ComponentHost.EnableComponentAsync/DisableComponentAsync` 链支持运行时动态管理
- **所有 Core 工具极简**：每个 20-40 行，纯粹的同步返回 `ToolResult`，无外部依赖
- **PluginSDK 契约设计完整**：ITool、ILoopComponent、IGlobalComponent、各类 Service 接口分离清晰
- **ComponentHost.ShutdownAsync 两阶段关闭正确**：Phase1 请求确认（带超时）→ Phase2 强制关闭，防止挂起
- **ToolExecutor 超时保护到位**：每个工具独立的 `CancellationTokenSource(tool.Timeout)` 防止单工具阻塞整个循环

---

## 判定

整体架构优秀——PluginLoader + ComponentHost + ToolRegistry 三层解耦清晰，组件模型设计合理。2 个中度 bug 需关注：ReloadAll 非原子（热重载风险）和 GlobalComponentHost 异常吞没不一致。全静态 ToolRegistry 测试隔离性差但目前无多租户需求可暂缓。插件实现质量整体一致，模式干净。
