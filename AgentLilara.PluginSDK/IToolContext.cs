using System;

namespace AgentLilara.PluginSDK
{
    /// <summary>
    /// 工具上下文。插件通过此接口访问宿主提供的内部服务。
    /// 内部模块实现 Contract/Services 下的接口并注册，插件通过 GetService 获取。
    /// 外向工具可以完全不使用此接口。
    /// </summary>
    public interface IToolContext
    {
        /// <summary>按类型获取已注册的服务，不存在时返回 null。</summary>
        T? GetService<T>() where T : class;

        /// <summary>按类型获取服务，不存在时抛出 InvalidOperationException。</summary>
        T Require<T>() where T : class
        {
            return GetService<T>()
                ?? throw new InvalidOperationException(
                    $"服务 {typeof(T).Name} 未注册。请确认对应的内部模块已初始化。");
        }

        /// <summary>插件隔离存储（由宿主根据插件名+作用域自动绑定路径）。</summary>
        IPluginStorage Storage { get; }
    }
}
