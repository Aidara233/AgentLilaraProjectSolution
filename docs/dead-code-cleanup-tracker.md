# 死代码清理跟踪

## 已清理（2026-05-24）

四轮共清理 ~1120 行死代码 + 修复 4 处空体 bug。

### 第一轮: Core / Engine / Adapter / SDK（`5b48b65`）

**整文件删除（6 个）：**
- `Core/ReviewCore.cs` — 被 AgentCore 取代
- `Core/MemoryQueryCore.cs` — 从未实例化，含 MemoryQueryIntent
- `Engine/Core/ChannelSession.cs` — WatchRules 已直接接入 ChannelEngine
- `Engine/Worker/ContextBuilder.cs` — 全 PLACEHOLDER，上下文构建已内联
- `Engine/Worker/Modules/SystemNotificationModule.cs` — 从未注册
- `Adapter/ConsoleAdapter.cs` — 从未实例化

**片段删除（9 处）：**
- `Engine/Dream/ReviewProgress.cs` — ReviewProgressEntry 类
- `Engine/System/ContextCompressionModule.cs` — CompressAsync + RoundCompletedEvent
- `Tool/TypeForwards.cs` — ToolExtensions 类（6 个死扩展方法）
- `Adapter/OneBot/OneBotMessageParser.cs` — 同步 HandleEvent(JObject)
- `Adapter/OneBot/OneBotActions.cs` — SetOnlineStatusAsync + SendForwardMsgAsync
- `Adapter/OneBot/OneBotAdapter.cs` — 遗留构造函数 + 未使用 raw 变量
- `Client/ModelClientBase.cs` — httpClient 字段
- `Memory/MemoryService.cs` — RecallAsync 死重载 + ComputeIntentBonus
- `WebUI/Providers/ConfigProvider.cs` — MemoryQueryCore 条目

**SDK 清理：**
- `PluginSDK/ToolMetaAttribute.cs` — PluginScope 枚举 + Scope 属性 + DefaultExpanded

**Bug 修复：**
- `Database/MemoryRepository.cs` — SearchAsync 表名 `MemoryEntry` → `Memories`

### 第二轮: Database 层（`1963046`）

**删除的方法（8 个）：**
- `ImageRepository.GetByHashesAsync` — N+1 低效实现
- `ImageRepository.GetTotalCountAsync` — 被 GetFilteredCountAsync 覆盖
- `PersonRepository.GetAllUserIdsAsync` — SessionManager 已内联
- `PersonRepository.MergeAsync` — 用户合并从未接入
- `UserRepository.LinkToPersonAsync` — UpdateAsync 等价覆盖
- `DbManager.MigrateMissingColumnsAsync` — 一次性迁移已完成
- `MessageRepository.GetByChannelAsync` — 无 LIMIT 危险模式
- `MessageRepository.GetByIdAsync` — 唯一零调用的 GetByIdAsync

**删除的字段（2 个）：**
- `ReviewHint.TopicId`
- `UserMessage.TopicId`

### 第三轮: 死模块 + 过剩属性 + 过期注释（`dff81d0`）

**整文件删除（2 个）：**
- `Engine/System/SystemStatusModule.cs` — Attach 只设 engineStartTime，从未读取
- `Engine/Worker/Modules/ToolStatusModule.cs` — Attach 体完全为空

**片段删除（4 处）：**
- `Engine/System/SystemEngine.cs` — 移除 SystemStatusModule + ToolStatusModule 注册
- `Engine/Worker/ChannelEngine.cs` — 移除 ToolStatusModule 注册
- `Engine/Core/MasterEngine.cs` — 移除 Phase 3 过期 TODO
- `MCP/McPbridgeTool.cs` — 移除 ToolGroup / DefaultExpanded 属性

### 第四轮: 代码去重 + PLACEHOLDER 清理 + 空体修复 + WebSearch 移除

**代码去重：**
- `Component/SimpleServiceProvider.cs` — 加字典构造器，统一三处实现
- `Engine/System/SystemEngine.cs` — 删除嵌套 SimpleServiceProvider，改用 Component 版
- `Engine/Worker/ChannelEngine.cs` — 删除嵌套 SimpleServiceProviderImpl，改用 Component 版

**PLACEHOLDER 标记删除（8 处，全部是过期分区注释）：**
- `Command/ConfigCommand.cs` — PLACEHOLDER_REST
- `Engine/Vision/VisionEngine.cs` — PLACEHOLDER_PROCESS_METHODS
- `Engine/Command/CommandSpawnCheck.cs` — PLACEHOLDER_METHODS
- `Tool/Host/ToolProfileManager.cs` — PLACEHOLDER_METHODS
- `WebUI/Providers/DreamProvider.cs` — PLACEHOLDER_FRAGMENTS_SOURCE / PLACEHOLDER_SESSION_DETAIL / PLACEHOLDER_SLEEP_SOURCES

**空体 bug 修复（C# 语法陷阱 — 无体 if 会吞下一条语句）：**
- `Engine/Vision/VisionEngine.cs:69-72` — 加 Signal.Warn，修复 if 意外控制下行赋值和主循环
- `Engine/Core/MasterEngine.cs:324,353` — 加 Signal.Warn，修复空 else 体

**死代码移除：**
- `Client/ClaudeModelClient.cs` — 移除 Web Search server tool 注入（已确认不可用）
- `Client/ApiClientCfg.cs` — 移除 `WebSearch` 配置属性
- `Engine/Core/MasterEngine.cs:564` — 移除 ScheduleParser 死注释

---

## 保留/预留

| 项目 | 原因 |
|------|------|
| `PluginDependencyAttribute` | 预留计划，插件依赖声明 |
| `MemoryRepository.DeleteByIdsAsync` | 做梦并行 Dedup 片段批量删除 |
| `MemoryLinkRepository.DeleteByIdsAsync` | 做梦并行 Dedup 片段批量清理 |
| `MemoryLinkRepository.GetAllLinksAsync` | 图谱页预留（注释标注） |
| `DreamLogRepository.CreateDetailAsync`（单条） | 做梦并行片段独立写 Detail |
| `Tool/TypeForwards.cs` global usings | 旧代码过渡，大量文件依赖 `ITool` 别名 |

---

## 待处理

### 日志/报错
- [ ] `OneBotAdapter.HandleEvent` async void — 异常会崩溃进程
- [ ] `OneBotActions.SendFileAsync` fire-and-forget — 错误静默吞没
- [ ] `OneBotActions.SendMessageAsync` 空错误分支 — retcode != 0 无日志
- [ ] 25+ 空 catch 块 — `ToolRegistry`/`ToolProfileManager`/`LogWriter`/`DreamEngine`/`MemoryProvider` 等多处无声吞异常

### 设计缺陷
- [ ] `OneBotAdapter.ReloadConfigAsync` — 连接变更后不重启 WS
- [ ] `ApiClientCfg.ConversationHistory` — 运行时状态混在配置对象中
- [ ] `Component/ChannelAccessAdapter.cs` — 4 个接口方法全是 TODO stub，已注册但无实际功能
- [ ] `ChannelEngine.cs:645` — `ToolContext = null!` null-forgiving 赋值，访问即 NRE
