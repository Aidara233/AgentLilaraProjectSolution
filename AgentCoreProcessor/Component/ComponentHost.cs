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
/// 每个 ExpressEngine/SessionEngine 持有一个 ComponentHost。
/// </summary>
internal class ComponentHost
{
    private readonly string _sessionId;
    private readonly string _loopType;
    private readonly ModuleBus _moduleBus;
    private readonly IServiceProvider _services;
    private readonly Action _wakeLoop;
    private readonly IServiceProvider _effectiveServices;

    private readonly List<LoopComponentInstance> _loopComponents = new();
    private readonly Dictionary<string, ITool> _localTools = new();

    /// <summary>由引擎注入，用于工具解析时回退到全局组件。</summary>
    public GlobalComponentHost? GlobalHost { get; set; }

    public ComponentHost(
        string sessionId,
        string loopType,
        ModuleBus moduleBus,
        IServiceProvider services,
        Action wakeLoop,
        Dictionary<Type, object>? perLoopServices = null)
    {
        _sessionId = sessionId;
        _loopType = loopType;
        _moduleBus = moduleBus;
        _services = services;
        _wakeLoop = wakeLoop;

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

    public async Task InitAsync(IReadOnlyList<string>? finalComponentIds = null)
    {
        var loopTimeout = TimeSpan.FromSeconds(ComponentTimeouts.LoopInitTimeoutSeconds);

        if (finalComponentIds != null)
        {
            // 仅加载指定列表内的组件（归属由会话类型文件 Components 决定）
            var nameSet = new HashSet<string>(finalComponentIds, StringComparer.OrdinalIgnoreCase);
            foreach (var reg in ComponentRegistry.GetLoopComponents(_loopType))
            {
                var compName = ComponentAttribute.GetFrom(reg.Type)?.Name ?? reg.Type.Name;
                if (!nameSet.Contains(compName)) continue;
                try
                {
                    var instance = CreateInstance(reg);
                    if (instance == null) continue;
                    _loopComponents.Add(instance);
                    // 超时兜底：单个组件挂起不阻塞 session 创建（express 会话在 eventLock 下创建，阻塞=卡消息路由）
                    await instance.Component.OnInitAsync(instance.Context, InitReason.Fresh)
                        .WaitAsync(loopTimeout);
                    RegisterTools(instance);
                }
                catch (TimeoutException)
                {
                    Signal.Warn(LogGroup.Engine, $"Loop组件初始化超时，跳过: {reg.Type.Name}",
                        new { component = reg.Type.Name, timeout = loopTimeout.TotalSeconds });
                }
                catch (Exception ex)
                {
                    Signal.Error(LogGroup.Engine, $"Loop组件初始化失败: {reg.Type.Name}",
                        new { type = reg.Type.FullName, error = ex.Message });
                }
            }
            return;
        }

        var registrations = ComponentRegistry.GetLoopComponents(_loopType);

        foreach (var reg in registrations)
        {
            try
            {
                var compName = ComponentAttribute.GetFrom(reg.Type)?.Name ?? reg.Type.Name;
                var instance = CreateInstance(reg);
                if (instance == null) continue;
                _loopComponents.Add(instance);
                // 超时兜底：单个组件挂起不阻塞 session 创建（express 会话在 eventLock 下创建，阻塞=卡消息路由）
                await instance.Component.OnInitAsync(instance.Context, InitReason.Fresh)
                    .WaitAsync(loopTimeout);
                RegisterTools(instance);
            }
            catch (TimeoutException)
            {
                Signal.Warn(LogGroup.Engine, $"Loop组件初始化超时，跳过: {reg.Type.Name}",
                    new { component = reg.Type.Name, timeout = loopTimeout.TotalSeconds });
            }
            catch (Exception ex)
            {
                Signal.Error(LogGroup.Engine, $"Loop组件初始化失败: {reg.Type.Name}",
                    new { type = reg.Type.FullName, error = ex.Message });
            }
        }
    }

    /// <summary>当前循环内已加载 Loop 组件的可见工具。</summary>
    public IEnumerable<ITool> GetVisibleTools()
    {
        foreach (var inst in _loopComponents)
        {
            if (!inst.Context.IsEnabled) continue;
            foreach (var tool in inst.Component.Tools)
                yield return tool;
        }
    }

    /// <summary>获取全局组件工具（按权限组过滤） + 本循环 Loop 组件工具。</summary>
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

    /// <summary>
    /// 轮询所有启用的 Loop 组件，任一 OnCanExitAsync 返回 false 即阻止 end_session。
    /// </summary>
    public async Task<bool> QueryCanExitAsync()
    {
        foreach (var inst in _loopComponents.Where(i => i.Context.IsEnabled))
        {
            try { if (!await inst.Component.OnCanExitAsync()) return false; }
            catch (Exception ex) { LogError(inst, "OnCanExit", ex); }
        }
        return true;
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
        var timeout = ComponentTimeouts.ShutdownTimeoutMs;

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

        var defaultEnabled = component.Meta.DefaultEnabled;
        var storage = new ComponentStorage(component.Meta.Name, _sessionId);

        var context = new LoopComponentContext(
            _sessionId, _loopType, _moduleBus, _effectiveServices, storage,
            _wakeLoop,
            (sessionId, enabled) =>
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
    public ComponentStorage(string componentName, string sessionId)
    {
        GlobalDirectory = Path.Combine(PathConfig.StoragePath, "PluginData", componentName);
        // 实例目录按裸 sessionId 隔离（LoopId 前缀概念已移除；sessionId 为 GUID 短码，无需 sanitize）
        InstanceDirectory = Path.Combine(PathConfig.StoragePath, "PluginData", componentName, sessionId);
        Directory.CreateDirectory(GlobalDirectory);
        Directory.CreateDirectory(InstanceDirectory);
    }

    public string GlobalDirectory { get; }
    public string InstanceDirectory { get; }
    public string WorkspaceDirectory => PathConfig.WorkspacePath;
}
