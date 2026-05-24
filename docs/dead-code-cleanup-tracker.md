# 死代码清理跟踪

## 已清理（2026-05-24）

三轮共清理 ~1060 行死代码。

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

### 第三轮: 死模块 + 过剩属性 + 过期注释

**整文件删除（2 个）：**
- `Engine/System/SystemStatusModule.cs` — Attach 只设 engineStartTime，从未读取
- `Engine/Worker/Modules/ToolStatusModule.cs` — Attach 体完全为空

**片段删除（4 处）：**
- `Engine/System/SystemEngine.cs` — 移除 SystemStatusModule + ToolStatusModule 注册
- `Engine/Worker/ChannelEngine.cs` — 移除 ToolStatusModule 注册
- `Engine/Core/MasterEngine.cs` — 移除 Phase 3 过期 TODO（子 agent 插件已完成）
- `MCP/McPbridgeTool.cs` — 移除 ToolGroup / DefaultExpanded 属性（无人读取）

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

## 待处理（NOTEWORTHY / 风险项）

### 代码重复
- [ ] `SimpleServiceProvider` — 三处实现已分化（Component 版 ConcurrentDictionary+Register，Engine 版只读 Dictionary），不直接等价，合并需重构

### 空壳/未完成
- [ ] `VisionEngine.cs:69-72` 空 if body — vision/OCR 不可用时静默吞掉配置错误
- [ ] 多处 PLACEHOLDER 标记 — CommandSpawnCheck / ConfigCommand / DreamProvider (x3) / VisionEngine / ToolProfileManager

### 风险设计（暂缓，涉及报错/日志路径）
- [ ] `OneBotAdapter.HandleEvent` async void — 异常会崩溃进程
- [ ] `OneBotActions.SendFileAsync` fire-and-forget — 错误静默吞没
- [ ] `OneBotActions.SendMessageAsync` 空错误分支 — retcode != 0 无日志
- [ ] `OneBotAdapter.ReloadConfigAsync` — 连接变更后不重启 WS
- [ ] `ApiClientCfg.ConversationHistory` — 运行时状态混在配置对象中

### 待确认
- [ ] `ClaudeModelClient` ServerTools.WebSearchVersionLegacy — 常量来自外部 SDK，需检查包版本确认是否可删
