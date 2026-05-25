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
├── CombineCore.cs               # 记忆组合 — 从关联记忆推导新洞察
├── LinkCore.cs                  # 关联重建 — 分析记忆间关系
├── ConsolidationCore.cs         # 临时记忆整合（第一轮）— 判断保留/合并/丢弃
├── ConsolidationFinalCore.cs    # 记忆整合（第二轮）— 跨组去重、最终合并
├── DedupCore.cs                 # 记忆去重
├── WeightCore.cs                # 记忆权重调整 — 评估重要性
├── MemoryExtractionCore.cs      # 记忆提取 — 从对话中提取事实/反馈
│
├── SummarizationCore.cs         # 上下文压缩 — 对话历史摘要
│
├── SleepTalkCore.cs             # 梦话生成 — 根据梦境片段生成呓语
└── PreprocessingCore.cs         # 消息分类器 — 基于 Embedding 的聊天/任务二分类
```

## 分类

| 分类 | 文件 | 说明 |
|------|------|------|
| [基础设施](infrastructure.md) | CoreBase, Processor, ModelOutput | 基类、配置加载、输出模型 |
| [Agent 核心](agent-core.md) | AgentCore | 统一对话核心，Express/Working 双模式 |
| [记忆处理核心](memory-cores.md) | Combine, Link, Consolidation, ConsolidationFinal, Dedup, Weight, MemoryExtraction | 记忆生命周期全流程 |
| [上下文管理](context-management.md) | SummarizationCore | 对话历史压缩 |
| [专用核心](special-purpose.md) | SleepTalkCore, PreprocessingCore | 梦话生成、消息分类 |
| [流事件处理器](stream-handlers.md) | NativeToolCallHandler, ExpressToolCallHandler | 原生 tool_use 事件解析 |
| [配置指南](configuration.md) | Storage/Core/*.json | 核心配置文件格式、协议选择、参数说明 |
