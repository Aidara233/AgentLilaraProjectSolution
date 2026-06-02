// AgentCoreProcessor/Component/ComponentRegistry.cs
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Component;

internal record ComponentRegistration(
    Type Type,
    ComponentScope Scope,
    Applicability ChannelApplicability,
    Applicability SystemApplicability,
    Applicability ReviewApplicability,
    Applicability SubAgentApplicability,
    Assembly SourceAssembly,
    bool DefaultEnabled);

internal static class ComponentRegistry
{
    private static readonly ConcurrentDictionary<string, ComponentRegistration> _registrations = new();

    public static bool Register(Type type)
    {
        var attr = type.GetCustomAttribute<ComponentAttribute>();
        if (attr == null) return false;

        var loopAttr = type.GetCustomAttribute<LoopApplicabilityAttribute>();

        var defaultEnabled = true;
        try
        {
            var blank = RuntimeHelpers.GetUninitializedObject(type);
            var metaProp = type.GetProperty("Meta");
            if (metaProp != null && metaProp.GetValue(blank) is ComponentMeta meta)
                defaultEnabled = meta.DefaultEnabled;
        }
        catch { }

        var reg = new ComponentRegistration(
            Type: type,
            Scope: attr.Scope,
            ChannelApplicability: loopAttr?.Channel ?? Applicability.Enabled,
            SystemApplicability: loopAttr?.System ?? Applicability.Enabled,
            ReviewApplicability: loopAttr?.Review ?? Applicability.Enabled,
            SubAgentApplicability: loopAttr?.SubAgent ?? Applicability.Enabled,
            SourceAssembly: type.Assembly,
            DefaultEnabled: defaultEnabled);

        return _registrations.TryAdd(attr.Name, reg);
    }

    public static void Unregister(string name) => _registrations.TryRemove(name, out _);

    public static ComponentRegistration? Get(string name)
    {
        _registrations.TryGetValue(name, out var reg);
        return reg;
    }

    public static IEnumerable<ComponentRegistration> GetAll() => _registrations.Values;

    public static IEnumerable<ComponentRegistration> GetGlobals() =>
        _registrations.Values.Where(r => r.Scope == ComponentScope.Global);

    public static IEnumerable<ComponentRegistration> GetLoopComponents(string loopType)
    {
        return _registrations.Values.Where(r =>
        {
            if (r.Scope != ComponentScope.Loop) return false;
            var applicability = loopType switch
            {
                "channel" => r.ChannelApplicability,
                "system" => r.SystemApplicability,
                "review" => r.ReviewApplicability,
                "sub-agent" => r.SubAgentApplicability,
                _ => Applicability.Enabled
            };
            return applicability != Applicability.NotApplicable;
        });
    }

    public static void Clear() => _registrations.Clear();
}
