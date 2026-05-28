# 路径配置指南

> 项目中所有路径都围绕两个根目录：**代码目录**（BaseDirectory）和 **数据目录**（StoragePath）。
> 搞混这两者是 Claude 最常犯的错。

## 两个根目录

| 根目录 | 路径来源 | 存放内容 |
|--------|----------|----------|
| **BaseDirectory** | `AppDomain.CurrentDomain.BaseDirectory`（exe 所在目录） | 程序集、插件 DLL、WebUI 静态资源、`paths.json` |
| **StoragePath** | `paths.json` 中的 `storagePath` 字段 | 数据库、日志、配置、工作区、插件持久数据 |

**当前 StoragePath**：`E:/Workspace/AgentLilaraProject/Storage`

## 路径全景

```
BaseDirectory (exe 目录)
├── AgentCoreProcessor.exe
├── paths.json                    ← 唯一配置文件，只有 storagePath 一个字段
├── Plugins/                      ← 插件 DLL 扫描目录（不在 Storage 下！）
│   ├── Plugin.BasicTools.dll
│   ├── Plugin.MemoryTools.dll
│   └── ...
└── WebUI/wwwroot/                ← 前端静态资源

StoragePath (数据目录)
├── Core/                         ← Core 模型配置（AgentCore.json 等）
├── Database/                     ← SQLite 数据库 + logs.db
├── Logs/                         ← Signal 日志数据库
├── Workspace/                    ← 所有文件工具共享沙箱
├── PluginData/                   ← 插件持久数据
│   ├── memory-tools/             ← 按组件名隔离
│   │   ├── _global/              ← Global 组件实例目录
│   │   └── per-channel-xxx/      ← Loop 组件实例目录
│   ├── file-tools/
│   └── ...
├── Engine/                       ← 引擎配置（ToolProfiles.json, ComponentConfig.json, ImpulseConfig.json）
├── Dream/                        ← 睡眠/复盘配置和进度
├── Images/                       ← 图片缓存
└── FileAdapter/                  ← 文件适配器测试数据
```

## PathConfig API

定义在 `AgentCoreProcessor/Config/PathConfig.cs`，**内部静态类**：

```csharp
internal static class PathConfig
{
    public static string StoragePath { get; }          // 从 paths.json 读取
    public static string CoreConfigPath => StoragePath + "/Core";
    public static string DatabasePath => StoragePath + "/Database";
    public static string LogPath      => StoragePath + "/Logs";
    public static string WorkspacePath => StoragePath + "/Workspace";
}
```

**加载流程**：
1. 启动时查找 `{BaseDirectory}/paths.json`
2. 不存在 → 运行 SetupWizard 交互式向导，写模板 + `paths.json`
3. 存在 → 读取 `storagePath` 字段，创建 `WorkspacePath` 目录

**没有运行时覆盖机制**。改路径只能手动编辑 `paths.json` 后重启。

## 插件路径

### 插件 DLL 在哪？

**`{BaseDirectory}/Plugins/*.dll`** — 递归扫描子目录。

注意：插件 DLL 在 **exe 目录**下，不在 Storage 下。这是 Claude 最容易搞错的地方。

### 插件数据在哪？

通过 `IPluginStorage` 接口获取：

```csharp
public interface IPluginStorage
{
    string GlobalDirectory { get; }      // Storage/PluginData/{组件名}/
    string InstanceDirectory { get; }    // Global 组件 = GlobalDirectory
                                         // Loop 组件 = GlobalDirectory/{loopId}/
    string WorkspaceDirectory { get; }   // Storage/Workspace/（所有插件共享）
}
```

**实际路径示例**：

| 场景 | GlobalDirectory | InstanceDirectory |
|------|-----------------|-------------------|
| Global 组件 `memory-tools` | `Storage/PluginData/memory-tools/` | `Storage/PluginData/memory-tools/_global/` |
| Loop 组件 `working-tools` (channel:abc) | `Storage/PluginData/working-tools/` | `Storage/PluginData/working-tools/per-channel-abc/` |

### 插件怎么拿路径？

**推荐方式**：通过构造函数注入的 `IPluginStorage` 获取。

```csharp
// 组件内工具
public MyTool(IPluginStorage storage)
{
    var configPath = Path.Combine(storage.GlobalDirectory, "config.json");
    var cachePath = Path.Combine(storage.InstanceDirectory, "cache.tmp");
    var sharedFile = Path.Combine(storage.WorkspaceDirectory, "shared.txt");
}

// 独立工具
public MyTool(IToolContext ctx)
{
    var storage = ctx.Storage;
    // 同上
}
```

**不要**在插件代码中直接用 `PathConfig.StoragePath`（插件不引用 AgentCoreProcessor）。

### 共享工作区

`WorkspaceDirectory` = `Storage/Workspace/` 是**所有文件工具共享的沙箱**。
文件类插件（FileTools, FileOps, GroupFileTools）都读写这个目录。

**规则**：
- 用 `IPluginStorage.WorkspaceDirectory` 获取
- **不要**通过 `GlobalDirectory` 做 `..` 相对跳转
- 继承 `FileToolBase`（`FileToolKit.Shared/`）可自动获得沙箱保护和快捷方法

## 其他路径常量

| 位置 | 路径 | 说明 |
|------|------|------|
| `PluginLoader.cs:24` | `{BaseDirectory}/Plugins/` | 插件 DLL 扫描根目录 |
| `Program.cs:283` | `{BaseDirectory}/WebUI/wwwroot/` | WebUI 前端静态资源 |
| `Program.cs:61` | `{StoragePath}/FileAdapter/` | 文件适配器测试目录 |
| `Program.cs:335` | `{StoragePath}/Images/` | 图片缓存 |
| `ToolRegistry.cs:24` | `{StoragePath}/ToolConfig.json` | 工具配置 |
| `ComponentConfig.cs` | `{StoragePath}/Engine/ComponentConfig.json` | 组件启用/禁用状态 |
| `ToolProfileManager.cs` | `{StoragePath}/Engine/ToolProfiles.json` | 工具集配置 |

## Claude 常见错误

| 错误 | 正确做法 |
|------|----------|
| 以为插件 DLL 在 Storage 目录下 | 插件 DLL 在 `{BaseDirectory}/Plugins/` |
| 在插件里用 `PathConfig.StoragePath` | 用 `IPluginStorage` 接口 |
| 用 `..` 从 GlobalDirectory 跳到 Workspace | 用 `storage.WorkspaceDirectory` |
| 硬编码绝对路径 | 全部通过 PathConfig 或 IPluginStorage 派生 |
| 以为 paths.json 在 Storage 目录下 | paths.json 在 `{BaseDirectory}` 下（exe 旁边） |
