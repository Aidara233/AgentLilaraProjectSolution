# 架构重构设计 — 通用会话框架

> 日期：2026-06-20
> 基于 arch-redesign-plan.md 及多轮讨论压缩细化

---

## 一、架构分层

```
┌──────────────────────────────────────────────────┐
│  框架层（随进程启动，无生命周期管理）               │
│  ChannelEngine — Express 响应 + 冲动值 + escalate   │
├──────────────────────────────────────────────────┤
│  基础设施引擎层（ISubEngine，固定生命周期）          │
│  MasterEngine / TimerEngine / VisionEngine         │
│  DreamEngine / ExtractionEngine / SessionEngine    │
│  MessageEngine / AdapterEngine                     │
├──────────────────────────────────────────────────┤
│  会话层（IRuntimeSession，不定生命周期）            │
│  RuntimeSession — 统一会话，行为由 CreateSessionSpec 驱动 │
└──────────────────────────────────────────────────┘
```

**Session ≠ 框架。** Session 是一个完成指定任务的通用 agent 循环，由其发起者指定工具白名单和基础提示词来限制行为。ChannelEngine 是框架基础设施，不实现 IRuntimeSession。

---

## 二、数据流

```
适配器 ──▶ AdapterEngine ──▶ MessageEngine ──┬── ChannelEngine（Express+冲动值）
                                             ├── ExtractionEngine（记忆提取+监听）
                                             ├── WorkingSession（agent循环）
                                             └── EavesdropSession（监听判定）

出向：
Session ── speak/send_media ──▶ MessageEngine（入库）──▶ AdapterEngine（发送）──▶ 适配器
```

数据流单向，出向走 OutputPort → MessageEngine → AdapterEngine，不走反向管道。

---

## 三、MessageEngine（ISubEngine）

双向枢纽：入向入库+去重+标记+分发，出向入库+路由。

### 入向

```
AdapterEngine 发来消息
  → SELECT … WHERE ChannelId=x AND MessageId=y  ← 内存不用，直接查 DB 去重
  → 已存在（不论来源）? 丢弃（兄弟适配器重复 / 自己发的回声）
  → 不存在 → INSERT → 标记 tag（normal/mention/command）
  → 按 tag 分流到子流 → 推送订阅者
```

### 出向

```
Session 调 speak → OutputPort["reply"] → MessageEngine
  → INSERT 出向消息（SentByBot=true）
  → 发给 AdapterEngine → 适配器实际发送
  → 发送回执可反写（非必须）
```

### 子流

每条频道下按 tag 拆子流：`normal` / `mention` / `command`。消息类型标记在 MessageEngine 完成。

### 订阅接口

```csharp
class MessageEngine {
    // 消费者订阅：指定频道+子流 → 返回 IInputPort
    IInputPort Subscribe(int channelId, string subStream);
    void Unsubscribe(IInputPort port);
    
    // 入向入口
    Task AcceptIncoming(IncomingMessage msg);
    
    // 出向入口
    Task SendOutgoing(OutgoingMessage msg);
}
```

---

## 四、AdapterEngine（ISubEngine）

### 职责

1. **托管适配器生命周期** — 取代现有 AdapterManager
2. **频道融合** — 多个适配器服务同一频道时合并为一条流
3. **消息分段** — Session 产出一条完整回复，AdapterEngine 按平台规则拆分发送
4. **发送优先级** — 多适配器时选优先级最高、最低 ID 的活着适配器发送

### 发送决策

```csharp
class AdapterHandle {
    int Priority;        // 越小越优先
    string AdapterId;    // 同优先级选 ID 最小
    bool IsAlive;
}

GetActiveAdapter(channelId):
  所有服务该频道的适配器 → 过滤 IsAlive → 按 Priority ASC, AdapterId ASC → 取第一个
```

### 与 MessageEngine 对接

- 入向：适配器消息 → `MessageEngine.AcceptIncoming()`
- 出向：`MessageEngine` 调用 AdapterEngine 的发送端口
- 入向去重：依赖 MessageEngine 的 DB 查重（消息 ID 统一）

---

## 五、ChannelEngine（框架层，非 ISubEngine）

全局单例，不实现 IRuntimeSession。进程启动即运行。

### 职责

1. **冲动值计算** — 每条消息查最近 20 条 → 计算冲动值（不持久化热力，纯计算）
2. **Express 响应** — 冲动值 > Express 阈值 → 直接执行 Express agent 循环
3. **频道占用检查** — 频道已被 Full 占用？→ 跳过（已有 Session 在处理）
4. **消息分发** — 调 MessageEngine 分发

WorkingSession 的**创建**由 Express 的 escalate 工具决定，不由冲动值直接触发。冲动值只驱动 Express 响应，是否升级到 Working 是 Express 的判断。WorkingSession 一旦存在，新消息通过端口权重累积决定何时激活它（见第八节）。

### Express 工具白名单（5 个）

```
speak        — 回话
send_media   — 发图/表情
escalate     — 升到 WorkingSession
wait         — 主动停止（Express 默认 auto-continue，调 wait 才停）
memory       — 只读记忆检索
```

当前时间每轮自动注入上下文，不作为工具。

Express 不能读文件、不能写文件、不能搜索、不能开子 agent。Express 不需要 ComponentHost，直接使用 IChannelAccess + 内置工具。

### Express 双模板

Express 有两套 persona 模板，由睡眠状态控制：

| 模板 | 触发 | 特点 |
|------|------|------|
| 清醒 | 正常状态 | 正常语气 |
| 梦话 | 睡眠期被@唤醒 | 提示词注入"你正在睡觉，说话可以语无伦次、逻辑崩坏" |

模板切换由 ChannelEngine 根据当前睡眠状态自动选择，不需 Express 感知。梦话使用相同的 5 个工具白名单，无额外功能。梦话素材用提示词模板即可，不需要记忆库随机拼凑。

### 伪代码

```csharp
class ChannelEngine {
    async Task OnMessage(IncomingMessage msg) {
        // 频道已被 Session Full 占用？跳过
        if (sessionEngine.IsChannelClaimed(msg.ChannelId))
            return;

        var recent = await db.GetRecentMessages(msg.ChannelId, 20);
        var impulse = CalculateImpulse(recent, msg);
        
        if (impulse > ExpressThreshold)
            await ExecuteExpressResponse(msg, recent);
        // 注意：WorkingSession 由 Express 的 escalate 工具创建，不由冲动值触发
        
        await messageEngine.AcceptIncoming(msg);  // 分发
    }
}
```

---

## 六、IRuntimeSession — 统一会话接口

Session 不分子类，行为差异由 `CreateSessionSpec`（组件列表 + 端口配置 + 超限参数）驱动。

```csharp
interface IRuntimeSession {
    // 标识
    string SessionId { get; }
    int MaxLevel { get; }              // 0-3
    SessionStatus Status { get; }
    bool IsAlive { get; }
    
    // 组件与工具
    IReadOnlyList<string> ComponentIds { get; }  // 启用的组件
    IReadOnlyList<string> ActiveTools { get; }   // 当前可用工具名
    
    // 端口 I/O — 数据来源和去向
    IReadOnlyDictionary<string, IInputPort> Inputs { get; }
    IReadOnlyDictionary<string, IOutputPort> Outputs { get; }
    
    // 生命周期
    string? ParentSessionId { get; }
    SessionLimits Limits { get; }
    Task RunAsync(CancellationToken ct);
    void RequestStop(string reason);
    bool CheckLifetimeLimits();
    DateTime? WaitUntil { get; }
    
    // 持久化
    Task PersistStateAsync();
    Task RestoreStateAsync();
    
    // 组件管理
    void EnableComponent(string componentId);
    void DisableComponent(string componentId);
    
    // 等级
    void SetLevel(int newLevel);
}
```

### 端口模型

**输入** — 注入到 agent 上下文，每轮 drain 新数据：

```csharp
interface IInputPort {
    string Name { get; }
    Type DataType { get; }
    int Weight { get; }         // 单条消息的激活权重：100=立即，5=攒多了激活
    Task<object?> ReceiveAsync(CancellationToken ct);
    bool TryPeek(out object? data);
}
```

**输出** — 工具写端口，工具不知道自己连到哪：

```csharp
interface IOutputPort {
    string Name { get; }
    Type DataType { get; }
    Task SendAsync(object data);
}
```

### 端口决定工具注册

有对应 OutputPort 才注册对应工具：

```
OutputPort["reply"]  → 注册 speak, send_media
OutputPort["parent"] → 注册 speak（连到父 Session）
OutputPort["alert"]  → 注册 alert
```

### enable_component / disable_component

Session 可动态加载/卸载本机已安装的插件组件，无需重启。模型自行判断需要什么工具，和调 `web_search` 一样是 L0 行为。框架信任模型的判断力。默认组件集只是起点。

---

## 七、SessionLimits — 统一生命周期限制

所有 Session 共享限制规则，基类统一检查：

```csharp
class SessionLimits {
    TimeSpan MaxLifetime { get; init; } = TimeSpan.FromHours(2);
    TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(10);
}
```

MaxMessages 不设硬限制——轮次由经济模型约束，Session 自行判断续期价值。

### 默认限制参考值

| 场景 | MaxLifetime | IdleTimeout |
|---|---|---|
| Working | 2h | 10min |
| Task | 30min | 5min |
| Eavesdrop | 15min | 5min |

创建 Session 时可根据场景覆写 Limits。

### 轮次定价

所有 Session 一律免费 20 轮。之后按上下文膨胀递增加价，对接现有 `ICurrencyService`（Bot 全局单账户，JSON 持久化）：

```
第 21-30 轮：1 币/轮
第 31-40 轮：2 币/轮
第 41-50 轮：3 币/轮
...

第 N 轮（N > 20）：ceil((N - 20) / 10) 币
```

**扣费流程：**

```
每轮开始前：
  余额 >= 本轮价格 → Spend 扣费，正常执行
  余额 < 本轮价格 → 不扣费，本轮注入警告，Session 自行收尾
```

### 余额分级警告 + 能力收紧

| 等级 | 余额范围 | 行为 |
|------|---------|------|
| 正常 | > L1 | 无警告 |
| L1 提醒 | ≤ L1 | 扣费越线时注入一次警告 |
| L2 收紧 | ≤ L2 | 新 Session 初始化时注入警告；禁止创建 TaskSession；禁止提权 |
| L3 危急 | ≤ L3 | 所有活跃 Session 每轮注入警告；TaskSession 禁开；Express 缩短至 5 轮；禁止提权、禁止子 agent |

> 阈值可配，默认 L1=500, L2=200, L3=50。

警告信息带余额数字 + 等级说明 + 暗示："余额不足时可向管理员要钱"。Session 自己决定开口还是收工。

---

## 八、Session 激活

Session 的**创建**由 Express 的 escalate 决定（或用户指令/ScheduledTask/TODO 触发）。激活不再依赖 Gate 闸门——行为由输入端口自然决定：

- **有输入端口** → 有新数据到达时直接激活一轮。权重端口分类消息（mention=100 立即激活，normal=5 多攒几条），分类工作在 MessageEngine 完成。Session 不感知"mention"，只感知端口 weight。
- **无输入端口（部分 task）** → 创建后立即开始执行，跑完结束。

Session 调用 `wait` 后进入等待态，设 `WaitUntil`。两条唤醒路径：
1. 新数据到达输入端口 → 提前唤醒
2. `WaitUntil` 到期 → SessionEngine 30s 循环检测 → 激活一轮

**无论哪种激活，立即清除 `WaitUntil`**。超时激活时通过输入端口注入标记消息 `{ type: "timeout", wait_reason: "..." }`。

### TODO 激活

TODO 无确切时间，激活动机来自 Session 自身判断：

- **空闲翻翻** — wait 超时激活，没有新消息，Session 检查 TODO
- **上下文触发** — 相关人/频道活跃时，SessionEngine 创建 Session 时注入相关 TODO
- **手动** — 用户指令或 Session 自行决定查看

---

## 九、权限体系 — 组件白名单 + 等级

### 工具等级（ToolLevel）

每个工具声明一个安全等级：

```
L0 — 默认全开
  所有正常工具：聊天、文件、搜索、网络、邮件、SSH、子agent…
  bot 是自身行为的负责者，这些都是其自然能力

L1 — 需授权（预留）
  修改 bot 自身配置的工具

L2 — 需 Admin 二次确认（预留）

L3 — 预留
```

目前几乎所有工具都是 L0，高等级基本空置。未来出了 L1+ 工具时门槛才生效。

### Session 权限

创建 Session 时指定：

```csharp
class CreateSessionSpec {
    string[] ComponentIds;     // 要激活的组件，如 ["basic-tools", "file-tools"]
    int MaxLevel;              // 工具等级上限，默认 0
    bool AutoClaimChannel;     // 自动 Full 占用归属频道
    // ... 端口、Limits、InitialState 等
}
```

**可用工具 = 所选组件的所有工具 ∩ MaxLevel 过滤**。

### 默认组件集

| 场景 | 默认组件 | 说明 |
|---|---|---|
| Express | basic-tools, memory-tools | speak/send_media/escalate/wait/memory |
| Working | basic-tools, memory-tools, working-tools, file-tools, file-ops, system-tools | 频道全功能 |
| Task | basic-tools, memory-tools, file-tools, file-ops | Working 去掉 system-tools（禁止套娃） |
| Eavesdrop | basic-tools | speak/alert |

创建时也可以追加网络、搜索、图片等其他组件。

### 动态组件管理

Session 可用 `enable_component` / `disable_component` 自行加载/卸载插件组件，L0 行为，无需父级授权。框架信任模型的判断力。

### 等级调整

- **创建时指定** — `CreateSessionSpec.MaxLevel`
- **子 Session** — 默认继承父的 MaxLevel，可降不可升
- **频道内提权** — 用户通过指令 `/elevate session_id L1`
- `SetLevel(newLevel)` 直接调整

---

## 十、SessionEngine（ISubEngine）

```csharp
class SessionEngine : ISubEngine {
    ConcurrentDictionary<string, IRuntimeSession> _sessions;
    
    // RunAsync：30秒监控循环 + 启动恢复 + 退出全量持久化
    // 每轮检查：所有 WaitUntil <= now 的 Session → 通过输入端口注入 timeout 消息激活
    async Task RunAsync(CancellationToken ct);
    
    // 创建
    IRuntimeSession CreateSession(CreateSessionSpec spec);
    
    // 停止
    StopResult StopSession(string id, string reason);
    StopResult ForceKill(string id);  // 无视子会话
    
    // 查询
    IRuntimeSession? GetSession(string id);
    bool HasActiveWorking(int channelId);
    bool Authorize(string sessionId, string toolName);
}
```

### CreateSessionSpec

```csharp
class CreateSessionSpec {
    string[] ComponentIds;                     // 要激活的组件
    int MaxLevel = 0;                          // 工具等级上限
    bool AutoClaimChannel = false;             // 自动 Full 占用归属频道
    Dictionary<string, PortSpec> InputPorts;   // 端口名 → (频道, 子流, 权重)
    Dictionary<string, string> OutputPorts;    // 端口名 → 目标
    object? InitialState;                      // 初始注入（ScheduledTask/TODO描述/历史消息）
    string? ParentSessionId;
    SessionLimits? OverrideLimits;
}

class PortSpec {
    int ChannelId;
    string SubStream;   // "normal" / "mention" / "command"
    int Weight;         // 单条消息的激活权重
}
```

### 父子级联

```csharp
class StopResult {
    bool Success;
    string? BlockReason;
    List<string>? BlockingChildren;  // 阻止停止的子会话
}

// 父停时阻塞（子还活着）; ForceKill 可强杀整个树
```

### 崩溃恢复

```
Storage/Sessions/{sessionId}/
  ├── meta.json     — type, status, created_at, port_config, limits
  ├── context.json  — agent 上下文（压缩摘要+保留消息）
  └── state.json    — cursor, mode, pinboard, thinking_notes

启动：
  RestoreAllAsync() → 扫描目录 → 
    状态 Running/Crashed → 实例化 → RestoreStateAsync() → RunAsync()
    状态 Completed → 跳过
```

---

## 十-B、频道占用

Session 需要和频道交互时（讨债、问话等），通过 SessionEngine 声明频道占用，抑制 ChannelEngine 重复创建。

### ClaimLevel

```csharp
enum ClaimLevel { Full, Listen }

Full   → 接管 reply 端口 + 抑制 ChannelEngine（该频道不再创建 Express/Working）
Listen → 只读消息流，不抑制 ChannelEngine（旁听）
```

同一个频道允许多个 `Listen` 占用，最多一个 `Full`。

### 自动占用

**WorkingSession 创建时自动以 Full 占用其归属频道**（端口配置中的 channelId）。无需显式调用 ClaimChannel。Session 结束或 deescalate 回 Express 时自动释放。

跨频道占用（例如 TaskSession 接管不相干频道的对话）仍需显式调用 `ClaimChannel`。

### 占用 API

```csharp
class SessionEngine {
    // 单独占用一个频道
    bool ClaimChannel(string sessionId, int channelId, ClaimLevel level);
    
    // 查询：谁占了这个频道的 Full？
    string? GetFullClaimer(int channelId);
    bool IsChannelClaimed(int channelId);
    
    // 释放
    void ReleaseChannel(string sessionId, int channelId);
}
```

一个 Session 同时只能 Hold 一个 Full 频道。跨频道需求顺序拿、顺序放：

```
TaskSession:
  ClaimChannel(A, Full) → 交涉 → ReleaseChannel(A)
  ClaimChannel(B, Full) → 汇报 → ReleaseChannel(B)
```

不允许同时持有多个 Full，死锁无窗口。

### ChannelEngine 检查

```csharp
// ChannelEngine.OnMessage:
if (sessionEngine.IsChannelClaimed(channelId))
    return;  // 有 Session 在处理，不插手
```

### MessageEngine 路由

被 Full 占用的频道消息跳过 ChannelEngine，直接路由到占用 Session 的消息端口。

Session 结束或调 `ReleaseChannel` → SessionEngine 释放占用 → ChannelEngine 恢复正常监听。

---

## 十-C、定时任务（ScheduledTask）

有确切时间的定时任务，统一合并一次性和重复任务。

### 数据结构

```csharp
class ScheduledTask {
    string TaskId;
    string Description;          // 任务描述（注入 Session 的初始提示词）
    ScheduleType Type;           // Once / Recurring
    DateTime ExecuteAt;          // 下次执行时间
    string? CronExpression;      // Recurring 用
    int? SourceChannelId;        // 来源频道
    int? SourcePersonId;         // 来源用户
    string Status;               // pending / executing / completed / cancelled
    DateTime CreatedAt;
}
```

Once 执行完自动 `completed`，Recurring 执行完自动排下一次。

### 生命周期

1. **创建** — Session 调 `schedule_task` 工具（或用户指令/插件），SessionEngine 写入 DB
2. **触发** — SessionEngine 30s 循环检测 `ExecuteAt <= now` → `CreateSession("task", { taskId })` → TaskSession 读取描述，自行 ClaimChannel + 执行
3. **完成/重复** — Once → `completed`；Recurring → 更新 `ExecuteAt` 到下一次

---

## 十-D、TODO 列表

无确切时间的待办事项，靠 Session 主动想起。

### 数据结构

```csharp
class TodoItem {
    string TodoId;
    string Description;          // "张三还欠我两块钱"
    int? RelatedPersonId;        // 关联用户（上下文触发用）
    int? RelatedChannelId;       // 关联频道
    decimal MinBalance;          // 最低余额要求：0=砸锅卖铁也得做；>0=余额够才考虑
    string Status;               // pending / done / cancelled
    DateTime CreatedAt;
}
```

### 激活方式

| 方式 | 场景 |
|------|------|
| 定时扫描 | SessionEngine 每 N 分钟扫 pending TODO，余额 >= MinBalance → 开 TaskSession |
| 空闲翻翻 | wait 超时激活，无新消息，Session 翻 TODO |
| 上下文触发 | 关联人/频道活跃时，创建 WorkingSession 时注入相关 TODO |
| 手动 | 用户指令或 Session 自行决定查看 |

MinBalance=0 的 TODO 永远符合条件（"讨债"再穷也得去）。MinBalance 高的 TODO 只在资金充裕时考虑（"搜集 AI 新闻"余额不到 100 就先搁着）。

TODO 不走定时队列。SessionEngine 创建 WorkingSession 时，将关联该频道/人的 pending TODO 注入上下文（类似记忆注入）。Session 可用 `add_todo` / `list_todos` / `complete_todo` 工具管理。

---

## 十一、ExtractionEngine（ISubEngine）

统一记忆提取 + 监听检测，共用 LLM 调用。

### 触发

每个频道独立计数。新消息累计到 10 条 → 一批处理。

### 一批处理内容

```
10条新消息 + 少量旧消息做上下文 → 一次 LLM 调用：

  1. 记忆提取（现有流程）→ 存入记忆库
  2. 监听检测（蹭 LLM）→ 检查该频道的监听规则
     → 命中 → SessionEngine.CreateSession("eavesdrop", ...)
```

### 全频道监听

指定频道跟着消息流实时走。全频道监听走定时扫描：

```
定时器（每 5-30 分钟）
  → 对每个全频道监听任务：
    查 DB → 自上次检查以来的消息
    → 先用关键词粗筛（滤掉 99%）
    → 剩余交轻量 LLM 判断
```

### 监听任务定义

```csharp
class EavesdropTask {
    string TaskId;
    string Name;              // "关注A群AI新闻"
    int[] ChannelIds;         // 空=全频道
    EavesdropRule[] Rules;    // [{ Keywords: ["AI","GPT"], Hint: "AI相关讨论" }]
    int NotificationTargetId; // 汇报到哪个频道
    string Status;            // active / paused / expired
}
```

监听就是监听——实时检测，发现动静立即响应。定时汇总之类的是定时 Session 的活，不混在一起。

### EavesdropSession

一次性短命判定会话：

```
触发 → 拿到触发消息+少量上下文 → 判断是否真的值得报告 → 报告/忽略 → 结束
```

限制：MaxMessages=5, IdleTimeout=5min。

---

## 十二、场景默认配置

Session 统一为 `RuntimeSession`，差异由 `CreateSessionSpec` 配置驱动。

| 场景 | 默认组件 | 自动 Claim | 说明 |
|---|---|---|---|
| express | basic-tools, memory-tools | ❌ | ChannelEngine 内部，不走 SessionEngine |
| working | basic-tools, working-tools, file-tools, file-ops, memory-tools, system-tools | ✅ Full | 频道全功能，可开子agent |
| task | basic-tools, file-tools, file-ops, memory-tools | ❌ | 任务执行，禁止套娃 |
| eavesdrop | basic-tools | ❌ | 监听判定 |

### 创建方式

Express 由 ChannelEngine 冲动值触发（不经过 SessionEngine），其余通过 `SessionEngine.CreateSession(spec)` 创建。

---

## 十三、Session 间通信

两种模式，覆盖全部场景。

### 1. 端口直连（父子/创建者-被创建者）

创建 Session 时，SessionEngine 自动搭线。例如 TaskSession 的 `OutputPort["result"]` 直连父 Session 的 `InputPort["child_result"]`。Session 只知道往端口写，不知道连到了谁。

### 2. 消息注入（跨频道）

往 MessageEngine 发内部消息（`SentByBot=true, InternalOnly=true`），指定目标频道。MessageEngine 按当前状态路由：

```
目标频道 B:
  ├── 有 WorkingSession  → 注入其 InputPort → WorkingSession 自行决定时机插入
  ├── 被 TaskSession Full 占用 → 注入该 Session 的 InputPort
  └── 无活跃 Session   → 走 ChannelEngine → 冲动值判断 → Express 可立即递送
```

发送方不需要知道 B 的状态，MessageEngine 统一处理。无 Session 时 Express 可即时送出通知，不用等。

典型场景：EavesdropSession 报告命中、TaskSession 跨频道提醒、TODO 讨债结果汇报。

### 不需要平级直连

两个 TaskSession 协作 → 共享父 Session 协调，不直接对话。两个频道 WorkingSession 需要同步 → 走 MessageEngine 消息注入。所有场景已被父子端口和 MessageEngine 覆盖，不引入平级 Session 间直连。

---

## 十四、去中心化意识 + 自激活定时

### SystemSession 删除

旧设计中 SystemEngine/SystemSession 是"Lilara 的核心意识"——唯一的决策中枢，所有频道的跨循环请求、子 agent 创建都经过它。

新架构中，**每个 Session 自己就是活着的 Lilara**。意识分布在各活跃 Session 中，不再需要中心化的"大脑"。

SystemSession 原有职责重新分配：

| 旧职责 | 新归属 |
|--------|--------|
| 子 agent 创建 | WorkingSession 直接调 `SessionEngine.CreateSession("task")` — 原生能力 |
| 跨循环请求 | SessionEngine 托管请求队列（纯数据结构，非 agent 循环） |
| 定时任务触发 | TimerEngine → `SessionEngine.CreateSession(...)` |
| "没事找事"（自学习） | 拆成触发源 + SessionEngine 创建 |
| 空闲检查 | Session 的 `wait` 超时自激活 |

### 自激活定时（wait 超时）

Session 调 `wait(reason, timeout_minutes)` 后进入等待态，设 `WaitUntil = now + timeout`。
两条唤醒路径：

1. **端口权重达阈值** → 有新消息 → 提前唤醒
2. **WaitUntil 到期** → SessionEngine 30s 循环检测 → 通过输入端口注入 timeout 消息 → 激活

超时激活后 Session 跑一轮，自己决定：释放、说句话、还是继续等。不需要 `NextAutoActivation` 和 `OnAutoActivateAsync` 接口方法。

### TimerEngine 定位收缩

TimerEngine 不再管理 Session 的时间逻辑。它只负责**无 Session 归属的定时触发**——也就是 cron 式的"到时间做某件事"。

但这本质上也是插件的活（Plugin.ScheduledTasks）。Session 体系完善后，TimerEngine 可能退化到可以删除。

**当前保留**，因为插件系统的 cron 基础设施仍依赖它。

---

## 十五、基础设施引擎（不变）

以下引擎不改动，保持 ISubEngine：

| 引擎 | 职责 | 改动 |
|------|------|------|
| MasterEngine | 内核，SpawnCheck 注册 + 全局组件托管 | 注册表加新引擎 |
| TimerEngine | 定时任务触发（cron 基础设施） | 定位收缩：不再管 Session 自激活定时，可能后续退化到插件 |
| VisionEngine | 图片描述 + OCR 调度 | 不变 |
| DreamEngine | 睡眠巡逻 + 秩序维护 + 热力衰减 | 梦话功能移入 Express 双模板；巡逻/秩序保留；睡眠期消息拦截后续移到 MessageEngine |

---

## 十六、插件系统变化

**不变**。Session 复用现有组件体系（ILoopComponent/IGlobalComponent）。`CreateSessionSpec.ComponentIds` 指定启用哪些组件，Session 内部创建 ComponentHost 实例管理。无需新增接口。

**不变**：
- plugin.json 格式
- ITool 接口
- IGlobalComponent / ILoopComponent
- PluginLoader
- ToolRegistry

---

## 十七、EventBus 退化

EventBus 从"万能总线"退化为"控制信号专用"，不再承载业务消息：

保留：
- EngineStartSignal / EngineStopSignal
- ConfigChangeSignal
- 其他内部控制信号

移除：
- 所有业务消息推送（由 MessageEngine 取代）
- 跨循环通信数据（由 Session 间 QueuePort 取代）

---

## 十八、WebUI 更新范围

需要更新的：
- Dashboard — 加 SessionEngine、ExtractionEngine 状态卡片
- Engines — 新增引擎卡片，移除 ChannelEngine（不再 ISubEngine）
- EngineDetail — 新引擎详情页
- Channel — 改查 SessionEngine 而非 ChannelEngine
- Plugins — 更新组件类型名（ILoopComponent→ISessionComponent）
- 可能新增 Sessions 页面 — 后台看所有活跃 Session

不需要改的：
- Memory / Logs / Config / Adapter / Modes / Console / Dream / Review

---

## 十九、迁移顺序（建议）

1. **新增基础设施** — 接口定义（IRuntimeSession, IInputPort, IOutputPort, SessionLimits）+ SessionEngine + MessageEngine + AdapterEngine。纯新增，不影响现有代码。
2. **重构 ChannelEngine** — 去掉 isWorkingMode、Working 代码、ComponentHost。保留冲动值+Express。全局单例化。
3. **实现 RuntimeSessionBase + WorkingSession** — 从旧 ChannelEngine 搬运 Working 代码。
4. **实现 EavesdropSession + ExtractionEngine**。
5. **清理** — 删除 ChannelEngineSpawnCheck、ChannelContextPersistence、旧 ContextPersistence。删除 SystemEngine/SystemSession。
6. **WebUI 更新**。
7. **插件接口迁移** — ILoopComponent→ISessionComponent，给插件作者过渡期。

---

## 二十、待后续细化

- [ ] EavesdropTask 的 WebUI 管理页面
- [ ] 全频道监听的采样策略（SQL 查询优化）
- [ ] ScheduledTask 和 TODO 的 WebUI 管理页面
- [ ] Sessions 统一管理页面 — 所有活跃/历史 Session 列表 + 状态监控
- [ ] WebUI 导航更新 — 新增 Sessions、ScheduledTask、TODO、EavesdropTask 入口
