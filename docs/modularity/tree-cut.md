# 模块切分：把 AgentLilara 切成棱角分明的树

> 本文是设计提案，不是已实现的架构。
> 所有结论来自静态分析（读 sln / csproj / 源码），**未 build、未 run、未 test**。
> 机器可读版本见同目录 `modules.json`，供另一个会话渲染网络图。

---

## 0. 先说一个前提：一张图切不出树

「模块连接网络图」如果只画一张，必然出现菱形与环。原因是硬性的：

- **依赖关系天生是 DAG（允许扇入）**
- **树要求每个非根节点入度恰好为 1**

两者不可兼得。想用一张图强行满足树，唯一的办法是把所有端口塞进一个「上帝接口」，把耦合从模块层搬进接口层——那正好是现在的样子，只是看不见。

所以本文把边分成三类，分别画：

| 边类型 | 渲染建议 | 语义 | 约束 |
|---|---|---|---|
| **assembly**（装配） | 黑色实线 | from 负责创建/持有 to | **必须成严格树**：除根外入度恒为 1 |
| **contract**（契约） | 细灰线 / 统一颜色的总线 | 每个模块恰好一条指向 `contracts`，表示它的类型来自契约根 | 不参与入度计算 |
| **port**（端口） | 虚线 | 跨模块调用，只经 `contracts` 里定义的接口 | 允许扇入，不产生模块间直接引用 |

**结论**：装配成树，契约成总线，调用走端口。这三层同时成立，图就既是树、又画得完。

如果只想要一张纯树图：把 23 条契约边折叠成「每个节点的类型来自 contracts」这个元信息，剩下的 assembly 边就是树（根 = `host`）。这也反过来证明了树的成立条件。

---

## 1. 现状诊断

### 1.1 根本问题：没有引用边，只有 using 边

`AgentCoreProcessor` 是**一个** csproj，装着 294 个手写 `.cs`、43458 行，横跨 15 个业务目录：

| 目录 | 文件 | 行数 | 目录 | 文件 | 行数 |
|---|---:|---:|---|---:|---:|
| `Engine/` | 90 | 13985 | `Command/` | 19 | 1094 |
| `WebUI/` | 35 | 9692 | `Core/` | 6 | 1043 |
| `Adapter/` | 27 | 5125 | `Config/` | 5 | 514 |
| `Tool/` | 27 | 2822 | `MCP/` | 4 | 405 |
| `Client/` | 18 | 2677 | `Models/` | 3 | 209 |
| `Database/` | 32 | 2403 | `Util/` | 3 | 161 |
| `Component/` | 11 | 1545 | `Program.cs` | 1 | 499 |
| `Logging/` | 13 | 1284 | | | |

这 15 个「模块」之间没有任何**编译期可见**的引用约束——任何文件都能 using 任何文件。于是：

- 图无从生成（没有边可画）
- 树无从谈起（没有边界可守）
- 「加新工具」很顺，因为什么都能拿到；「换实现、mock、单测」全部做不了，也是同一个原因

**这是所有其他问题的母问题。切口 C1 是其余 10 条的前提。**

### 1.2 抽象是名义上的

| 抽象 | 实现者 | 问题 |
|---|---|---|
| `ISubEngine` | Adapter / Message / Mind / Session / Timer / Vision 共 6 个 | 接口只有 5 个成员 + 3 个默认实现，实现者各自额外暴露 11–28 个 public/internal 成员。**只抽象了生命周期，业务面裸奔**；消费方一律 `OfType<SessionEngine>()` 强转回去 |
| `CoreBase` | 2 个（`AgentCore`、`ExtractionCore`） | `abstract class` 却 **0 个 abstract 成员**，509 行装的是共享 LLM 调用/流解析——是工具基类，不是扩展点 |
| Repository | 32 个具体类 | `Database/` 下**零个 `I*Repository` 接口**；约定成立在分层上，不成立在接口边界上 |

### 1.3 真环与假边

- **真环**：`SessionEngine.cs:97` 向 `RuntimeSession` 注入 `this`，`RuntimeSession.cs:90,282` 反调 `ClaimChannel(:481)` / `SubscribeChannel(:490)` / `CreateFromEscalateAsync(:671)` / `AnyResourceBlocksExit(:628)`。两类因此无法分开单测。
- **假边（比真环更危险）**：4 处进程级 static —— `SignalContext.cs:8-9`、`CoreBase.cs:23`、`ImageStorage.cs:20-23`、`SessionBrainRuntime.cs:12`。它们让所有模块隐式相连，编译期看不见、grep 也 grep 不到调用链。
- **隐式边**：`SessionsProvider.cs:221-232` 用反射读 `RuntimeSession._roundCount`。改名后编译不报错，运行期静默返回 0。

### 1.4 好消息：叶子层已经合规

23 个插件是**独立的 csproj**，22 个只引用 `PluginSDK`、统一 `<Private>false</Private>`、零互引，运行期走可卸载 ALC（`PluginLoader.cs:500` `isCollectible: true`）并有真实的 `ReloadAll`/`ReloadSingle`/`UnloadAll` + 注册表反注册。

**树最大的那块叶子层不用动。** 要切的全部集中在 `AgentCoreProcessor` 内部 + 一个越界的 `Plugin.WorkingTools`。

### 1.5 channel：一个领域被切碎在 6 处

「会话」之外，频道是系统里唯一的长期实体（有配置、亲和度、参与者、记忆绑定、占用状态）。但按目录切分把它拆散了：**19 个文件、1934 行**（不含 634 行测试）。

| 频道的哪部分 | 文件 | 行数 |
|---|---|---:|
| 实体 / 仓储 | `Database/Channel.cs`(15) + `ChannelRepository.cs`(40) | 55 |
| **频道上下文构建** | `Engine/Context/ChannelContextService.cs`(521) + `ChannelContextState.cs`(26) + `IChannelDataSource.cs`(31) | **578** |
| **占用 / 连接态** | `Engine/Session/ConnectedChannelState.cs`(29) + `SessionEngine` 的 `ClaimChannel:688` / `SubscribeChannel:675` / `ReleaseChannel:724` | 29 + 3 方法 |
| **配置与持久化** | `Engine/Worker/ChannelStateManager.cs`(47) + `ChannelConfig.cs`(13) + `ChannelContextPersistence.cs`(111) | 171 |
| 对外端口实现 | `Tool/Host/ChannelAccessImpl.cs` | 409 |
| 命令 / 工具 | `Command/ChannelCommand.cs`(61) + `Tool/Core/ConnectChannelTool.cs`(75) | 136 |
| 管理面卡片 | `WebUI/Providers/ChannelProvider.cs` | 325 |
| 信号 | `Engine/Core/ChannelSignal.cs` | 23 |
| SDK 端口 | `Services/IChannelAccess.cs`(115) + `IConnectedChannel.cs`(29) | 144 |
| 适配器 | `Adapter/IChannelAwareAdapter.cs` | 12 |
| 插件工具 | `Plugin.BasicTools/ListChannelsTool.cs` | 52 |

三处状态分离 —— 上下文服务在 `Context`、配置持久化在 `Worker`、占用状态机在 `SessionEngine` —— 意味着**「频道现在被谁占着」没有一个权威来源**。收成 `Lilara.Channel` 后占用只有一个所有者。

**代价**：这会把 claim 的所有权从 `SessionEngine` 移走，连带改 `RuntimeSession` 的 6 处调用（`:481` / `:490,495` / `:530`）。因此 **C12 必须与 C3（断环）一起设计**，不能分两次做。

**待查隐患**：`ChannelStateManager` 是 static 类、直接读写 `PathConfig.StoragePath` 下的 `ChannelState/{id}.json`，与 `Database/Channel` 表并存；`LoadConfig(int channelId, float? dbAffinity = null)` 的签名暗示**亲和度有两个存储**（见 §7 Q5）。

### 1.6 webui 独立：三件连带的事

1. **反向依赖要翻**。现在宿主往 webui 里伸手：`MasterEngine.cs:99` 暴露 `WebUI.Shell.ProviderRegistry`，`PluginLoader.cs:25,35` 依赖它，`Program.cs:104-117` 注册 12 个 Provider。摘除 WebUI 不是删目录，必须先断这三处（`C14`）。
2. **`OfType<SessionEngine>()` 从「可以慢慢改」变成「必须先改」**。WebUI 现在靠同进程 DI 直接拿到引擎实例，独立进程后拿不到，只能调 API。所以 `C2` 升级为 `C13` 的硬前置，且端口必须全 DTO 可序列化（新增 `A4` / `A5` 两条验收）。
3. **插件卡片能力下线**（已确认策略）：`IWebUIProvider` / `WebUIProviderAttribute` 从 `contracts` 撤出，webui 只管内建 Provider；卡片模型（`CardDefinition` / `CardSchema` / `PageDefinition` / `DataSource` / `IPageContext`）迁入 webui 项目自身。

**顺带一个数字**：WebUI 9692 行里 17 个 Provider 占 8050 行（83%），真正的 Blazor 页面只有 `Components` 2 文件 25 行 + `Navigation` 1 文件 12 行。所以「webui 独立」实质是**管理面后端独立**。摘除后宿主从 43458 → **33766 行**，`Microsoft.NET.Sdk.Web` 可降为 `Microsoft.NET.Sdk` + Exe。

---

## 2. 规则

| id | 规则 | 为什么 |
|---|---|---|
| **R1** | 单向：依赖只指向下层，禁止回指 | 现在的 `SessionEngine ↔ RuntimeSession` 双向环使两类拆不开 |
| **R2** | 单父：除装配根外，每个节点 assembly 入度恰好 1 | 这是「树」的定义。现在 `SessionEngine` 入度 8、`GraphRuntime` 入度 3 |
| **R3** | 无兄弟横连：同层零 assembly 边，协作走端口 | Tool / Component / Command / WebUI 四个模块都 `OfType<SessionEngine>()` |
| **R4** | `contracts` 只放 interface / record / enum / attribute，**允许零依赖的默认实现** | 根被所有模块引用，一旦有可变逻辑就成了新的耦合中心。**已确认**：允许 `FileToolBase` 的 `Ok()/Fail()/Workspace.Resolve` 这类零依赖默认实现上收 |
| **R5** | 禁止进程级 static 可变状态，单例一律经 host 注入 | 静态全局使 R1–R3 全部失效 |

### 为什么根必须是契约层，不是主程序

如果主程序当根，插件就只有两个选择：依赖主程序（现在 `Plugin.WorkingTools` 就是这样，破坏 ALC 隔离），或者无法编译。**没有第三种可能。**

所以根只能是「只有契约、没有逻辑」的那一层：它被所有人引用，但因为它没有可变状态，永远不会成为需要改动的点。

代价要说清：**根会变胖**，从现在的 2237 行涨到大约 2700–3000 行接口。这是树形状的门票，不是浪费。

---

## 3. 目标树

```
══ 装配树（黑色实线）· 根 = Lilara.Host · 除装配根外入度恒为 1 ══

Lilara.Host                          ← 装配根（Exe）· 只组装，不含业务
├── Lilara.Data                      ← 实体 + 仓储实现（接口在根）
├── Lilara.Llm                       ← 模型客户端 / 流解析 / 重试
├── Lilara.Logging                   ← Signal 日志落盘
├── Lilara.PluginHost                ← ALC / ToolRegistry / ToolExecutor / 内核桥
│   └── Plugin.*（23 个叶子）         ← 动态加载，只依赖根
├── Lilara.Adapters                  ← OneBot / HttpBridge / 图像存储
├── Lilara.Commands                  ← 斜杠命令
├── Lilara.Graph                     ← 图运行时 + 图模块 + GraphManager
├── Lilara.Channel                   ← 频道域：占用唯一所有者 / 配置 / 上下文构建
└── Lilara.Session                   ← 会话池 / session 生命周期 / 升级
    ├── Lilara.Turn                  ← 单轮：组上下文 → 调 LLM → 执行工具 → 落库
    │   ├── Lilara.Context           ← 三层压缩 / 注入 / 提示贡献汇总
    │   ├── Lilara.Mind              ← 情绪 / 记忆 / 审核 / 写作
    │   └── Lilara.Vision            ← 图像理解 / OCR
    └── Lilara.Recurring             ← 定时 / 唤醒 / 事件源 / 跨会话投递

Lilara.ConsoleClient                 ← 第二棵装配树的根（独立应用，零装配子节点）

Lilara.WebUI                         ← 第三棵装配树的根（独立进程，零装配子节点）
                                       与 host 只经 API（点划线）通信

── 不在装配树上 ──
graph-sandbox（src/AgentLilara.GraphSandbox/ → 迁入 tests/fixtures/）

══ 契约总线（细灰线）· 每个模块恰好一条指向 Lilara.Contracts · 不参与入度 ══
  被以下 18 个模块各引用一次（图上画成竖线落到同一条总线，不要画成树边）：
  host · data · llm · logging · plugin-host · adapters · commands · webui
  graph · session · channel · turn · context · mind · vision · recurring
  console-client · plugin-leaves

── tests/（不入生产树）──
AgentCoreProcessor.Tests · AgentLilara.GraphRuntime.Tests · AgentLilara.GraphSandbox.Tests

══ 端口边（虚线，允许扇入）══
  Adapters  → ISessionPort        Commands  → ISessionControl / IContextControl
  Session, Turn, Adapters → IChannelPort
  Turn      → ILlmPort / IDataPort / IToolHost / ISignalLogger
  Mind, Vision → ILlmPort；Mind → IDataPort
  Plugin.*  → IPluginDataAccess / IToolContext

══ API 边（点划线，跨进程）══
  WebUI → Host（HTTP / WebSocket，载荷全为 contracts 中的 DTO，禁止传引擎实例）
```

**规模**：18 个生产模块 + 23 个插件叶子 + GraphSandbox（迁入 tests/fixtures）+ 3 个测试项目 = **45 个项目**（现 35，净增 10）。合并项：`PluginSDK`+`SessionBrain`+`FileToolKit.Shared` → `contracts`（3→1）、`Tool`+`AlcBridge` → `plugin-host`、`GraphRuntime`+`GraphModules` → `graph`。拆出项：`WebUI`（脱离宿主，独立进程）、`channel`（新增，从 5 个模块收回）。

### 模块清单

| 模块 | 一句话职责 | 现在在哪儿 | 文件 | 行数 | 状态 |
|---|---|---|---:|---:|---|
| `contracts` | 全部端口/DTO/事件/特性，零逻辑 | `PluginSDK/` + `SessionBrain/` + 上收 `FileToolBase`、`ISubEngine` | 70+ | 2237+ | 合并 |
| `host` | 装配根：读配置、造对象、起停 | `Program.cs` + `Engine/Core/MasterEngine` + `Core/AgentCore` + `Component/` + `Config/` + `Util/` | ~33 | ~3954 | 拆分 |
| `data` | 实体与仓储实现 | `Database/`（32/2403） | 35 | 2612 | 抽出 |
| `llm` | 模型客户端与流解析 | `Client/`（18/2677）+ `Core/CoreBase.cs`（509） | 19 | 3186 | 抽出 |
| `logging` | Signal 日志 | `Logging/` | 13 | 1284 | 抽出 |
| `plugin-host` | ALC 装卸、工具注册执行、内核桥 | `Tool/`（27/2822）+ `AlcBridge/`（5/1133）+ `MCP/`（4/405） | 36 | 4360 | 合并 |
| `adapters` | 外部协议接入 | `Adapter/`（27/5125）+ `Engine/AdapterEng/`（3/119） | 30 | 5244 | 合并 |
| `commands` | 命令 | `Command/`（19/1094）+ `Engine/Command/`（1/223） | 20 | 1317 | 合并 |
| `webui` | 管理面（**独立进程**）：Blazor 页面 + 17 个内建 Provider | `WebUI/` | 35 | 9692 | **独立进程** |
| `graph` | 图执行 | `Engine/Graph/`（10/1473）+ `GraphRuntime/`（22/3186）+ `GraphModules/`（1/87） | 33 | 4746 | 合并 |
| `session` | 会话池与生命周期（频道占用已移交 channel） | `Engine/Session/` 扣掉转轮主体与 `ConnectedChannelState` | ~12 | ~2770 | 拆分 |
| `channel` | **频道域：占用的唯一所有者**、配置与持久化、上下文构建、`IChannelAccess` 实现 | `Engine/Context/Channel*`(3) + `Engine/Worker/Channel*`(3) + `Engine/Session/ConnectedChannelState` + `Engine/Core/ChannelSignal` + `Tool/Host/ChannelAccessImpl` + `Command/ChannelCommand` + `Tool/Core/ConnectChannelTool` | 11 | 1346 | 抽出 |
| `turn` | 单轮执行 | `RuntimeSession.cs` 主体 | 1 | 1591 | 抽出 |
| `context` | 压缩与注入 | `Engine/Context/`（4/624）+ `Engine/Modules/`（2/100） | 6 | 724 | 合并 |
| `mind` | 认知域 | `Engine/Mind/` | 14 | 2340 | 抽出 |
| `vision` | 感知域 | `Engine/Vision/` | 4 | 797 | 抽出 |
| `recurring` | 定时与事件源 | `Engine/Worker/`（15/1169）+ `Source/`（3/281）+ `Messaging/`（3/206）+ `Timer/`（1/142） | 22 | 1798 | 合并 |
| `console-client` | 命令行客户端 | `LilaraConsoleClient/` | 4 | 631 | 已有 |
| `plugin-leaves` | 23 个工具插件 | `Plugins/Plugin.*/` | 191 | 19325 | 已有（合规） |

未归属（见 §6 待定）：`MCP/`、`Models/`、以及 `RuntimeSession` 中「一轮」与「会话」的切分线。

---

## 4. 11 个切口

每条切口对应修掉一条违反 R1/R2/R3/R5 的边。**C1 是所有其他切口的前提。**

| # | 切口 | 修掉什么 | 证据 | 风险 | 前置 |
|---|---|---|---|---|---|
| **C1** | `AgentCoreProcessor` 拆成 16 个 csproj | 一切。现在没有引用边可画 | 294 文件 / 43458 行共处一个编译单元 | 高 | — |
| **C2** | 抽 `ISessionPort`/`ISessionQuery`/`ISessionControl` 进根 | `session` 入度 8 → 1；消掉四个模块的横连 | 11 处 `OfType<SessionEngine>()`（见 §5） | 中 | C1 |
| **C3** | 断 `SessionEngine ↔ RuntimeSession` 双向环 | R1 唯一真环；拆出 `turn` 的前提 | `SessionEngine.cs:97` ↔ `RuntimeSession.cs:90,282,481,490,628,671` | 高 | C1 |
| **C4** | `FileToolBase` 上收根 | `FileToolKit` 入度 3 → 根 | `Plugins/FileToolKit.Shared/FileToolBase.cs:11` | 低 | C1 |
| **C5** | `IInjectProvider`/`InjectContext` 上收根，删 WorkingTools 对主程序的引用 | 唯一的插件→宿主反向边 | `Plugin.WorkingTools.csproj:14-16` | 中 | C1 |
| **C6** | `ISubEngine` 上下文去掉 8 个具体 `XxxRepository` | `data` 入度 N → 1 | `ISubEngine.cs:79-86`；`MasterEngine.cs:377-394` | 高 | C1 |
| **C7** | 4 处进程级 static 改注入 | 假边。不修则前面所有边界都只是名义上的 | `SignalContext.cs:8-9`、`CoreBase.cs:23`、`ImageStorage.cs:20-23`、`SessionBrainRuntime.cs:12` | 中 | C1 |
| **C8** | WebUI 反射读私有字段改走查询端口 | 隐式边 | `SessionsProvider.cs:221-232` | 低 | C2 |
| **C9** | Graph 三项目合一，GraphSandbox 移出生产树 | `graph` 入度 3 → 1 | `GraphRuntime` 三个消费者 | 低 | C1 |
| **C10** | `AgentCoreProcessor.Tests` 去掉对 `Plugin.Email` 的引用 | 测试→插件反向边 | `AgentCoreProcessor.Tests.csproj:17` | 低 | C1 |
| **C11** | TFM 统一到 net10.0 | 根若停在 net8，宿主用 net10 API 就经不了根暴露 | 6 个 csproj 是 net8.0，宿主 net10.0 | 中 | C1 |
| **C12** | channel 独立成模块，claim / subscribe / release 所有权移出 `SessionEngine` | `channel` 从 5 个模块收回，结束三处状态分离 | 11 文件 1346 行散在 context / recurring / session / plugin-host / commands；`SessionEngine.cs:675,688,724` | 高 | C1，**须与 C3 一起设计** |
| **C13** | WebUI 摘出宿主，独立进程，只走 HTTP / WebSocket API | host 不再包含 webui；`webui` 成为第三棵装配根 | `csproj:1` Sdk.Web；`Program.cs:104-117`；5 处 `OfType` + 反射读 `_roundCount` | 高 | C1 + **C2（端口必须先 DTO 化）** + C8 |
| **C14** | 断 host / plugin-host 对 `WebUI.Shell.ProviderRegistry` 的依赖，插件卡片下线 | 现存 `plugin-host → webui` 反向依赖 | `MasterEngine.cs:99`；`PluginLoader.cs:25,35,253,254,328`；`Program.cs:9,104-117` | 中 | C13 |

### 建议顺序

```
C1（拆 csproj）
 ├─ C7（static 改注入）              ← 地基，不做则后面全是假的
 ├─ C4 / C9 / C10（低风险三件套，先建立信心）
 ├─ C2（抽会话端口，DTO 化）→ C8
 │    └─ C13（webui 独立进程）→ C14（断 ProviderRegistry，卡片下线）
 ├─ C5（插件越界）
 └─ C3（断环）
     ├─ C12（channel 独立，与 C3 一体设计）   ← claim 所有权迁移，不能分两次做
     ├─ 拆 session / turn                    ← 必须 C3 先完成
     └─ C6（仓储接口）
         └─ C11（TFM 统一，最后做）
```

---

## 5. 违反清单（现状）

| 规则 | 模块 | 证据 | 影响 |
|---|---|---|---|
| R1 | `plugin-host` | `MasterEngine.cs:99`、`PluginLoader.cs:25,35`、`Program.cs:104-117` | **`plugin-host` → `webui` 反向依赖**，是 webui 拆不出去的直接障碍；此前漏计 |
| R2 | `channel` | 11 文件 1346 行散在 context(578) / recurring(171) / session(29+3 方法) / plugin-host(409) / commands(136) / host(23) | 一个领域没有单一所有者；亲和度与占用分散在 DB 列、`ChannelState/*.json`、`SessionEngine` 三处 |
| R1 | `plugin-leaves` | `Plugin.WorkingTools.csproj:14-16` | 唯一破坏 ALC 隔离的插件；绑死宿主程序集 |
| R1 | `session` | `SessionEngine.cs:97` ↔ `RuntimeSession.cs:90,282` | 两类无法分开单测，也无法拆模块 |
| R2 | `session` | 11 处 `OfType<SessionEngine>()`：`SessionAccessImpl.cs:25`、`SubAgentAccessAdapter.cs:49,60`、`CoreCommand.cs:22`、`SessionsProvider.cs:269,303,362,511,618`、`EngineAlertProvider.cs:18`、`ChannelAccessImpl.cs:30` | 入度 8 |
| R2 | `graph` | `AgentCoreProcessor.csproj:45` / `GraphModules.csproj:12` / `GraphSandbox.csproj:13` | 入度 3 |
| R2 | `contracts` | `FileToolBase.cs:11` 被 3 个插件引用且不在契约层 | 出现第二个共享库，树破功 |
| R2 | `data` | `ISubEngine.cs:79-86`；`Database/` 零 `I*Repository` | 入度 = 子引擎数量 |
| R3 | `webui` | `SessionsProvider.cs:221-232` 反射读 `_roundCount` | 编译期不可见 |
| R5 | 多处 | 4 处 static 全局 | 阻断多实例与单测 |

---

## 6. 验收：怎么判定切干净了

三条可自动化的断言，可以直接当 CI 检查：

1. **A1 入度检查**：`dotnet list <每个 proj> reference` 汇总成邻接表，断言「除装配根外，每个节点 assembly 入度 == 1」。
2. **A2 禁止 using**：断言 `using AgentCoreProcessor.Engine.Session` 不出现在 `adapters` / `webui` / `commands` / `plugin-host` 的源码里。
3. **A3 单模块可测**：每个模块能 `dotnet test` 独立跑通。**这条最狠**——跑不通就说明还有隐藏耦合没被 A1/A2 抓到。
4. **A4 API 载荷纯净**：所有 `api` 边的两端载荷类型都定义在 `contracts` 里，不出现宿主类型——用序列化往返测试兜底。
5. **A5 webui 单向**：webui 项目只引用 `contracts`，不引用任何实现项目；host 不引用 webui。

## 7. 待定（需要通读源码才能定）

| id | 问题 | 当前猜测 | 原因 |
|---|---|---|---|
| Q1 | `MCP/`（4/405）归 `plugin-host` 还是 `adapters`？ | `plugin-host` | 未读源码，按「工具协议」猜 |
| Q2 | `Models/`（3/209）是纯 DTO（归 `contracts`）还是含逻辑（归 `data`）？ | `data` | 未读源码 |
| Q3 | `session` / `turn` 的切分线在哪：`RuntimeSession.cs` 里 core 切换、频道 connect、Bootstrap、组件启停是否都留 `session`？ | 只有「一轮执行」进 `turn` | 需通读 1591 行，本轮只统计了结构 |
| Q4 | `console-client` 接 `contracts` 变成单根树，还是保留第二棵装配树？ | 保留森林 | 当前零 ProjectReference |
| Q5 | 频道亲和度到底有几个存储？`ChannelStateManager` 读 `ChannelState/{id}.json`，`ChannelRepository` 又有 `Channel` 表，哪个权威？ | json 优先、DB 作 fallback | 只读了 `ChannelStateManager` 47 行全文与 `LoadConfig` 签名，未追 `ChannelContextPersistence` 与迁移路径 |
| Q6 | webui 独立后 host 侧 API 的粒度：给 17 个 Provider 各配一组端点，还是收敛成少数通用查询端点？ | 按 Provider 分资源，共用鉴权与分页 | 需先看 `WebUI/Services/WebAuthService.cs` 与 `Shell/DataSourceManager.cs` |
| Q7 | 除 WebUI 外，宿主是否还有别的隐藏依赖（`WebUI/Services` 里 `SystemMonitor`、`LogStreamService` 是否被 Engine 用到）？ | 无（grep 未发现） | 只 grep 了 `AgentCoreProcessor.WebUI` 前缀，未逐一核对 12 个 Services 文件 |

---

## 8. 未验证声明

- 全文基于静态阅读，**未 build / 未 run / 未 test**。`net10.0` 宿主引用 `net8.0` 契约库能否编过、有什么警告，未实测。
- 行数为 `find + cat | wc -l` 口径，排除 `obj/`、`bin/`。拆分模块的行数为**估算**（如 `session` / `turn` 的切分比例）。
- 11 处 `OfType` 只确认了调用点存在，未追踪完整调用链。
- `EventBus`（28 处引用）/ `ModuleBus`（18 处）是否真在解耦、还是只做旁路通知，未逐条追。
- collectible ALC 是否真能回收（无 `GC.Collect`、无 `WaitForUnload`），静态无法判断。
