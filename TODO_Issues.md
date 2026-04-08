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

2. **ConversationHistory 仍在 ApiClientCfg 中** — 虽然已从序列化中排除，但从职责上讲对话历史不属于"配置"。彻底拆分需要改动 AIApiClient 和上层调用方式，留待架构重构。
