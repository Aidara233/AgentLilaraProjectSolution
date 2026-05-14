using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Component;
using AgentCoreProcessor.Engine;
using AgentLilara.PluginSDK;

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
        private readonly WebUI.Shell.ProviderRegistry? _providerRegistry;
        private readonly List<PluginEntry> loadedPlugins = new();

        public int LoadedToolCount => loadedPlugins.Sum(p => p.ToolNames.Count);
        public IReadOnlyList<PluginEntry> LoadedPlugins => loadedPlugins;

        public PluginLoader(IToolContext toolContext, WebUI.Shell.ProviderRegistry? providerRegistry = null)
        {
            this.toolContext = toolContext;
            _providerRegistry = providerRegistry;
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
                foreach (var id in entry.ProviderIds)
                    _providerRegistry?.Unregister(id);
                foreach (var name in entry.ToolNames)
                    ToolRegistry.Unregister(name);
                foreach (var name in entry.ComponentNames)
                    ComponentRegistry.Unregister(name);

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
                var componentTypes = DiscoverComponentTypes(assembly);
                var earlyProviderTypes = DiscoverProviderTypes(assembly);

                if (toolTypes.Count == 0 && componentTypes.Count == 0 && earlyProviderTypes.Count == 0)
                {
                    loadContext.Unload();
                    return;
                }

                var entry = new PluginEntry
                {
                    FilePath = dllPath,
                    FileName = fileName,
                    LoadContext = loadContext,
                    ToolNames = new List<string>(),
                    ComponentNames = new List<string>()
                };

                // 如果 DLL 有 Component，工具由 Component 管理，PluginLoader 不独立注册
                if (componentTypes.Count == 0)
                {
                    foreach (var type in toolTypes)
                    {
                        var tool = InstantiateTool(type);
                        if (tool == null) continue;

                        if (ToolRegistry.Register(tool))
                        {
                            entry.ToolNames.Add(tool.Name);
                        }
                        else
                        {
                            FrameworkLogger.Log("PluginLoader",
                                $"工具名冲突，跳过: {tool.Name} (来自 {fileName})");
                        }
                    }
                }

                foreach (var type in componentTypes)
                {
                    if (ComponentRegistry.Register(type))
                    {
                        var attr = type.GetCustomAttribute<ComponentAttribute>()!;
                        entry.ComponentNames.Add(attr.Name);
                        FrameworkLogger.Log("PluginLoader",
                            $"已注册组件: {attr.Name} ({attr.Scope}) 来自 {fileName}");
                    }
                }

                var providerTypes = earlyProviderTypes;
                foreach (var type in providerTypes)
                {
                    try
                    {
                        var provider = (AgentLilara.PluginSDK.WebUI.IWebUIProvider)Activator.CreateInstance(type)!;
                        var attr = type.GetCustomAttribute<AgentLilara.PluginSDK.WebUI.WebUIProviderAttribute>();
                        if (_providerRegistry?.Register(provider, attr?.BuiltIn ?? false) == true)
                        {
                            entry.ProviderIds.Add(provider.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        FrameworkLogger.Log("PluginLoader", $"Provider 实例化失败 {type.Name}: {ex.Message}");
                    }
                }

                if (entry.ToolNames.Count > 0 || entry.ComponentNames.Count > 0 || entry.ProviderIds.Count > 0)
                {
                    loadedPlugins.Add(entry);
                    FrameworkLogger.Log("PluginLoader",
                        $"已加载 {fileName}: {string.Join(", ", entry.ToolNames.Concat(entry.ComponentNames))}");
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
            var iToolType = typeof(AgentLilara.PluginSDK.ITool);
            return assembly.GetExportedTypes()
                .Where(t => t.IsClass && !t.IsAbstract && iToolType.IsAssignableFrom(t))
                .ToList();
        }

        private static List<Type> DiscoverComponentTypes(Assembly assembly)
        {
            return assembly.GetExportedTypes()
                .Where(t => t.IsClass && !t.IsAbstract
                    && t.GetCustomAttribute<ComponentAttribute>() != null)
                .ToList();
        }

        private static List<Type> DiscoverProviderTypes(Assembly assembly)
        {
            var iProviderType = typeof(AgentLilara.PluginSDK.WebUI.IWebUIProvider);
            return assembly.GetExportedTypes()
                .Where(t => t.IsClass && !t.IsAbstract && iProviderType.IsAssignableFrom(t))
                .ToList();
        }

        private AgentLilara.PluginSDK.ITool? InstantiateTool(Type type)
        {
            try
            {
                // 优先找 IToolContext 构造函数
                var ctorWithContext = type.GetConstructor(new[] { typeof(IToolContext) });
                if (ctorWithContext != null)
                    return (AgentLilara.PluginSDK.ITool)ctorWithContext.Invoke(new object[] { toolContext });

                // 无参构造函数
                var ctorDefault = type.GetConstructor(Type.EmptyTypes);
                if (ctorDefault != null)
                    return (AgentLilara.PluginSDK.ITool)ctorDefault.Invoke(null);

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
        public List<string> ComponentNames { get; set; } = new();
        public List<string> ProviderIds { get; set; } = new();
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
            if (assemblyName.Name == "AgentCoreProcessor" || assemblyName.Name == "AgentLilara.PluginSDK")
                return null;

            var path = resolver.ResolveAssemblyToPath(assemblyName);
            return path != null ? LoadFromAssemblyPath(path) : null;
        }
    }
}
