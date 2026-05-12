namespace AgentCoreProcessor.Tool.Contract
{
    /// <summary>
    /// 插件隔离存储接口。每个插件实例拿到的是绑定了自己路径的实例。
    /// </summary>
    public interface IPluginStorage
    {
        /// <summary>插件全局目录（配置、共享数据）。所有实例共享同一份。</summary>
        string GlobalDirectory { get; }

        /// <summary>当前实例目录（会话状态）。PerSession 每实例不同，Singleton 等同 Global。</summary>
        string InstanceDirectory { get; }
    }
}
