using System;

namespace AgentCoreProcessor.Tool.Contract.Services
{
    /// <summary>
    /// 事件总线访问接口。插件可发布信号和订阅事件。
    /// </summary>
    public interface IEventBusAccess
    {
        /// <summary>发布信号事件。</summary>
        void PublishSignal(string signal, string? source = null);

        /// <summary>订阅指定类型的事件。</summary>
        void Subscribe<T>(Action<T> handler) where T : class;

        /// <summary>取消订阅。</summary>
        void Unsubscribe<T>(Action<T> handler) where T : class;
    }
}
