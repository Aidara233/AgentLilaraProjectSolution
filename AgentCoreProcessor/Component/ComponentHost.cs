// AgentCoreProcessor/Component/ComponentHost.cs
using System.Reflection;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Logging;
using AgentCoreProcessor.Tool;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Component;

/// <summary>
/// 管理一个循环内所有 Component 实例的生命周期、工具收集、Prompt 收集。
/// 每个 ChannelEngine/SystemEngine 持有一个 ComponentHost。
/// </summary>
internal class ComponentHost
{
    private readonly string _loopId;
    private readonly string _loopType;
    private readonly ModuleBus _moduleBus;
    private readonly IServiceProvider _services;
    private readonly Action _wakeLoop;
    private readonly IServiceProvider _effectiveServices;

    private readonly List<LoopComponentInstance> _loopComponents = new();
    private readonly Dictionary<string, ITool> _localTools = new();
    private readonly ComponentConfig _config;

    /// <summary>由引擎注入，用于工具解析时回退到全局组件。</summary>
    public GlobalComponentHost? GlobalHost { get; set; }

    public ComponentHost(
        string loopId,
        string loopType,
        ModuleBus moduleBus,
        IServiceProvider services,
        Action wakeLoop,
        Dictionary<Type, object>? perLoopServices = null)
    {
        _loopId = loopId;
        _loopType = loopType;
        _moduleBus = moduleBus;
        _services = services;
        _wakeLoop = wakeLoop;
        _config = ComponentConfig.Load();

        if (perLoopServices != null && perLoopServices.Count > 0)
        {
            var merged = new Dictionary<Type, object>();
            foreach (var kv in perLoopServices)
                merged[kv.Key] = kv.Value;
            // per-loop services win over global
            if (services is SimpleServiceProvider ssp)
            {
                foreach (var kv in ssp.GetAllServices())
                {
                    if (!merged.ContainsKey(kv.Key))
                        merged[kv.Key] = kv.Value;
                }
            }
            _effectiveServices = new SimpleServiceProvider(merged);
        }
        else
        {
            _effectiveServices = services;
        }
    }

    public async Task InitAsync()
    {
        var registrations = ComponentRegistry.GetLoopComponents(_loopType);

        foreach (var reg in registrations)
        {
            try
            {
                var compName = ComponentAttribute.GetFrom(reg.Type)?.Name ?? reg.Type.Name;
                if (!_config.IsEnabled(compName, _loopType, true))
                    continue;
                var instance = CreateInstance(reg);
                if (instance == null) continue;
                _loopComponents.Add(instance);
                await instance.Component.OnInitAsync(instance.Context, InitReason.Fresh);
                RegisterTools(instance);
            }
            catch (Exception ex)
            {
                Signal.Error(LogGroup.Engine, $"Loop组件初始化失败: {reg.Type.Name}",
                    new { type = reg.Type.FullName, error = ex.Message });
            }
        }
    }

    public IEnumerable<ITool> GetVisibleTools()
    {
        foreach (var inst in _loopComponents)
        {
            if (!inst.Context.IsEnabled) continue;
            foreach (var tool in inst.Component.Tools)
                yield return tool;
        }
    }

    /// <summary>获取全局组件工具（按引擎类型过滤） + 本循环 Loop 组件工具。</summary>
    public IEnumerable<ITool> GetAllVisibleTools()
    {
        if (GlobalHost != null)
        {
            foreach (var inst in GlobalHost.Instances)
            {
                if (!_config.IsEnabled(inst.Component.Meta.Name, _loopType, inst.Component.Meta.DefaultEnabled))
                    continue;
                foreach (var tool in inst.Component.Tools)
                    yield return tool;
            }
        }
        foreach (var tool in GetVisibleTools())
            yield return tool;
    }

    /// <summary>获取当前引擎类型下所有可见工具的名称集合（供 ToolExecutor 白名单）。</summary>
    public HashSet<string> GetAllVisibleToolNames()
    {
        var names = new HashSet<string>();
        foreach (var tool in GetAllVisibleTools())
        {
            if (!ToolRegistry.IsDisabled(tool.Name))
                names.Add(tool.Name);
        }
        return names;
    }

    public List<string> BuildPromptSections()
    {
        var sections = new List<(int priority, string content)>();

        foreach (var inst in _loopComponents)
        {
            if (!inst.Context.IsEnabled) continue;
            var section = inst.Component.BuildPromptSection();
            if (section != null)
                sections.Add((inst.Component.Meta.PromptPriority, section));
        }

        return sections
            .OrderBy(s => s.priority)
            .Select(s => s.content)
            .ToList();
    }

    public async Task OnActivatedAsync()
    {
        foreach (var inst in _loopComponents.Where(i => i.Context.IsEnabled))
        {
            try { await inst.Component.OnActivatedAsync(); }
            catch (Exception ex) { LogError(inst, "OnActivated", ex); }
        }
    }

    public async Task OnPauseAsync()
    {
        foreach (var inst in _loopComponents.Where(i => i.Context.IsEnabled))
        {
            try { await inst.Component.OnPauseAsync(); }
            catch (Exception ex) { LogError(inst, "OnPause", ex); }
        }
    }
    public async Task OnBeforeInvokeAsync()
    {
        foreach (var inst in _loopComponents.Where(i => i.Context.IsEnabled))
        {
            try { await inst.Component.OnBeforeInvokeAsync(); }
            catch (Exception ex) { LogError(inst, "OnBeforeInvoke", ex); }
        }
    }

    public async Task OnAfterInvokeAsync()
    {
        foreach (var inst in _loopComponents.Where(i => i.Context.IsEnabled))
        {
            try { await inst.Component.OnAfterInvokeAsync(); }
            catch (Exception ex) { LogError(inst, "OnAfterInvoke", ex); }
        }
    }

    public async Task EnableComponentAsync(string name)
    {
        var inst = _loopComponents.FirstOrDefault(i => i.Component.Meta.Name == name);
        if (inst == null || inst.Context.IsEnabled) return;
        inst.Context.SetEnabledDirect(true);
        RegisterTools(inst);
        try { await inst.Component.OnEnabledAsync(); }
        catch (Exception ex) { LogError(inst, "OnEnabled", ex); }
    }

    public async Task DisableComponentAsync(string name)
    {
        var inst = _loopComponents.FirstOrDefault(i => i.Component.Meta.Name == name);
        if (inst == null || !inst.Context.IsEnabled) return;
        inst.Context.SetEnabledDirect(false);
        UnregisterTools(inst);
        try { await inst.Component.OnDisabledAsync(); }
        catch (Exception ex) { LogError(inst, "OnDisabled", ex); }
    }

    public async Task ShutdownAsync(ShutdownReason reason)
    {
        var timeout = _config.ShutdownTimeoutMs;

        // Phase 1: 等待就绪
        using var cts = new CancellationTokenSource(timeout);
        var tasks = _loopComponents.Select(async inst =>
        {
            try { return await inst.Component.OnShutdownRequestedAsync(reason); }
            catch (Exception ex) { Signal.Warn(LogGroup.Plugin, "组件关闭回调异常", new { component = inst.Component.Meta.Name, error = ex.Message }); return ShutdownResponse.Ok; }
        });
        try { await Task.WhenAll(tasks).WaitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
        }

        // Phase 2: 最终关闭
        foreach (var inst in _loopComponents)
        {
            UnregisterTools(inst);
            try { await inst.Component.OnShutdownAsync(reason).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception ex) { LogError(inst, "OnShutdown", ex); }
        }

        _loopComponents.Clear();
    }

    public IReadOnlyList<LoopComponentInstance> Instances => _loopComponents;

    /// <summary>
    /// 解析工具：本地 Loop 工具 → 全局组件工具 → ToolRegistry。
    /// 供 ToolExecutor 用作自定义 resolver。
    /// </summary>
    public ITool? TryGetTool(string name)
    {
        if (_localTools.TryGetValue(name, out var localTool))
            return localTool;
        var globalTool = GlobalHost?.TryGetTool(name);
        if (globalTool != null) return globalTool;
        return ToolRegistry.Get(name);
    }

    /// <summary>获取当前循环的工具定义列表（用于 API tool_use）。</summary>
    public List<ToolDefinition> GetToolDefinitions()
    {
        var defs = new List<ToolDefinition>();
        foreach (var tool in _localTools.Values)
        {
            if (ToolRegistry.IsDisabled(tool.Name)) continue;
            defs.Add(new ToolDefinition
            {
                Name = tool.Name,
                Description = tool.Description,
                Parameters = tool.GetInputSchema()
            });
        }
        return defs;
    }

    private void UnregisterTools(LoopComponentInstance inst)
    {
        foreach (var tool in inst.Component.Tools)
            _localTools.Remove(tool.Name);
    }

    private LoopComponentInstance? CreateInstance(ComponentRegistration reg)
    {
        // Try constructor injection via PluginLoader if available, fall back to Activator
        var pluginLoader = _effectiveServices.GetService(typeof(Tool.Host.PluginLoader)) as Tool.Host.PluginLoader;
        var instance = pluginLoader?.InstantiateWithInjection(reg.Type, _effectiveServices)
                       ?? Activator.CreateInstance(reg.Type);

        if (instance is not ILoopComponent component) return null;

        var defaultEnabled = _config.IsEnabled(component.Meta.Name, _loopType, component.Meta.DefaultEnabled);
        var storage = new ComponentStorage(component.Meta.Name, _loopId);

        var context = new LoopComponentContext(
            _loopId, _loopType, _moduleBus, _effectiveServices, storage,
            _wakeLoop,
            (loopId, enabled) =>
            {
                if (enabled) _ = EnableComponentAsync(component.Meta.Name);
                else _ = DisableComponentAsync(component.Meta.Name);
            },
            defaultEnabled);

        return new LoopComponentInstance(component, context, reg);
    }

    private void RegisterTools(LoopComponentInstance inst)
    {
        if (!inst.Context.IsEnabled) return;
        foreach (var tool in inst.Component.Tools)
            _localTools[tool.Name] = tool;
    }

    private static void LogError(LoopComponentInstance inst, string hook, Exception ex)
    {
        Signal.Error(LogGroup.Engine, $"组件{inst.Component.Meta.Name}.{hook}异常",
            new { component = inst.Component.Meta.Name, hook, error = ex.Message, type = ex.GetType().Name });
    }
}

internal record LoopComponentInstance(
    ILoopComponent Component,
    LoopComponentContext Context,
    ComponentRegistration Registration);

internal class ComponentStorage : IPluginStorage
{
    public ComponentStorage(string componentName, string loopId)
    {
        GlobalDirectory = Path.Combine(PathConfig.StoragePath, "PluginData", componentName);
        InstanceDirectory = Path.Combine(PathConfig.StoragePath, "PluginData", componentName,
            SanitizeLoopId(loopId));
        Directory.CreateDirectory(GlobalDirectory);
        Directory.CreateDirectory(InstanceDirectory);
    }

    private static string SanitizeLoopId(string loopId) =>
        loopId.Replace(':', '_');

    public string GlobalDirectory { get; }
    public string InstanceDirectory { get; }
    public string WorkspaceDirectory => PathConfig.WorkspacePath;
}
