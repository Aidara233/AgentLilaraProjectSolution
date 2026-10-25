using System.Reflection;
using AgentLilara.GraphRuntime.Graph;

namespace AgentLilara.GraphRuntime.Modules;

/// <summary>
/// 从接口 + 特性生成模块描述符（`14-modules.md` §3）。编译期与实现保持一致，不手写 JSON。
/// 规则是严格的：任何说不清的东西都直接失败，而不是悄悄漏一个节点。
/// </summary>
public static class ModuleDescriptorGenerator
{
    public static ModuleCatalog FromTypes(IEnumerable<Type> moduleInterfaces) =>
        new(moduleInterfaces.Select(Generate));

    public static ModuleDescriptor Generate(Type interfaceType)
    {
        ArgumentNullException.ThrowIfNull(interfaceType);

        if (!interfaceType.IsInterface)
        {
            throw new ModuleDescriptorException(
                $"module type '{interfaceType.Name}' must be an interface");
        }

        var module = interfaceType.GetCustomAttribute<ModuleAttribute>()
                     ?? throw new ModuleDescriptorException(
                         $"module interface '{interfaceType.Name}' is missing [Module]");

        if (string.IsNullOrWhiteSpace(module.Id) || module.Id.Contains(' '))
        {
            throw new ModuleDescriptorException(
                $"module interface '{interfaceType.Name}': module id must be non-empty and contain no spaces");
        }

        if (string.IsNullOrWhiteSpace(module.Version))
        {
            throw new ModuleDescriptorException($"module '{module.Id}': version is required");
        }

        foreach (var capability in module.RequiredCapabilities)
        {
            if (!Capabilities.All.Contains(capability))
            {
                throw new ModuleDescriptorException(
                    $"module '{module.Id}': unknown capability '{capability}'");
            }
        }

        var methods = new List<MethodDescriptor>();
        var activations = new List<ActivationDescriptor>();
        var methodNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var method in interfaceType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            // 属性/事件的访问器不是方法节点（它们由 [Property] 描述）
            if (method.IsSpecialName)
            {
                continue;
            }

            var lifecycle = method.GetCustomAttribute<LifecycleAttribute>();
            var declared = method.GetCustomAttribute<MethodAttribute>();

            if (lifecycle is not null && declared is not null)
            {
                throw new ModuleDescriptorException(
                    $"module '{module.Id}': method '{method.Name}' cannot be both [Method] and [Lifecycle]");
            }

            if (lifecycle is not null)
            {
                if (lifecycle.Phase is not (LifecycleAttribute.Mount or LifecycleAttribute.Kill))
                {
                    throw new ModuleDescriptorException(
                        $"module '{module.Id}': method '{method.Name}' has unknown lifecycle phase '{lifecycle.Phase}'");
                }

                activations.Add(new ActivationDescriptor(lifecycle.Phase, method.Name));
                continue;
            }

            if (declared is null)
            {
                throw new ModuleDescriptorException(
                    $"module '{module.Id}': method '{method.Name}' must be marked [Method] or [Lifecycle]");
            }

            var name = ModuleNames.Camel(ModuleNames.StripAsync(method.Name));
            if (!methodNames.Add(name))
            {
                throw new ModuleDescriptorException(
                    $"module '{module.Id}': duplicate method node name '{name}'");
            }

            methods.Add(DescribeMethod(module.Id, name, method, declared));
        }

        var properties = new List<PropertyDescriptor>();
        var propertyNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in interfaceType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var attribute = property.GetCustomAttribute<PropertyAttribute>()
                            ?? throw new ModuleDescriptorException(
                                $"module '{module.Id}': property '{property.Name}' must be marked [Property]");

            if (property.GetMethod is null)
            {
                throw new ModuleDescriptorException(
                    $"module '{module.Id}': property '{property.Name}' must be readable");
            }

            if (!ModuleValueType.IsSupported(property.PropertyType))
            {
                throw new ModuleDescriptorException(
                    $"module '{module.Id}': property '{property.Name}' has unsupported type " +
                    $"'{property.PropertyType.Name}' (allowed: bool / int / long / number / text / enum / record)");
            }

            var name = ModuleNames.Camel(property.Name);
            if (!propertyNames.Add(name))
            {
                throw new ModuleDescriptorException(
                    $"module '{module.Id}': duplicate property node name '{name}'");
            }

            var readOnly = attribute.ReadOnly || property.SetMethod is null;
            properties.Add(new PropertyDescriptor(name, ModuleValueType.Name(property.PropertyType), readOnly));
        }

        return new ModuleDescriptor(
            module.Id,
            module.Version,
            methods,
            properties,
            activations,
            module.RequiredCapabilities.ToList());
    }

    /// <summary>把方法投影成方法节点描述符；参数即端口。</summary>
    private static MethodDescriptor DescribeMethod(
        string moduleId,
        string name,
        MethodInfo method,
        MethodAttribute attribute)
    {
        var parameters = new List<ParamDescriptor>();
        var portNames = new HashSet<string>(StringComparer.Ordinal) { "event" };

        foreach (var parameter in method.GetParameters())
        {
            // CancellationToken 由框架注入，不占端口
            if (parameter.ParameterType == typeof(CancellationToken))
            {
                continue;
            }

            var paramAttribute = parameter.GetCustomAttribute<ParamAttribute>();
            var portName = paramAttribute?.Name ?? parameter.Name
                           ?? throw new ModuleDescriptorException(
                               $"module '{moduleId}': method '{method.Name}' has an unnamed parameter");

            if (!portNames.Add(portName))
            {
                throw new ModuleDescriptorException(
                    $"module '{moduleId}': method '{method.Name}' has duplicate port '{portName}'");
            }

            if (!ModuleValueType.IsSupported(parameter.ParameterType))
            {
                throw new ModuleDescriptorException(
                    $"module '{moduleId}': method '{method.Name}' parameter '{portName}' has unsupported type " +
                    $"'{parameter.ParameterType.Name}' (allowed: bool / int / long / number / text / enum / record)");
            }

            var optional = paramAttribute?.Optional == true || parameter.HasDefaultValue;
            var defaultValue = Cell.Unknown;
            if (paramAttribute?.Default is { } explicitDefault)
            {
                optional = true;
                defaultValue = Cell.Of(explicitDefault);
            }
            else if (parameter.HasDefaultValue)
            {
                defaultValue = Cell.Of(parameter.DefaultValue);
            }

            parameters.Add(new ParamDescriptor(portName, ModuleValueType.Name(parameter.ParameterType), optional, defaultValue));
        }

        var returnType = ModuleValueType.UnwrapAwaitable(method.ReturnType);
        var returns = returnType is null
            ? Array.Empty<string>()
            : attribute.Returns is { } explicitReturn
                ? new[] { explicitReturn }
                : ModuleValueType.ResultPorts(returnType);

        return new MethodDescriptor(
            name,
            returnType is null ? "void" : ModuleValueType.Name(returnType),
            returns,
            parameters,
            attribute.SideEffect,
            attribute.SupportsWait,
            attribute.Nondeterministic,
            attribute.TimeoutMs);
    }
}

/// <summary>描述符里的名字投影规则（方法名 / 属性名 → 节点名）。</summary>
public static class ModuleNames
{
    /// <summary>去掉 <c>Async</c> 后缀：<c>RunAsync</c> → <c>run</c>。</summary>
    public static string StripAsync(string name) =>
        name.Length > 5 && name.EndsWith("Async", StringComparison.Ordinal) ? name[..^5] : name;

    /// <summary>首字母小写：<c>Nickname</c> → <c>nickname</c>。</summary>
    public static string Camel(string name) =>
        name.Length == 0 || char.IsLower(name[0]) ? name : char.ToLowerInvariant(name[0]) + name[1..];
}

/// <summary>模块值的类型规则与 Cell ↔ CLR 转换。</summary>
public static class ModuleValueType
{
    /// <summary>描述符里的类型名。</summary>
    public static string Name(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying.IsEnum)
        {
            return "enum:" + underlying.Name;
        }

        return underlying switch
        {
            _ when underlying == typeof(bool) => "bool",
            _ when underlying == typeof(int) => "int",
            _ when underlying == typeof(long) => "int",
            _ when underlying == typeof(double) => "number",
            _ when underlying == typeof(float) => "number",
            _ when underlying == typeof(string) => "text",
            _ when underlying.IsGenericType => $"list:{Name(underlying.GetGenericArguments()[0])}",
            _ => "record:" + underlying.Name,
        };
    }

    /// <summary>允许出现在模块接口上的类型。</summary>
    public static bool IsSupported(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(object) || typeof(Delegate).IsAssignableFrom(underlying))
        {
            return false;
        }

        if (underlying.IsEnum || underlying.IsPrimitive)
        {
            return true;
        }

        if (underlying == typeof(string) || underlying == typeof(decimal))
        {
            return true;
        }

        if (underlying.IsArray)
        {
            return IsSupported(underlying.GetElementType()!);
        }

        if (underlying.IsGenericType)
        {
            var definition = underlying.GetGenericTypeDefinition();
            if (definition == typeof(IReadOnlyList<>)
                || definition == typeof(IList<>)
                || definition == typeof(List<>)
                || definition == typeof(IEnumerable<>)
                || definition == typeof(IReadOnlyCollection<>))
            {
                return IsSupported(underlying.GetGenericArguments()[0]);
            }

            return false;
        }

        // record / class：允许，但不得是接口或抽象类（无法在 Cell 里承载未知实现）
        return !underlying.IsInterface && !underlying.IsAbstract;
    }

    /// <summary>剥掉 <c>Task&lt;T&gt;</c> / <c>ValueTask&lt;T&gt;</c>；void / Task / ValueTask → null。</summary>
    public static Type? UnwrapAwaitable(Type type)
    {
        if (type == typeof(void) || type == typeof(Task) || type == typeof(ValueTask))
        {
            return null;
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(Task<>) || definition == typeof(ValueTask<>))
            {
                return type.GetGenericArguments()[0];
            }
        }

        return type;
    }

    /// <summary>返回值 → 预留输出口名（方法节点当前无输出口，这里只给编辑器与续延用）。</summary>
    public static IReadOnlyList<string> ResultPorts(Type returnType)
    {
        var underlying = Nullable.GetUnderlyingType(returnType) ?? returnType;
        if (underlying.IsPrimitive || underlying.IsEnum || underlying == typeof(string))
        {
            return new[] { "result" };
        }

        var properties = underlying.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod is not null)
            .Select(p => ModuleNames.Camel(p.Name))
            .ToList();

        return properties.Count == 0 ? new[] { "result" } : properties;
    }

    /// <summary>Cell → 方法实参。类型必须严格匹配；不匹配就是图的错，不是模块的错。</summary>
    public static bool TryToClr(Cell cell, Type target, out object? value, out string? error)
    {
        value = null;
        error = null;

        var underlying = Nullable.GetUnderlyingType(target) ?? target;

        if (!cell.IsKnown)
        {
            error = $"expected {Name(target)}, got {(cell.IsNoEvent ? "no-event" : "unknown")}";
            return false;
        }

        var payload = cell.Payload;
        if (payload is null)
        {
            if (!target.IsValueType || Nullable.GetUnderlyingType(target) is not null)
            {
                return true;
            }

            error = $"expected {Name(target)}, got null";
            return false;
        }

        if (underlying.IsInstanceOfType(payload))
        {
            value = payload;
            return true;
        }

        switch (payload)
        {
            case int i when underlying == typeof(long):
                value = (long)i;
                return true;

            case int i when underlying == typeof(double):
                value = (double)i;
                return true;

            case long l when underlying == typeof(int) && l is >= int.MinValue and <= int.MaxValue:
                value = (int)l;
                return true;

            case long l when underlying == typeof(double):
                value = (double)l;
                return true;

            case double d when underlying == typeof(int) && d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue:
                value = (int)d;
                return true;

            case string s when underlying.IsEnum:
                if (Enum.TryParse(underlying, s, ignoreCase: true, out var parsed))
                {
                    value = parsed;
                    return true;
                }

                error = $"'{s}' is not a member of {underlying.Name}";
                return false;
        }

        error = $"expected {Name(target)}, got {Name(payload.GetType())}";
        return false;
    }

    /// <summary>方法返回值 → Cell。</summary>
    public static Cell FromClr(object? value) => value is null ? Cell.NoEvent : Cell.Of(value);
}
