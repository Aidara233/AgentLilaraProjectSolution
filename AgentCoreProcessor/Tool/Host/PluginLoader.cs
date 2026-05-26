using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Logging;
using AgentCoreProcessor.Component;
using AgentCoreProcessor.Engine;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

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
        private readonly List<Type> _injectProviderTypes = new();
        private readonly List<Type> _lifecycleTypes = new();

        public int LoadedToolCount => loadedPlugins.Sum(p => p.ToolNames.Count);
        public IReadOnlyList<PluginEntry> LoadedPlugins => loadedPlugins;
        public IReadOnlyList<Type> InjectProviderTypes => _injectProviderTypes.AsReadOnly();
        public IReadOnlyList<Type> LifecycleTypes => _lifecycleTypes.AsReadOnly();

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
                return;
            }

            var dlls = Directory.GetFiles(PluginDir, "*.dll", SearchOption.AllDirectories);
            if (dlls.Length == 0)
            {
                Signal.Debug(LogGroup.Plugin, "插件目录为空，跳过加载", new { dir = PluginDir });
                return;
            }

            foreach (var dllPath in dlls)
            {
                LoadPlugin(dllPath);
            }

        }

        /// <summary>重载所有插件。运行时调用。</summary>
        public void ReloadAll()
        {
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
            _injectProviderTypes.Clear();
            _lifecycleTypes.Clear();
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
                var injectProviderTypes = DiscoverInjectProviderTypes(assembly);
                var lifecycleTypes = DiscoverLifecycleTypes(assembly);

                if (toolTypes.Count == 0 && componentTypes.Count == 0 && earlyProviderTypes.Count == 0
                    && injectProviderTypes.Count == 0 && lifecycleTypes.Count == 0)
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
                    ComponentNames = new List<string>(),
                    InjectProviderNames = new List<string>(),
                    LifecycleNames = new List<string>()
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
                            Signal.Warn(LogGroup.Plugin, "插件工具注册失败（名称冲突）", new { tool = tool.Name, dll = Path.GetFileName(dllPath) });
                        }
                    }
                }

                foreach (var type in componentTypes)
                {
                    if (ComponentRegistry.Register(type))
                    {
                        var attr = type.GetCustomAttribute<ComponentAttribute>()!;
                        entry.ComponentNames.Add(attr.Name);
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
                        Console.Error.WriteLine($"[PluginLoader] Provider 注册失败: {ex.Message}");
                    }
                }

                entry.InjectProviderNames.AddRange(injectProviderTypes.Select(t => t.Name));
                entry.LifecycleNames.AddRange(lifecycleTypes.Select(t => t.Name));
                _injectProviderTypes.AddRange(injectProviderTypes);
                _lifecycleTypes.AddRange(lifecycleTypes);

                if (entry.ToolNames.Count > 0 || entry.ComponentNames.Count > 0 || entry.ProviderIds.Count > 0
                    || entry.InjectProviderNames.Count > 0 || entry.LifecycleNames.Count > 0)
                {
                    loadedPlugins.Add(entry);
                }
                else
                {
                    loadContext.Unload();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[PluginLoader] 插件加载失败 {dllPath}: {ex.Message}");
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

        private static List<Type> DiscoverInjectProviderTypes(Assembly assembly)
        {
            var iType = typeof(Engine.IInjectProvider);
            return assembly.GetExportedTypes()
                .Where(t => t.IsClass && !t.IsAbstract && iType.IsAssignableFrom(t))
                .ToList();
        }

        private static List<Type> DiscoverLifecycleTypes(Assembly assembly)
        {
            var iType = typeof(Engine.IEngineLifecycle);
            return assembly.GetExportedTypes()
                .Where(t => t.IsClass && !t.IsAbstract && iType.IsAssignableFrom(t))
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

                return null;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[PluginLoader] 工具实例化失败 {type.Name}: {ex.Message}");
                return null;
            }
        }

        private static readonly Dictionary<Type, Func<IServiceProvider, object?>> s_injectableResolvers = new()
        {
            [typeof(Engine.EventBus)] = sp => sp.GetService(typeof(Engine.EventBus)),
            [typeof(Engine.ModuleBus)] = sp => sp.GetService(typeof(Engine.ModuleBus)),
            [typeof(Engine.Gate)] = sp => sp.GetService(typeof(Engine.Gate)),
            [typeof(AgentLilara.PluginSDK.Services.IMemoryAccess)] = sp => sp.GetService(typeof(AgentLilara.PluginSDK.Services.IMemoryAccess)),
        };

        public object? InstantiateWithInjection(Type type, IServiceProvider services)
        {
            var ctors = type.GetConstructors()
                .OrderByDescending(c => c.GetParameters().Length);

            foreach (var ctor in ctors)
            {
                var parms = ctor.GetParameters();
                var args = new object?[parms.Length];
                bool ok = true;
                for (int i = 0; i < parms.Length; i++)
                {
                    var pType = parms[i].ParameterType;
                    if (s_injectableResolvers.TryGetValue(pType, out var resolver))
                        args[i] = resolver(services);
                    else if (pType == typeof(IServiceProvider))
                        args[i] = services;
                    else
                    {
                        args[i] = services.GetService(pType);
                        if (args[i] == null && !parms[i].IsOptional)
                        {
                            ok = false;
                            break;
                        }
                    }
                }
                if (!ok) continue;

                try { return Activator.CreateInstance(type, args); }
                catch { Signal.Warn(LogGroup.Plugin, "插件类型构造失败", new { type = type.FullName }); continue; }
            }
            return null;
        }

        public Engine.IInjectProvider? InstantiateInjectProvider(Type type, IServiceProvider engineServices)
            => InstantiateWithInjection(type, engineServices) as Engine.IInjectProvider;

        public Engine.IEngineLifecycle? InstantiateLifecycle(Type type, IServiceProvider engineServices)
            => InstantiateWithInjection(type, engineServices) as Engine.IEngineLifecycle;
    }

    internal class PluginEntry
    {
        public string FilePath { get; set; } = "";
        public string FileName { get; set; } = "";
        public AssemblyLoadContext LoadContext { get; set; } = null!;
        public List<string> ToolNames { get; set; } = new();
        public List<string> ComponentNames { get; set; } = new();
        public List<string> ProviderIds { get; set; } = new();
        public List<string> InjectProviderNames { get; set; } = new();
        public List<string> LifecycleNames { get; set; } = new();
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
