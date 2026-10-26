namespace AgentLilara.PluginSDK.Sources;

/// <summary>
/// 子信源：**单向事件流**。邮件、webhook、定时、文件变化都实现它。
/// <para>⚠️ 与 <c>IAdapter</c> 的根本差别：</para>
/// <list type="bullet">
/// <item>没有频道 —— 事件不带对话归属，投递目标由订阅记录决定；</item>
/// <item>不能发送 —— 没有 <c>SendMessageAsync</c>，"回一封信"是另一条链路；</item>
/// <item>可能是拉取式的 —— 因此有游标与 <c>StartAsync</c> 生命周期，而不是长连接回调。</item>
/// </list>
/// <para>子信源自己管自己的游标、去重与重连；汇集器不理解业务，只负责登记、启停、汇总与转发。</para>
/// <para>⚠️ 这个接口是**插件可见契约**：实现它并在 <c>StartAsync</c> 里跑自己的循环，
/// 宿主侧的汇集器会在启动时把它拉起来。</para>
/// </summary>
public interface IEventSource
{
    /// <summary>子信源实例标识（配置里的唯一键）。</summary>
    string Id { get; }

    /// <summary>事件的信源类别，会成为 <see cref="SourceEvent.SourceId"/>（如 `mail`）。</summary>
    string Kind { get; }

    /// <summary>
    /// 本信源会在 <see cref="SourceEvent.Ext"/> 里提供哪些键。
    /// <para>存在的意义：图上的 <c>event.ext("from")</c> 与信源写的 <c>"from"</c> 之间
    /// 原本只有"字符串自觉"这一层保障——写错不会报错，只会**永远匹配不上**，
    /// 而失败表现是"什么都不发生"，最难排查。声明之后，创建订阅时就能当场拒绝写错的键。</para>
    /// <para>返回空表示不声明、不做键校验（向后兼容既有插件）。</para>
    /// </summary>
    IReadOnlyList<string> Fields => Array.Empty<string>();

    /// <summary>产出一条事件。汇集器负责把它交给统一入站入口。</summary>
    event Func<SourceEvent, Task>? OnEvent;

    Task StartAsync(CancellationToken ct = default);

    Task StopAsync();

    EventSourceStatus GetStatus();
}
