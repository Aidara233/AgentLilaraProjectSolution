using AgentLilara.PluginSDK.Sources;

namespace AgentLilara.PluginSDK.Services;

/// <summary>
/// 信源登记面：插件在初始化时把自己的子信源交给宿主汇集器。
/// <para>宿主会在启动后统一 <c>StartAsync</c>，插件不需要自己管生命周期；
/// 但**信源内部**的循环、游标、重连仍归插件自己——汇集器只负责登记、启停与转发。</para>
/// <para>⚠️ 这是唯一被认可的登记入口。不要用 <c>PublishGlobal</c> 往宿主丢事件：
/// 那条路绕过汇集器，等于没有登记、没有启停、没有健康状态、没有失败隔离。</para>
/// </summary>
public interface ISourceRegistrar
{
    /// <summary>登记一个子信源。返回 false 表示重名（先 <see cref="Unregister"/> 再登记）。</summary>
    bool Register(IEventSource source);

    bool Unregister(string sourceId);
}
