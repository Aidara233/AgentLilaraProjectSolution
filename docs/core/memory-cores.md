# 记忆处理核心

心智体系下只剩一个记忆相关核心：`ExtractionCore`（伴随提取）。

旧的记忆核（`MemoryExtractionCore` / `RelationClassificationCore` / `ConsolidationCore` / `LinkCore` / `DedupCore` / `CombineCore` 等）已随记忆重构整体删除——旧记忆库、做梦系统、复盘系统一并推倒重来，对照表见 `docs/memory-redesign/04-migration-todos.md`。

记忆核心为纯工具性（无角色扮演），通过 `ResetProcessor()` 确保每次调用独立的对话上下文。

---

## ExtractionCore（伴随提取）

**文件：** `Core/ExtractionCore.cs`
**实例化：** `MindEngine` 构造时一次（常驻，随心智引擎生命周期）
**配置：** `Storage/Core/ExtractionCore.json`（轻量模型，temperature 0.3）
**提示词：** `Storage/Core/Prompts/ExtractionPrompt.txt`（可热改，不需重编译）

### 功能

从攒批的聊天消息中提取语义三元组断言。单轮、无工具、无状态——不开 session。

实现 `IExtractionLlm`（契约定义在 `Engine/Mind/ExtractService.cs`），由 `ExpressExtractor` 消费：

```csharp
Task<string?> ExtractClaimsAsync(string context)  // context = 索引化消息列表
```

调用方只提供消息列表，提取指令全在提示词模板里，通过 `{{MESSAGES}}` 占位符注入。

### 输出契约

```json
{"claims":[
  {"msg":0,
   "subject":{"type":"entity","index":"咪咪"},
   "predicate":{"type":"relation","index":"喜欢"},
   "object":{"type":"keyword","index":"三文鱼"},
   "raw":"咪咪喜欢吃三文鱼",
   "strength":0.8, "credibility":0.7, "inferred":false}
]}
```

| 字段 | 说明 |
|---|---|
| `msg` | 该断言来自哪条消息（回连 source 引用 + utterance_id）；定位失败则丢弃 |
| 三槽位 | `{type, index}`。缺任一则丢弃该条 |
| `strength` | 态度强度 `[-1,1]`，否定用负值；客观事实留空 |
| `credibility` | 模型按上下文态度自评，缺省 0.7（默认不假设为真），夹取 `[0.05, 0.95]` |
| `inferred` | 直述 false / 推断 true |

槽位类型：`entity`（有名字的人物）、`keyword`（普通概念）、`attribute`（属性名或值）、`literal`（数值日期，**不建关键词索引**）、`assertion`（引用断言，填 `ref`）。predicate 限 `relation` / `attribute`。

非法 type 纠正为 `keyword`（predicate 纠正为 `relation`）；解析失败或槽位残缺一律丢弃，不兜底造断言——消息仍在数据库里，可后续重提。

### 可信度自评

判据是**上下文里的态度**，不是"谁说的"。用户亲口说也只给 0.7，只有出现明确印证或否定信号时才偏移：他人赞同 0.85~0.95、用户自己迟疑 0.3~0.5、明显玩笑 0.2~0.4、被他人否定 0.1~0.3、模型推断 0.3~0.6。

上限夹 0.95 而非 1.0：锚点进 logit 时 1.0 会被 clamp 到 1-1e-9（logit ≈ 20.7），证据边基本推不动，后续 Review 也失去调节空间。

### 为什么产出完整槽位

早期实现让模型只填 `strength`，框架套死骨架产出 `发送者 --说了--> 整条消息(literal)`。这类断言 Writer 会跳过关键词索引（literal 槽位不建索引），导致检索检不到、冲突检测测不了、语义没解析——存了等于没存。现在模型直接产语义三元组，keyword 槽位建索引后才能真正参与检索与排他冲突检测。

详细链路（触发条件、攒批阈值、空闲判定）见 `docs/architecture-map.md` 的伴随提取小节。
