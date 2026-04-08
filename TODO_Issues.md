# 待修复问题记录

> 本文件记录重构过程中发现的问题及其状态。

## 已全部解决

- ~~ConversationHistory 职责错位~~ — 已改为 `[JsonIgnore]`，不再参与配置序列化
- ~~Processor 硬编码路径~~ — 已改为相对路径默认值
- ~~Processor.client 是 public 字段~~ — 已改为 private + 只读属性
- ~~Payload.cs 死代码~~ — 已删除
- ~~ApiRequest.Model 默认值不一致~~ — 已对齐为 `"deepseek-ai/DeepSeek-R1-Distill-Qwen-7B"`
- ~~AgentCoreProcesser 拼写~~ — 已全局重命名为 AgentCoreProcessor
- ~~databaseDirection / cfgDirectionPath 拼写~~ — 已修复
- ~~MasterEngine.databaseDirectory 硬编码路径~~ — 已改为相对路径默认值
- ~~MasterEngine async 方法无 await~~ — 已改为返回 Task.CompletedTask
- ~~EngineRequest.userMessage 未赋值~~ — 已改为 required 属性
- ~~csproj 冗余 SQLite 包~~ — 已精简为仅 sqlite-net-pcl
- ~~csproj 多余的 None/Folder 引用~~ — 已移除
- ~~Program.cs 无意义代码~~ — 已清理
- ~~无用 using~~ — 已清理（ApiClientCfg、AIApiClient）

## 待后续重构

1. **ToolCall.AfterThan → AfterThen** — JSON 字段名拼写错误，用户要求后续重构时处理。目前无兼容性约束。

2. **ConversationHistory 需要重新设计** — 当前已改为 `[JsonIgnore]`，但配置文件中的 system prompt 和预设对话（`conversationHistory` 字段）依赖序列化加载。需要拆分为"预设消息模板"（从配置加载）和"运行时对话历史"（不序列化）两个概念，避免配置回写污染模板。

## 架构规划

### Adapter 层（新增）

纯粹的消息收发适配层，每个平台一个实现。

```
Adapter/
  ├── IAdapter          — 统一的消息收发接口
  ├── ConsoleAdapter    — 控制台（开发调试）
  ├── HttpApiAdapter    — 通用 HTTP 接口
  ├── AdapterManager    — 管理所有 Adapter 实例的生命周期
  └── IncomingMessage   — 统一消息格式（平台来源、用户平台ID、频道ID、内容）
```

职责：接收平台原始消息 → 转换为 IncomingMessage → 交给 MasterEngine；接收 Engine 输出 → 转换为平台格式发回。

### Engine 层（重构）

两级 Engine 架构：

```
Engine/
  ├── MasterEngine      — 全局唯一，调度员
  │     ├── 接收 IncomingMessage，分配 WorkerEngine
  │     ├── 管理并发（限流、队列）
  │     └── 持有全局资源（SessionManager、AuthService、DB 连接）
  │
  ├── WorkerEngine      — 每个任务一个实例（或池化复用），干活的人
  │     ├── 持有一条完整的处理链路状态
  │     ├── 按需调用不同 Core（Preprocessing → Working / Express）
  │     └── 管理工具 DAG 的并发执行
  │
  ├── SessionManager    — 会话/频道的历史消息管理
  ├── AuthService       — 用户鉴权 + TrustLevel 校验
  └── 数据实体（User、Channel、Topic、Memory、UserMessage）
```

处理流程：
1. Adapter 收到消息 → IncomingMessage → MasterEngine
2. MasterEngine: AuthService 鉴权 → SessionManager 获取历史 → 分配 WorkerEngine
3. WorkerEngine: PreprocessingCore 分类 → 路由到 ExpressCore / WorkingCore → 输出结果
4. MasterEngine → 通过 Adapter 发回响应
