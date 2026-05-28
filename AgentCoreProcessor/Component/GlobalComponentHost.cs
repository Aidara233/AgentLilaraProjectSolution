// AgentCoreProcessor/Component/GlobalComponentHost.cs
using System.Reflection;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Logging;
using AgentCoreProcessor.Tool;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Component;

/// <summary>
/// 管理所有 Global Component 实例。MasterEngine 持有唯一实例。
/// </summary>
internal class GlobalComponentHost
{
    private readonly ModuleBus _moduleBus;
    private readonly IServiceProvider _services;
    private readonly Action<string> _wakeLoop;
    private readonly ComponentConfig _config;

    private readonly List<GlobalComponentInstance> _components = new();
    private readonly Dictionary<string, ITool> _tools = new();

    public GlobalComponentHost(
        ModuleBus moduleBus,
        IServiceProvider services,
        Action<string> wakeLoop)
    {
        _moduleBus = moduleBus;
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
                Signal.Error(LogGroup.Engine, $"Global组件初始化失败: {reg.Type.Name}",
                    new { type = reg.Type.FullName, error = ex.Message });
            }
        }

    }

    public IEnumerable<ITool> GetVisibleTools(string loopType)
    {
        foreach (var inst in _components)
        {
            if (!inst.Context.IsEnabled) continue;
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
            Signal.Warn(LogGroup.Plugin, "组件启用回调异常", new { component = name, error = ex.Message });
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
            Signal.Warn(LogGroup.Plugin, "组件禁用回调异常", new { component = name, error = ex.Message });
        }
    }
    public async Task ShutdownAsync(ShutdownReason reason)
    {
        var timeout = _config.ShutdownTimeoutMs;
        using var cts = new CancellationTokenSource(timeout);

        var tasks = _components.Select(async inst =>
        {
            try { return await inst.Component.OnShutdownRequestedAsync(reason); }
            catch (Exception ex) { Signal.Warn(LogGroup.Plugin, "组件关闭回调异常", new { component = inst.Component.Meta.Name, error = ex.Message }); return ShutdownResponse.Ok; }
        });

        try { await Task.WhenAll(tasks).WaitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
        }

        foreach (var inst in _components)
        {
            UnregisterTools(inst);
            try { await inst.Component.OnShutdownAsync(reason).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception)
            {
            }
        }

        _components.Clear();
    }

    public IReadOnlyList<GlobalComponentInstance> Instances => _components;

    public IEnumerable<ITool> GetAllTools() => _tools.Values;

    /// <summary>获取指定引擎类型下所有可见工具的名称白名单（供子agent等场景使用）。</summary>
    public HashSet<string> GetToolWhitelist(string engineType)
    {
        var names = new HashSet<string>();
        foreach (var inst in _components)
        {
            var applicability = engineType switch
            {
                "channel" => inst.Registration.ChannelApplicability,
                "system" => inst.Registration.SystemApplicability,
                "review" => inst.Registration.ReviewApplicability,
                "sub-agent" => inst.Registration.SubAgentApplicability,
                _ => Applicability.Enabled
            };
            if (!_config.IsEnabled(inst.Component.Meta.Name, engineType, inst.Component.Meta.DefaultEnabled, applicability))
                continue;
            foreach (var tool in inst.Component.Tools)
            {
                if (!ToolRegistry.IsDisabled(tool.Name))
                    names.Add(tool.Name);
            }
        }
        return names;
    }

    private void RegisterTools(GlobalComponentInstance inst)
    {
        foreach (var tool in inst.Component.Tools)
        {
            _tools[tool.Name] = tool;
            ToolRegistry.Register(tool);
        }
    }

    private void UnregisterTools(GlobalComponentInstance inst)
    {
        foreach (var tool in inst.Component.Tools)
        {
            _tools.Remove(tool.Name);
            ToolRegistry.Unregister(tool.Name);
        }
    }

    public ITool? TryGetTool(string name)
    {
        _tools.TryGetValue(name, out var tool);
        return tool;
    }

    public List<ToolDefinition> GetToolDefinitions()
    {
        var defs = new List<ToolDefinition>();
        foreach (var tool in _tools.Values)
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

    private GlobalComponentInstance? CreateInstance(ComponentRegistration reg)
    {
        // Try constructor injection via PluginLoader if available, fall back to Activator
        var pluginLoader = _services.GetService(typeof(Tool.Host.PluginLoader)) as Tool.Host.PluginLoader;
        var instance = pluginLoader?.InstantiateWithInjection(reg.Type, _services)
                       ?? Activator.CreateInstance(reg.Type);

        if (instance is not IGlobalComponent component) return null;

        // Global组件默认全部启用（具体引擎类型的过滤在 ComponentHost 中处理）
        var defaultEnabled = component.Meta.DefaultEnabled;
        var storage = new ComponentStorage(component.Meta.Name, "_global");

        var context = new GlobalComponentContext(
            _moduleBus, _services, storage, _wakeLoop,
            enabled =>
            {
                if (enabled) _ = EnableComponentAsync(component.Meta.Name);
                else _ = DisableComponentAsync(component.Meta.Name);
            },
            defaultEnabled);

        return new GlobalComponentInstance(component, context, reg);
    }
}

internal record GlobalComponentInstance(
    IGlobalComponent Component,
    GlobalComponentContext Context,
    ComponentRegistration Registration);
