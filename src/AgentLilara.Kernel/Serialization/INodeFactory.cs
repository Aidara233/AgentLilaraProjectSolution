using System.Text.Json;
using AgentLilara.Kernel.Graph;

namespace AgentLilara.Kernel.Serialization;

/// <summary>节点类型 → 构造器。内建节点、模块投影节点都通过它接入图加载。</summary>
public interface INodeFactory
{
    /// <summary>本工厂支持的节点类型（给编辑器与差异对比用）。</summary>
    IReadOnlyCollection<string> Types { get; }

    /// <summary>构造节点；不认识的类型必须抛 <see cref="GraphLoadException"/>。</summary>
    INode Create(string id, string type, IReadOnlyDictionary<string, JsonElement>? parameters);
}
