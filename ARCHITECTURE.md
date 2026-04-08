# Agent Lilara 架构设计文档

## 总览

Agent Lilara 是一个多平台 AI Agent 框架，核心能力是接收来自不同平台的用户消息，经过分类、处理、工具调用后，以人格化的方式回复用户。

```
Adapter ←→ MasterEngine → WorkerEngine → Core → 输出
              ↕                ↕
        AuthService       SessionManager
        AdapterManager    MemoryService
```

---

## 分层架构

### Adapter 层

纯粹的消息收发适配层，不含业务逻辑。

```
Adapter/
  ├── IAdapter          — 统一的消息收发接口（收 + 发）
  ├── ConsoleAdapter    — 控制台（开发调试）
  ├── HttpApiAdapter    — 通用 HTTP 接口
  ├── AdapterManager    — 管理所有 Adapter 实例的生命周期，提供统一的发送入口
  └── IncomingMessage   — 统一消息格式
```

IncomingMessage 包含：平台来源、用户平台ID、频道ID、消息内容、回复引用等元信息。

Adapter 职责：
- 接收平台原始消息 → 转换为 IncomingMessage → 交给 MasterEngine
- 所有群聊消息都会流入（不只是 @Lilara 的），供 SessionManager 做 Topic 归类
- 提供主动发送能力，供 Engine 层在任意时机向任意频道/用户发送消息

主动发送场景：
- 错误/降级通知（API 故障、解析失败等）
- 定时任务完成后的结果推送
- 工具执行完毕的通知
- 被要求联系其他用户
- 主动唤醒（模型判断当前话题值得插话）

主动发送统一走 MasterEngine → AdapterManager → 对应 Adapter 的路径。

### Engine 层

两级 Engine 架构。

```
Engine/
  ├── MasterEngine       — 全局唯一，调度员
  ├── WorkerEngine       — 每个任务一个实例（或池化复用）
  ├── SessionManager     — 会话 / Topic / 历史消息管理
  │     ├── TopicClassifier  — 消息归类（规则层 + 模型辅助）
  │     ├── TopicStore       — Topic CRUD 和生命周期
  │     └── MessageStore     — 消息存取
  ├── AuthService        — 用户鉴权 + TrustLevel
  ├── MemoryService      — 记忆存取 + 语义检索
  └── 数据实体（User, Channel, Topic, Memory, UserMessage）
```

#### MasterEngine

- 接收 IncomingMessage，调用 AuthService 鉴权
- 分配 WorkerEngine 处理任务（支持并发，多用户请求并行）
- 持有全局资源（SessionManager、MemoryService、AdapterManager、DB 连接）
- 通过 AdapterManager 发回响应或主动发送消息

#### WorkerEngine

- 每个任务一个实例，持有完整的处理链路状态
- 调用 PreprocessingCore 分类 → 路由到对应 Core
- 管理工具 DAG 的并发执行
- 仅在向用户输出信息时经过 ExpressCore 人格化（任务中间步骤不人格化）

#### SessionManager

负责会话上下文管理，所有消息（无论是否触发 Lilara）都经过这里。

- OnMessage(msg) — 每条消息进来都做归类入库
- GetContext(channelId, topicId) — 返回指定 Topic 的历史消息
- GetActiveTopics(channelId) — 获取频道内活跃话题列表

Topic 归类策略：
- 规则层（零成本）：回复/引用关系直接归入同一 Topic；同一用户短时间连续消息归为同一话题；@提及标记为需响应
- 模型辅助层：规则层无法判定时，用轻量模型做语义分类（给出活跃 Topic 摘要 + 新消息，判断归入已有 Topic 或新建）
- Lilara 被唤醒时，未归类的积压消息触发模型辅助层加速处理

Topic 数据结构：
- Id, ChannelId, Name, LastMessageTime（已有）
- Summary — 话题摘要，用于匹配和上下文注入
- IsActive — 是否活跃（超时自动关闭）

### Core 层

三个 Core 子类，各自通过类名加载对应的配置文件（Storage/Core/{ClassName}.json），实现不同的功能。

```
Core/
  ├── CoreBase            — 抽象基类，提供流式生成、break 检测等通用能力
  ├── PreprocessingCore   — 消息分类路由（Qwen3-8B, temperature=0, thinking=disabled）
  │     输入：用户消息
  │     输出：分类数字 1-4（聊天 / 需要额外知识 / 任务 / 大型任务）
  ├── ExpressCore         — 人格化输出（DeepSeek-R1, temperature=1.2, thinking=enabled）
  │     输入：来自其他 Core 或 Engine 的原始回复
  │     输出：以 Lilara 人格风格重新表达的内容
  ├── WorkingCore         — 工具调用编排（DeepSeek-R1, temperature=0.4, thinking=enabled）
  │     输入：用户需求 + 可用工具列表
  │     输出：ToolCall JSON 序列，以 <over> 标签分隔
  ├── Processor           — Core 与 AIApiClient 之间的桥梁，负责加载配置并发起请求
  └── ToolCall            — 工具调用数据结构（见下方工具调用系统）
```

### Client 层

封装 HTTP 请求和 SSE 流式解析，对接 OpenAI 兼容 API。

```
Client/
  ├── AIApiClient    — HTTP 客户端，流式请求，链式配置 API
  └── ApiClientCfg   — 配置类（endpoint, apiKey, model, 参数, extraBody 等）
```

配置文件驱动：每个 Core 对应一个 JSON 配置文件，包含 endpoint、apiKey、模型名、参数、预设消息模板（system prompt + few-shot 示例）。

---

## 记忆系统

### 记忆维度

按作用域：
- Global — 跨所有频道和用户的知识（Lilara 人设、通用知识）
- User — 绑定到具体用户（"这个人叫小明，喜欢编程"）
- Channel — 绑定到频道（"这个群主要聊游戏"）
- Topic — 绑定到具体话题（"这次讨论的是某个 bug"）

按生命周期：
- Persistent — 永久保留，手动创建或模型判断值得长期记住
- Ephemeral — 有 TTL，过期自动清理（"用户刚才说他心情不好"）

### 记忆数据结构

```
Memory
  ├── Id
  ├── Scope           — Global / User / Channel / Topic
  ├── ScopeId         — 对应的 userId / channelId / topicId（Global 时为 0）
  ├── Content         — 记忆内容
  ├── Embedding       — 向量嵌入（用于语义检索）
  ├── IsPersistent    — 永久 or 临时
  ├── ExpiresAt       — 临时记忆的过期时间（永久为 null）
  ├── CreatedAt
  └── LastAccessedAt  — 用于 LRU 淘汰或权重衰减
```

### MemoryService

```
MemoryService
  ├── Store(scope, scopeId, content, isPersistent)     — 写入记忆
  ├── Recall(userId, channelId, topicId, query, topK)  — 按作用域 + 语义检索
  ├── Forget(memoryId)                                  — 删除
  └── Cleanup()                                         — 定期清理过期临时记忆
```

检索策略：先按当前请求的 userId / channelId / topicId 缩小作用域范围，再在范围内做语义相似度匹配取 top-K。

### 记忆写入

由模型自主决定。Core 输出中可包含记忆指令标签：

```
<memory scope="user" persistent="true">这个用户叫小明，是个程序员</memory>
```

WorkerEngine 解析输出时提取记忆指令，交给 MemoryService 存储。

### Embedding 方案

本地方案优先（通用性考虑，不依赖外部 API）。可选：
- ONNX Runtime + 小模型（bge-small-zh / all-MiniLM-L6-v2），纯 CPU 可跑
- 起步阶段可先用关键词 + TF-IDF 做检索，后续再接入向量模型

---

## 工具调用系统

### ToolCall 数据结构

```json
{
  "tool": "工具名",
  "toolId": "唯一标识",
  "inputs": [
    { "type": "value", "value": "字面值" },
    { "type": "ref", "source": "其他工具的toolId" }
  ],
  "output": "输出标识（可选，无输出的工具可省略）",
  "critical": false
}
```

字段说明：
- `tool` — 工具名称
- `toolId` — 本次调用的唯一标识，用于其他工具引用
- `inputs` — 输入数组，每项为字面值（`type: "value"`）或引用其他工具输出（`type: "ref"`）
  - ref 隐含了依赖关系：引用了某个 toolId 的输出，就必须等该工具完成
  - 支持多输入，解决了旧设计只有单个 input 的限制
- `output` — 输出标识，供其他工具的 inputs 引用；无输出的工具可省略
- `critical` — 是否为关键步骤，默认 false
  - false：失败后记录错误，其他无依赖的工具继续执行
  - true：失败后立即中断整条 DAG

依赖关系完全由 inputs 中的 ref 推导，不需要单独的依赖字段。

### DAG 执行流程

1. WorkingCore 输出一组 ToolCall JSON（以 `<over>` 分隔）
2. WorkerEngine 解析所有 ToolCall，根据 inputs 中的 ref 构建 DAG
3. 无依赖的工具并行执行，有依赖的等待上游完成后执行
4. 某个工具失败时：
   - 若 `critical: true` → 立即中断整条 DAG
   - 若 `critical: false` → 记录错误，继续执行其他无依赖的工具；依赖该工具的下游标记为"未执行"
5. 所有工具完成后（无论成功/失败），汇总结果提交回模型：

```
[read1] 成功，返回值：文件内容...
[transform1] 异常：格式不支持
[write1] 未执行（依赖 transform1）
```

### 示例

用户需求："将 example.txt 的内容复制到 output.txt"

```json
{"tool": "文件流读取器", "toolId": "read1", "inputs": [{"type": "value", "value": "example.txt"}], "output": "read1_out"}<over>
{"tool": "文件流写入器", "toolId": "write1", "inputs": [{"type": "ref", "source": "read1"}, {"type": "value", "value": "output.txt"}]}<over>
```

read1 无依赖，立即执行；write1 引用了 read1 的输出，等 read1 完成后执行。

---

## 唤醒机制

### 被动唤醒

- 用户 @Lilara 或直接喊名
- 私聊消息
- 定时任务触发
- 外部消息流触发

### 主动唤醒

- 模型通过 SessionManager 监听频道消息流，判断当前话题是否值得主动参与
- 主动唤醒同样走 MasterEngine → WorkerEngine 的标准处理流程

---

## 错误处理

### 策略

1. 报错 → 重试（同一 provider）
2. 重试失败 → 切换备用 provider/模型
3. 备用也失败 → 向用户报告错误

### 原则

- 无论是否降级成功，都必须让用户感知到"出现了问题"
- 错误通知通过 AdapterManager 主动发送到指定频道/用户
- 即使备用方案奏效，输出中也应包含降级提示

---

## 实施顺序

1. 修复 ConversationHistory 拆分（预设消息模板 vs 运行时历史）
2. 搭建 Adapter 层 + ConsoleAdapter（让系统能跑起来）
3. 实现 WorkerEngine + MasterEngine 调度链路（核心流程打通）
4. 实现 SessionManager（Topic 分类 + 上下文管理）
5. 实现 MemoryService（记忆存取 + 检索）

---

## 待重构项

- ToolCall 重写 — 当前代码中的 ToolCall 结构（pipeIn/pipeOut/afterThan/input）需要按新设计（inputs 数组 + output + critical）重写
- ConversationHistory 彻底从 ApiClientCfg 中拆出（当前仅做了 JsonIgnore，职责仍混合）
