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

    private readonly List<LoopComponentInstance> _loopComponents = new();
    private readonly ComponentConfig _config;

    public ComponentHost(
        string loopId,
        string loopType,
        ModuleBus moduleBus,
        IServiceProvider services,
        Action wakeLoop)
    {
        _loopId = loopId;
        _loopType = loopType;
        _moduleBus = moduleBus;
        _services = services;
        _wakeLoop = wakeLoop;
        _config = ComponentConfig.Load();
    }

    public async Task InitAsync()
    {
        var registrations = ComponentRegistry.GetLoopComponents(_loopType);

        foreach (var reg in registrations)
        {
            try
            {
                var instance = CreateInstance(reg);
                if (instance == null) continue;
                _loopComponents.Add(instance);
                await instance.Component.OnInitAsync(instance.Context, InitReason.Fresh);
                RegisterTools(instance);
            }
            catch (Exception)
            {
            }
        }
    }

    public IEnumerable<ITool> GetVisibleTools()
    {
        foreach (var inst in _loopComponents)
        {
            if (!ShouldShowTools(inst)) continue;
            foreach (var tool in inst.Component.Tools)
                yield return tool;
        }
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
            catch { return ShutdownResponse.Ok; }
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

    private void RegisterTools(LoopComponentInstance inst)
    {
        if (!ShouldShowTools(inst)) return;
        foreach (var tool in inst.Component.Tools)
            ToolRegistry.Register(tool);
    }

    private void UnregisterTools(LoopComponentInstance inst)
    {
        foreach (var tool in inst.Component.Tools)
            ToolRegistry.Unregister(tool.Name);
    }

    private LoopComponentInstance? CreateInstance(ComponentRegistration reg)
    {
        // Try constructor injection via PluginLoader if available, fall back to Activator
        var pluginLoader = _services.GetService(typeof(Tool.Host.PluginLoader)) as Tool.Host.PluginLoader;
        var instance = pluginLoader?.InstantiateWithInjection(reg.Type, _services)
                       ?? Activator.CreateInstance(reg.Type);

        if (instance is not ILoopComponent component) return null;

        var defaultEnabled = _config.IsEnabled(component.Meta.Name, component.Meta.DefaultEnabled);
        var storage = new ComponentStorage(component.Meta.Name, _loopId);

        var context = new LoopComponentContext(
            _loopId, _loopType, _moduleBus, _services, storage,
            _wakeLoop,
            (loopId, enabled) =>
            {
                if (enabled) _ = EnableComponentAsync(component.Meta.Name);
                else _ = DisableComponentAsync(component.Meta.Name);
            },
            defaultEnabled);

        return new LoopComponentInstance(component, context, reg);
    }

    private bool ShouldShowTools(LoopComponentInstance inst)
    {
        var visibility = _config.GetVisibility(
            inst.Component.Meta.Name,
            inst.Registration.Type.GetCustomAttribute<ToolVisibilityAttribute>()?.Default ?? Visibility.FollowState);

        return visibility switch
        {
            Visibility.AlwaysVisible => true,
            Visibility.AlwaysHidden => false,
            Visibility.FollowState => inst.Context.IsEnabled,
            _ => inst.Context.IsEnabled
        };
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
        InstanceDirectory = Path.Combine(PathConfig.StoragePath, "PluginData", componentName, loopId);
        Directory.CreateDirectory(GlobalDirectory);
        Directory.CreateDirectory(InstanceDirectory);
    }

    public string GlobalDirectory { get; }
    public string InstanceDirectory { get; }
}
