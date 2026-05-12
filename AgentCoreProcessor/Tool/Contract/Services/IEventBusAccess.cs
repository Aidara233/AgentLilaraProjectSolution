namespace AgentCoreProcessor.Tool.Contract.Services
{
    /// <summary>
    /// 事件总线访问接口。
    /// </summary>
    public interface IEventBusAccess
    {
        /// <summary>发布信号事件。</summary>
        void PublishSignal(string signal, string? source = null);
    }
}
