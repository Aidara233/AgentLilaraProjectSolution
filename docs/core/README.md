# Core 系统文档

Core 系统是 Agent Lilara 的 LLM 调用抽象层，所有与语言模型的交互都通过 Core 类完成。

## 文件清单

```
Core/
├── CoreBase.cs                  # 抽象基类 — 所有 Core 的根
├── Processor.cs                 # 模型客户端包装器 + 配置加载 + Persona 注入
├── AgentCore.cs                 # 统一 Agent 核心（Express + Working 双模式）
├── ModelOutput.cs               # 不可变输出数据结构（Text / ToolCalls / Thinking）
├── NativeToolCallHandler.cs     # Working 模式原生 tool_use 流事件处理
├── ExpressToolCallHandler.cs    # Express 模式流事件处理（分离文本回复和工具调用）
│
├── MemoryExtractionCore.cs      # 记忆提取 — 从对话中提取事实/反馈
├── RelationClassificationCore.cs # 关系分类 — 单中心 + N 候选，输出 support 值
│
├── SleepTalkCore.cs             # 梦话生成 — 根据梦境片段生成呓语
└── PreprocessingCore.cs         # 消息分类器 — 基于 Embedding 的聊天/任务二分类
```

## 分类

| 分类 | 文件 | 说明 |
|------|------|------|
| [基础设施](infrastructure.md) | CoreBase, Processor, ModelOutput | 基类、配置加载、输出模型 |
| [Agent 核心](agent-core.md) | AgentCore | 统一对话核心，Express/Working 双模式 |
| [记忆处理核心](memory-cores.md) | MemoryExtractionCore, RelationClassificationCore | 记忆提取 + 关系分类 |
| [上下文管理](context-management.md) | CompressionTierModule + CompactTool | 三层压缩判定 + compact 工具接管 |
| [专用核心](special-purpose.md) | SleepTalkCore, PreprocessingCore | 梦话生成、消息分类 |
| [流事件处理器](stream-handlers.md) | NativeToolCallHandler, ExpressToolCallHandler | 原生 tool_use 事件解析 |
| [配置指南](configuration.md) | Storage/Core/*.json | 核心配置文件格式、协议选择、参数说明 |
