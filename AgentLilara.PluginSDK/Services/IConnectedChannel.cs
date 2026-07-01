namespace AgentLilara.PluginSDK.Services
{
    /// <summary>频道连接强度。</summary>
    public enum ConnectionMode
    {
        /// <summary>未连接任何频道。</summary>
        None,
        /// <summary>旁听：订阅消息但不占用频道、不打断 express，不能发言。</summary>
        Listen,
        /// <summary>占用：抢占频道（停 express），可发言，订阅 normal+mention。</summary>
        Full
    }

    /// <summary>
    /// 当前 session 连接的频道状态。per-session 服务，发送工具（speak 等）读它决定目标频道，
    /// 连接工具（connect_channel 等）通过 session 改它。独占单频道：连新的先断旧的。
    /// </summary>
    public interface IConnectedChannel
    {
        /// <summary>当前连接的频道 ID，0 = 未连接。</summary>
        int CurrentChannelId { get; }

        /// <summary>当前连接强度。</summary>
        ConnectionMode Mode { get; }

        /// <summary>能否发言（仅 Full 连接态可发言）。</summary>
        bool CanSpeak { get; }
    }
}
