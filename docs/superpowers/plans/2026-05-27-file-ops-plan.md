# 高级文件系统操作 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 Plugin.FileOps 中新增 8 个高级文件操作工具（归档/搜索/元数据/对比），提取 FileToolKit.Shared 共享基类，重构 Plugin.FileTools 消除重复代码。

**Architecture:** FileToolKit.Shared 纯类库提供 FileToolBase 抽象基类（路径解析/沙箱/快捷方法），Plugin.FileTools 和 Plugin.FileOps 各自引用。IPluginStorage 新增 WorkspaceDirectory 统一路径获取。归档用 .NET 内置 ZIP+Tar 和 SharpCompress 7z。

**Tech Stack:** .NET 8, C#, AgentLilara.PluginSDK, SharpCompress v0.38.0, System.IO.Compression, System.Formats.Tar

---

### Task 1: 扩展 IPluginStorage 增加 WorkspaceDirectory

**Files:**
- Modify: `AgentLilara.PluginSDK/IPluginStorage.cs`
- Modify: `AgentCoreProcessor/Tool/Host/ToolContextImpl.cs:50-71`
- Modify: `AgentCoreProcessor/Component/ComponentHost.cs:314-330`

- [ ] **Step 1: 在 IPluginStorage 接口添加 WorkspaceDirectory**

```csharp
// AgentLilara.PluginSDK/IPluginStorage.cs
namespace AgentLilara.PluginSDK
{
    public interface IPluginStorage
    {
        string GlobalDirectory { get; }
        string InstanceDirectory { get; }
        /// <summary>共享工作区目录（Storage/Workspace/），所有文件工具共用。</summary>
        string WorkspaceDirectory { get; }
    }
}
```

- [ ] **Step 2: 在 PluginStorageImpl 中实现 WorkspaceDirectory**

```csharp
// AgentCoreProcessor/Tool/Host/ToolContextImpl.cs
// 在 PluginStorageImpl 类中，GlobalDirectory/InstanceDirectory 属性之后添加：
public string WorkspaceDirectory => Config.PathConfig.WorkspacePath;
```

- [ ] **Step 3: 在 ComponentStorage 中实现 WorkspaceDirectory**

```csharp
// AgentCoreProcessor/Component/ComponentHost.cs
// 在 ComponentStorage 类中，InstanceDirectory 属性之后添加：
public string WorkspaceDirectory => Config.PathConfig.WorkspacePath;
```

- [ ] **Step 4: 编译验证**

```bash
dotnet build
```

预期: Build succeeded, 无错误。

- [ ] **Step 5: 提交**

```bash
cd AgentLilaraProjectSolution
git add AgentLilara.PluginSDK/IPluginStorage.cs AgentCoreProcessor/Tool/Host/ToolContextImpl.cs AgentCoreProcessor/Component/ComponentHost.cs
git commit -m "feat: add WorkspaceDirectory to IPluginStorage"
```

---

### Task 2: 创建 FileToolKit.Shared 类库项目

**Files:**
- Create: `Plugins/FileToolKit.Shared/FileToolKit.Shared.csproj`
- Create: `Plugins/FileToolKit.Shared/FileToolBase.cs`
- Modify: `AgentLilaraProjectSolution.sln` (add project + nest under Plugins)

- [ ] **Step 1: 创建 .csproj**

```xml
<!-- Plugins/FileToolKit.Shared/FileToolKit.Shared.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>FileToolKit.Shared</RootNamespace>
    <!-- 不设 CopyToHostPlugins — 这不是插件，只是共享类库 -->
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\AgentLilara.PluginSDK\AgentLilara.PluginSDK.csproj">
      <Private>false</Private>
    </ProjectReference>
  </ItemGroup>

</Project>
```

- [ ] **Step 2: 编写 FileToolBase 抽象基类**

```csharp
// Plugins/FileToolKit.Shared/FileToolBase.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;

namespace FileToolKit.Shared
{
    public abstract class FileToolBase : ITool
    {
        protected readonly string WorkspaceDir;

        protected FileToolBase(string workspaceDir)
        {
            WorkspaceDir = Path.GetFullPath(workspaceDir);
            Directory.CreateDirectory(WorkspaceDir);
        }

        public abstract string Name { get; }
        public abstract string Description { get; }
        public abstract IReadOnlyList<ToolParameter> Parameters { get; }
        public abstract TimeSpan Timeout { get; }
        public abstract Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct);

        protected string? ResolvePath(string relativePath)
        {
            var full = Path.GetFullPath(Path.Combine(WorkspaceDir, relativePath));
            return full.StartsWith(WorkspaceDir, StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        protected static Task<ToolResult> Ok(string data) =>
            Task.FromResult(new ToolResult { Status = "success", Data = data });

        protected static Task<ToolResult> Fail(string error) =>
            Task.FromResult(new ToolResult { Status = "failed", Error = error });

        protected static string Truncate(string text, int maxLen, int totalCount, string itemLabel)
        {
            if (text.Length <= maxLen) return text;
            return text[..maxLen] + $"\n... (结果已截断，共 {totalCount} {itemLabel})";
        }

        /// <summary>从扩展名推断归档格式</summary>
        protected static string DetectFormat(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".zip" => "zip",
                ".tar" => "tar",
                ".gz" or ".tgz" => "tar.gz",
                ".7z" => "7z",
                _ => "zip"
            };
        }

        protected static string FormatSize(long bytes) => bytes switch
        {
            < 1024 => $"{bytes}B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1}KB",
            _ => $"{bytes / (1024.0 * 1024):F1}MB"
        };

        /// <summary>简单 glob 转正则，支持 ** * ?</summary>
        protected static Regex ConvertGlobToRegex(string glob)
        {
            var pattern = Regex.Escape(glob)
                .Replace("\\*\\*", "~~~DOTSTAR~~~")
                .Replace("\\*", "[^/\\\\]*")
                .Replace("\\?", "[^/\\\\]")
                .Replace("~~~DOTSTAR~~~", ".*");
            return new Regex($"^{pattern}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }
    }
}
```

- [ ] **Step 3: 将项目加入 solution，放在 Plugins 文件夹下**

在 `AgentLilaraProjectSolution.sln` 的 Plugins 文件夹 Nest 段中添加两个条目：
- 在 Project 段末尾（Plugin.GroupFileTools 之后）添加新项目声明
- 在 NestedProjects 段中添加 GUID 映射

使用 `dotnet sln` 命令：
```bash
cd AgentLilaraProjectSolution
dotnet sln AgentLilaraProjectSolution.sln add Plugins/FileToolKit.Shared/FileToolKit.Shared.csproj --solution-folder Plugins
```

- [ ] **Step 4: 编译验证**

```bash
dotnet build Plugins/FileToolKit.Shared/FileToolKit.Shared.csproj
```

预期: Build succeeded.

- [ ] **Step 5: 提交**

```bash
git add Plugins/FileToolKit.Shared/ AgentLilaraProjectSolution.sln
git commit -m "feat: add FileToolKit.Shared with FileToolBase abstract class"
```

---

### Task 3: 重构 Plugin.FileTools 继承 FileToolBase

**Files:**
- Modify: `Plugins/Plugin.FileTools/Plugin.FileTools.csproj`
- Modify: `Plugins/Plugin.FileTools/FileTools.cs`
- Modify: `Plugins/Plugin.FileTools/FileToolsComponent.cs`

核心思路：每个工具类从 `FileToolBase` 继承，去掉 `_workspaceDir`、`ResolvePath`、`Ok`、`Fail` 的所有手动实现。工具逻辑（ExecuteAsync 的输入解析和执行）保持不变。

- [ ] **Step 1: 添加项目引用**

```xml
<!-- Plugins/Plugin.FileTools/Plugin.FileTools.csproj -->
<!-- 在现有 <ItemGroup> 的 ProjectReference 之后，新增一个 ItemGroup: -->
<ItemGroup>
  <ProjectReference Include="..\FileToolKit.Shared\FileToolKit.Shared.csproj">
    <Private>false</Private>
  </ProjectReference>
</ItemGroup>
```

- [ ] **Step 2: 重写 FileTools.cs — 全部6个工具类**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using FileToolKit.Shared;

namespace Plugin.FileTools
{
    [ToolMeta(Group = "file", ContinueLoop = true, CapabilitySummary = "读取文本文件内容")]
    public class ReadTextTool : FileToolBase
    {
        public ReadTextTool(string workspaceDir) : base(workspaceDir) { }

        public override string Name => "read_text";
        public override string Description => "读取文本文件内容。路径相对于 Workspace 目录，只能访问该目录内的文件。支持指定行范围。";
        public override IReadOnlyList<ToolParameter> Parameters =>
        [
            new("path", "文件路径（相对于 Workspace 目录）", 0),
            new("start_line", "（可选）起始行号，从1开始", 1, isRequired: false),
            new("end_line", "（可选）结束行号", 2, isRequired: false)
        ];
        public override TimeSpan Timeout => TimeSpan.FromSeconds(10);

        public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var path = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
            var startStr = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";
            var endStr = resolvedInputs.Count > 2 ? resolvedInputs[2].Trim() : "";

            if (string.IsNullOrEmpty(path))
                return Fail("path 不能为空");

            var fullPath = ResolvePath(path);
            if (fullPath == null)
                return Fail("路径不合法：不能访问 Workspace 目录之外的文件");

            if (!File.Exists(fullPath))
                return Fail($"文件不存在: {path}");

            try
            {
                var lines = File.ReadAllLines(fullPath);
                int start = 1, end = lines.Length;

                if (int.TryParse(startStr, out var s) && s >= 1) start = s;
                if (int.TryParse(endStr, out var e) && e >= 1) end = Math.Min(e, lines.Length);
                if (start > lines.Length) return Ok($"(文件共 {lines.Length} 行，起始行超出范围)");

                var selected = lines[(start - 1)..end];
                var result = string.Join("\n", selected);

                if (result.Length > 8000)
                    result = result[..8000] + $"\n... (截断，文件共 {lines.Length} 行)";

                return Ok($"[{path}] 行 {start}-{end}/{lines.Length}\n{result}");
            }
            catch (Exception ex)
            {
                return Fail($"读取失败: {ex.Message}");
            }
        }
    }

    [ToolMeta(Group = "file", ContinueLoop = true, CapabilitySummary = "写入或追加文本文件")]
    public class WriteTextTool : FileToolBase
    {
        public WriteTextTool(string workspaceDir) : base(workspaceDir) { }

        public override string Name => "write_text";
        public override string Description => "写入文本文件。路径相对于 Workspace 目录。action: write(覆盖写入) / append(追加)。自动创建不存在的目录。";
        public override IReadOnlyList<ToolParameter> Parameters =>
        [
            new("path", "文件路径（相对于 Workspace 目录）", 0),
            new("content", "要写入的文本内容", 1),
            new("action", "（可选）write=覆盖（默认）/ append=追加", 2, isRequired: false)
        ];
        public override TimeSpan Timeout => TimeSpan.FromSeconds(10);

        public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var path = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
            var content = resolvedInputs.Count > 1 ? resolvedInputs[1] : "";
            var action = resolvedInputs.Count > 2 ? resolvedInputs[2].Trim().ToLower() : "write";

            if (string.IsNullOrEmpty(path))
                return Fail("path 不能为空");

            var fullPath = ResolvePath(path);
            if (fullPath == null)
                return Fail("路径不合法：不能访问 Workspace 目录之外的文件");

            try
            {
                var dir = Path.GetDirectoryName(fullPath)!;
                Directory.CreateDirectory(dir);

                if (action == "append")
                    File.AppendAllText(fullPath, content);
                else
                    File.WriteAllText(fullPath, content);

                var size = new FileInfo(fullPath).Length;
                return Ok($"已{(action == "append" ? "追加" : "写入")} {path} ({size} bytes)");
            }
            catch (Exception ex)
            {
                return Fail($"写入失败: {ex.Message}");
            }
        }
    }

    [ToolMeta(Group = "file", ContinueLoop = true, CapabilitySummary = "列出目录内容")]
    public class ListDirTool : FileToolBase
    {
        public ListDirTool(string workspaceDir) : base(workspaceDir) { }

        public override string Name => "list_dir";
        public override string Description => "列出目录下的文件和子目录。路径相对于 Workspace 目录，为空则列出根目录。";
        public override IReadOnlyList<ToolParameter> Parameters =>
        [
            new("path", "（可选）目录路径，相对于 Workspace，为空则列出根目录", 0, isRequired: false)
        ];
        public override TimeSpan Timeout => TimeSpan.FromSeconds(5);

        public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var path = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
            var fullPath = string.IsNullOrEmpty(path)
                ? WorkspaceDir
                : ResolvePath(path);

            if (fullPath == null)
                return Fail("路径不合法：不能访问 Workspace 目录之外");
            if (!Directory.Exists(fullPath))
                return Fail($"目录不存在: {path}");

            var sb = new System.Text.StringBuilder();
            var dirs = Directory.GetDirectories(fullPath);
            var files = Directory.GetFiles(fullPath);

            sb.AppendLine($"[{(string.IsNullOrEmpty(path) ? "/" : path)}] {dirs.Length} 个目录, {files.Length} 个文件");
            foreach (var d in dirs)
                sb.AppendLine($"  📁 {Path.GetFileName(d)}/");
            foreach (var f in files)
            {
                var info = new FileInfo(f);
                sb.AppendLine($"  📄 {info.Name} ({FormatSize(info.Length)})");
            }
            return Ok(sb.ToString().TrimEnd());
        }
    }

    [ToolMeta(Group = "file", ContinueLoop = true)]
    public class MoveFileTool : FileToolBase
    {
        public MoveFileTool(string workspaceDir) : base(workspaceDir) { }

        public override string Name => "move_file";
        public override string Description => "移动或重命名文件/目录。源和目标路径都相对于 Workspace 目录。";
        public override IReadOnlyList<ToolParameter> Parameters =>
        [
            new("source", "源路径", 0),
            new("destination", "目标路径", 1)
        ];
        public override TimeSpan Timeout => TimeSpan.FromSeconds(10);

        public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var src = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
            var dst = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";

            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(dst))
                return Fail("source 和 destination 都不能为空");

            var srcFull = ResolvePath(src);
            var dstFull = ResolvePath(dst);
            if (srcFull == null || dstFull == null)
                return Fail("路径不合法：不能访问 Workspace 目录之外");

            try
            {
                var dstDir = Path.GetDirectoryName(dstFull)!;
                Directory.CreateDirectory(dstDir);

                if (Directory.Exists(srcFull))
                    Directory.Move(srcFull, dstFull);
                else if (File.Exists(srcFull))
                    File.Move(srcFull, dstFull, overwrite: true);
                else
                    return Fail($"源不存在: {src}");

                return Ok($"已移动: {src} → {dst}");
            }
            catch (Exception ex) { return Fail($"移动失败: {ex.Message}"); }
        }
    }

    [ToolMeta(Group = "file", ContinueLoop = true)]
    public class DeleteFileTool : FileToolBase
    {
        public DeleteFileTool(string workspaceDir) : base(workspaceDir) { }

        public override string Name => "delete_file";
        public override string Description => "删除文件或空目录。路径相对于 Workspace 目录。非空目录需要先清空。";
        public override IReadOnlyList<ToolParameter> Parameters =>
        [
            new("path", "要删除的文件或空目录路径", 0)
        ];
        public override TimeSpan Timeout => TimeSpan.FromSeconds(5);

        public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var path = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
            if (string.IsNullOrEmpty(path))
                return Fail("path 不能为空");

            var fullPath = ResolvePath(path);
            if (fullPath == null)
                return Fail("路径不合法：不能访问 Workspace 目录之外");

            try
            {
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                    return Ok($"已删除文件: {path}");
                }
                if (Directory.Exists(fullPath))
                {
                    Directory.Delete(fullPath, recursive: false);
                    return Ok($"已删除目录: {path}");
                }
                return Fail($"不存在: {path}");
            }
            catch (IOException ex) when (ex.Message.Contains("not empty"))
            {
                return Fail($"目录非空，无法删除: {path}");
            }
            catch (Exception ex) { return Fail($"删除失败: {ex.Message}"); }
        }
    }

    [ToolMeta(Group = "file", ContinueLoop = true)]
    public class CopyFileTool : FileToolBase
    {
        public CopyFileTool(string workspaceDir) : base(workspaceDir) { }

        public override string Name => "copy_file";
        public override string Description => "复制文件。源和目标路径都相对于 Workspace 目录。";
        public override IReadOnlyList<ToolParameter> Parameters =>
        [
            new("source", "源文件路径", 0),
            new("destination", "目标文件路径", 1)
        ];
        public override TimeSpan Timeout => TimeSpan.FromSeconds(10);

        public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var src = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
            var dst = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";

            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(dst))
                return Fail("source 和 destination 都不能为空");

            var srcFull = ResolvePath(src);
            var dstFull = ResolvePath(dst);
            if (srcFull == null || dstFull == null)
                return Fail("路径不合法：不能访问 Workspace 目录之外");

            if (!File.Exists(srcFull))
                return Fail($"源文件不存在: {src}");

            try
            {
                var dstDir = Path.GetDirectoryName(dstFull)!;
                Directory.CreateDirectory(dstDir);
                File.Copy(srcFull, dstFull, overwrite: true);
                return Ok($"已复制: {src} → {dst}");
            }
            catch (Exception ex) { return Fail($"复制失败: {ex.Message}"); }
        }
    }
}
```

- [ ] **Step 3: 更新 FileToolsComponent 传 workspaceDir**

```csharp
// Plugins/Plugin.FileTools/FileToolsComponent.cs
using AgentLilara.PluginSDK;
using FileToolKit.Shared;

namespace Plugin.FileTools;

[Component(Name = "file-tools", Scope = ComponentScope.Global)]
public class FileToolsComponent : GlobalComponentBase
{
    private readonly List<ITool> _tools = new();

    public override ComponentMeta Meta => new()
    {
        Name = "file-tools",
        Description = "文件读写操作",
        DefaultEnabled = true,
        PromptPriority = 100
    };

    public override IEnumerable<ITool> Tools => _tools;

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        var workspaceDir = context.Storage.WorkspaceDirectory;
        _tools.Add(new ReadTextTool(workspaceDir));
        _tools.Add(new WriteTextTool(workspaceDir));
        _tools.Add(new ListDirTool(workspaceDir));
        _tools.Add(new MoveFileTool(workspaceDir));
        _tools.Add(new DeleteFileTool(workspaceDir));
        _tools.Add(new CopyFileTool(workspaceDir));
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 4: 编译验证**

```bash
dotnet build Plugins/Plugin.FileTools/Plugin.FileTools.csproj
```

预期: Build succeeded, 无警告。

- [ ] **Step 5: 提交**

```bash
git add Plugins/Plugin.FileTools/
git commit -m "refactor: migrate Plugin.FileTools to FileToolBase base class"
```

---

### Task 4: 创建 Plugin.FileOps 项目骨架

**Files:**
- Create: `Plugins/Plugin.FileOps/Plugin.FileOps.csproj`
- Create: `Plugins/Plugin.FileOps/FileOpsComponent.cs`
- Modify: `AgentLilaraProjectSolution.sln`

- [ ] **Step 1: 创建 .csproj**

```xml
<!-- Plugins/Plugin.FileOps/Plugin.FileOps.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Plugin.FileOps</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\AgentLilara.PluginSDK\AgentLilara.PluginSDK.csproj">
      <Private>false</Private>
    </ProjectReference>
    <ProjectReference Include="..\FileToolKit.Shared\FileToolKit.Shared.csproj">
      <Private>false</Private>
    </ProjectReference>
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="SharpCompress" Version="0.38.0" />
  </ItemGroup>

  <Target Name="CopyToHostPlugins" AfterTargets="Build">
    <Copy SourceFiles="$(OutputPath)Plugin.FileOps.dll"
          DestinationFolder="$(SolutionDir)AgentCoreProcessor\bin\$(Configuration)\net8.0\Plugins\" />
  </Target>

</Project>
```

- [ ] **Step 2: 创建空 Component**

```csharp
// Plugins/Plugin.FileOps/FileOpsComponent.cs
using AgentLilara.PluginSDK;

namespace Plugin.FileOps;

[Component(Name = "file-ops", Scope = ComponentScope.Global)]
public class FileOpsComponent : GlobalComponentBase
{
    private readonly List<ITool> _tools = new();

    public override ComponentMeta Meta => new()
    {
        Name = "file-ops",
        Description = "高级文件操作：压缩归档、搜索、元数据、对比",
        DefaultEnabled = true,
        PromptPriority = 100
    };

    public override IEnumerable<ITool> Tools => _tools;

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        // 工具在后续 Task 中逐个添加
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 3: 加入 solution**

```bash
cd AgentLilaraProjectSolution
dotnet sln AgentLilaraProjectSolution.sln add Plugins/Plugin.FileOps/Plugin.FileOps.csproj --solution-folder Plugins
```

- [ ] **Step 4: 编译验证**

```bash
dotnet build Plugins/Plugin.FileOps/Plugin.FileOps.csproj
```

预期: Build succeeded. SharpCompress 包已恢复。

- [ ] **Step 5: 提交**

```bash
git add Plugins/Plugin.FileOps/ AgentLilaraProjectSolution.sln
git commit -m "feat: add Plugin.FileOps project skeleton with SharpCompress"
```

---

### Task 5: 实现归档工具（archive_create / archive_extract / archive_list）

**Files:**
- Create: `Plugins/Plugin.FileOps/ArchiveTools.cs`
- Modify: `Plugins/Plugin.FileOps/FileOpsComponent.cs`

- [ ] **Step 1: 编写 ArchiveTools.cs**

```csharp
// Plugins/Plugin.FileOps/ArchiveTools.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using FileToolKit.Shared;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Writers;

namespace Plugin.FileOps
{
    [ToolMeta(Group = "file", ContinueLoop = true, CapabilitySummary = "创建压缩归档")]
    public class ArchiveCreateTool : FileToolBase
    {
        public ArchiveCreateTool(string workspaceDir) : base(workspaceDir) { }

        public override string Name => "archive_create";
        public override string Description => "将文件或目录打包压缩。format 从目标扩展名自动推断（zip/tar.gz/7z），也可手动指定。level: fast/balanced/best。";
        public override IReadOnlyList<ToolParameter> Parameters =>
        [
            new("path", "要压缩的文件或目录路径", 0),
            new("output", "输出归档文件路径", 1),
            new("format", "（可选）zip / tar.gz / 7z，默认从 output 扩展名推断", 2, isRequired: false),
            new("level", "（可选）fast / balanced / best，默认 balanced", 3, isRequired: false)
        ];
        public override TimeSpan Timeout => TimeSpan.FromMinutes(2);

        public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var path = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
            var output = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";
            var format = resolvedInputs.Count > 2 ? resolvedInputs[2].Trim().ToLower() : "";
            var levelStr = resolvedInputs.Count > 3 ? resolvedInputs[3].Trim().ToLower() : "";

            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(output))
                return Fail("path 和 output 都不能为空");

            if (string.IsNullOrEmpty(format)) format = DetectFormat(output);
            var compressionLevel = levelStr switch
            {
                "fast" => System.IO.Compression.CompressionLevel.Fastest,
                "best" => System.IO.Compression.CompressionLevel.Optimal,
                _ => System.IO.Compression.CompressionLevel.Optimal
            };

            var srcFull = ResolvePath(path);
            var dstFull = ResolvePath(output);
            if (srcFull == null || dstFull == null)
                return Fail("路径不合法：不能访问 Workspace 目录之外");
            if (!File.Exists(srcFull) && !Directory.Exists(srcFull))
                return Fail($"源不存在: {path}");

            try
            {
                var dstDir = Path.GetDirectoryName(dstFull)!;
                Directory.CreateDirectory(dstDir);

                switch (format)
                {
                    case "zip":
                        if (File.Exists(srcFull))
                        {
                            using var archive = ZipFile.Open(dstFull, ZipArchiveMode.Create);
                            archive.CreateEntryFromFile(srcFull, Path.GetFileName(srcFull), compressionLevel);
                        }
                        else
                        {
                            ZipFile.CreateFromDirectory(srcFull, dstFull, compressionLevel, includeBaseDirectory: true);
                        }
                        break;

                    case "tar":
                        {
                            using var fs = File.Create(dstFull);
                            using var writer = new System.Formats.Tar.TarWriter(fs, leaveOpen: false);
                            AddToTar(writer, srcFull, Path.GetFileName(srcFull), ct);
                        }
                        break;

                    case "tar.gz":
                    case "tgz":
                        {
                            using var fs = File.Create(dstFull);
                            using var gz = new GZipStream(fs, compressionLevel);
                            using var writer = new System.Formats.Tar.TarWriter(gz, leaveOpen: false);
                            AddToTar(writer, srcFull, Path.GetFileName(srcFull), ct);
                        }
                        break;

                    case "7z":
                        {
                            using var archive = SevenZipArchive.Create();
                            if (File.Exists(srcFull))
                                archive.AddEntry(Path.GetFileName(srcFull), srcFull);
                            else
                                archive.AddAllFromDirectory(srcFull);
                            archive.SaveTo(dstFull, new WriterOptions(CompressionType.LZMA));
                        }
                        break;

                    default:
                        return Fail($"不支持的格式: {format}。支持 zip / tar / tar.gz / 7z");
                }

                var size = new FileInfo(dstFull).Length;
                return Ok($"已创建 {format} 归档: {output} ({FormatSize(size)})");
            }
            catch (Exception ex) { return Fail($"创建归档失败: {ex.Message}"); }
        }

        private static void AddToTar(System.Formats.Tar.TarWriter writer, string sourcePath, string entryName, CancellationToken ct)
        {
            if (File.Exists(sourcePath))
            {
                writer.WriteEntry(sourcePath, entryName);
            }
            else if (Directory.Exists(sourcePath))
            {
                foreach (var file in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    var relativePath = Path.GetRelativePath(sourcePath, file);
                    writer.WriteEntry(file, Path.Combine(entryName, relativePath));
                }
            }
        }

    }

    [ToolMeta(Group = "file", ContinueLoop = true, CapabilitySummary = "解压归档文件")]
    public class ArchiveExtractTool : FileToolBase
    {
        public ArchiveExtractTool(string workspaceDir) : base(workspaceDir) { }

        public override string Name => "archive_extract";
        public override string Description => "解压归档文件到指定目录。支持 zip/tar/tar.gz/7z 格式。";
        public override IReadOnlyList<ToolParameter> Parameters =>
        [
            new("source", "归档文件路径", 0),
            new("target_dir", "（可选）解压目标目录，默认为归档文件所在目录下同名文件夹", 1, isRequired: false)
        ];
        public override TimeSpan Timeout => TimeSpan.FromMinutes(2);

        public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var source = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
            var targetDir = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";

            if (string.IsNullOrEmpty(source))
                return Fail("source 不能为空");

            var srcFull = ResolvePath(source);
            if (srcFull == null) return Fail("路径不合法：不能访问 Workspace 目录之外");
            if (!File.Exists(srcFull)) return Fail($"归档文件不存在: {source}");

            if (string.IsNullOrEmpty(targetDir))
                targetDir = Path.GetFileNameWithoutExtension(source);
            var targetFull = ResolvePath(targetDir);
            if (targetFull == null) return Fail("目标路径不合法");

            try
            {
                Directory.CreateDirectory(targetFull);
                var format = DetectFormat(source);

                switch (format)
                {
                    case "zip":
                        ZipFile.ExtractToDirectory(srcFull, targetFull, overwriteFiles: true);
                        break;

                    case "tar":
                        {
                            using var fs = File.OpenRead(srcFull);
                            System.Formats.Tar.TarFile.ExtractToDirectory(fs, targetFull, overwriteFiles: true);
                        }
                        break;

                    case "tar.gz":
                    case "tgz":
                        {
                            using var fs = File.OpenRead(srcFull);
                            using var gz = new GZipStream(fs, CompressionMode.Decompress);
                            System.Formats.Tar.TarFile.ExtractToDirectory(gz, targetFull, overwriteFiles: true);
                        }
                        break;

                    case "7z":
                        {
                            using var archive = SevenZipArchive.Open(srcFull);
                            foreach (var entry in archive.Entries)
                            {
                                ct.ThrowIfCancellationRequested();
                                if (!entry.IsDirectory)
                                    entry.WriteToDirectory(targetFull, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
                            }
                        }
                        break;

                    default:
                        return Fail($"无法识别归档格式: {source}");
                }

                var count = Directory.GetFileSystemEntries(targetFull).Length;
                return Ok($"已解压 {source} → {targetDir}/ ({count} 个条目)");
            }
            catch (Exception ex) { return Fail($"解压失败: {ex.Message}"); }
        }
    }

    [ToolMeta(Group = "file", ContinueLoop = true, CapabilitySummary = "列出归档内容")]
    public class ArchiveListTool : FileToolBase
    {
        public ArchiveListTool(string workspaceDir) : base(workspaceDir) { }

        public override string Name => "archive_list";
        public override string Description => "列出压缩包内容（不实际解压）。最多显示 100 个条目。";
        public override IReadOnlyList<ToolParameter> Parameters =>
        [
            new("source", "归档文件路径", 0)
        ];
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var source = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
            if (string.IsNullOrEmpty(source)) return Fail("source 不能为空");

            var srcFull = ResolvePath(source);
            if (srcFull == null) return Fail("路径不合法：不能访问 Workspace 目录之外");
            if (!File.Exists(srcFull)) return Fail($"归档文件不存在: {source}");

            try
            {
                var format = DetectFormat(source);
                var sb = new System.Text.StringBuilder();
                var count = 0;
                const int maxEntries = 100;

                switch (format)
                {
                    case "zip":
                        using (var archive = ZipFile.OpenRead(srcFull))
                        {
                            foreach (var entry in archive.Entries)
                            {
                                if (count >= maxEntries) break;
                                sb.AppendLine(entry.FullName + (entry.Length > 0 ? $" ({FormatSize(entry.Length)})" : ""));
                                count++;
                            }
                        }
                        break;

                    case "tar":
                    case "tar.gz":
                    case "tgz":
                        {
                            using var fs = File.OpenRead(srcFull);
                            Stream stream = fs;
                            if (format is "tar.gz" or "tgz")
                                stream = new GZipStream(fs, CompressionMode.Decompress);
                            using var reader = new System.Formats.Tar.TarReader(stream, leaveOpen: false);
                            while (reader.GetNextEntry() is { } entry)
                            {
                                if (count >= maxEntries) break;
                                sb.AppendLine($"{entry.Name} ({FormatSize(entry.Length)}) [{entry.EntryType}]");
                                count++;
                            }
                        }
                        break;

                    case "7z":
                        using (var archive = SevenZipArchive.Open(srcFull))
                        {
                            foreach (var entry in archive.Entries)
                            {
                                if (count >= maxEntries) break;
                                sb.AppendLine(entry.Key + (entry.Size > 0 ? $" ({FormatSize((long)entry.Size)})" : ""));
                                count++;
                            }
                        }
                        break;

                    default:
                        return Fail($"无法识别归档格式: {source}");
                }

                var result = count >= maxEntries
                    ? sb.ToString().TrimEnd() + $"\n... (结果已截断，共 {count} 条目)"
                    : sb.ToString().TrimEnd();
                return Ok(result.Length > 0 ? result : "(空归档)");
            }
            catch (Exception ex) { return Fail($"列出归档失败: {ex.Message}"); }
        }
    }
}
```

- [ ] **Step 2: 在 FileOpsComponent 中注册归档工具**

```csharp
// 修改 FileOpsComponent.OnInitAsync
public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
{
    var workspaceDir = context.Storage.WorkspaceDirectory;
    _tools.Add(new ArchiveCreateTool(workspaceDir));
    _tools.Add(new ArchiveExtractTool(workspaceDir));
    _tools.Add(new ArchiveListTool(workspaceDir));
    return Task.CompletedTask;
}
```

- [ ] **Step 3: 编译验证**

```bash
dotnet build Plugins/Plugin.FileOps/Plugin.FileOps.csproj
```

预期: Build succeeded. 若 SharpCompress API 有差异，按编译错误调整。

- [ ] **Step 4: 提交**

```bash
git add Plugins/Plugin.FileOps/
git commit -m "feat: add archive tools (create/extract/list) with ZIP/tar.gz/7z support"
```

---

### Task 6: 实现搜索工具（search_files / grep_files）

**Files:**
- Create: `Plugins/Plugin.FileOps/SearchTools.cs`
- Modify: `Plugins/Plugin.FileOps/FileOpsComponent.cs`

- [ ] **Step 1: 编写 SearchTools.cs**

```csharp
// Plugins/Plugin.FileOps/SearchTools.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using FileToolKit.Shared;

namespace Plugin.FileOps
{
    [ToolMeta(Group = "file", ContinueLoop = true, CapabilitySummary = "按文件名模式搜索文件")]
    public class SearchFilesTool : FileToolBase
    {
        public SearchFilesTool(string workspaceDir) : base(workspaceDir) { }

        public override string Name => "search_files";
        public override string Description => "使用 glob 模式搜索文件（如 **/*.cs, *.txt, logs/*.json）。最多返回 100 个结果。";
        public override IReadOnlyList<ToolParameter> Parameters =>
        [
            new("pattern", "glob 模式，如 **/*.cs", 0),
            new("dir", "（可选）搜索起始目录，默认 Workspace 根目录", 1, isRequired: false),
            new("recursive", "（可选）递归搜索，默认 true", 2, isRequired: false)
        ];
        public override TimeSpan Timeout => TimeSpan.FromSeconds(15);

        public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var pattern = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
            var dir = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";
            var recursive = resolvedInputs.Count <= 2 || !resolvedInputs[2].Trim().Equals("false", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrEmpty(pattern))
                return Fail("pattern 不能为空");

            var baseDir = string.IsNullOrEmpty(dir) ? WorkspaceDir : ResolvePath(dir);
            if (baseDir == null) return Fail("路径不合法：不能访问 Workspace 目录之外");
            if (!Directory.Exists(baseDir)) return Fail($"目录不存在: {dir}");

            try
            {
                var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                var globPattern = ConvertGlobToRegex(pattern);

                var allFiles = Directory.GetFiles(baseDir, "*", searchOption);
                var sb = new System.Text.StringBuilder();
                var count = 0;
                const int maxResults = 100;

                foreach (var file in allFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(WorkspaceDir, file);
                    if (globPattern.IsMatch(relative) || globPattern.IsMatch(Path.GetFileName(file)))
                    {
                        if (count >= maxResults) break;
                        var info = new FileInfo(file);
                        sb.AppendLine($"{relative} ({FormatSize(info.Length)})");
                        count++;
                    }
                }

                sb.Insert(0, $"搜索 '{pattern}' 结果 ({count} 个文件):\n");
                if (count >= maxResults)
                    sb.AppendLine($"... (结果已截断，共 {count} 文件)");
                return Ok(sb.ToString().TrimEnd());
            }
            catch (Exception ex) { return Fail($"搜索失败: {ex.Message}"); }
        }
    }

    [ToolMeta(Group = "file", ContinueLoop = true, CapabilitySummary = "在文件内容中搜索文本")]
    public class GrepFilesTool : FileToolBase
    {
        public GrepFilesTool(string workspaceDir) : base(workspaceDir) { }

        public override string Name => "grep_files";
        public override string Description => "在目录中搜索文件内容（支持正则）。最多返回 30 条匹配，每条截断到 500 字符。";
        public override IReadOnlyList<ToolParameter> Parameters =>
        [
            new("pattern", "搜索模式（正则表达式）", 0),
            new("dir", "（可选）搜索起始目录，默认 Workspace 根目录", 1, isRequired: false),
            new("file_pattern", "（可选）glob 过滤文件名，如 *.cs", 2, isRequired: false),
            new("max_results", "（可选）最大结果数，默认 30", 3, isRequired: false)
        ];
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var pattern = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
            var dir = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";
            var filePattern = resolvedInputs.Count > 2 ? resolvedInputs[2].Trim() : "";
            var maxStr = resolvedInputs.Count > 3 ? resolvedInputs[3].Trim() : "30";

            if (string.IsNullOrEmpty(pattern))
                return Fail("pattern 不能为空");
            if (!int.TryParse(maxStr, out var maxResults)) maxResults = 30;
            maxResults = Math.Min(maxResults, 30);

            var baseDir = string.IsNullOrEmpty(dir) ? WorkspaceDir : ResolvePath(dir);
            if (baseDir == null) return Fail("路径不合法：不能访问 Workspace 目录之外");
            if (!Directory.Exists(baseDir)) return Fail($"目录不存在: {dir}");

            try
            {
                var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled,
                    TimeSpan.FromSeconds(5));
                var fileRegex = string.IsNullOrEmpty(filePattern) ? null
                    : ConvertGlobToRegex(filePattern);

                var files = Directory.GetFiles(baseDir, "*", SearchOption.AllDirectories);
                var sb = new System.Text.StringBuilder();
                var totalCount = 0;

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (totalCount >= maxResults) break;

                    if (fileRegex != null)
                    {
                        var relative = Path.GetRelativePath(WorkspaceDir, file);
                        if (!fileRegex.IsMatch(relative)) continue;
                    }

                    // 跳过二进制/大文件
                    var info = new FileInfo(file);
                    if (info.Length > 10 * 1024 * 1024) continue; // >10MB

                    try
                    {
                        var lines = File.ReadAllLines(file);
                        for (int i = 0; i < lines.Length && totalCount < maxResults; i++)
                        {
                            var match = regex.Match(lines[i]);
                            if (match.Success)
                            {
                                var relative = Path.GetRelativePath(WorkspaceDir, file);
                                var line = lines[i].Length > 500 ? lines[i][..500] + "..." : lines[i];
                                sb.AppendLine($"{relative}:{i + 1}: {line}");
                                totalCount++;
                            }
                        }
                    }
                    catch (IOException) { continue; }
                }

                sb.Insert(0, $"grep '{pattern}' 结果 ({totalCount} 条匹配):\n");
                if (totalCount >= maxResults)
                    sb.AppendLine($"... (结果已截断，共 {totalCount} 条匹配)");
                return Ok(sb.ToString().TrimEnd());
            }
            catch (RegexMatchTimeoutException) { return Fail("正则匹配超时，请简化表达式"); }
            catch (Exception ex) { return Fail($"搜索失败: {ex.Message}"); }
        }
    }
}
```

- [ ] **Step 2: 在 FileOpsComponent.OnInitAsync 中添加注册**

```csharp
_tools.Add(new SearchFilesTool(workspaceDir));
_tools.Add(new GrepFilesTool(workspaceDir));
```

- [ ] **Step 3: 编译验证**

```bash
dotnet build Plugins/Plugin.FileOps/Plugin.FileOps.csproj
```

预期: Build succeeded.

- [ ] **Step 4: 提交**

```bash
git add Plugins/Plugin.FileOps/
git commit -m "feat: add search_files and grep_files tools"
```

---

### Task 7: 实现元数据和对比工具（file_info / file_hash / compare_files）

**Files:**
- Create: `Plugins/Plugin.FileOps/MetaTools.cs`
- Modify: `Plugins/Plugin.FileOps/FileOpsComponent.cs`

- [ ] **Step 1: 编写 MetaTools.cs**

```csharp
// Plugins/Plugin.FileOps/MetaTools.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using FileToolKit.Shared;

namespace Plugin.FileOps
{
    [ToolMeta(Group = "file", ContinueLoop = true, CapabilitySummary = "获取文件详细信息")]
    public class FileInfoTool : FileToolBase
    {
        public FileInfoTool(string workspaceDir) : base(workspaceDir) { }

        public override string Name => "file_info";
        public override string Description => "获取文件/目录的详细信息：大小、创建/修改时间、MIME类型、文本文件行数。";
        public override IReadOnlyList<ToolParameter> Parameters =>
        [
            new("path", "文件或目录路径", 0)
        ];
        public override TimeSpan Timeout => TimeSpan.FromSeconds(5);

        public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var path = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
            if (string.IsNullOrEmpty(path)) return Fail("path 不能为空");

            var fullPath = ResolvePath(path);
            if (fullPath == null) return Fail("路径不合法：不能访问 Workspace 目录之外");

            try
            {
                if (File.Exists(fullPath))
                {
                    var info = new FileInfo(fullPath);
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"类型: 文件");
                    sb.AppendLine($"大小: {FormatSize(info.Length)} ({info.Length} bytes)");
                    sb.AppendLine($"创建时间: {info.CreationTime:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine($"修改时间: {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine($"MIME: {GetMimeType(info.Extension)}");

                    if (IsTextFile(info.Extension))
                    {
                        try
                        {
                            var lineCount = File.ReadLines(fullPath).Count();
                            sb.AppendLine($"行数: {lineCount}");
                        }
                        catch { sb.AppendLine("行数: (读取失败)"); }
                    }

                    return Ok(sb.ToString().TrimEnd());
                }
                else if (Directory.Exists(fullPath))
                {
                    var info = new DirectoryInfo(fullPath);
                    var files = Directory.GetFiles(fullPath).Length;
                    var dirs = Directory.GetDirectories(fullPath).Length;
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"类型: 目录");
                    sb.AppendLine($"创建时间: {info.CreationTime:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine($"修改时间: {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine($"包含: {files} 个文件, {dirs} 个子目录");

                    try
                    {
                        var totalSize = Directory.GetFiles(fullPath, "*", SearchOption.AllDirectories)
                            .Sum(f => new FileInfo(f).Length);
                        sb.AppendLine($"总大小: {FormatSize(totalSize)}");
                    }
                    catch { /* 权限不足时忽略 */ }

                    return Ok(sb.ToString().TrimEnd());
                }
                return Fail($"不存在: {path}");
            }
            catch (Exception ex) { return Fail($"获取信息失败: {ex.Message}"); }
        }

        private static string GetMimeType(string extension) => extension.ToLowerInvariant() switch
        {
            ".txt" => "text/plain",
            ".md" => "text/markdown",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".js" => "text/javascript",
            ".ts" => "text/typescript",
            ".cs" => "text/x-csharp",
            ".csproj" => "text/xml",
            ".sln" => "text/plain",
            ".py" => "text/x-python",
            ".yaml" or ".yml" => "text/yaml",
            ".toml" => "application/toml",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            ".tar" => "application/x-tar",
            ".gz" => "application/gzip",
            ".7z" => "application/x-7z-compressed",
            ".dll" => "application/x-msdownload",
            ".exe" => "application/x-msdownload",
            _ => "application/octet-stream"
        };

        private static bool IsTextFile(string extension) => extension.ToLowerInvariant() switch
        {
            ".txt" or ".md" or ".json" or ".xml" or ".html" or ".htm" or ".css"
                or ".js" or ".ts" or ".cs" or ".py" or ".yaml" or ".yml" or ".toml"
                or ".csproj" or ".sln" or ".svg" or ".csv" or ".log"
                or ".config" or ".props" or ".targets" => true,
            _ => false
        };
    }

    [ToolMeta(Group = "file", ContinueLoop = true, CapabilitySummary = "计算文件哈希值")]
    public class FileHashTool : FileToolBase
    {
        public FileHashTool(string workspaceDir) : base(workspaceDir) { }

        public override string Name => "file_hash";
        public override string Description => "计算文件的哈希值。支持 MD5 和 SHA256，默认 SHA256。";
        public override IReadOnlyList<ToolParameter> Parameters =>
        [
            new("path", "文件路径", 0),
            new("algorithm", "（可选）md5 / sha256，默认 sha256", 1, isRequired: false)
        ];
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var path = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
            var algo = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim().ToLower() : "sha256";

            if (string.IsNullOrEmpty(path)) return Fail("path 不能为空");

            var fullPath = ResolvePath(path);
            if (fullPath == null) return Fail("路径不合法：不能访问 Workspace 目录之外");
            if (!File.Exists(fullPath)) return Fail($"文件不存在: {path}");

            try
            {
                using var stream = File.OpenRead(fullPath);
                var hashBytes = algo switch
                {
                    "md5" => MD5.HashData(stream),
                    "sha256" => SHA256.HashData(stream),
                    _ => { stream.Dispose(); return null; }
                };

                if (hashBytes == null)
                    return Fail($"不支持的算法: {algo}。支持 md5 / sha256");

                var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
                return Ok($"{algo.ToUpper()}: {hash}");
            }
            catch (Exception ex) { return Fail($"计算哈希失败: {ex.Message}"); }
        }
    }

    [ToolMeta(Group = "file", ContinueLoop = true, CapabilitySummary = "对比两个文本文件")]
    public class CompareFilesTool : FileToolBase
    {
        public CompareFilesTool(string workspaceDir) : base(workspaceDir) { }

        public override string Name => "compare_files";
        public override string Description => "逐行对比两个文本文件，返回差异摘要。最多显示前 100 行差异。";
        public override IReadOnlyList<ToolParameter> Parameters =>
        [
            new("source", "源文件路径", 0),
            new("target", "目标文件路径", 1)
        ];
        public override TimeSpan Timeout => TimeSpan.FromSeconds(15);

        public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var src = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
            var dst = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";

            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(dst))
                return Fail("source 和 target 都不能为空");

            var srcFull = ResolvePath(src);
            var dstFull = ResolvePath(dst);
            if (srcFull == null || dstFull == null)
                return Fail("路径不合法：不能访问 Workspace 目录之外");
            if (!File.Exists(srcFull)) return Fail($"源文件不存在: {src}");
            if (!File.Exists(dstFull)) return Fail($"目标文件不存在: {dst}");

            try
            {
                var srcLines = File.ReadAllLines(srcFull);
                var dstLines = File.ReadAllLines(dstFull);

                // 简易 LCS diff
                var diffs = ComputeDiff(srcLines, dstLines, 100);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"对比 {src} ↔ {dst}:");
                sb.AppendLine($"源: {srcLines.Length} 行, 目标: {dstLines.Length} 行");

                var added = diffs.Count(d => d.Type == '+');
                var removed = diffs.Count(d => d.Type == '-');
                var changed = diffs.Count(d => d.Type == '~');
                sb.AppendLine($"差异: +{added} 增 / -{removed} 删 / ~{changed} 改");

                if (diffs.Count > 0)
                {
                    sb.AppendLine("---");
                    foreach (var d in diffs.Take(100))
                    {
                        sb.AppendLine($"{d.Type} L{d.SrcLine:D4}→L{d.DstLine:D4}: {d.Content}");
                    }
                    if (diffs.Count > 100)
                        sb.AppendLine($"... (结果已截断，共 {diffs.Count} 处差异)");
                }
                else
                {
                    sb.AppendLine("文件完全相同。");
                }

                return Ok(sb.ToString().TrimEnd());
            }
            catch (Exception ex) { return Fail($"对比失败: {ex.Message}"); }
        }

        private record DiffEntry(char Type, int SrcLine, int DstLine, string Content);

        private static List<DiffEntry> ComputeDiff(string[] src, string[] dst, int maxDiffs)
        {
            var diffs = new List<DiffEntry>();
            int i = 0, j = 0;

            while (i < src.Length && j < dst.Length)
            {
                if (diffs.Count >= maxDiffs) break;

                if (src[i] == dst[j])
                {
                    i++; j++;
                }
                else
                {
                    // 查找下一处匹配
                    var found = false;
                    for (int look = 1; look <= 3 && (i + look < src.Length || j + look < dst.Length); look++)
                    {
                        if (j + look < dst.Length && src[i] == dst[j + look])
                        {
                            for (int k = 0; k < look; k++)
                                diffs.Add(new DiffEntry('+', i, j + k + 1, dst[j + k].Length > 200 ? dst[j + k][..200] + "..." : dst[j + k]));
                            j += look;
                            found = true;
                            break;
                        }
                        if (i + look < src.Length && src[i + look] == dst[j])
                        {
                            for (int k = 0; k < look; k++)
                                diffs.Add(new DiffEntry('-', i + k + 1, j, src[i + k].Length > 200 ? src[i + k][..200] + "..." : src[i + k]));
                            i += look;
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                    {
                        var content = src[i].Length > 200 ? src[i][..200] + "..." : src[i];
                        diffs.Add(new DiffEntry('~', i + 1, j + 1, content));
                        i++; j++;
                    }
                }
            }

            while (i < src.Length && diffs.Count < maxDiffs)
            {
                diffs.Add(new DiffEntry('-', i + 1, dst.Length, src[i].Length > 200 ? src[i][..200] + "..." : src[i]));
                i++;
            }
            while (j < dst.Length && diffs.Count < maxDiffs)
            {
                diffs.Add(new DiffEntry('+', src.Length, j + 1, dst[j].Length > 200 ? dst[j][..200] + "..." : dst[j]));
                j++;
            }

            return diffs;
        }
    }
}
```

- [ ] **Step 2: 在 FileOpsComponent.OnInitAsync 中注册**

```csharp
_tools.Add(new FileInfoTool(workspaceDir));
_tools.Add(new FileHashTool(workspaceDir));
_tools.Add(new CompareFilesTool(workspaceDir));
```

- [ ] **Step 3: 编译验证**

```bash
dotnet build Plugins/Plugin.FileOps/Plugin.FileOps.csproj
```

预期: Build succeeded.

- [ ] **Step 4: 提交**

```bash
git add Plugins/Plugin.FileOps/
git commit -m "feat: add file_info, file_hash, and compare_files tools"
```

---

### Task 8: 完整构建验证和试运行

**Files:**
- 无新建文件

- [ ] **Step 1: 全量编译**

```bash
cd AgentLilaraProjectSolution
dotnet build
```

预期: 零错误零警告。

- [ ] **Step 2: 验证 DLL 就位**

```bash
ls AgentCoreProcessor/bin/Debug/net8.0/Plugins/Plugin.FileOps.dll
ls AgentCoreProcessor/bin/Debug/net8.0/Plugins/Plugin.FileTools.dll
```

预期: 两个 DLL 都在 Plugins 目录下。

- [ ] **Step 3: 启动试运行**

```bash
cmd //c "taskkill /IM AgentCoreProcessor.exe /T /F" 2>/dev/null
dotnet run --project AgentCoreProcessor/AgentCoreProcessor.csproj -- --test --delay 2
```

观察启动日志，确认：
- `Plugin.FileTools` 组件加载成功（6 个工具注册）
- `Plugin.FileOps` 组件加载成功（8 个工具注册）
- 无 `WorkspaceDirectory` 相关的路径解析错误

- [ ] **Step 4: 提交**

```bash
git add -A
git commit -m "chore: verify full build and DLL deployment for file-ops"
```

---

### Task 9: 更新文档和 CLAUDE.md

**Files:**
- Modify: `docs/plugins/api-reference.md`
- Modify: `CLAUDE.md`（项目根目录）
- Modify: `CLAUDE.md`（solution 目录，`AgentLilaraProjectSolution/CLAUDE.md`）

- [ ] **Step 1: 更新插件 API 参考**

在 `docs/plugins/api-reference.md` 的 `IPluginStorage` 接口部分，添加 `WorkspaceDirectory` 属性说明：

```markdown
| `WorkspaceDirectory` | `string` | 共享工作区目录（`Storage/Workspace/`），所有文件工具共用此目录进行沙箱化文件操作 |
```

- [ ] **Step 2: 更新 solution CLAUDE.md**

在 `AgentLilaraProjectSolution/CLAUDE.md` 的"编码约定"部分添加路径使用规范：

```markdown
- 插件内需要访问 Workspace/ 目录时，优先使用 `IPluginStorage.WorkspaceDirectory`，不要通过 `GlobalDirectory` 做 `..` 相对跳转。路径结构变化时只需改一处（PluginStorageImpl / ComponentStorage），插件代码无需变动。
- 任何文件插件都应让 Component 从 `context.Storage.WorkspaceDirectory` 获取路径，传给工具构造函数。不要在工具类内部自行计算路径。
```

以及在"关键路径"的插件项目部分追加：

```markdown
-           Plugins/FileToolKit.Shared/ — FileToolBase 抽象基类（路径解析/沙箱/快捷方法），文件类插件共享引用
-           Plugins/Plugin.FileOps/ — archive_create/archive_extract/archive_list/search_files/grep_files/file_info/file_hash/compare_files
```

- [ ] **Step 3: 提交**

```bash
git add docs/plugins/api-reference.md CLAUDE.md AgentLilaraProjectSolution/CLAUDE.md
git commit -m "docs: add WorkspaceDirectory guidance and file-ops plugin references"
```
