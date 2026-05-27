# 高级文件系统操作设计

## 目标

在现有 `Plugin.FileTools`（6个基础文件工具）之上，新增高级文件操作能力：压缩归档、文件搜索、元数据查询、文件对比。

## 架构

```
Plugins/
├── FileToolKit.Shared/              ← 新建：纯类库（非插件 DLL）
│   ├── FileToolBase.cs              ← 抽象基类，消除重复代码
│   └── FileToolKit.Shared.csproj    ← 引用 PluginSDK，不实现 [Component]
├── Plugin.FileTools/                ← 改造：6工具改为继承 FileToolBase
│   ├── FileTools.cs                 ← 逻辑不变，去除样板代码
│   ├── FileToolsComponent.cs        ← 传 WorkspaceDirectory 给工具
│   └── Plugin.FileTools.csproj      ← 加 ProjectReference → FileToolKit.Shared
├── Plugin.FileOps/                  ← 新建：高级文件操作插件
│   ├── ArchiveTools.cs              ← archive_create/extract/list
│   ├── SearchTools.cs               ← search_files, grep_files
│   ├── MetaTools.cs                 ← file_info, file_hash, compare_files
│   ├── FileOpsComponent.cs          ← 注册工具，传 WorkspaceDirectory
│   └── Plugin.FileOps.csproj        ← 引用 PluginSDK + FileToolKit.Shared + SharpCompress
└── (未来其他文件插件可同样引用 FileToolKit.Shared)
```

## FileToolBase 设计

```csharp
public abstract class FileToolBase : ITool
{
    protected readonly string WorkspaceDir;

    protected FileToolBase(string workspaceDir)
    {
        WorkspaceDir = Path.GetFullPath(workspaceDir);
        Directory.CreateDirectory(WorkspaceDir);
    }

    // 子类实现
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract IReadOnlyList<ToolParameter> Parameters { get; }
    public abstract TimeSpan Timeout { get; }
    public abstract Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct);

    // 基类提供
    protected string? ResolvePath(string relativePath) { /* 沙箱路径解析，路径穿越检查 */ }
    protected static Task<ToolResult> Ok(string data) => ...;
    protected static Task<ToolResult> Fail(string error) => ...;
}
```

## IPluginStorage 扩展

新增 `WorkspaceDirectory` 属性，值来自 `PathConfig.WorkspacePath`（`Storage/Workspace/`）。

`PluginStorageImpl` 中实现：`public string WorkspaceDirectory => Config.PathConfig.WorkspacePath;`

## Plugin.FileOps 工具清单

### 压缩归档

| 工具 | 参数 | 功能 |
|------|------|------|
| `archive_create` | path, format?(zip/tar.gz/7z/auto), level?(fast/balanced/best) | 压缩文件/目录。format 默认从扩展名推断。 |
| `archive_extract` | source, target_dir? | 解压到目标目录（默认当前目录） |
| `archive_list` | source | 列出压缩包内容，最多100条，超出截断 |

- ZIP: `System.IO.Compression.ZipArchive`（.NET 内置）
- Tar/GZip: `System.Formats.Tar` + `GZipStream`（.NET 7+ 内置）
- 7z: NuGet `SharpCompress` v0.38.0（MIT 许可，纯托管）

### 文件搜索

| 工具 | 参数 | 功能 | 上限 |
|------|------|------|------|
| `search_files` | pattern(glob), dir?, recursive?(true) | Glob 搜索文件，返回路径列表 | 100 |
| `grep_files` | pattern(regex), dir?, file_pattern?(glob), max_results?(30) | 在文件中搜索匹配内容 | 30，每行截断500字符 |

### 元数据

| 工具 | 参数 | 功能 |
|------|------|------|
| `file_info` | path | 大小、创建/修改时间、MIME类型（扩展名推断）、文本文件加行数 |
| `file_hash` | path, algorithm?(md5/sha256) | 计算文件哈希，默认 SHA256 |

### 文件对比

| 工具 | 参数 | 功能 |
|------|------|------|
| `compare_files` | source, target | 逐行 diff，返回增/删/改行数 + 前几处差异，最多100行 |

## 依赖

| 包 | 用途 | 来源 |
|---|---|---|
| `System.IO.Compression` | ZIP | .NET 内置 |
| `System.Formats.Tar` | Tar/GZip | .NET 7+ 内置 |
| `SharpCompress` v0.38.0 | 7z | NuGet（MIT 许可） |

## 沙箱安全

所有工具路径统一通过 `FileToolBase.ResolvePath()` 校验，确保不超出 `Workspace/` 目录。路径穿越检测：解析后的绝对路径必须以 WorkspaceDir 开头（Win32 大小写不敏感）。

## 测试策略

- `FileToolBase.ResolvePath` 路径穿越单元测试
- 各归档格式往返测试（create → list → extract → 验证内容）
- 搜索工具边界测试（大目录、无匹配、glob/regex 语法错误）
- 截断上限验证
