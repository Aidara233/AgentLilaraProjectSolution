# 上下文管理

## 三层压缩系统（L1/L2/L3 + CompactTool）

**文件：** `Engine/Modules/CompressionTierModule.cs` + `Tool/Core/CompactTool.cs`
**阈值配置：** `Engine/Core/AgentConfig.cs`（CompressL1/L2/L3Tokens）

### 核心认知：compact 是接管，不是扳机

旧设计里 `compress` 工具对 `_history` 做摘要+截短原地替换，留下多个难题（配对半截、压缩模式特殊分支、L3 跑路无终止、副模型 cache 利用率低）。

新方案：**模型调 `compact(summary)` → 旧 session 闭合 + 新 session 启动**。
- compact 参数就是模型自己写的摘要
- 旧 session 在 compact 工具执行后 EndSession=true 退出循环
- 新 session 走标准 `CreateSession` 路径，`ParentSessionId = 旧 sessionId`，`InitialState = "[上一会话摘要]\n{summary}\n\n请继续工作"`
- 新 session 同步 claim 频道（无缝接管），轮次计数接续旧 session

一切难题消失：配对完整、频道上下文自然注入、claim 切换无缝、无特殊分支、L3 锁定后模型必调。

### 三层阈值（基于 200k 上下文模型）

| 层级 | 阈值 | 占比 | 行为 |
|------|------|------|------|
| L1 | 80000 tokens | 40% | 温和提醒：注入提示文字，建议模型调 compact |
| L2 | 130000 tokens | 65% | 明确催促：注入催促文字，建议尽快调 compact |
| L3 | 150000 tokens | 75% | 强制锁定：`_l3Lockdown=true`，下一轮工具表只剩 compact |

- `CompressMinTokens = 5000`：低于此值评估为 None tier，避免极短对话误触发
- 运行时 token 估算：`history.Sum(EstimateChars) / 3`
- L3 留出 50k 给 compact summary 输出和新会话起步

### CompressionTierModule API

```csharp
CompressionTier Evaluate(int estimatedTokens);  // 判定层级，L3 切入时触发 onL3Triggered 回调
string? GetInjectText(int estimatedTokens);     // 返回当前层级的提示文字（L1 仅注入一次）
void MarkL1Injected();                          // 标记 L1 已注入，避免重复
```

模块仅做判定与提示，不执行压缩动作。压缩动作由模型调 `CompactTool` 触发。

### CompactTool

**位置：** `Tool/Core/CompactTool.cs`
**注入：** RuntimeSession.InitCompression 时直接注入 `_tools`，不走 ToolRegistry

| 属性 | 值 |
|------|------|
| Name | `compact` |
| Parameters | `summary`（必填，会话完整总结） |
| ContinueLoop | false |

**执行流：**
1. 模型调 `compact(summary="...")`
2. CompactTool.ExecuteAsync → `SessionEngine.CreateFromCompact(oldSession, summary)`
3. 新 session 创建：复用旧 session 的组件/端口/InitActions/Limits，InitialState = 摘要文本
4. 新 session 同步 claim 频道（标准 CreateSession 内 AutoClaim 完成）
5. 返回 ToolResult(success)
6. RuntimeSession.ApplyToolEffectAsync 检测 compact → `ToolEffect(EndBurst=false, EndSession=true)`
7. 旧 session 退出循环（assistant + tool_result 已配对追加到 _history 并持久化，"善始善终"）

### L3 锁定执行流

```
轮 N: tokens > 150k (L3)
    → GetInjectText 返回 L3 提示文字
    → onL3Triggered 回调：_l3Lockdown = true
轮 N+1 开始:
    → 检测 _l3Lockdown && _compactTool != null
    → _preLockdownTools = new(_tools)  // 备份
    → _tools.Clear(); _activeTools.Clear()
    → _tools["compact"] = _compactTool; _activeTools.Add("compact")
    → _agentCore.AdditionalTools = [compact]
    → 模型本轮可选工具只有 compact，必调
```

旧 session 在 compact 执行后立即退出循环，不需要还原工具表。

### Session 持久化集成

每轮 agent 推理后 `PersistRoundAsync(newMessages)`：
1. `SessionPersistence.AppendHistoryAsync(newMessages)` — 追加到 `history.jsonl`
2. `SessionPersistence.SaveStateAsync(BuildStateSnapshot())` — 原子重写 `state.json`

`state.json` 中的 `compressionTier` 字段记录当前层级，恢复时可用于诊断。

### 已废弃

- `SummarizationCore` — 已删除。旧设计中的副模型压缩路径已被 compact 工具的"模型自己写摘要"方案取代（模板文件 `templates/Core/SummarizationCore.json` + `templates/Core/Prompts/SummarizationPrompt.txt` 保留作归档）
- `CompressTool` — 已删除，被 `CompactTool` 取代
- `CompressionTierModule.CompressAsync / CompressL3Async` — 已删除，不再需要原地压缩
