using System;
using System.Collections.Concurrent;
using AgentCoreProcessor.Tool.Contract;

namespace AgentCoreProcessor.Tool.Host
{
    /// <summary>
    /// IToolContext 实现。纯服务容器，内部模块注册服务，插件按类型获取。
    /// </summary>
    internal class ToolContextImpl : IToolContext
    {
        private readonly ConcurrentDictionary<Type, object> _services = new();

        /// <summary>注册服务实例。重复注册同类型会覆盖。</summary>
        public void Register<T>(T service) where T : class
        {
            _services[typeof(T)] = service;
        }

        /// <summary>注销服务。</summary>
        public void Unregister<T>() where T : class
        {
            _services.TryRemove(typeof(T), out _);
        }

        /// <summary>检查服务是否已注册。</summary>
        public bool Has<T>() where T : class
        {
            return _services.ContainsKey(typeof(T));
        }

        public T? GetService<T>() where T : class
        {
            return _services.TryGetValue(typeof(T), out var svc) ? (T)svc : null;
        }
    }
}
