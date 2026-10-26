using AgentLilara.PluginSDK.Services;
using AgentLilara.PluginSDK.Sources;

namespace AgentCoreProcessor.Component;

/// <summary>
/// <see cref="ISourceRegistrar"/> 的实现：把插件声明的子信源登记进宿主的信源汇集器。
/// <para>薄到没有逻辑——这正是目的：插件说"我有这么一个信源"，宿主决定怎么跑它。</para>
/// </summary>
internal sealed class SourceRegistrarImpl : ISourceRegistrar
{
    private readonly Engine.Source.SourceManager _sources;

    public SourceRegistrarImpl(Engine.Source.SourceManager sources) => _sources = sources;

    public bool Register(IEventSource source) => _sources.Register(source);

    public bool Unregister(string sourceId) => _sources.Unregister(sourceId);
}
