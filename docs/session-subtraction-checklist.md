# Session 减法清单（半重构第一步）

> 方针：先做减法，剃掉死代码 + 半残设计 + 子模式，缩小代码面，让入向接通的真问题浮出来。
> 分支 `feature/session-inbound-connection`，base = master(590af54)。
> **本轮只减不加**：动态启用组件等加法功能等减完法、入向接通后再单做。

## 减法决策（已与用户确认）
- session 工具范围 = `ComponentIds` 指定组件的全部可见工具 + ToolRegistry 独立工具（`RefreshToolList()` 已实现此逻辑）。
- `CurrentModeId` 置 null → AgentCore 不再过 ModeConfig 白名单二次过滤（AgentCore:178/244 `CurrentModeId==null` 即全放行）。
- ModeConfig 只留 express，Working 子模式全删。

---

## 第一档：撤子模式（核心减法）

### RuntimeSession.cs
- [ ] `:268` 删 `_agentCore.CurrentModeId = MaxLevel>0?"build":"plan"`，改为不设（保持 null）或显式 `= null`。
- [ ] `:335` 删上一轮加的 `switch_mode` case（子模式切换已无意义）。
- [ ] `:28/68/142` `MaxLevel` 字段 / 构造赋值 / `SetLevel`：撤子模式后 MaxLevel 仅剩 meta 存读(486/507) + WebUI 显示(SessionsProvider:195)。**决策点见下方"MaxLevel 去留"**。

### SwitchModeTool
- [ ] 整个工具下线（`GetModesByMetaType("Working")` 已无 Working 模式可切）。从注册点移除（需 grep 注册位置）。

### ModeConfig.json（模板 + Storage 运行时副本）
- [ ] 删 plan / browse / build / manage / fullpower 五个 Working 模式，只留 express。
- [ ] `escalateTarget: "plan"` 失去意义 → 删或留空（escalate 现在直接建 session，不经 mode）。
- [ ] **注意**：发行版 Storage 在 `E:\Tool\Lilara\Storage`，ModeConfig 运行时副本需手动同步（与 SessionCore 重命名同样的坑）。

### ModeConfigLoader.cs
- [ ] `GetEscalateTarget()` / `EscalateTarget` 字段：escalate 不再经 mode，确认无其他调用后删。
- [ ] `GetModesByMetaType("Working")`：SwitchModeTool 下线后确认无调用者，删。
- [ ] `IsToolEnabled`：仅 express 用，保留（AgentCore 仍可能对 express 调用）。需确认 express 路径是否真用到。

---

## 第二档：死代码（确定剃，无行为风险）

- [ ] `ChannelEngine.agent` 字段：Working 移除残骸，全仓仅 `=null`/`==null`，无 `new Agent`。删字段 + 相关判断。
- [ ] `ChannelContextService.FormatMessageAttrs`：第一批复审发现的 pre-existing 死代码。grep 确认零调用后删。
- [ ] `AdapterEngine.OnAdapterMessage`：零调用旁路（入向真路是 RouteMessageAsync）。**删**。

## 第三档：半残接口（标记或填实）

- [ ] `RuntimeSession.EnableComponent`（:127）：空壳，只 `_componentIds.Add` 不实例化/不注册工具/不 RefreshToolList。
      → 本轮**标记 `throw NotImplementedException` 或注释明确"未实现，勿用"**，防误用。填实留加法轮。
- [ ] `RuntimeSession.DisableComponent`（:134）：与 Enable 配对，加载侧空，移除侧形同虚设。同上处理。

## 保留（B 入向接通要用，不删）

- `MessageEngine.AcceptIncoming`：零调用但是 B 接通的现成落点（入库+去重+Distribute）。**留，下一步改造为接通点**。
- `RefreshToolList()`：工具范围的真实载体，撤子模式后它是唯一控制点。**留**。

---

## 待用户拍板（开工前最后一项）

**MaxLevel 去留**：撤子模式后 `MaxLevel` 唯一实际用途（268 行映射模式）消失，只剩：
- meta 持久化存读（486/507）
- WebUI 显示等级（SessionsProvider:195）
- escalate/ChannelEngine 创建 session 时仍传 `MaxLevel=1`（943/179）

两个选择：
- **A. 彻底删 MaxLevel**：连同 CreateSessionSpec.MaxLevel / RuntimeSession.MaxLevel / SetLevel / meta 字段 / WebUI 显示一起清。最干净，但 WebUI 等级列要改。
- **B. 留作纯展示标签**：保留字段和 meta，仅删 268 行的模式映射。WebUI 不动，字段变成"创建时记录的等级意图"无实际约束。

减法精神倾向 A，但 A 动 WebUI。请定。

---

## 执行顺序（逐步提交）

1. commit: 撤子模式核心（RuntimeSession 268/335 + SwitchModeTool 下线 + ModeConfig 删 Working 模式）
2. commit: 清 ModeConfigLoader 连带死代码（EscalateTarget / GetModesByMetaType）
3. commit: 删死字段 ChannelEngine.agent + FormatMessageAttrs + OnAdapterMessage
4. commit: 标记 EnableComponent/DisableComponent 半残接口
5. commit: MaxLevel 处理（按 A 或 B）
每步 build + test 验证，不攒。
