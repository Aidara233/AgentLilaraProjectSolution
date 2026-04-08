# 待修复问题记录

## Client 层重构时发现

1. **ConversationHistory 职责错位** — `ApiClientCfg` 是"配置"类，但持有了 `ConversationHistory`（运行时状态）。理想情况下对话历史应由 Engine 层或 Processor 管理，Config 只存静态参数。当前未动，因为 Processor 和上层都依赖这个结构。

2. **Processor 硬编码路径** — `Processor` 构造函数默认参数 `cfgDirectionPath = "E:\\Workspace\\AgentLilaraProject\\Storage\\Core"`，绝对路径硬编码不利于部署和协作。

3. **Processor.client 是 public 字段** — 外部可以随意替换 client 实例，应改为属性或降低可见性。

4. **Payload.cs 整个文件被注释掉** — 看起来是被 ApiResponse.cs 替代了，可以删除这个文件。

5. **ApiRequest.Model 默认值 "gpt-3.5-turbo"** — 与 ApiClientCfg.Model 的默认值 "deepseek-ai/DeepSeek-R1-Distill-Qwen-7B" 不一致，虽然实际使用时会被覆盖，但容易造成混淆。
