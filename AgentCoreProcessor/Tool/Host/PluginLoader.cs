using System;
using System.Collections.Concurrent;
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
using Newtonsoft.Json;
using Parallel = System.Threading.Tasks.Parallel;

namespace AgentCoreProcessor.Tool.Host
{
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

        /// <summary>扫描子目录中的 plugin.json 并加载插件。并行扫描，失败串行重试。</summary>
        public void LoadAll()
        {
            if (!Directory.Exists(PluginDir))
            {
                Directory.CreateDirectory(PluginDir);
                return;
            }

            var dirs = Directory.GetDirectories(PluginDir);
            if (dirs.Length == 0)
            {
                Signal.Debug(LogGroup.Plugin, "插件目录为空，跳过加载", new { dir = PluginDir });
                return;
            }

            var failures = new ConcurrentBag<string>();
            Parallel.ForEach(dirs, dir =>
            {
                if (!LoadPluginFromDir(dir))
                    failures.Add(dir);
            });

            foreach (var dir in failures)
                LoadPluginFromDir(dir);
        }

        /// <summary>从子目录加载插件。读取 plugin.json → 加载 entry DLL。</summary>
        private bool LoadPluginFromDir(string dirPath)
        {
            var manifestPath = Path.Combine(dirPath, "plugin.json");
            if (!File.Exists(manifestPath))
                return false;

            try
            {
                var manifest = JsonConvert.DeserializeObject<PluginManifest>(File.ReadAllText(manifestPath));
                if (manifest?.Entry == null)
                {
                    Signal.Warn(LogGroup.Plugin, "plugin.json 缺少 entry 字段", new { dir = Path.GetFileName(dirPath) });
                    return false;
                }

                var entryPath = Path.Combine(dirPath, manifest.Entry);
                if (!File.Exists(entryPath))
                {
                    Signal.Warn(LogGroup.Plugin, "入口 DLL 不存在", new { dir = Path.GetFileName(dirPath), entry = manifest.Entry });
                    return false;
                }

                LoadPlugin(entryPath, manifest);
                return true;
            }
            catch (Exception ex)
            {
                Signal.Error(LogGroup.Plugin, "插件加载失败", new { dir = Path.GetFileName(dirPath), error = ex.Message });
                return false;
            }
        }

        public void ReloadAll()
        {
            UnloadAll();
            LoadAll();
        }

        /// <summary>按插件 Id 重新加载单个插件。</summary>
        public void ReloadSingle(string pluginName)
        {
            var entries = loadedPlugins
                .Where(p => p.PluginId.Equals(pluginName, StringComparison.OrdinalIgnoreCase)
                         || Path.GetFileNameWithoutExtension(p.FileName).Equals(pluginName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (entries.Count == 0)
                throw new InvalidOperationException($"未找到插件: {pluginName}");

            foreach (var entry in entries)
            {
                foreach (var id in entry.ProviderIds)
                    _providerRegistry?.Unregister(id);
                foreach (var name in entry.ToolNames)
                    ToolRegistry.Unregister(name);
                foreach (var name in entry.ComponentNames)
                    ComponentRegistry.Unregister(name);

                entry.LoadContext.Unload();
                loadedPlugins.Remove(entry);
                _injectProviderTypes.RemoveAll(t => entry.InjectProviderNames.Contains(t.Name));
                _lifecycleTypes.RemoveAll(t => entry.LifecycleNames.Contains(t.Name));
            }

            // 从原目录重新加载（取最后一条的目录路径）
            var srcDir = Path.GetDirectoryName(entries.Last().FilePath)!;
            LoadPluginFromDir(srcDir);
        }

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

        private void LoadPlugin(string dllPath, PluginManifest manifest)
        {
            var fileName = Path.GetFileName(dllPath);
            var dirName = Path.GetFileName(Path.GetDirectoryName(dllPath))!;

            if (!IsManagedAssembly(dllPath))
            {
                Signal.Debug(LogGroup.Plugin, "跳过原生 DLL（非托管程序集）", new { dll = fileName });
                return;
            }

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
                    PluginId = manifest.Id,
                    FilePath = dllPath,
                    FileName = dirName,
                    LoadContext = loadContext,
                    Manifest = manifest,
                    ToolNames = new List<string>(),
                    ComponentNames = new List<string>(),
                    InjectProviderNames = new List<string>(),
                    LifecycleNames = new List<string>()
                };

                if (componentTypes.Count == 0)
                {
                    foreach (var type in toolTypes)
                    {
                        var tool = InstantiateTool(type);
                        if (tool == null) continue;

                        if (ToolRegistry.Register(tool, isNonComponent: true))
                            entry.ToolNames.Add(tool.Name);
                        else
                            Signal.Warn(LogGroup.Plugin, "插件工具注册失败（名称冲突）", new { tool = tool.Name, dll = fileName });
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
                            entry.ProviderIds.Add(provider.Id);
                    }
                    catch (Exception ex)
                    {
                        Signal.Warn(LogGroup.Plugin, "Provider 注册失败", new { type = type.Name, error = ex.Message });
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
                    Signal.Debug(LogGroup.Plugin, "插件已加载", new
                    {
                        id = manifest.Id,
                        dll = fileName,
                        tools = entry.ToolNames.Count,
                        components = entry.ComponentNames.Count,
                        providers = entry.ProviderIds.Count
                    });
                }
                else
                {
                    loadContext.Unload();
                }
            }
            catch (Exception ex)
            {
                Signal.Error(LogGroup.Plugin, "插件加载失败", new { dll = fileName, error = ex.Message });
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
                var ctorWithContext = type.GetConstructor(new[] { typeof(IToolContext) });
                if (ctorWithContext != null)
                    return (AgentLilara.PluginSDK.ITool)ctorWithContext.Invoke(new object[] { toolContext });

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

        private static bool IsManagedAssembly(string dllPath)
        {
            try
            {
                using var fs = new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (fs.Length < 512) return false;

                Span<byte> header = stackalloc byte[512];
                fs.Read(header);

                if (header[0] != 'M' || header[1] != 'Z') return false;

                int peOffset = BitConverter.ToInt32(header.Slice(0x3C, 4));
                if (peOffset < 64 || peOffset > 448) return false;

                if (header[peOffset] != 'P' || header[peOffset + 1] != 'E'
                    || header[peOffset + 2] != 0 || header[peOffset + 3] != 0)
                    return false;

                int optHeader = peOffset + 4 + 20;
                ushort magic = BitConverter.ToUInt16(header.Slice(optHeader, 2));
                if (magic != 0x10B && magic != 0x20B) return false;

                int clrDirOffset = optHeader + (magic == 0x20B ? 224 : 208);
                int clrRva = BitConverter.ToInt32(header.Slice(clrDirOffset, 4));
                return clrRva != 0;
            }
            catch
            {
                return false;
            }
        }
    }

    internal class PluginManifest
    {
        [JsonProperty("id")]
        public string Id { get; set; } = "";

        [JsonProperty("name")]
        public string Name { get; set; } = "";

        [JsonProperty("version")]
        public string Version { get; set; } = "1.0.0";

        [JsonProperty("entry")]
        public string Entry { get; set; } = "";

        [JsonProperty("description")]
        public string Description { get; set; } = "";

        [JsonProperty("components")]
        public List<string> Components { get; set; } = new();

        [JsonProperty("author")]
        public string Author { get; set; } = "";
    }

    internal class PluginEntry
    {
        public string PluginId { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string FileName { get; set; } = "";
        public PluginManifest Manifest { get; set; } = null!;
        public AssemblyLoadContext LoadContext { get; set; } = null!;
        public List<string> ToolNames { get; set; } = new();
        public List<string> ComponentNames { get; set; } = new();
        public List<string> ProviderIds { get; set; } = new();
        public List<string> InjectProviderNames { get; set; } = new();
        public List<string> LifecycleNames { get; set; } = new();
    }

    internal class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver resolver;
        private readonly string _pluginDir;

        public PluginLoadContext(string pluginPath) : base(isCollectible: true)
        {
            resolver = new AssemblyDependencyResolver(pluginPath);
            _pluginDir = Path.GetDirectoryName(pluginPath)!;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name == "AgentCoreProcessor" || assemblyName.Name == "AgentLilara.PluginSDK")
                return null;

            var path = resolver.ResolveAssemblyToPath(assemblyName);
            if (path != null)
                return LoadFromAssemblyPath(path);

            // 回退：在插件目录中直接探测同名 DLL
            var probe = Path.Combine(_pluginDir, $"{assemblyName.Name}.dll");
            if (File.Exists(probe))
                return LoadFromAssemblyPath(probe);

            return null;
        }
    }
}
