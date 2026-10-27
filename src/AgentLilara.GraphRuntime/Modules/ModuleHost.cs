using System.Reflection;
using System.Text.Json;
using AgentLilara.GraphRuntime.Graph;
using AgentLilara.GraphRuntime.Serialization;

namespace AgentLilara.GraphRuntime.Modules;

/// <summary>
/// 模块宿主：把模块清单投影成节点类型，并持有已绑定的实现。
/// 绑定是显式的——没绑定实现的模块，包含它的图加载即失败（不给"静默空实现"）。
/// </summary>
public sealed class ModuleHost : INodeFactory
{
    private readonly ModuleCatalog _catalog;
    private readonly Dictionary<string, object> _implementations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NodeSpec> _nodeTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<(string ModuleId, string InstanceId), object> _instances = new();

    public ModuleHost(ModuleCatalog catalog)
    {
        _catalog = catalog;

        foreach (var module in catalog.Modules)
        {
            var interfaceType = catalog.FindInterface(module.Id)
                                ?? throw new ModuleDescriptorException(
                                    $"module '{module.Id}': interface type is unknown " +
                                    "(build the catalog with ModuleCatalog.FromTypes)");

            foreach (var method in module.Methods)
            {
                _nodeTypes[ModuleNodeTypes.Method(module.Id, method.Name)] =
                    new NodeSpec(module, interfaceType, method, null, IsWrite: false);
            }

            foreach (var property in module.Properties)
            {
                _nodeTypes[ModuleNodeTypes.PropertyRead(module.Id, property.Name)] =
                    new NodeSpec(module, interfaceType, null, property, IsWrite: false);

                if (!property.ReadOnly)
                {
                    _nodeTypes[ModuleNodeTypes.PropertyWrite(module.Id, property.Name)] =
                        new NodeSpec(module, interfaceType, null, property, IsWrite: true);
                }
            }
        }
    }

    public ModuleCatalog Catalog => _catalog;

    public IReadOnlyCollection<string> Types => _nodeTypes.Keys;

    public IReadOnlyDictionary<string, object> Implementations => _implementations;

    /// <summary>内建节点 + 本宿主的模块节点，直接交给 <see cref="GraphLoader"/>。</summary>
    public INodeFactory Registry => NodeRegistry.Default.With(this);

    /// <summary>绑定实现；实现必须实现该模块接口，且一个模块只绑定一次。</summary>
    public ModuleHost Bind(string moduleId, object implementation)
    {
        ArgumentNullException.ThrowIfNull(implementation);

        var interfaceType = _catalog.FindInterface(moduleId)
                            ?? throw new ModuleDescriptorException($"module '{moduleId}' is not in the catalog");

        if (!interfaceType.IsInstanceOfType(implementation))
        {
            throw new ModuleDescriptorException(
                $"module '{moduleId}': implementation '{implementation.GetType().Name}' " +
                $"does not implement '{interfaceType.Name}'");
        }

        if (!_implementations.TryAdd(moduleId, implementation))
        {
            throw new ModuleDescriptorException($"module '{moduleId}' is already bound");
        }

        return this;
    }

    /// <summary>绑定命名实例；实例名为字面值，使用 ordinal 比较。</summary>
    public ModuleHost BindInstance(string moduleId, string instanceId, object implementation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(implementation);
        var contract = _catalog.FindInterface(moduleId)
            ?? throw new ModuleDescriptorException($"module '{moduleId}' is not in the catalog");
        if (!contract.IsInstanceOfType(implementation))
            throw new ModuleDescriptorException($"module '{moduleId}': implementation does not implement '{contract.Name}'");
        if (!_instances.TryAdd((moduleId, instanceId), implementation))
            throw new ModuleDescriptorException($"module '{moduleId}' instance '{instanceId}' is already bound");
        return this;
    }

    public INode Create(string id, string type, IReadOnlyDictionary<string, JsonElement>? parameters)
    {
        var bag = new ParamBag(id, parameters);
        var instanceId = bag.GetString("instance");
        bag.EnsureOnly("instance");
        if (instanceId is not null && string.IsNullOrWhiteSpace(instanceId))
            throw new GraphLoadException($"node '{id}': instance must be a non-empty literal");

        if (!_nodeTypes.TryGetValue(type, out var spec))
        {
            throw new GraphLoadException($"node '{id}': unknown type '{type}'");
        }

        object? implementation;
        var found = instanceId is null
            ? _implementations.TryGetValue(spec.Module.Id, out implementation)
            : _instances.TryGetValue((spec.Module.Id, instanceId), out implementation);
        if (!found || implementation is null)
        {
            throw new GraphLoadException(
                $"node '{id}': module '{spec.Module.Id}' has no bound implementation" +
                (instanceId is null ? "" : $" for instance '{instanceId}'"));
        }

        if (spec.Method is not null)
        {
            var method = ResolveMethod(spec.Interface, spec.Method.Name)
                         ?? throw new GraphLoadException(
                             $"node '{id}': module '{spec.Module.Id}' has no method '{spec.Method.Name}'");
            return new ModuleMethodNode(id, spec.Module, spec.Method, method, implementation);
        }

        var property = ResolveProperty(spec.Interface, spec.Property!.Name)
                       ?? throw new GraphLoadException(
                           $"node '{id}': module '{spec.Module.Id}' has no property '{spec.Property.Name}'");

        return spec.IsWrite
            ? new ModulePropertyWriteNode(id, spec.Module, spec.Property, property, implementation)
            : new ModulePropertyReadNode(id, spec.Module, spec.Property, property, implementation);
    }

    /// <summary>描述符名字（camelCase）→ 接口成员。</summary>
    private static MethodInfo? ResolveMethod(Type interfaceType, string name) =>
        interfaceType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => ModuleNames.Camel(ModuleNames.StripAsync(m.Name)) == name);

    private static PropertyInfo? ResolveProperty(Type interfaceType, string name) =>
        interfaceType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => ModuleNames.Camel(p.Name) == name);

    private sealed record NodeSpec(
        ModuleDescriptor Module,
        Type Interface,
        MethodDescriptor? Method,
        PropertyDescriptor? Property,
        bool IsWrite);
}
