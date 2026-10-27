namespace AgentLilara.GraphRuntime.Modules;

/// <summary>
/// 能力常量。模块声明它需要什么；运行时门禁尚未实现（见 `07-governor.md`）。
/// </summary>
public static class Capabilities
{
    public const string Net = "net";
    public const string Disk = "disk";
    public const string Db = "db";
    public const string Exec = "exec";
    public const string Secret = "secret";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Net, Disk, Db, Exec, Secret,
    };
}

/// <summary>
/// 声明一个接口是模块。模块 = 一段自成体系的代码，只暴露接口（`14-modules.md`）。
/// 描述符由反射生成，不手写 JSON；图的 `module.*` / `read.*` / `write.*` 节点从描述符投影。
/// </summary>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = false)]
public sealed class ModuleAttribute : Attribute
{
    public ModuleAttribute(string id)
    {
        Id = id;
    }

    /// <summary>模块 id，如 <c>agent.loop</c>。节点类型形如 <c>module.agent.loop.run</c>。</summary>
    public string Id { get; }

    public string Version { get; set; } = "1.0.0";

    /// <summary>形如 <c>new[] { Capabilities.Net, Capabilities.Disk }</c>。</summary>
    public string[] RequiredCapabilities { get; set; } = Array.Empty<string>();
}

/// <summary>
/// 声明一个可被图调用的方法。方法与生命周期方法必须二选一显式标注：
/// 未标注的公开方法会让描述符生成直接失败（防止"改了代码但图上凭空少了一个节点"）。
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class MethodAttribute : Attribute
{
    /// <summary>显式指定返回值的输出口名（预留：当前方法节点无输出口，见 `14-modules.md` §4）。</summary>
    public string? Returns { get; set; }

    public bool SideEffect { get; set; }

    public bool SupportsWait { get; set; }

    public bool Nondeterministic { get; set; }

    /// <summary>超时上限；执行器尚未强制（需要调度器）。</summary>
    public int TimeoutMs { get; set; } = 30_000;
}

/// <summary>声明生命周期入口。它不由画布驱动，而是由框架在实例化 / 销毁前调用。</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class LifecycleAttribute : Attribute
{
    public const string Mount = "mount";
    public const string Kill = "kill";

    public LifecycleAttribute(string phase)
    {
        Phase = phase;
    }

    /// <summary>mount | kill。</summary>
    public string Phase { get; }
}

/// <summary>声明模块属性。有 setter = 可写（额外生成一个数据汇节点）；无 setter = 只读。</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class PropertyAttribute : Attribute
{
    /// <summary>强制只读（即使有 setter 也不生成写节点）。</summary>
    public bool ReadOnly { get; set; }
}

/// <summary>方法参数的端口名 / 可选性覆盖。缺省取参数名与 C# 默认值。</summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class ParamAttribute : Attribute
{
    /// <summary>图中的端口名（缺省用参数名）。</summary>
    public string? Name { get; set; }

    /// <summary>可选参数，不要求上游接线；未接线时用 <see cref="Default"/>。</summary>
    public bool Optional { get; set; }

    /// <summary>可选参数的默认值（bool / int / string）。</summary>
    public object? Default { get; set; }
}
