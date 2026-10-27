using System.Text.Json;
using AgentLilara.GraphRuntime.Graph;

namespace AgentLilara.GraphRuntime.Serialization;

/// <summary>节点参数声明。`Required=true` 表示不传就构造失败。</summary>
public sealed record NodeTypeParam(string Name, string Type, bool Required, string Example);

/// <summary>
/// 一个节点类型的对外说明：端口是**探针构造出来的事实**，不是手写副本，因此不会与实现失同步。
/// </summary>
public sealed record NodeTypeSpec(
    string Type,
    NodeKind Kind,
    bool IsActivationSource,
    IReadOnlyList<string> InputPorts,
    IReadOnlyList<string> OutputPorts,
    IReadOnlyList<NodeTypeParam> Params,
    string Summary);

/// <summary>能自报节点类型清单的工厂（内建 / 模块宿主 / 组合注册表）。</summary>
public interface INodeTypeCatalog
{
    IReadOnlyList<NodeTypeSpec> NodeTypes { get; }
}

/// <summary>
/// 节点类型目录：供 `wire.describe` / 图编辑器读取。
/// <para>⚠️ <b>端口不手写</b>——每个类型用示例参数造一个探针节点，端口、Kind、是否激活源都从探针读。
/// 这样实现改了端口，目录自动跟上；若实现改成"示例参数造不出来"，<see cref="Build"/> 直接抛异常，
/// 由 <c>NodeTypeCatalogTests</c> 在 CI 里抓住，而不是等模型写出一张加载不了的图。</para>
/// </summary>
public static class NodeTypeCatalog
{
    /// <summary>手写部分：参数声明 + 示例 + 一句话说明。端口与类别由探针补全。</summary>
    private sealed record Raw(string Type, string Summary, NodeTypeParam[] Params, (string Key, object? Value)[] Example);

    private static readonly Raw[] Handwritten =
    {
        // ── 激活源（每图恰好一个）──
        new("on.message",
            "消息到达时激活（唯一常用的激活源）",
            Array.Empty<NodeTypeParam>(),
            Array.Empty<(string, object?)>()),
        new("on.clock",
            "时钟沿激活（动态图；每拍都可能激活）",
            Array.Empty<NodeTypeParam>(),
            Array.Empty<(string, object?)>()),
        new("on.error",
            "上一个动作失败后，骨架以错误事件再激活一次（兜底图用）",
            Array.Empty<NodeTypeParam>(),
            Array.Empty<(string, object?)>()),
        new("on.external",
            "外部信源（邮件/定时任务等）推送时激活",
            Array.Empty<NodeTypeParam>(),
            Array.Empty<(string, object?)>()),

        // ── 非激活源的数据源（Kind=Source 但不触发求解）──
        new("read.const",
            "常量值",
            new[] { new NodeTypeParam("value", "bool|int|string", false, "\"hello\"") },
            new[] { ("value", (object?)"hello") }),
        new("read.variable",
            "读图变量（variables 里声明的）",
            new[] { new NodeTypeParam("name", "string", true, "\"count\"") },
            new[] { ("name", (object?)"count") }),
        new("read.mount",
            "读挂载参数（mounts.json 里该实例的 params）",
            new[]
            {
                new NodeTypeParam("name", "string", true, "\"sessionType\""),
                new NodeTypeParam("default", "string", false, "\"channel\""),
            },
            new[] { ("name", (object?)"sessionType") }),

        // ── 事件读取 ──
        new("event.get",
            "读事件字段：text/sender/channel/adapter/kind/mentions/isReply/received/dedupe",
            new[] { new NodeTypeParam("field", "string", true, "\"text\"") },
            new[] { ("field", (object?)"text") }),
        new("event.mention",
            "事件是否点名了机器人（bool）",
            Array.Empty<NodeTypeParam>(),
            Array.Empty<(string, object?)>()),
        new("event.ext",
            "读信封里的扩展载荷键（例如订阅里传的 from）",
            new[] { new NodeTypeParam("name", "string", true, "\"from\"") },
            new[] { ("name", (object?)"from") }),

        // ── 纯算子 ──
        new("compare.eq", "相等比较（a == b）", Array.Empty<NodeTypeParam>(), Array.Empty<(string, object?)>()),
        new("compare.ge", "大于等于比较（value >= threshold）", Array.Empty<NodeTypeParam>(), Array.Empty<(string, object?)>()),
        new("logic.and", "逻辑与", Array.Empty<NodeTypeParam>(), Array.Empty<(string, object?)>()),
        new("logic.or", "逻辑或", Array.Empty<NodeTypeParam>(), Array.Empty<(string, object?)>()),
        new("logic.select", "三目（cond ? a : b）", Array.Empty<NodeTypeParam>(), Array.Empty<(string, object?)>()),
        new("gate.if", "条件放行：cond 为真才把 event 往下传，否则本拍不往下走", Array.Empty<NodeTypeParam>(), Array.Empty<(string, object?)>()),
        new("text.contains", "文本包含判断（text 含 needle）", Array.Empty<NodeTypeParam>(), Array.Empty<(string, object?)>()),
        new("math.add", "整数加法（a + b）", Array.Empty<NodeTypeParam>(), Array.Empty<(string, object?)>()),
        new("list.first", "取列表第一个元素，并给出是否找到", Array.Empty<NodeTypeParam>(), Array.Empty<(string, object?)>()),

        // ── 变量写 ──
        new("write.variable",
            "写图变量（激活成功结束才提交）",
            new[] { new NodeTypeParam("name", "string", true, "\"count\"") },
            new[] { ("name", (object?)"count") }),
    };

    private static readonly object Gate = new();

    private static IReadOnlyList<NodeTypeSpec>? _last;
    private static INodeFactory? _lastFactory;

    /// <summary>构造目录。同一个工厂实例只算一次（探针构造有反射/查找开销）。</summary>
    public static IReadOnlyList<NodeTypeSpec> Build(INodeFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        lock (Gate)
        {
            if (_last is not null && ReferenceEquals(_lastFactory, factory))
            {
                return _last;
            }
        }

        var specs = new List<NodeTypeSpec>(Handwritten.Length + IntentCatalog.All.Count);

        foreach (var raw in Handwritten)
        {
            specs.Add(Probe(factory, raw));
        }

        // 动作汇块：清单驱动，与 BuiltInNodeFactory 的注册循环同源
        foreach (var intent in IntentCatalog.All)
        {
            specs.Add(Probe(factory, new Raw(
                intent.NodeType,
                $"动作汇块（{intent.Kind}）：只产出意图，由骨架执行",
                Array.Empty<NodeTypeParam>(),
                Array.Empty<(string, object?)>())));
        }

        // 目录必须完整；缺少说明时立即失败，避免对模型返回虚假的端口与类别。
        var catalogTypes = specs.Select(spec => spec.Type).ToHashSet(StringComparer.Ordinal);
        foreach (var type in factory.Types)
        {
            if (catalogTypes.Contains(type))
            {
                continue;
            }

            throw new GraphLoadException($"node type '{type}': catalog description is missing");
        }

        var result = specs
            .OrderBy(spec => spec.Type, StringComparer.Ordinal)
            .ToList();

        lock (Gate)
        {
            _last = result;
            _lastFactory = factory;
        }

        return result;
    }

    /// <summary>一段最小可加载的图源码，教模型 JSON 的形状。</summary>
    public static string SampleSource() => """
        {
          "classId": "user.example",
          "kind": "static",
          "version": 1,
          "variables": [
            { "name": "hits", "type": "int", "initial": 0 }
          ],
          "nodes": [
            { "id": "in",   "type": "on.message" },
            { "id": "say",  "type": "sink.send" }
          ],
          "links": [
            { "from": "in.event", "to": "say.event" },
            { "from": "in.channel", "to": "say.text" }
          ]
        }
        """;

    private static NodeTypeSpec Probe(INodeFactory factory, Raw raw)
    {
        var parameters = ToJson(raw.Example);
        INode probe;
        try
        {
            probe = factory.Create("probe", raw.Type, parameters);
        }
        catch (Exception ex)
        {
            throw new GraphLoadException(
                $"node type '{raw.Type}': catalog probe failed " +
                $"(the example parameters no longer construct a node): {ex.Message}");
        }

        return new NodeTypeSpec(
            raw.Type,
            probe.Kind,
            probe.IsActivationSource,
            probe.InputPorts,
            probe.OutputPorts,
            raw.Params,
            raw.Summary);
    }

    private static IReadOnlyDictionary<string, JsonElement>? ToJson((string Key, object? Value)[] example)
    {
        if (example.Length == 0)
        {
            return null;
        }

        var json = JsonSerializer.Serialize(example.ToDictionary(pair => pair.Key, pair => pair.Value));
        using var document = JsonDocument.Parse(json);

        var bag = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            bag[property.Name] = property.Value.Clone();
        }

        return bag;
    }
}
