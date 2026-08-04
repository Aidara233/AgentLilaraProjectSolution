// AgentLilara.PluginSDK/ILoopComponentContext.cs
namespace AgentLilara.PluginSDK;

public interface ILoopComponentContext
{
    /// <summary>当前会话实例的 sessionId（裸，无 "session:" 前缀）。LoopId 概念已移除。</summary>
    string SessionId { get; }
    string LoopType { get; }

    bool IsEnabled { get; }
    void Enable();
    void Disable();

    void WakeLoop();

    void PublishLocal<TEvent>(TEvent e) where TEvent : class;
    void PublishGlobal<TEvent>(TEvent e) where TEvent : class;
    void Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class;
    void Unsubscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class;

    T? GetService<T>() where T : class;
    IPluginStorage Storage { get; }
}
