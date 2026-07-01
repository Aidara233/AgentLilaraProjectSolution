# 频道连接工具组 实现计划（2026-06-30）

## 背景与目标

监听（Eavesdrop）落地的前置基础设施。当前 session 的"往哪发消息"由 `SpeakTool` 在组件初始化时从 loopId 解析、写死 `_channelId`，运行期无法变更。无 InputPort 的 session（如未来的监听 session）无法 speak。

本轮目标：让 session 能在运行时**连接/断开频道**，发送工具跟着切换目标，并正确接管/释放 express。监听判定逻辑**不在本轮**，下一轮做。

## 核心认知（已核实）

- `IChannelAccess.SendMessageAsync(channelId, ...)` 本就频道无关；唯一把 speak 钉死的是 `SpeakTool._channelId`（init 写死）。
- 输出**不走 OutputPort**，是工具直接调全局服务 `IChannelAccess`。OutputPort 仅用于 session 父子端口直连。
- claim（`SessionEngine.ClaimChannel/ReleaseChannel`）、订阅（`MessageEngine.Subscribe/Unsubscribe`）都支持运行时增删（锁保护）。
- per-session 服务注入通道已存在：`ComponentHost` 的 `perLoopServices` 参数（ChannelEngine 注入 `IAgentMessaging` 是现成模板），per-loop 覆盖 global。
- `RuntimeSession.InitComponents` 当前**没传** perLoopServices —— 本轮要加。

## 连接模型（两档 + 断开，独占单频道）

| 工具 | claim | express | 订阅 | speak | 用途 |
|---|---|---|---|---|---|
| `connect_channel` | Full | 停 | normal+mention | ✓ | 接管频道对话（汇报、主导） |
| `listen_channel` | Listen（非独占） | 不停 | normal | ✗ | 安静旁听，只看不说 |
| `disconnect_channel` | 释放 | 恢复 | 退订 | — | 断开当前连接 |

约束：
- 独占单频道——连任何一档前先断开当前连接。
- 发送工具（speak/send_media/send_file）仅在 **full** 连接态可用；listen 态或无连接态调用 → 返回 `failed`，提示"当前未连接任何频道（或处于旁听态），请先用 connect_channel 连接"。
- `adapter_action` 绑 adapterId（平台），与频道连接无关，**不改**。
- connect 时自动汇报历史消息（复用 `SessionAgentHost` 的 PreloadHistory 机制）。

## 实现分解

### 1. 新增 per-session 服务 `IConnectedChannel`（PluginSDK）
`AgentLilara.PluginSDK/Services/IConnectedChannel.cs`：
```csharp
public enum ConnectionMode { None, Listen, Full }
public interface IConnectedChannel
{
    int CurrentChannelId { get; }      // 0 = 未连接
    ConnectionMode Mode { get; }
    bool CanSpeak { get; }             // Mode == Full
}
```
- 实现 `ConnectedChannelState`（AgentCoreProcessor 侧，内部可变 set），由 session 持有、注入 perLoopServices。
- 连接工具改 Mode/ChannelId，发送工具只读。

### 2. 改造发送工具（Plugin.BasicTools）
`BasicTools.cs` 的 `SpeakTool` / `SendMediaTool` / `SendFileTool`：
- 构造从 `(IChannelAccess, int channelId, ...)` 改为 `(IChannelAccess, IConnectedChannel, ...)`。
- 执行时读 `_connected.CurrentChannelId`，并先判 `_connected.CanSpeak`，否则返回 failed 提示。
- `BasicToolsComponent.OnInitAsync` 从 `context.GetService<IConnectedChannel>()` 取，注入工具。

### 3. 连接工具组（新 Core 工具，AgentCoreProcessor/Tool/Core/）
三个工具：`connect_channel(channel_id)` / `listen_channel(channel_id)` / `disconnect_channel()`。
- 工具本身只是触发器，**实际接线由 RuntimeSession 执行**（工具无法直接动端口/claim）。
- 仿 escalate/end_session 模式：工具 `ExecuteAsync` 返回 success + 参数，RunAsync 的工具副作用段识别并调用 session 的连接方法。
- 这些工具仅对 session 可见（ChannelEngine express 不需要——它本就绑定单频道）。

### 4. RuntimeSession 连接接线
新增方法（在 RunAsync 工具副作用 switch 中调用）：
- `ConnectChannel(int channelId, ConnectionMode mode)`：
  1. 若已有连接，先 `DisconnectChannel()`。
  2. Full：`SessionEngine.ClaimChannel(id, Full)`；Listen：`ClaimChannel(id, Listen)`。
  3. `MessageEngine.Subscribe(id, "normal")`（Full 额外订 "mention"），`AddInputPort`。
  4. 更新 `ConnectedChannelState`（ChannelId + Mode）。
  5. 重建/更新 `SessionAgentHost` 频道状态，下一轮 BuildRound 注入该频道历史（PreloadHistory）。
- `DisconnectChannel()`：`Unsubscribe` 所有频道端口、移除 InputPort、`ReleaseChannel`、State 归零、host 频道状态清理。
- session 结束时 `ReleaseSessionClaims` 已覆盖 claim 释放；补 Unsubscribe。

依赖注入：RuntimeSession 需要能访问 SessionEngine（claim）和 MessageEngine（已有 `_messageEngine`）。SessionEngine 引用通过 SetXxx 注入或在 CreateSession 时传入。

### 5. InitComponents 传 perLoopServices
`RuntimeSession.InitComponents` 增加参数把 `IConnectedChannel`（= 该 session 的 ConnectedChannelState）注入 ComponentHost 的 perLoopServices。`SessionEngine.CreateSession` 在调用处构造 state 并传入。

### 6. 现有 session 向后兼容
working/task 等带端口的 session：CreateSession 后，把 ConnectedChannelState 初始化为"已 Full 连接到首个端口频道"，行为与现状完全一致（speak 照发原频道）。escalate 出来的 working session 不受影响。

### 7. 清理旧 WatchRule 死代码（顺手）
`ChannelEngine` 的 `watchRules` 字段、`CheckWatchRulesAsync`（含 :364 调用）、`GetWatchRules`/`UpdateWatchRules`、`WatchRule`/`WatchAction` 类型——无调用方（SetWatchRuleTool 已不存在），与连接/监听功能重叠，删除避免混淆。

## 不在本轮范围
- 监听判定逻辑（EavesdropTask 塞提取提示词、命中建 session）——下一轮。
- 同时连接多个频道（明确放弃）。
- 动态增删**组件**（EnableComponent/DisableComponent 仍抛 NotSupported）——连接只动端口/claim，不动组件。
- listen 态接收 mention（listen 只订 normal）。

## 验证
- build 全量（kill→build），0 warning / 0 error。
- 现有 E2E：express、双向切换、working session、escalate 行为不回归（speak 仍发对频道）。
- 新增：构造一个测试 session（空端口起步）→ connect_channel(某频道) → speak 能发到该频道 → disconnect → express 恢复。
- 单元测试：ConnectedChannelState 状态机（None→Full→None、Full→切换到另一频道先断后连）。

## 提交
按 CLAUDE.md：kill→build→commit（English message）→test→更新 architecture-map。
新增 Core 工具若需 sln 注册——本批工具在 AgentCoreProcessor 内（非新插件），无需 sln add。
