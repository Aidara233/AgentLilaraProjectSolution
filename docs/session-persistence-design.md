# Session 持久化与完整续跑设计

> 状态：**设计阶段，暂不实现**（用户拍板稍后做）。本文档先把方案定下来，实现时与第二批"频道级游标接力"对接。

## 目标

重启后 session **完整续跑（方案 A）**：从存档接着干，实现完美重启。重点解决频道 session 的"重启后同步禁用 ChannelEngine + 恢复管道"。

## 现状（为什么必须重做）

- `RuntimeSession.PersistStateAsync`：只写 `meta.json`（status/maxLevel/createdAt/roundCount/componentIds）。**丢了**会话历史、端口、claim、InitialState、WaitUntil、per-channel 游标。
- `SessionEngine.PersistAllAsync`：仅引擎优雅退出时调一次。崩溃/强杀不写。
- `RuntimeSession.RestoreStateAsync`：能读 meta，但**全仓无调用**，死代码。
- `SessionEngine.RestoreAll`：是个桩，扫到存档只 `Console.WriteLine` 不重建。
- 频道占用 `_channelFullClaims`：纯内存，**不持久化**。重启后 claim 蒸发 → ChannelEngine 直接接管频道回 Express，而 session 凭空消失。

旧 channel 方案反而有 `ChannelContextPersistence`（per-channel 原子 JSON）。session 这块是退化的。

## 核心设计：三文件分级，增量落地

每个 session 一个目录 `Storage/Sessions/{SessionId}/`，按**写入频率**拆三个文件：

| 文件 | 写入时机 | 内容 | 写入方式 |
|---|---|---|---|
| `spec.json` | 创建时**一次** | CreateSessionSpec 全量（ComponentIds/MaxLevel/AutoClaimChannel/InputPorts/OutputPorts/CoreConfigName/ParentSessionId/OverrideLimits/InitialState） | 写一次，不可变 |
| `state.json` | **每轮**重写 | roundCount/agentRound/status/waitUntil/lastActivity + per-channel 游标（channelId→LastConsumedMessageId） | 原子 tmp+move，体积小（几百字节）重写无所谓 |
| `history.jsonl` | **每轮追加** | 本轮新增的 Message（assistant turn 含 tool_use ContentParts、tool_result turn、注入的 user 消息），一行一条 | append-only |

**这就是避免"一大坨 JSON 重生成"的关键**：历史按**追加**增长（每轮 O(新增)），而非现在 `SaveContext` 的每轮全量重序列化（累积 O(总量²)）。state.json 虽每轮重写但极小，可忽略。

### 压缩特例

SummarizationCore 压缩历史（N 条 → 摘要）时不能只追加，需整体重写 `history.jsonl`——这是低频事件，照搬 `CrossRequestRegistry.CompactJournal` 的"清空+逐行重写"即可。摘要本身作为一条特殊 Message 落进 jsonl 头部。

## 恢复时序（频道同步的命门）

ChannelEngine 在**消息到达**（`ChannelEngineSpawnCheck.HandleMessageAsync`）或冷启动时孵化。所以恢复必须**抢在适配器开始投递消息之前**完成 claim，否则 ChannelEngine 会无视"尚未恢复的 claim"直接接管频道，造成双头消费。

恢复步骤（在 SessionEngine 初始化、监控循环首次 Delay 之前同步完成）：

1. 扫 `Storage/Sessions/*`，筛 status ∈ {Running, Crashed} 的存档。
2. 读 `spec.json` + `state.json` + `history.jsonl`。
3. **先重新 claim 频道**（同步填 `_channelFullClaims`）——这一步挡住 ChannelEngine 接管。
4. 走**同一条 CreateSession 路径**重建 session，但差异：
   - 复用存档 SessionId（非新 Guid）
   - 用 `history.jsonl` 重建 RunAsync 的起始 `messages`，**跳过** BuildStartAsync 的全新历史构建
   - per-channel 游标灌回 ChannelContextState
   - roundCount/agentRound/waitUntil 灌回
5. session 从断点继续循环。

⚠️ **开放风险（实现时必须验证）**：当前 `RestoreAll` 在 `SessionEngine.RunAsync`（:297）内调用。需确认 AutoStart 引擎顺序 + 适配器连接时机，保证"恢复+claim"先于消息路由。若有窗口，可能要把 claim 恢复提到更早（SessionEngine 构造或 SystemEvent.Started 同步段）。

## 与第二批"频道级游标接力"的关系（关键耦合）

第二批硬约束：**频道游标是频道级唯一真相，谁占有谁推进同一份**。
- 现在游标存两处（ChannelEngine 经 ChannelContextPersistence；session 经 ChannelContextState）。
- 本方案里 session 暂把 per-channel 游标存进 `state.json`，**是临时的**。第二批落地后，游标应统一到频道级存储，session 与 ChannelEngine 读写同一份；届时 `state.json` 不再存权威游标，只存会话私有的 roundCount 等。
- 这正是"稍后做"的合理性：可与第二批同期实现，避免游标真相源分裂。

## 崩溃半截状态

每轮追加 → 崩溃时 jsonl 持有到"最后完成轮"。但若崩在一轮中段（模型已调工具、tool_result 还没落），jsonl 里会有**孤儿 tool_use（无配对 tool_result）**。Claude API 要求严格配对，恢复时必须检测并处理：要么丢弃该悬空 assistant turn，要么补一条 error tool_result。落地原则：**整轮原子追加**（assistant+tool_result 两条一起写，写完才算一轮），从根上避免半截。

## 需要的代码改动（实现时）

**RuntimeSession**：
- 接受外部传入 SessionId（恢复用）。
- `messages` 从 RunAsync 局部提升为可被持久化钩子访问。
- 加每轮持久化钩子（镜像 `Agent.OnRoundCompleted`）：本轮 messages 定稿后，原子追加新消息到 history.jsonl + 重写 state.json。
- 加恢复路径：从 history.jsonl 灌 messages，而非 BuildStartAsync。

**SessionEngine**：
- CreateSession 时写 spec.json。
- RestoreAll 改成真恢复：重建 session + 重新 claim 频道（按上面时序）。
- PersistAllAsync 保留作优雅退出兜底，但每轮钩子才是主力。

**持久化模块**：新建 `SessionPersistence`（类比 ChannelContextPersistence），封装三文件读写 + jsonl 追加 + 压缩重写 + 原子写。

## 落地频率结论

**每轮必落地**（用户拍板，效果基础）。靠 history.jsonl 追加 + state.json 小文件原子重写把每轮成本压到最低。引擎优雅退出再兜底全量持久化一次。

## 待用户在实现前确认

- 恢复时序的开放风险（适配器/ChannelEngine 是否会在恢复前抢频道）需实测，可能影响 claim 恢复的放置点。
- 是否与第二批游标接力同期做（推荐），以免游标真相源临时分裂又重构。
