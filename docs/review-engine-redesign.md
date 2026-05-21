# ReviewEngine 重设计：自由探索模式

## 核心理念

抛弃预设模式（ChannelDaily/PersonProfile/CrossDomain/ContradictionDetect），改为无目标自由探索。
模型从种子内容出发，跟着好奇心走，所有行为从数据中涌现。

## 架构

- ReviewEngine 实现 ISubEngine + IAgentHost，独立生命周期
- DreamEngine Phase 2 启动后 fire-and-forget（ctx.StartEngine）
- 使用 Agent 循环，不限轮次，限总 token 预算
- 单层压缩：旧 browse 内容摘要化，保留 thinking_notes 和行动结果
- DeepSeek 模型（ReviewCore.json），输入缓存便宜，压缩主要防注意力涣散

## 种子机制

启动时 BuildStartInjectAsync 注入种子：

1. **有信标** → 列出所有未处理信标（消息位置 + reason + 标记时间），模型自己选 focus 目标
2. **无信标** → 随机选一个活跃频道，给几条近期消息预览
3. **有未完成进度** → 恢复上次的 findings + next steps

信标来源：频道循环工作时模型主动标记（mark_for_review 工具）。

## 游标机制

ReviewEngine 实例内存维护一个阅读游标（currentMessageId → 隐含 channelId）。

- `review_focus(message_id)` — 移动游标到指定消息
- `review_browse(count=20)` — 从游标处顺序读取当前频道消息，游标前进
- browse 输出不带消息 ID（干净的阅读流：时间+发言人(P#ID)+内容）
- search 输出带消息 ID（供 focus 跳转用）

## 工具集（14个）

### 导航（5个）

| 工具 | 参数 | 说明 |
|------|------|------|
| review_focus | message_id | 设置游标到指定消息 |
| review_browse | count? (默认20) | 从游标顺序读取，游标前进 |
| review_search_messages | query, channel_id?, person_id?, time_start?, time_end? | 按条件搜索消息，结果带 ID |
| review_search_memory | query, person_id?, limit? | 语义搜索记忆库 |
| review_get_person | person_id | 查询人物详情 |

### 行动（4个）

| 工具 | 参数 | 说明 |
|------|------|------|
| review_write_memory | content, importance?, person_id? | 写入记忆（描述要求先 search 确认无重复） |
| review_update_person | person_id, name?, aliases?, fast_memory? | 更新人物（描述要求先 get_person） |
| review_update_affinity | channel_id, delta | 调整亲和度（描述要求 ±0.1~0.2，说明理由） |
| review_link_memory | memory_id_a, memory_id_b, action(create/delete) | 创建/删除记忆关联 |

### 元（4个）

| 工具 | 参数 | 说明 |
|------|------|------|
| review_thinking_notes | action(read/append/clear), content? | 思考草稿，跨轮保留，压缩不丢 |
| review_save_progress | findings, next_steps | 保存进度（跨睡眠保持） |
| review_request_reinforcement | (无) | 请求备用预算（仅一次） |
| review_complete | (无) | 标记完成 |

### 补充：工作端信标工具

| 工具 | 位置 | 说明 |
|------|------|------|
| mark_for_review | 频道循环 working-tools | 标记当前位置为复盘信标，附带 reason |

## 工具描述内嵌工作流规范

- **review_write_memory**: "写入前请先 search_memory 确认无重复或高度相似的记忆。"
- **review_update_person**: "更新前请先 get_person 了解当前状态，确认有实质变化再修改。"
- **review_update_affinity**: "每次调整幅度 ±0.1~0.2，不要大跳。基于你阅读到的频道活跃度和互动质量。"
- **review_thinking_notes**: "browse 的原始内容可能会被压缩，但 notes 始终保留。养成边读边记的习惯。"
- **review_get_person**: "当你在消息中注意到某个人物并想了解更多时使用。不要仅凭单条消息下结论。"

## 每轮注入（BuildRoundInjectAsync）

每轮返回一条状态消息：

```
[预算] 已用: ~8000 / 上限: 30000 | 备用预算: 可申请
```

状态变体：
- `备用预算: 可申请` — 未使用，可 request_reinforcement
- `备用预算: 已激活 (+5000)` — 已申请，上限已扩展
- `备用预算: 不可用` — 系统已醒来

唤醒通知（语气平和，不催促）：
```
[通知] 系统已醒来。备用预算不可用，但你可以继续完成当前工作。
```

空转提醒（连续 2-3 轮只有导航没有行动时）：
```
[提示] 你已经浏览了一段时间没有记录。如果有想法可以先写入 thinking_notes。
```

行动判定：thinking_notes / write_memory / update_person / update_affinity / link_memory / save_progress / complete / request_reinforcement 算行动；browse / focus / search / get_person / get_links 不算。

## 压缩策略

**触发：** history token 超过阈值（如 30k）

**保留：**
- thinking_notes 全文
- 所有行动工具的调用和结果
- 最近 2-3 轮完整内容

**压缩：**
- 早期 browse 原文 → 一句话摘要（"阅读了频道X 14:00-15:30 的 20 条消息"）
- 早期 search 结果 → 摘要

**压缩后注入提示：**
```
[系统] 早期阅读内容已压缩。你的 thinking_notes 和所有行动记录完整保留。
```

## 系统提示词

```
你是 Lilara 的复盘模块。系统当前处于深度睡眠，你在离线状态下自由探索和整理。

## 工作方式
你的任务没有固定目标。从下方的起始内容出发，跟着好奇心走：
- 用 browse 顺序阅读消息流
- 用 focus 跳转到感兴趣的位置
- 用 search 拉取特定条件的消息或记忆
- 发现有价值的东西就行动（写记忆、更新人物、调亲和度、修正矛盾）

## 习惯
- 边读边记：看到重要信息先写 thinking_notes，browse 的原文可能会被压缩
- 先查再写：写记忆前搜索确认无重复，更新人物前先查看现状
- 小幅调整：亲和度每次 ±0.1~0.2，不要大跳
- 批量操作：你可以一次调用多个工具，不需要一个一个来

## 预算
你有有限的 token 预算。当剩余预算不多时，用 save_progress 保存进度后 complete。
如果工作进行到一半确实需要更多资源，可以申请一次备用预算。
```

## 预算配置（DreamConfig）

- ReviewTokenBudget: 总 token 上限（输入+输出）
- ReviewReserveBudget: 备用预算额度
- ReviewCompressionThreshold: 触发压缩的 history token 阈值

## 信标系统

### 数据结构（扩展现有 ReviewHint）

```
MessageId     int       标记位置
ChannelId     int       所在频道
PersonId      int?      相关人物（可选）
Reason        string    标记原因
CreatedAt     DateTime  标记时间
Processed     bool      是否已被复盘消费
```

### 消费策略

- 复盘启动时取所有未处理信标注入种子
- review_complete 时批量标记为已处理

### 去重

- 同一 MessageId 不重复标记
- 随机种子选择时排除最近 N 次复盘已覆盖的频道

## 与现有代码的关系

### 保留
- ReviewEngine 基本骨架（ISubEngine + IAgentHost + Agent 循环）
- ReviewControlImpl（唤醒通知 + 备用预算 + 完成状态）
- DreamEngine Phase 2 启动逻辑（ctx.StartEngine）
- ToolProfile "review" 控制工具可见性
- Plugin.ReviewTools 项目结构

### 删除/重写
- ReviewModeSelector（不再需要模式选择）
- ReviewMode enum（不再需要）
- 现有 10 个工具全部重写（参数和逻辑变化大）
- BuildSystemPrompt（新提示词）
- BuildStartInjectAsync（新种子逻辑）
- BuildRoundInjectAsync（新每轮注入）

### 新增
- 游标状态管理（ReviewEngine 内）
- 压缩逻辑（可复用 SummarizationCore 或新建）
- mark_for_review 工具（频道循环侧）
- review_get_person 工具
- review_get_links / review_link_memory 工具
- token 计数追踪（每轮累加 usage）
- 空转检测逻辑
