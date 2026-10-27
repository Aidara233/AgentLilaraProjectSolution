// AgentCoreProcessor/Engine/Axes/AxisPort.cs
using System;
using System.Collections.Generic;

namespace AgentCoreProcessor.Engine.Axes;

/// <summary>
/// <see cref="IAxisPort"/> 的实现 —— **纯委托**，只把静态表的查询转发出去，
/// 并把 <see cref="Freeze"/> 绑到某个会话的 <see cref="AxisStore"/> 上。
///
/// <para>⚠️ 它**刻意不含任何判断**：任何"这条轴该不该生效""缺值该取什么"的逻辑都必须留在
/// <see cref="Axes"/>（唯一真相）或调用方（有上下文），不能在这里长出第二套规则。
/// 这里多一句 if，就会多一个和表不一致的地方。</para>
///
/// <para>⚠️ 端口是**每个会话一份**（因为 <see cref="Freeze"/> 要绑定该会话的当前值），
/// 但表是进程级共享的——这正是"单例的数据 + 每会话的取值"该有的形状。</para>
/// </summary>
internal sealed class AxisPort : IAxisPort
{
    private readonly AxisStore _store;

    public AxisPort(AxisStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    public IReadOnlyList<string> Names => Axes.Names;

    public IReadOnlyList<string> RenderOrder => Axes.RenderOrder;

    public IReadOnlyList<string>? ValuesOf(string? axis) => Axes.ValuesOf(axis);

    public string LabelOf(string? axis) => Axes.LabelOf(axis);

    public bool InContext(string? axis) => Axes.InContext(axis);

    public string? DescriptionOf(string? axis, string? value) => Axes.DescriptionOf(axis, value);

    public bool IsKnown(string? axis) => Axes.IsKnown(axis);

    public bool IsNumeric(string? axis) => Axes.IsNumeric(axis);

    public double? NumberOrNull(string? axis, string? canonical) => Axes.NumberOrNull(axis, canonical);

    public bool TryParse(string? axis, string value, out string canonical) => Axes.TryParse(axis, value, out canonical);

    public AxisDefinition? DefinitionOf(string? axis) => Axes.DefinitionOf(axis);

    public IReadOnlyDictionary<string, string> Defaults() => Axes.Defaults();

    /// <summary>
    /// 冻结快照。⚠️ 传 <see langword="null"/> 表示"用本会话当前值"——那是**唯一**允许的省略，
    /// 因为它就是本端口绑定的那个 store；显式传一份值则用于对拍与控制面预览。
    /// </summary>
    public AxisSnapshot Freeze(IReadOnlyDictionary<string, string> values, DateTimeOffset at) =>
        new(new Dictionary<string, string>(values, StringComparer.Ordinal), at);

    /// <summary>本会话当前值的副本（给 <see cref="Freeze"/> 的默认输入）。</summary>
    public Dictionary<string, string> CurrentValues() => _store.Snapshot();

    /// <summary>本会话当前的冻结快照（便捷入口：`Freeze(CurrentValues(), now)`）。</summary>
    public AxisSnapshot FreezeNow(DateTimeOffset at) => Freeze(CurrentValues(), at);
}
