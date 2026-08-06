namespace AgentCoreProcessor.Adapter
{
    /// <summary>
    /// 声明频道归属的适配器。AdapterManager.ResolveByChannelId 在多适配器共存时
    /// 优先把出向消息路由给 OwnsChannel 命中的适配器（如 http_xxx → HttpBridge）。
    /// 不实现该接口的适配器保持原"先到先得"行为，零回归。
    /// </summary>
    internal interface IChannelAwareAdapter
    {
        bool OwnsChannel(string channelId);
    }
}
