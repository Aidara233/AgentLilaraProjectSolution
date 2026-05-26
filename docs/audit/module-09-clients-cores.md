# 模块9审计报告：客户端+Core处理 (Clients & Cores)

审计时间：2026-05-26
文件数：~25 | 核心实现 ~4,200 行（含 2 个大 ModelClient + CoreBase + 6 个 Core + 3 个 Provider）

---

## 发现问题

### 🔴 BUG — 中度

**1. Processor.CfgName setter 配置不存在时静默 fallback 到 Base** (`Processor.cs:29-38`) ✅ 已修复 2026-05-26
```csharp
set {
    var fullPath = Path.Combine(cfgDirectoryPath, value) + ".json";
    if (!File.Exists(fullPath)) {
        cfgName = "Base";
        return;  // 调用方完全不知道配置没加载
    }
```
配置文件写错名字或丢失时静默降级到 `Base` 配置。调用方以为在用 `SummarizationCore` 配置（高压缩模型），实际用的是 `Base`（默认 DeepSeek-R1-Distill-Qwen-7B，temperature 0.7）——完全不同的模型行为。应至少 `Signal.Warn` 记录。

**2. NativeToolCallHandler 与 ExpressToolCallHandler ~95% 代码重复** (`NativeToolCallHandler.cs` + `ExpressToolCallHandler.cs`)
两个 class 共约 230 行，逻辑几乎完全相同：
- 相同的流事件状态机（ToolUseStart → ToolUseDelta → ToolUseEnd）
- 相同的 `FinalizeCurrentCall` 参数映射逻辑（properties 顺序 → fallback）
- 相同的 JSON 解析/错误处理

唯一区别：
- `Text` 事件：Express 追加到 `textParts`，Native 追加到 `thinkingParts`
- `GetResult`：Express 多返回 `Text` 字段

任何 bug fix 或参数映射改进需改两处。应提取基类，参数化 Text 事件行为。

**3. CoreBase.GenerateWithToolsAsync 错误路径不设 span detail** (`CoreBase.cs:135-155`)
```csharp
try { ... LogOutput(...); }
catch {
    LogOutput(..., isError: true);
    throw;  // span 通过 using Dispose 关闭，但无 detail 信息
}
```
对比 `GenerateAsync` 的错误处理：
```csharp
catch (Exception ex) {
    span.SetCloseDetail(new { ... error = ex.Message });  // 有 detail
    ...
    throw;
}
```
`GenerateWithToolsAsync` 在异常时 span 关闭无 detail——日志中工具调用失败的 span 缺失 model/elapsed/tokens 等关键调试信息。

### 🟡 BUG — 轻度

**4. OpenAIModelClient + ClaudeModelClient BuildImage 裸 catch 吞异常** (`OpenAIModelClient.cs:339`, `ClaudeModelClient.cs:441`) ✅ 已修复 2026-05-26
```csharp
catch { return null; }
```
图片加载失败（文件不存在/权限不足/OOM/非法 base64）静默丢弃。AI 收到缺图的消息，不知道图片加载失败，可能产生幻觉描述。

**5. Processor.InjectPersona 每次构造读文件** (`Processor.cs:79-97`)
```csharp
private void InjectPersona()
{
    var personaPath = Path.Combine(cfgDirectoryPath, "Persona.txt");
    if (!File.Exists(personaPath)) return;
    var persona = File.ReadAllText(personaPath).Trim();
```
`AgentCore.InvokeAsync` 每次调用都 new Processor → 读 Persona.txt。频道循环每轮一次，高频操作（每分钟多次）。Persona.txt 内容不变，应静态缓存。

**6. SiliconFlowEmbeddingProvider 无重试机制** (`SiliconFlowEmbeddingProvider.cs:39-74`)
对比 `Processor.ProcessAsync` 有一次重试（`onRetryReset` + 再调 `StreamChatAsync`），Embedding 调用无任何容错。网络瞬断 → `EnsureSuccessStatusCode` 直接抛 → 整个记忆召回路径不可用。

**7. ClaudeModelClient StreamChatAsync 中 mergedUsage 被传给 BuildSyntheticResponse 的 usageResp** (`ClaudeModelClient.cs:161-166`)
```csharp
if (resp.Usage != null || resp.StreamStartMessage?.Usage != null)
{
    var usageResp = BuildSyntheticResponse(null, null);
    usageResp.Usage = mergedUsage;
    onDelta(usageResp);
}
```
每次 message_delta（output_tokens 更新）都重新发送一次 usage——流式过程中 usage 事件可能触发多次（message_start 的 input tokens → 每个 message_delta 的 output tokens 更新 → 共 ~2+ 次）。虽然上层 `CoreBase` 的 handler 只取最后一次值（覆盖），但多余的 callback 调用增加了不必要的对象分配。

**8. CoreBase.GenerateAsync break 检测每次 delta 都遍历 breakString + buffer.ToString()** (`CoreBase.cs:232-262`)
```csharp
buffer.Append(delta.Content);
while (true) {
    var text = buffer.ToString();  // 每次循环都重新分配字符串
    foreach (var breakStr in breakString) { ... }
}
```
每个文本 delta 到来时都做 `buffer.ToString()`（每次分配新 string），然后用 `IndexOf` 扫描。breakString 只有 1 个元素（`<over>`），但算法是 O(chars_per_delta × breakString_count) 的。当前数据量极小（1 个 break string，通常不匹配），实际影响可忽略，但模式可优化。

### 🟠 设计问题 — 中度

**9. OpenAIModelClient.CacheCaptureHandler 为提取两个数字捕获整个 SSE 流** (`OpenAIModelClient.cs:428-484, 387-405`)
每次 HTTP 请求创建 `TeeStream`，将**全部 SSE 响应字节**复制到 `MemoryStream`，仅为了在 `SupplementCacheTokens` 中用正则提取 DeepSeek 的 `prompt_cache_hit/miss_tokens`（两个整数）。大型响应（含图片 base64 工具调用结果）时，`_captured` 可能达数 MB。这是用 MB 级内存换两个整数。应直接解析 SSE 流而非完整捕获。

**10. ClaudeModelClient 与 OpenAIModelClient 约 15% 代码重复** (各 ~520 行)
- `InferMediaType` / `GuessMimeType` — 完全相同的 switch（4 个分支 + default）
- `BuildSyntheticResponse` — 完全相同的 ApiResponse 构造
- 图片处理路径（文件→bytes→base64→mediaType）— 高度相似
- `BuildImageContent` / `BuildImagePart` — 结构相同、SDK API 不同

可提取共享的图片加载/媒体类型推断到 `ModelClientBase` 或独立工具类。

**11. CoreBase 三个 Generate 方法 boilerplate 重复** — `GenerateAsync`、`GenerateOnceAsync`、`GenerateWithToolsAsync` 各有 30+ 行相同模板：Signal.Debug 请求发出、首 token 计时、Signal span 生命周期、catch-LogOutput-throw。三个方法的错误处理也不一致（span detail 设不设、firstTokenLogged 回不回调）。

**12. PreprocessingCore 锚点首次调用时阻塞** (`PreprocessingCore.cs:46-50`)
```csharp
public async Task IsTaskAsync(string content) {
    await InitAnchorsAsync();  // 首次触发时会发 HTTP embedding 请求
```
首次消息分类时同步等待 10 句锚点 embedding（1 次 API 调用 ≈ 200-500ms），增加首条消息的处理延迟。应在启动时预初始化。

### 🟢 ISSUE — 轻度

**13. AgentCore.InvokeAsync 每次 new Processor 而非复用** (`AgentCore.cs:61`)
频道循环每轮都调用 `InvokeAsync` → 每次都 new Processor → 读 Persona.txt + 读配置文件 JSON。对比系统循环的 `InvokeWithHistoryAsync` 复用 Processor 实例。频道循环可同样复用。

**14. CoreBase.LogOutput 写 DB 用 fire-and-forget** (`CoreBase.cs:453-468`)
```csharp
_ = CallLogRepo.InsertAsync(new ModelCallLog { ... });
```
丢弃 Task。Insert 失败 → 异常被 `UnobservedTaskException` 处理或静默吞掉。token 日志丢失无感知。

**15. ClaudeModelClient 与 OpenAIModelClient 的 InferMediaType 缺失 jpg/jpeg 区分** — Claude 版 `.jpg or .jpeg` 合并，OpenAI 版 `GuessMimeType` 也是合并的。但 Claude 版缺 `.bmp`，OpenAI 版有。不一致。

**16. 项目混用 Newtonsoft.Json 和 System.Text.Json** — `ClaudeModelClient.BuildContentBlocks` 用 `System.Text.Json.JsonSerializer.Deserialize<JsonNode>`，`CoreBase.LogOutput` 用 `Newtonsoft.Json.JsonConvert.SerializeObject`。同一文件（ClaudeModelClient.cs）导入了两个 JSON 库但只用其一。无功能问题但依赖不统一——Newtonsoft 的 `NullValueHandling.Ignore` 和 System.Text.Json 的 `JsonSerializerOptions` 行为有细微差异。

**17. OpenAIModelClient.ParseCacheTokensFromSse 裸 catch 吞 SSE 解析异常** (`OpenAIModelClient.cs:420-421`) ✅ 已修复 2026-05-26
```csharp
catch { }
```
正则解析失败时静默返回 `(0, 0)`。无日志，无感知。如果 DeepSeek 修改了 SSE 字段名，cache hit 数据静默丢失。

**18. ClaudeModelClient 缺 DeepSeek prompt_cache_hit_tokens 补充** — OpenAIModelClient 有完整的 TeeStream + SupplementCacheTokens 链路提取 DeepSeek cache tokens。ClaudeModelClient 无此逻辑（Anthropic SDK 原生支持 `usage.CacheReadInputTokens`/`CacheCreationInputTokens`，不需要补充）。但如果 Claude 模型的 cache 字段被中转站修改，也不会有补充逻辑。

---

## 正面发现

- **ModelClientBase 对话历史管理完整**：AddMessage/Clear/GetHistory/SetHistory/RemoveLast/GetHistoryCount 覆盖所有操作
- **OpenAIModelClient TeeStream 实现精巧**：Stream wrapper 透明复制双向（同步+异步 Read + Memory<byte> Read），零拷贝开销
- **CoreBase.LogOutput 日志记录详尽**：完整 system prompt + contentParts 详情 + usage + SHA256 hash + 工具列表，比大多数 LLM 应用日志丰富
- **Processor 重试机制正确**：调用方通过 `onRetryReset` 清空累积的部分内容，避免脏数据混入
- **ClaudeModelClient 连续同角色消息合并**合理：Anthropic API 要求 user/assistant 交替，SDK 层自动合并避免 API 400
- **ExpressToolCallHandler / NativeToolCallHandler 参数映射正确**：按 schema properties 定义顺序映射到 positional inputs，与 ITool.Parameters 一致
- **SiliconFlow 三个 Provider API 调用一致**：统一使用 HttpClient + Bearer token + JObject 请求构建模式
- **Processor.Persona 注入逻辑清晰**：前置到 system prompt 第一条，支持无 Persona（工具性 Core `UsePersona=false`）
- **CoreBase.ResetProcessor 支持重新初始化**：Agent.Core 需要在模式切换时重置 Processor，API 设计正确
- **ModelClientFactory 简洁**：一行 switch 决定类型，新增 Provider 只需加一个分支

---

## 判定

核心路径（AgentCore → Processor → ModelClient → 流式生成）质量稳定，重试和异常处理基本到位。最大的问题是**代码重复**：NativeToolCallHandler/ExpressToolCallHandler 95% 重复（高风险——bug fix 漏一半），ClaudeModelClient/OpenAIModelClient 图片处理/工具函数重复。`CacheCaptureHandler` 全量捕获 SSE 只为两个整数的设计过于浪费——是代码简洁性与内存效率的取舍。Processor 配置加载静默 fallback 是潜在的配置问题调试陷阱。三个 Generate 方法的 boilerplate 重复可后续清理。整体：功能正确，但 DRY 原则在多处被违反。
