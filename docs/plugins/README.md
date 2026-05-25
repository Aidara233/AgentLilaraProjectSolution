# 插件系统

Agent Lilara 的插件系统允许通过独立的 DLL 扩展功能，无需修改核心代码。所有插件编译为 `.dll`，放在 `Plugins/` 目录下，启动时自动发现和加载。

## 核心概念

插件的两层抽象：

- **组件 (Component)** — 插件的顶层组织单元，管理生命周期、暴露工具列表、注入 prompt 片段。分为 `Global`（全局单例）和 `Loop`（每个引擎循环一个实例）两种作用域。
- **工具 (Tool)** — 实际可被 AI 调用的功能单元。定义名称、描述、参数，通过 `ExecuteAsync` 执行。

组件是容器，工具是内容。一个 DLL 可以只有一个组件 + 多个工具，也可以只有独立工具（无组件）。

## 目录结构

```
AgentLilaraProjectSolution/
  AgentLilara.PluginSDK/         ← 共享契约（引用此项目即可开发插件）
  Plugins/                        ← 所有插件项目
    Plugin.BasicTools/            ← 基础通信（speak, send_media）
    Plugin.WorkingTools/          ← 工作空间（便签板、任务列表等）
    Plugin.MemoryTools/           ← 记忆存储/检索
    Plugin.FileTools/             ← 文件操作
    Plugin.SystemTools/           ← 子 agent 管理
    Plugin.ReviewTools/           ← 复盘工具
    Plugin.CrossLoopTools/        ← 跨循环通信
  AgentCoreProcessor/             ← 宿主应用
    Tool/Host/PluginLoader.cs     ← 插件加载器
    Component/                     ← 组件注册表和生命周期管理
```

## 文档导航

| 文档 | 内容 |
|------|------|
| [快速上手](quickstart.md) | 从零创建一个插件，含完整代码示例 |
| [API 参考](api-reference.md) | 所有接口、属性、枚举、服务的完整说明 |

## 关键设计决策

- **组件优先**：如果 DLL 中有 `[Component]` 标记的类，工具由组件管理而非独立注册
- **AssemblyLoadContext 隔离**：每个插件 DLL 运行在自己的 ALC 中，支持热重载（卸载后重新扫描）
- **服务定位器模式**：插件通过 `GetService<T>()` 获取宿主服务（IMemoryAccess、IAgentMessaging 等），不直接引用宿主程序集
- **无清单文件**：所有元数据通过 C# 属性声明（`[Component]`、`[ToolMeta]`、`[LoopApplicability]` 等）
- **参数名必须英文**：这是 Bedrock 代理的硬性限制
