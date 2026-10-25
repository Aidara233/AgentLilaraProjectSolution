using System.Text.Json;
using AgentLilara.Kernel.Graph;

namespace AgentLilara.Kernel.Serialization;

/// <summary>
/// 内建节点（激活源 / 数据源 / 纯算子 / 变量写 / 动作意图）。
/// 动作汇块由 <see cref="IntentCatalog"/> 驱动：清单里加一行就有新节点类型，不用加类。
/// </summary>
public sealed class BuiltInNodeFactory : INodeFactory
{
    private readonly Dictionary<string, Func<string, ParamBag, INode>> _factories;

    public BuiltInNodeFactory()
    {
        _factories = new Dictionary<string, Func<string, ParamBag, INode>>(StringComparer.Ordinal)
        {
            ["on.message"] = (id, p) =>
            {
                p.EnsureOnly();
                return new OnMessageEvent(id);
            },
            ["on.clock"] = (id, p) =>
            {
                p.EnsureOnly();
                return new OnClock(id);
            },
            ["read.const"] = (id, p) =>
            {
                var cell = p.GetCell("value", Cell.Unknown);
                p.EnsureOnly("value");
                return new ConstValue(id, cell);
            },
            ["read.variable"] = (id, p) =>
            {
                var name = p.GetString("name")
                           ?? throw new GraphLoadException($"node '{id}': param 'name' is required");
                p.EnsureOnly("name");
                return new ReadVariable(id, name);
            },
            ["event.get"] = (id, p) =>
            {
                var field = p.GetString("field")
                            ?? throw new GraphLoadException($"node '{id}': param 'field' is required");
                p.EnsureOnly("field");
                if (!EventField.AllowedFields.Contains(field, StringComparer.Ordinal))
                {
                    throw new GraphLoadException(
                        $"node '{id}': unknown event field '{field}' " +
                        $"(allowed: {string.Join(", ", EventField.AllowedFields)})");
                }

                return new EventField(id, field);
            },
            ["event.mention"] = (id, p) =>
            {
                p.EnsureOnly();
                return new EventMention(id);
            },
            ["logic.and"] = (id, p) =>
            {
                p.EnsureOnly();
                return new LogicAnd(id);
            },
            ["logic.or"] = (id, p) =>
            {
                p.EnsureOnly();
                return new LogicOr(id);
            },
            ["logic.select"] = (id, p) =>
            {
                p.EnsureOnly();
                return new SelectValue(id);
            },
            ["gate.if"] = (id, p) =>
            {
                p.EnsureOnly();
                return new GateIf(id);
            },
            ["compare.ge"] = (id, p) =>
            {
                p.EnsureOnly();
                return new CompareGe(id);
            },
            ["math.add"] = (id, p) =>
            {
                p.EnsureOnly();
                return new AddInt(id);
            },
            ["list.first"] = (id, p) =>
            {
                p.EnsureOnly();
                return new ListFirst(id);
            },
            ["write.variable"] = (id, p) =>
            {
                var name = p.GetString("name")
                           ?? throw new GraphLoadException($"node '{id}': param 'name' is required");
                p.EnsureOnly("name");
                return new WriteVariable(id, name);
            },
        };

        // 动作汇块：清单驱动，一个节点类型一行
        foreach (var spec in IntentCatalog.All)
        {
            _factories[spec.NodeType] = (id, p) =>
            {
                p.EnsureOnly();
                return new IntentSink(id, spec);
            };
        }
    }

    public IReadOnlyCollection<string> Types => _factories.Keys;

    public INode Create(string id, string type, IReadOnlyDictionary<string, JsonElement>? parameters)
    {
        if (!_factories.TryGetValue(type, out var factory))
        {
            throw new GraphLoadException($"node '{id}': unknown type '{type}'");
        }

        return factory(id, new ParamBag(id, parameters));
    }
}
