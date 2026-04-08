# 待修复问题记录

## Client 层重构时发现

1. **ConversationHistory 职责错位** — `ApiClientCfg` 是"配置"类，但持有了 `ConversationHistory`（运行时状态）。理想情况下对话历史应由 Engine 层或 Processor 管理，Config 只存静态参数。当前未动，因为 Processor 和上层都依赖这个结构。

2. **Processor 硬编码路径** — `Processor` 构造函数默认参数 `cfgDirectionPath = "E:\\Workspace\\AgentLilaraProject\\Storage\\Core"`，绝对路径硬编码不利于部署和协作。

3. **Processor.client 是 public 字段** — 外部可以随意替换 client 实例，应改为属性或降低可见性。

4. **Payload.cs 整个文件被注释掉** — 看起来是被 ApiResponse.cs 替代了，可以删除这个文件。

5. **ApiRequest.Model 默认值 "gpt-3.5-turbo"** — 与 ApiClientCfg.Model 的默认值 "deepseek-ai/DeepSeek-R1-Distill-Qwen-7B" 不一致，虽然实际使用时会被覆盖，但容易造成混淆。

## 拼写问题

1. **AgentCoreProcesser → AgentCoreProcessor** — 项目名、命名空间、文件夹名全部拼错。涉及 .csproj、.sln、所有 namespace 声明，改动面太大，建议找一个合适的时机统一重命名。

2. **ToolCall.AfterThan → AfterThen** — JSON 字段名 `"afterThan"` 语法错误，但改了会破坏已有 JSON 数据的兼容性，需要评估是否有持久化数据依赖这个字段名。

3. ~~**databaseDirection → databaseDirectory**~~ — 已修复（MasterEngine.cs）。

4. ~~**cfgDirectionPath → cfgDirectoryPath**~~ — 已修复（Processor.cs 重构时）。

## Core/Models 层重构时发现

1. **MasterEngine.databaseDirectory 硬编码绝对路径** — 与 Processor 同样的问题。

2. **MasterEngine 的 async 方法没有 await** — `EngineMain()` 和 `PreProcess()` 标记为 async 但方法体为空，编译器警告 CS1998。属于未完成的代码。

3. **EngineRequest.userMessage 从未赋值** — 编译器警告 CS0649/CS8618，字段声明了但没有初始化也没有赋值路径。
