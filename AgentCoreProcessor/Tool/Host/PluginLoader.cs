using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Tool.Contract;

namespace AgentCoreProcessor.Tool.Host
{
    /// <summary>
    /// 插件加载器。扫描 Storage/Plugins/ 目录下的 DLL，
    /// 加载实现 ITool 的类并注册到 ToolRegistry。
    /// 支持运行时重载（卸载旧 context + 重新加载）。
    /// </summary>
    internal class PluginLoader
    {
        private static string PluginDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins");

        private readonly IToolContext toolContext;
        private readonly List<PluginEntry> loadedPlugins = new();

        public int LoadedToolCount => loadedPlugins.Sum(p => p.ToolNames.Count);
        public IReadOnlyList<PluginEntry> LoadedPlugins => loadedPlugins;

        public PluginLoader(IToolContext toolContext)
        {
            this.toolContext = toolContext;
        }

        /// <summary>扫描并加载所有插件。启动时调用。</summary>
        public void LoadAll()
        {
            if (!Directory.Exists(PluginDir))
            {
                Directory.CreateDirectory(PluginDir);
                FrameworkLogger.Log("PluginLoader", $"插件目录已创建: {PluginDir}");
                return;
            }

            var dlls = Directory.GetFiles(PluginDir, "*.dll", SearchOption.AllDirectories);
            if (dlls.Length == 0)
            {
                FrameworkLogger.Log("PluginLoader", "未发现插件 DLL");
                return;
            }

            foreach (var dllPath in dlls)
            {
                LoadPlugin(dllPath);
            }

            FrameworkLogger.Log("PluginLoader",
                $"插件加载完成: {loadedPlugins.Count} 个 DLL, {LoadedToolCount} 个工具");
        }

        /// <summary>重载所有插件。运行时调用。</summary>
        public void ReloadAll()
        {
            FrameworkLogger.Log("PluginLoader", "开始重载插件...");
            UnloadAll();
            LoadAll();
        }

        /// <summary>卸载所有插件。</summary>
        public void UnloadAll()
        {
            foreach (var entry in loadedPlugins)
            {
                foreach (var name in entry.ToolNames)
                    ToolRegistry.Unregister(name);

                entry.LoadContext.Unload();
            }
            loadedPlugins.Clear();
        }

        private void LoadPlugin(string dllPath)
        {
            var fileName = Path.GetFileName(dllPath);
            PluginLoadContext? loadContext = null;

            try
            {
                loadContext = new PluginLoadContext(dllPath);
                var assembly = loadContext.LoadFromAssemblyPath(dllPath);
                var toolTypes = DiscoverToolTypes(assembly);

                if (toolTypes.Count == 0)
                {
                    loadContext.Unload();
                    return;
                }

                var entry = new PluginEntry
                {
                    FilePath = dllPath,
                    FileName = fileName,
                    LoadContext = loadContext,
                    ToolNames = new List<string>()
                };

                foreach (var type in toolTypes)
                {
                    var tool = InstantiateTool(type);
                    if (tool == null) continue;

                    // 过渡期：Contract.ITool 通过适配器注册到旧 ToolRegistry
                    var adapted = new PluginToolAdapter(tool);
                    if (ToolRegistry.Register(adapted))
                    {
                        entry.ToolNames.Add(tool.Name);
                    }
                    else
                    {
                        FrameworkLogger.Log("PluginLoader",
                            $"工具名冲突，跳过: {tool.Name} (来自 {fileName})");
                    }
                }

                if (entry.ToolNames.Count > 0)
                {
                    loadedPlugins.Add(entry);
                    FrameworkLogger.Log("PluginLoader",
                        $"已加载 {fileName}: {string.Join(", ", entry.ToolNames)}");
                }
                else
                {
                    loadContext.Unload();
                }
            }
            catch (Exception ex)
            {
                FrameworkLogger.Log("PluginLoader", $"加载失败 {fileName}: {ex.Message}");
                loadContext?.Unload();
            }
        }

        private static List<Type> DiscoverToolTypes(Assembly assembly)
        {
            var iToolType = typeof(Contract.ITool);
            return assembly.GetExportedTypes()
                .Where(t => t.IsClass && !t.IsAbstract && iToolType.IsAssignableFrom(t))
                .ToList();
        }

        private Contract.ITool? InstantiateTool(Type type)
        {
            try
            {
                // 优先找 IToolContext 构造函数
                var ctorWithContext = type.GetConstructor(new[] { typeof(IToolContext) });
                if (ctorWithContext != null)
                    return (Contract.ITool)ctorWithContext.Invoke(new object[] { toolContext });

                // 无参构造函数
                var ctorDefault = type.GetConstructor(Type.EmptyTypes);
                if (ctorDefault != null)
                    return (Contract.ITool)ctorDefault.Invoke(null);

                FrameworkLogger.Log("PluginLoader",
                    $"无法实例化 {type.Name}: 缺少兼容的构造函数");
                return null;
            }
            catch (Exception ex)
            {
                FrameworkLogger.Log("PluginLoader",
                    $"实例化失败 {type.Name}: {ex.InnerException?.Message ?? ex.Message}");
                return null;
            }
        }
    }

    internal class PluginEntry
    {
        public string FilePath { get; set; } = "";
        public string FileName { get; set; } = "";
        public AssemblyLoadContext LoadContext { get; set; } = null!;
        public List<string> ToolNames { get; set; } = new();
    }

    /// <summary>
    /// 过渡期适配器：将 Contract.ITool 包装为旧 Tool.ITool，
    /// 使插件工具能注册到现有 ToolRegistry。
    /// 待 ToolRegistry 迁移到 Contract.ITool 后移除。
    /// </summary>
    internal class PluginToolAdapter : ITool
    {
        private readonly Contract.ITool inner;
        private readonly Contract.ToolMetaAttribute? meta;

        public PluginToolAdapter(Contract.ITool tool)
        {
            inner = tool;
            meta = Attribute.GetCustomAttribute(tool.GetType(), typeof(Contract.ToolMetaAttribute))
                as Contract.ToolMetaAttribute;
        }

        public string Name => inner.Name;
        public string Description => inner.Description;
        public IReadOnlyList<ToolParameter> Parameters =>
            inner.Parameters.Select(p => new ToolParameter(p.Name, p.Description, p.Index)).ToList();
        public TimeSpan Timeout => inner.Timeout;

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            return inner.ExecuteAsync(resolvedInputs, ct).ContinueWith(t =>
            {
                var r = t.Result;
                return new ToolResult { Status = r.Status, Data = r.Data, Error = r.Error };
            }, ct);
        }

        public System.Text.Json.Nodes.JsonNode GetInputSchema() => inner.GetInputSchema();
        public bool AllowSubAgent => meta?.AllowSubAgent ?? true;
        public Database.PermissionLevel RequiredPermission =>
            (Database.PermissionLevel)(int)(meta?.Permission ?? Contract.PermissionLevel.Default);
        public bool ContinueLoop => meta?.ContinueLoop ?? false;
        public bool RetainResult => meta?.RetainResult ?? false;
        public string? CapabilitySummary => meta?.CapabilitySummary;
        public string? ToolGroup => meta?.Group;
        public bool DefaultExpanded => meta?.DefaultExpanded ?? true;
    }

    /// <summary>
    /// 插件专用 AssemblyLoadContext，支持卸载。
    /// </summary>
    internal class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver resolver;

        public PluginLoadContext(string pluginPath) : base(isCollectible: true)
        {
            resolver = new AssemblyDependencyResolver(pluginPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // 契约类型从主程序加载，不从插件 DLL 重复加载
            if (assemblyName.Name == "AgentCoreProcessor")
                return null;

            var path = resolver.ResolveAssemblyToPath(assemblyName);
            return path != null ? LoadFromAssemblyPath(path) : null;
        }
    }
}
