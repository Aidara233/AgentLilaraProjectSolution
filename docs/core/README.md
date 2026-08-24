# Core 系统文档

Core 系统是 Agent Lilara 的 LLM 调用抽象层，所有与语言模型的交互都通过 Core 类完成。

## 文件清单

```
Core/
├── CoreBase.cs                  # 抽象基类 — 所有 Core 的根
├── Processor.cs                 # 模型客户端包装器 + 配置加载
├── AgentCore.cs                 # 统一 Agent 核心（按配置名加载 ExpressCore/ChannelCore/TaskCore）
├── ExtractionCore.cs            # 记忆提取 — 从对话中提取语义三元组断言（伴随提取）
├── ModelOutput.cs               # 不可变输出数据结构（Text / ToolCalls / Thinking）
└── NativeToolCallHandler.cs     # 原生 tool_use 流事件处理
```

> 已删除（死代码清理，2026-08-24）：`ExpressToolCallHandler`（Express 模式内切换已随 express 会话化废弃）、
> `SleepTalkCore`（做梦系统已随记忆重构移除）、`PreprocessingCore`（消息二分类无调用点）、旧 7 个记忆核。
> 存储于 `Storage/Core/{CoreName}.json` 的配置仅保留活跃核：`Base` / `ExpressCore` / `ChannelCore` / `TaskCore` / `ExtractionCore`。

## 分类

| 分类 | 文件 | 说明 |
|------|------|------|
| [基础设施](infrastructure.md) | CoreBase, Processor, ModelOutput | 基类、配置加载、输出模型 |
| [Agent 核心](agent-core.md) | AgentCore | 统一对话核心，按会话类型核配置（ExpressCore/ChannelCore/TaskCore） |
| [记忆处理核心](memory-cores.md) | ExtractionCore | 伴随提取（输出契约 + 可信度自评） |
| [上下文管理](context-management.md) | CompressionTierModule + CompactTool | 三层压缩判定 + compact 工具接管 |
| [流事件处理器](stream-handlers.md) | NativeToolCallHandler | 原生 tool_use 事件解析 |
| [配置指南](configuration.md) | Storage/Core/*.json | 核心配置文件格式、协议选择、参数说明 |