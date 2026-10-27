// AgentCoreProcessor/Engine/Axes/AxisStore.cs
using System;
using System.Collections.Generic;

namespace AgentCoreProcessor.Engine.Axes;

/// <summary>
/// 一个会话的轴值存储 —— **唯一的写入点**。
///
/// <para>它是"每会话一份"的可变状态：默认值来自 <see cref="AxesCatalog.Defaults"/>，
/// 从 <c>state.json</c> 恢复时按轴名逐条校验（未知轴丢弃、非法值丢弃），
/// 缺失的轴补默认值。</para>
///
/// <para>⚠️ **只有扁平字符串值，没有结构**。按已定决策 D3，实例图/会话状态只存"变量值"，
/// 不存"节点/连线的差异"——所以这里不出现任何图的形状。</para>
///
/// <para>⚠️ **未知轴与非法值一律丢弃、不报错**：`state.json` 可能来自装了插件的另一个部署，
/// 或者手改过。丢一条轴的代价是"回到默认值"，而抛异常会让整个会话恢复失败——
/// 后者严重得多。但**丢掉的必须记日志**，否则就是静默失效。</para>
/// </summary>
internal sealed class AxisStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public AxisStore()
    {
        Reset(null);
    }

    /// <summary>
    /// 重置为"默认值 + 传入的覆盖"。<paramref name="initial"/> 为 <see langword="null"/>
    /// 表示纯默认（新会话、或旧版 state.json 没有 axes 字段）。
    /// </summary>
    /// <returns>被丢弃的条目（轴名 → 原因），供调用方记日志。</returns>
    public IReadOnlyList<(string Axis, string Reason)> Reset(IReadOnlyDictionary<string, string>? initial)
    {
        _values.Clear();
        foreach (var (axis, value) in AxesCatalog.Defaults())
            _values[axis] = value;

        var dropped = new List<(string, string)>();
        if (initial is null) return dropped;

        foreach (var (axis, value) in initial)
        {
            if (!AxesCatalog.IsKnown(axis))
            {
                dropped.Add((axis, "unknown-axis"));
                continue;
            }

            if (!AxesCatalog.TryParse(axis, value, out var canonical))
            {
                dropped.Add((axis, "invalid-value"));
                continue;
            }

            _values[axis] = canonical;
        }

        return dropped;
    }

    /// <summary>读一条轴的当前值；未知轴 ⇒ <see langword="null"/>。</summary>
    public string? Get(string? axis) =>
        axis is not null && _values.TryGetValue(axis, out var v) ? v : null;

    /// <summary>
    /// 写一条轴。⚠️ **先校验再写**：非法值不落盘、不改变状态，返回 <see langword="false"/> 让调用方
    /// 把 <see cref="AxesCatalog.LegalValuesText"/> 拼进错误信息——别在调用方各写一遍合法值清单。
    /// </summary>
    public bool TrySet(string? axis, string? value, out string canonical)
    {
        canonical = "";
        if (axis is null || value is null) return false;
        if (!AxesCatalog.TryParse(axis, value, out canonical)) return false;
        _values[axis] = canonical;
        return true;
    }

    /// <summary>
    /// 当前全部轴值的**副本**。⚠️ 返回新字典：调用方拿去进快照或落盘，
    /// 共享同一份会让"拍内不变"变成"随时可被写"。
    /// </summary>
    public Dictionary<string, string> Snapshot() => new(_values, StringComparer.Ordinal);

    /// <summary>补齐缺失的轴（旧 state.json、或某条轴是后加的）。返回补齐的轴名。</summary>
    public IReadOnlyList<string> CompleteFromDefaults()
    {
        var added = new List<string>();
        foreach (var (axis, value) in AxesCatalog.Defaults())
        {
            if (_values.ContainsKey(axis)) continue;
            _values[axis] = value;
            added.Add(axis);
        }
        return added;
    }
}
