# A1 改动清单：`axes` 模块落地

> 阶段 A（对齐 ALC 形状，**仍在 1 个 csproj 内**）的第一步。
> **本清单只列要改什么，不包含代码**。确认后我按「编译 → 提交」的顺序做。
> 前置：`decisions.md` 已定稿（D1–D8）。

---

## 1. 目标与边界

**做**：把 ALC 的轴机制落成本项目的一个模块——轴表（唯一真相）、冻结快照、per-session 轴值、`IAxisPort` 出口。

**不做**：
- 不改任何现有行为（轴值未设时，发出去的请求参数与现在**逐字节相同**）
- 不做插件轴注册（`plugin-host` 侧留到 A4 之后）
- 不做 WebUI 控制面（webui 走独立进程，届时经 API）
- 不改提示词模板（三槽位是 A2/A3 的事）

---

## 2. 现状核查（四条事实，决定改动形状）

| # | 事实 | 证据 | 影响 |
|---|---|---|---|
| F1 | **本项目没有任何"轴"概念** | `grep -rln 'Axis\|Axes'` 命中的 6 个文件全是别的含义（`FrameworkEvent`/`Journal` 的字段、`EmotionService` 的"运动"） | A1 是**纯新增**，不迁移旧轴 |
| F2 | **请求参数已存在，但在 client 配置层** | `Client/ApiClientCfg.cs:21-22` `Temperature` 默认 **0.7**；`:25` `MaxTokens` | 轴要覆盖它，不能并行两套 |
| F3 | **`effort` 不存在** | 全仓无 `Effort`；只有 `OpenAIModelClient.cs:747` 提到 `reasoning_effort` 与 thinking 互斥 | `effort` 是纯新增，属于**映射层**（轴值 → 各协议的参数名） |
| F4 | **`SessionStateSnapshot` 无轴字段** | `SessionPersistence.cs:384-399` 共 14 个字段；`StateFormatVersion = 2` | 要加 `Axes` 字段 → 版本升 3 |

### 一个必须指出的差异

ALC 的 `temperature` 默认值是 **`1.0`**，注释写得很明确：

> 默认 `1.0` = **两家 API 各自的默认采样温度** ⇒ 发它等于不发……**不是**看着更讨喜的 0.7 —— 那会把每一个既有会话的采样行为**悄悄改掉**。

**而本项目现在默认是 `0.7`**（`ApiClientCfg.cs:22`）。

所以：**轴表的 `temperature` 默认值必须写 `0.7`，不能照抄 ALC 的 `1.0`。** 否则「轴默认值」与「实际发出去的值」不一致，第一天的行为就变了。

---

## 3. 新增文件（`AgentCoreProcessor/Engine/Axes/`）

| 文件 | 内容 | 量级 |
|---|---|---:|
| `Axes.cs` | 轴表（`Table` + `Spec`）、`Names`（表序）、`RenderOrder`（ordinal）、`ValuesOf`/`LabelOf`/`InContext`/`DescriptionOf`/`TryParse`/`NumberOrNull`/`LegalValuesText`/`Defaults`、数值轴 `Grid`/`Format`/`NumericRange` | ~420 |
| `AxisDefinition.cs` | `AxisDefinition`（插件轴注册用）、`AxisSnapshot`（冻结快照）、`AxisValue`、`AxisCapability`（先只声明 `None`，其余 `NotSupported`） | ~90 |
| `IAxisPort.cs` | 模块端口：`Values(axis)` / `Label(axis)` / `TryParse` / `Describe` / `Number` / `Defaults()` / `Freeze(values)` / `RenderOrder` / `Names` / `IsNumeric` | ~40 |
| `AxisStore.cs` | per-session 轴值的持久化（落在 `state.json` 的 `axes` 字段，不另起文件，见 D3 的"只存扁平值"） | ~60 |
| `AxisPort.cs` | `IAxisPort` 实现，注入 `AxisStore` | ~80 |

**总量约 690 行**，全部新增，零改动现有逻辑。

---

## 4. 修改文件

| 文件 | 改什么 | 风险 | 是否可逆 |
|---|---|---|---|
| `Engine/Session/SessionPersistence.cs` | ① `SessionStateSnapshot` 加 `Dictionary<string,string> Axes`（默认空，旧文件读出为 null → 补 `Axes.Defaults()`）② `StateFormatVersion` **2 → 3** | **中**：升版会让所有旧文件在下次写入时变成新格式。ALC 的做法是「旧文件仍可读，读取不回写；下次写入用当前版本」——照抄 | 是（版本号 + 字段） |
| `Engine/Session/RuntimeSession.cs` | ① 持有一个 `Dictionary<string,string> _axes`（初值 `Axes.Defaults()`）② `BuildStateSnapshot`/`RestoreFromSnapshot` 带上它 ③ 暴露 `IAxisPort` 给内部 | **低**：只是搬运一个新字段 | 是 |
| `Client/ApiClientCfg.cs` | **不动**（见 §5 的 A1-Q1） | — | — |
| `Client/ClaudeModelClient.cs` / `OpenAIModelClient.cs` | **不动**（A1 不接请求参数，留给 A4） | — | — |

**只有 2 个文件被改**，其余全是新增。

---

## 5. 三个子问题（我按下面处理，可推翻）

### A1-Q1 `Temperature` 默认值：轴表 0.7，不动 client

- 轴表写 `Numeric(temperature, "温度", [0,2] step 0.1, default: "0.7")`
- `ApiClientCfg.Temperature` 保持 0.7 不动
- **理由**：两者一致 → A1 零行为变化。等 A4 把轴接进请求构造器时，再讨论"要不要统一到 1.0"（那是一次真实的行为变更，该单独决策）

### A1-Q2 全局配置 vs 会话轴值的优先级

- **会话轴值优先；轴值缺失时落回 client 配置**
- 实现上：`IAxisPort.Number("temperature", canonical)` 返回 `double?`，`null` 表示"这条轴没设值" → 调用方落回 client
- **理由**：`ApiClientCfg` 是 provider 级（一个 client 一份），轴是会话级。会话级更具体，必须优先；但"没设"与"设成默认值"要能区分——所以 `Number` 返回可空

### A1-Q3 state 版本对齐

- 本项目升到 **3**，不追 ALC 的 7
- **理由**：ALC 的 7 里含插件轴绑定（`SessionPluginBinding`），本项目还没有插件轴；追一个跳号版本没有意义。等插件轴真正落地时再升

---

## 6. 验收标准

| # | 验收 | 怎么验 |
|---|---|---|
| V1 | 编译通过 | `dotnet build`（无警告新增） |
| V2 | **零行为变化** | 轴全部取默认值时，`ClaudeModelClient`/`OpenAIModelClient` 发出的请求体与改动前逐字节相同（可用现有 `WireRequestCapture` 对比） |
| V3 | 旧 state 文件可读 | 拿一个现有 `state.json`（版本 2）跑 `Restore`，`axes` 补齐为 `Defaults()`，不报错 |
| V4 | 新 state 文件版本 3 | 写入后 `formatVersion: 3` + `axes` 字段存在 |
| V5 | `TryParse` 契约 | `TryParse("safety","PROD") == true`（值大小写不敏感）且 `TryParse("Safety","dev") == false`（轴名敏感）；`TryParse("temperature","0.75") == false`（不在网格上，拒绝而非吸附） |
| V6 | `RenderOrder` 是 ordinal | 断言 `RenderOrder` 与 `Names` 可以不同序，且 `RenderOrder` 排序结果与 `OrderBy(n => n, StringComparer.Ordinal)` 一致 |

V2 是**最关键**的一条：它是"纯新增、零行为变化"的唯一证明。

---

## 7. 不做什么（明确排除）

- ❌ 不新增 csproj（A 阶段的全部前提是"仍在 1 个 csproj 内"）
- ❌ 不引入 `Axes` 的插件注册（`IAxisPlugin` 留到插件轴阶段）
- ❌ 不改提示词（尾块渲染是 A3）
- ❌ 不接 `effort` / `temperature` 到请求构造器（A4）
- ❌ 不碰 WebUI

---

## 8. 一个需要你知道的连带风险

`StateFormatVersion` 从 2 升到 3 之后，**任何用旧版本二进制读新 state 文件的场景都会读到不认识的字段**。如果 `publish/` 下还有在跑的旧版本，升级期间会出现"新写旧读"。ALC 的处理是「旧核心不会静默覆盖插件字段」（靠版本号判断），本项目要照抄这个判断，否则旧进程一写就把 `axes` 抹掉。

具体做法：`LoadState` 时若 `formatVersion < StateFormatVersion` → 允许读 + **不立即回写**；`SaveState` 一律写当前版本。这条会写进 A1 的实现。

---

## 9. 轴表不能照抄 ALC —— 6 条轴里有 2 条在本项目站不住

补做了一次现状核查（`Tool/Core/`、`SessionTypeProfiles`、联网开关），结论：**轴集合必须裁剪**。

| ALC 的轴 | 本项目现状 | 处置 |
|---|---|---|
| `initiative`（step/standard/goal/iterate） | **无此概念**。循环终止靠 `end_session` / `wait` 工具 | 可迁，但语义要重新定义：本项目没有"目标模式""迭代模式"，只有"这轮是否该停" → 见 §9.1 |
| `permission`（off…trusted 共 6 档） | **已有另一套**：`ToolExecutor.PermissionGroup` = 会话类型，`SessionTypeProfiles.IsAllowed(type, tool)` 按 `templates/SessionTypes/{channel,express,review,task}.json` 的白黑名单判 | **语义重叠、机制不同 → 冲突，必须定（§9.2）** |
| `safety`（dev/staging/prod） | 无 | 可直接新增（纯提示，无机械效果） |
| `verify`（none/self/strict） | 无 | 可直接新增（纯提示） |
| `network`（off/search/fetch/all） | **无开关**。`web_search` / `NetworkTools` 插件的可见性完全由会话类型的工具白名单决定 | 与 `permission` 同型冲突：能力开关已由白名单承担 → 见 §9.2 |
| `effort`（low…max） | 无独立概念；`OpenAIModelClient.cs:747` 有 `reasoning_effort`（与 thinking 互斥） | 可迁，但只作**请求参数**（`InContext:false`），映射留给 A4 |
| `temperature`（0..2） | `ApiClientCfg.Temperature` 默认 0.7 | 可迁，默认值取 **0.7**（§2 的差异） |
| `mode`（plan/act） | **本项目没有 plan 工具、没有计划树**（`Tool/Core/` 无 PlanTool，`grep Plan` 零命中） | **不该迁** —— 迁了就是一个永远只有 `act` 一档的轴。除非同期引入计划工具 |

### 9.1 `initiative` 的语义要重写

ALC 的四档（step/standard/goal/iterate）依赖它那套"目标 / 计划树 / `finish_goal`"。本项目没有这些，只有：

- `end_session` —— 主动终结
- `wait` —— 挂起等回复
- 无工具调用时注入「你没有调用任何工具…继续工作」提醒（`RuntimeSession.cs:1279`）

→ 本项目的 `initiative` 应该只描述**能力**而非模式，建议先只做两档：

| 档 | 含义（对应本项目的机械事实） |
|---|---|
| `task` | 完成当前任务即停：给出 `end_session` 或 `wait` 是正常终点 |
| `persistent` | 没有活可干也不要停：无工具调用时按 `:1279` 的提醒继续（**这是现在的实际行为**） |

默认取 `persistent`（= 现状），这样 A1 仍然零行为变化。

### 9.2 `permission` / `network` 与现有白名单的冲突（这是唯一的硬冲突）

现状：**工具可见性 = 会话类型白名单**（`SessionTypeProfiles.IsAllowed`），粒度是「某个 type 允许哪些工具名」。
ALC：**权限轴 = 0..5 单调阶梯**（readonly / investigate / standard / authorized / trusted），按"工具类别"判。

两种做法的差别是本质的：

| | 本项目（白名单） | ALC（轴） |
|---|---|---|
| 粒度 | 工具名，逐条列举 | 工具**类别**，5 档阶梯 |
| 存储 | `templates/SessionTypes/*.json` 的 `Tools` 段 | 会话 state 的一个字符串 |
| 变更 | 改文件 + 重启 | 一条命令 |
| 运行时改 | 不支持 | 支持（AGUI 面板/命令行） |

三个选项：

| 选项 | 做法 | 代价 |
|---|---|---|
| **P1** | 轴**包裹**白名单：先按轴筛类别，再按 type 白名单筛工具名（两道闸） | 两道闸会互相遮蔽，用户看到的拒绝原因要能说清是哪一道 |
| **P2** | 把白名单升级成轴：给每个工具打 `PermissionClass`，白名单退化为"这个 type 默认哪几档" | 要给 ~150 个工具逐个打标签，是一次大迁移 |
| **P3** | 只迁 `safety` / `verify` / `temperature` / `effort` / `initiative`，**不迁** `permission` / `network` | 最小改动，但 ALC 那套"能改档"的体验就没有 |

**我的倾向是 P2，但分两步**：A1 只建轴表骨架（不接权限），A4 之后再逐个给工具打 `PermissionClass`。因为 P1 的两道闸是最难解释给用户的形态，而 P3 迁了一半等于没迁。

> ⚠️ 这条影响 A1 的**范围**：如果选 P2，A1 的 `Axes.cs` 里 `permission` 轴先只声明不生效（`InContext: true` 但无机械效果），真正的接线在 B/C 阶段。

### 9.3 修正后的 A1 轴表（7 条，其中 2 条"只声明不生效"）

| 轴 | 值 | 默认 | 进上下文 | A1 是否生效 |
|---|---|---|---|---|
| `initiative` | `task` / `persistent` | `persistent` | 是 | 是（仅提示词） |
| `permission` | `off` / `readonly` / `investigate` / `standard` / `authorized` / `trusted` | `standard` | 是 | **否**（等工具打标签） |
| `safety` | `dev` / `staging` / `prod` | `dev` | 是 | 是（纯提示） |
| `verify` | `none` / `self` / `strict` | `self` | 是 | 是（纯提示） |
| `network` | `off` / `all` | `off` | 是 | **否**（等白名单改造） |
| `effort` | `low` / `medium` / `high` / `xhigh` / `max` | `high` | **否** | 否（A4 接请求） |
| `temperature` | 0.0–2.0 网格，步长 0.1 | **`0.7`** | **否** | 否（A4 接请求） |

`mode` **不进表**（本项目无 plan 工具）；`network` 从 ALC 的 4 档压到 2 档（`search` / `fetch` 的区分本项目没有对应物）。

**这 7 条轴 = A1 的实现范围。** 其中 5 条生效（2 条纯提示 + 1 条提示 + 2 条只声明），2 条等后续阶段。

