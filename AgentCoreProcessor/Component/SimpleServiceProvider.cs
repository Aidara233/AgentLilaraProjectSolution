// AgentCoreProcessor/Component/SimpleServiceProvider.cs
using System.Collections.Concurrent;

namespace AgentCoreProcessor.Component;

/// <summary>
/// 极简 IServiceProvider 实现。供 ComponentHost/GlobalComponentHost 使用。
/// 内部模块注册服务实例，组件按类型获取。
/// </summary>
internal class SimpleServiceProvider : IServiceProvider
{
    private readonly ConcurrentDictionary<Type, object> _services;

    public SimpleServiceProvider() => _services = new();

    public SimpleServiceProvider(Dictionary<Type, object> services)
    {
        _services = new ConcurrentDictionary<Type, object>(services);
    }

    public void Register<T>(T service) where T : class
    {
        _services[typeof(T)] = service;
    }

    public void Register(Type serviceType, object service)
    {
        _services[serviceType] = service;
    }

    public object? GetService(Type serviceType)
    {
        return _services.TryGetValue(serviceType, out var svc) ? svc : null;
    }

    internal IReadOnlyDictionary<Type, object> GetAllServices() => _services;
}
