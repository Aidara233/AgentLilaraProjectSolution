using System.Text.Json;
using AgentLilara.GraphRuntime.Graph;

namespace AgentLilara.GraphRuntime.Serialization;

/// <summary>
/// 节点注册表：把若干 <see cref="INodeFactory"/> 串起来（内建 + 模块投影）。
/// 默认只含内建节点；要加载 `module.*` 图，用 <see cref="With"/> 挂上模块宿主。
/// </summary>
public sealed class NodeRegistry : INodeFactory
{
    private readonly IReadOnlyList<INodeFactory> _factories;

    private NodeRegistry(IReadOnlyList<INodeFactory> factories)
    {
        _factories = factories;
    }

    /// <summary>只含内建节点。</summary>
    public static NodeRegistry Default { get; } = new(new INodeFactory[] { new BuiltInNodeFactory() });

    /// <summary>追加一个工厂；同一类型以先注册者为准。</summary>
    public NodeRegistry With(INodeFactory factory) =>
        new(_factories.Concat(new[] { factory }).ToList());

    public IReadOnlyCollection<string> Types =>
        _factories.SelectMany(f => f.Types).Distinct(StringComparer.Ordinal).ToList();

    public INode Create(string id, string type, IReadOnlyDictionary<string, JsonElement>? parameters)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new GraphLoadException($"node of type '{type}' has empty id");
        }

        foreach (var factory in _factories)
        {
            if (factory.Types.Contains(type, StringComparer.Ordinal))
            {
                return factory.Create(id, type, parameters);
            }
        }

        throw new GraphLoadException($"node '{id}': unknown type '{type}'");
    }
}
