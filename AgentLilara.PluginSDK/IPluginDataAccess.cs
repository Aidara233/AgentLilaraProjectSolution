namespace AgentLilara.PluginSDK
{
    /// <summary>
    /// 插件数据目录访问接口。只允许 GlobalDirectory / InstanceDirectory 内。
    /// 插件禁止拿到目录字符串自行拼路径，路径解析一律走此接口。
    /// </summary>
    public interface IPluginDataAccess
    {
        /// <summary>解析到插件全局目录（配置、共享数据）内绝对路径；越界返回 null。</summary>
        string? ResolveGlobal(string path);

        /// <summary>解析到当前实例目录（会话状态）内绝对路径；越界返回 null。</summary>
        string? ResolveInstance(string path);
    }
}