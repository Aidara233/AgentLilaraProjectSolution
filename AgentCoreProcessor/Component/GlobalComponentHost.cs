// AgentCoreProcessor/Component/GlobalComponentHost.cs
using System.Reflection;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Tool;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Component;

/// <summary>
/// 管理所有 Global Component 实例。MasterEngine 持有唯一实例。
/// </summary>
internal class GlobalComponentHost
{
    private readonly ComponentEventBus _eventBus;
    private readonly IServiceProvider _services;
    private readonly Action<string> _wakeLoop;
    private readonly ComponentConfig _config;

    private readonly List<GlobalComponentInstance> _components = new();

    public GlobalComponentHost(
        ComponentEventBus eventBus,
        IServiceProvider services,
        Action<string> wakeLoop)
    {
        _eventBus = eventBus;
        _services = services;
        _wakeLoop = wakeLoop;
        _config = ComponentConfig.Load();
    }

    public async Task InitAsync()
    {
        var registrations = ComponentRegistry.GetGlobals();

        foreach (var reg in registrations)
        {
            try
            {
                var instance = CreateInstance(reg);
                if (instance == null) continue;
                _components.Add(instance);
                await instance.Component.OnInitAsync(instance.Context, InitReason.Fresh);
                RegisterTools(instance);
            }
            catch (Exception ex)
            {
                FrameworkLogger.Log("GlobalComponentHost", $"Init failed for {reg.Type.Name}: {ex.Message}");
            }
        }

        FrameworkLogger.Log("GlobalComponentHost", $"Initialized {_components.Count} global components");
    }

    public IEnumerable<ITool> GetVisibleTools(string loopType)
    {
        foreach (var inst in _components)
        {
            if (!ShouldShowTools(inst)) continue;
            foreach (var tool in inst.Component.Tools)
                yield return tool;
        }
    }

    public List<string> BuildPromptSections(LoopInfo caller)
    {
        var sections = new List<(int priority, string content)>();

        foreach (var inst in _components)
        {
            if (!inst.Context.IsEnabled) continue;
            var section = inst.Component.BuildPromptSection(caller);
            if (section != null)
                sections.Add((inst.Component.Meta.PromptPriority, section));
        }

        return sections
            .OrderBy(s => s.priority)
            .Select(s => s.content)
            .ToList();
    }

    public async Task EnableComponentAsync(string name)
    {
        var inst = _components.FirstOrDefault(i => i.Component.Meta.Name == name);
        if (inst == null || inst.Context.IsEnabled) return;
        inst.Context.SetEnabledDirect(true);
        RegisterTools(inst);
        try { await inst.Component.OnEnabledAsync(); }
        catch (Exception ex)
        {
            FrameworkLogger.Log("GlobalComponentHost", $"OnEnabled error in {name}: {ex.Message}");
        }
    }

    public async Task DisableComponentAsync(string name)
    {
        var inst = _components.FirstOrDefault(i => i.Component.Meta.Name == name);
        if (inst == null || !inst.Context.IsEnabled) return;
        inst.Context.SetEnabledDirect(false);
        UnregisterTools(inst);
        try { await inst.Component.OnDisabledAsync(); }
        catch (Exception ex)
        {
            FrameworkLogger.Log("GlobalComponentHost", $"OnDisabled error in {name}: {ex.Message}");
        }
    }
    public async Task ShutdownAsync(ShutdownReason reason)
    {
        var timeout = _config.ShutdownTimeoutMs;
        using var cts = new CancellationTokenSource(timeout);

        var tasks = _components.Select(async inst =>
        {
            try { return await inst.Component.OnShutdownRequestedAsync(reason); }
            catch { return ShutdownResponse.Ok; }
        });

        try { await Task.WhenAll(tasks).WaitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            FrameworkLogger.Log("GlobalComponentHost", "Shutdown timeout, forcing phase 2");
        }

        foreach (var inst in _components)
        {
            UnregisterTools(inst);
            try { await inst.Component.OnShutdownAsync(reason).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception ex)
            {
                FrameworkLogger.Log("GlobalComponentHost", $"OnShutdown error in {inst.Component.Meta.Name}: {ex.Message}");
            }
        }

        _components.Clear();
    }

    public IReadOnlyList<GlobalComponentInstance> Instances => _components;

    private void RegisterTools(GlobalComponentInstance inst)
    {
        if (!ShouldShowTools(inst)) return;
        foreach (var tool in inst.Component.Tools)
            ToolRegistry.Register(tool);
    }

    private void UnregisterTools(GlobalComponentInstance inst)
    {
        foreach (var tool in inst.Component.Tools)
            ToolRegistry.Unregister(tool.Name);
    }

    private GlobalComponentInstance? CreateInstance(ComponentRegistration reg)
    {
        var component = (IGlobalComponent?)Activator.CreateInstance(reg.Type);
        if (component == null) return null;

        var defaultEnabled = _config.IsEnabled(component.Meta.Name, component.Meta.DefaultEnabled);
        var storage = new ComponentStorage(component.Meta.Name, "_global");

        var context = new GlobalComponentContext(
            _eventBus, _services, storage, _wakeLoop,
            enabled =>
            {
                if (enabled) _ = EnableComponentAsync(component.Meta.Name);
                else _ = DisableComponentAsync(component.Meta.Name);
            },
            defaultEnabled);

        return new GlobalComponentInstance(component, context, reg);
    }

    private bool ShouldShowTools(GlobalComponentInstance inst)
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
}

internal record GlobalComponentInstance(
    IGlobalComponent Component,
    GlobalComponentContext Context,
    ComponentRegistration Registration);
