using System.Text.Json;
using System.Text.Json.Serialization;
using AgentLilara.GraphRuntime.Graph;

namespace AgentLilara.GraphRuntime.Modules;

/// <summary>模块描述符生成失败（接口写错了，或漏了特性）。</summary>
public sealed class ModuleDescriptorException : Exception
{
    public ModuleDescriptorException(string message) : base(message)
    {
    }
}

/// <summary>方法参数 = 方法节点的一个输入口。</summary>
public sealed record ParamDescriptor(string Name, string Type, bool Optional, Cell Default);

/// <summary>
/// 方法描述符。方法在图中是**激活汇**（无输出口）；<see cref="Returns"/> 只是给编辑器与未来续延用的元数据。
/// </summary>
public sealed record MethodDescriptor(
    string Name,
    string ReturnType,
    IReadOnlyList<string> Returns,
    IReadOnlyList<ParamDescriptor> Params,
    bool SideEffect,
    bool SupportsWait,
    bool Nondeterministic,
    int TimeoutMs);

/// <summary>属性描述符：只读属性生成数据源节点，可写属性额外生成数据汇节点。</summary>
public sealed record PropertyDescriptor(string Name, string Type, bool ReadOnly);

/// <summary>生命周期入口描述符：由框架调用，不出现在画布上。</summary>
public sealed record ActivationDescriptor(string Phase, string MethodName);

/// <summary>一个模块的完整接口清单。</summary>
public sealed record ModuleDescriptor(
    string Id,
    string Version,
    IReadOnlyList<MethodDescriptor> Methods,
    IReadOnlyList<PropertyDescriptor> Properties,
    IReadOnlyList<ActivationDescriptor> Activations,
    IReadOnlyList<string> RequiredCapabilities)
{
    public MethodDescriptor? FindMethod(string name) =>
        Methods.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.Ordinal));

    public PropertyDescriptor? FindProperty(string name) =>
        Properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
}

/// <summary>节点类型命名规则：模块清单 → 画布节点类型。</summary>
public static class ModuleNodeTypes
{
    public static string Method(string moduleId, string method) => $"module.{moduleId}.{method}";

    public static string PropertyRead(string moduleId, string property) => $"read.{moduleId}.{property}";

    public static string PropertyWrite(string moduleId, string property) => $"write.{moduleId}.{property}";

    /// <summary>该模块贡献的全部节点类型。</summary>
    public static IEnumerable<string> For(ModuleDescriptor module)
    {
        foreach (var method in module.Methods)
        {
            yield return Method(module.Id, method.Name);
        }

        foreach (var property in module.Properties)
        {
            yield return PropertyRead(module.Id, property.Name);
            if (!property.ReadOnly)
            {
                yield return PropertyWrite(module.Id, property.Name);
            }
        }
    }
}

/// <summary>模块清单：描述符的集合，可落盘给 WebUI / 编辑器读取（以代码为准）。</summary>
public sealed class ModuleCatalog
{
    private readonly Dictionary<string, ModuleDescriptor> _modules;
    private readonly Dictionary<string, Type> _interfaces;

    public ModuleCatalog(IEnumerable<ModuleDescriptor> modules)
        : this(modules.Select(m => (Descriptor: m, Interface: (Type?)null)))
    {
    }

    private ModuleCatalog(IEnumerable<(ModuleDescriptor Descriptor, Type? Interface)> entries)
    {
        _modules = new Dictionary<string, ModuleDescriptor>(StringComparer.Ordinal);
        _interfaces = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var (descriptor, interfaceType) in entries)
        {
            if (!_modules.TryAdd(descriptor.Id, descriptor))
            {
                throw new ModuleDescriptorException($"duplicate module id '{descriptor.Id}'");
            }

            if (interfaceType is not null)
            {
                _interfaces[descriptor.Id] = interfaceType;
            }
        }
    }

    /// <summary>正常入口：从模块接口生成清单（描述符由反射产生）。</summary>
    public static ModuleCatalog FromTypes(IEnumerable<Type> moduleInterfaces)
    {
        var entries = moduleInterfaces
            .Select(type => (Descriptor: ModuleDescriptorGenerator.Generate(type), Interface: (Type?)type))
            .ToList();
        return new ModuleCatalog(entries);
    }

    public static ModuleCatalog Empty { get; } = new(Array.Empty<ModuleDescriptor>());

    public IReadOnlyCollection<ModuleDescriptor> Modules => _modules.Values;

    public ModuleDescriptor? Find(string id) => _modules.TryGetValue(id, out var module) ? module : null;

    /// <summary>模块接口类型（节点投影需要它把描述符名字解析回成员）。</summary>
    public Type? FindInterface(string id) => _interfaces.TryGetValue(id, out var type) ? type : null;

    /// <summary>全部模块贡献的节点类型（排序后，便于测试与差异对比）。</summary>
    public IReadOnlyList<string> NodeTypes =>
        _modules.Values
            .SelectMany(ModuleNodeTypes.For)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

    /// <summary>落盘形态：给编辑器读的清单快照。</summary>
    public string ToJson()
    {
        var payload = new
        {
            version = 1,
            modules = _modules.Values
                .OrderBy(m => m.Id, StringComparer.Ordinal)
                .Select(m => new
                {
                    id = m.Id,
                    version = m.Version,
                    requiredCapabilities = m.RequiredCapabilities,
                    activations = m.Activations.Select(a => new { phase = a.Phase, method = a.MethodName }),
                    methods = m.Methods.Select(method => new
                    {
                        name = method.Name,
                        returnType = method.ReturnType,
                        returns = method.Returns,
                        sideEffect = method.SideEffect,
                        supportsWait = method.SupportsWait,
                        nondeterministic = method.Nondeterministic,
                        timeoutMs = method.TimeoutMs,
                        nodeType = ModuleNodeTypes.Method(m.Id, method.Name),
                        @params = method.Params.Select(p => new
                        {
                            name = p.Name,
                            type = p.Type,
                            optional = p.Optional,
                            @default = p.Default.ToString(),
                        }),
                    }),
                    properties = m.Properties.Select(p => new
                    {
                        name = p.Name,
                        type = p.Type,
                        readOnly = p.ReadOnly,
                        readNodeType = ModuleNodeTypes.PropertyRead(m.Id, p.Name),
                        writeNodeType = p.ReadOnly ? null : ModuleNodeTypes.PropertyWrite(m.Id, p.Name),
                    }),
                }),
        };

        return JsonSerializer.Serialize(payload, Json);
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
