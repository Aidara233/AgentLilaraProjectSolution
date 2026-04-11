# 待修复问题记录

> 本文件记录重构过程中发现的问题及其状态。

## 已全部解决

- ~~ConversationHistory 职责错位~~ — 已改为 `[JsonIgnore]`，不再参与配置序列化
- ~~Processor 硬编码路径~~ — PathConfig + paths.json 管理绝对路径
- ~~Processor.client 是 public 字段~~ — 已改为 private + 只读属性
- ~~Payload.cs 死代码~~ — 已删除
- ~~ApiRequest.Model 默认值不一致~~ — 已对齐
- ~~AgentCoreProcesser 拼写~~ — 已全局重命名为 AgentCoreProcessor
- ~~databaseDirection / cfgDirectionPath 拼写~~ — 已修复
- ~~MasterEngine.databaseDirectory 硬编码路径~~ — PathConfig 管理
- ~~MasterEngine async 方法无 await~~ — 已修复
- ~~EngineRequest.userMessage 未赋值~~ — EngineRequest 已删除
- ~~csproj 冗余 SQLite 包~~ — 已精简为仅 sqlite-net-pcl
- ~~csproj 多余的 None/Folder 引用~~ — 已移除
- ~~Program.cs 无意义代码~~ — 已清理
- ~~无用 using~~ — 已清理
- ~~ToolCall 重写~~ — 已实现：inputs 数组 + output + outputToModel + retain + ToolResult + DAG 寄存器机制
- ~~ConversationHistory 重新设计~~ — 已拆分为 PresetMessages + ConversationHistory
- ~~Storage 路径硬编码~~ — PathConfig + paths.json 解决

## 待后续实现

1. **AuthService 鉴权** — MasterEngine.HandleMessageAsync 中 TODO 标记，需实现用户鉴权 + TrustLevel 校验
2. **语义向量搜索** — MemoryService.RecallAsync 当前为关键词匹配，需接入向量嵌入实现语义检索
3. **语义话题分类** — SessionManager.ClassifyTopicAsync 当前为规则引擎（时间窗口 + 频道），需接入模型做语义分类
4. **Topic Summary 生成** — 话题结束时自动生成摘要
5. **记忆系统接入 Agent 循环** — PromptBuilder 预留了 additionalContext 参数，待 Engine 查询记忆后传入
