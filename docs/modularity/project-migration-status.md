# 项目迁移当前状态

此表描述已经编译的程序集边界，不是最终装配树。大量内部类型通过 InternalsVisibleTo 对宿主和测试开放；Logging 等实现库仍有多个消费者。不能用项目引用入度代替 assembly/contract/port 三种设计边，也不能据此宣告 C 完成。

| 项目 | 已提取 | 仍待迁移或解除依赖 |
|---|---|---|
| Lilara.Data | 实体、仓储、DbManager | 仓储接口与宿主上下文具体仓储引用 |
| Lilara.Logging | 信号、写入、查询、SDK桥 | 宿主清理服务、静态 SignalContext、全部旧信号的数据流迁移 |
| Lilara.Llm | 模型客户端、协议模型、embedding providers、ModelProcessor 配置/客户端缓存/提示词装配/流式一次重试 | CoreBase 模型日志和仓储接缝；Processor 为宿主路径/提示词/日志适配 |
| Lilara.Context | Axes、Prompts、装配器、变更意图、压缩策略 | SessionContextHost、全局组件采集与宿主压缩适配 |
| Lilara.Channel | 配置存储、参与者、快照/渲染契约与渲染主体 | 占用唯一所有者、亲和度权威、游标持久化、兼容门面与端口全面注入 |
| Lilara.Graph | 管理器、挂载/订阅、运行器与意图基础设施 | 运行时/模块契约/管理层已统一为 Lilara.Graph；其他模块通用挂载、内置路径迁移仍待完成 |
| Lilara.Session | 资源池、连接状态、资源注册、工具效果规则、阶段值、实例化会话类型配置存储（默认值/缓存/读写/工具规则）、三文件会话存储与快照端口 | RuntimeSession/SessionEngine、生命周期与资源重建仍在宿主；SessionTypeProfiles 静态兼容门面负责路径与告警绑定 |
| Lilara.Turn | 原生工具流解析、ModelOutput | 单轮模型/工具执行编排仍在宿主 |
| Lilara.Mind | 证据数学、提取接口、伴随提取与文本围栏 | 写入、检索、审核、情绪、复活和引擎装配 |
| Lilara.Vision | OCR/Vision providers | VisionEngine、阶段配置与图片存储 |
| Lilara.Recurring | 信源管理与心跳调度 | 消息、定时、唤醒与跨会话宿主编排 |
| Lilara.Adapters | 协议契约、File/OneBot/Icalingua/HTTP、管理器、工厂 | ImageStorage、宿主访问/媒体适配器及出向路由图化 |
| Lilara.PluginHost | 工具描述、执行作用域、策略委托执行器 | 静态注册表、插件ALC、MCP、权限与配置装配 |

Commands、独立 WebUI 与最终 Host 装配根尚未完成。PluginSDK 仍为现有契约根；WorkingTools 已删除宿主项目引用，注入接口迁入 SDK 并由宿主类型转发。FileToolBase 已原文上收 SDK，三个文件插件不再引用 FileToolKit.Shared；旧共享库仅类型转发兼容已有插件二进制。GraphSandbox 已移入 tests/fixtures。

AssemblyBoundaryTests 读取实际编译 DLL 元数据，验证所有 src/Lilara.* 库无宿主引用，库间依赖无环；Channel/Context 不引用其他应用程序集。插件项目引用另从 csproj 检查，避免未使用项目引用被编译器从元数据省略而漏报。该检查不证明静态状态隔离、端口契约完整或最终装配树已经达标。

D1 全路径影子对拍、Inline/D5 时序验证尚未完成，不能据此开始 D2 全部内置路径切换。现有 session/session-type 消息图仅支持局部注入与变量；图数据流日志已验证入库，但其他入口和旧信号仍待迁移。真实模型、外部平台、OCR/ONNX 与 WebUI 冒烟未覆盖。
