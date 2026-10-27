// AgentCoreProcessor/Engine/Axes/AxisDefinition.cs
using System;
using System.Collections.Generic;

namespace AgentCoreProcessor.Engine.Axes;

/// <summary>
/// 一条轴的完整定义——**只描述，不判定**。判定仍在 <see cref="Axes"/> 那张表里。
///
/// <para>它存在的理由：控制面（将来的 webui 独立进程）与插件轴注册都需要"把一条轴整个交出去"
/// 的形状，而 <see cref="Axes"/> 的静态方法是一个个取的。有了这个 record，
/// 交出去的是**不可变快照**，调用方拿不到进程级静态表的引用。</para>
///
/// <para>⚠️ <see cref="Values"/> 是只读视图；<see cref="NumericMin"/> 等三个字段非空
/// ⇒ 这是一条**数值范围**轴，此时 <see cref="Values"/> 必定是由范围生成的网格。</para>
/// </summary>
internal sealed record AxisDefinition(
    string Id,
    string Label,
    IReadOnlyList<string> Values,
    string DefaultValue,
    bool InContext,
    IReadOnlyDictionary<string, string>? LevelDescriptions = null,
    double? NumericMin = null,
    double? NumericMax = null,
    double? NumericStep = null)
{
    /// <summary>是不是数值范围轴（控制面据此画数字输入而不是下拉）。</summary>
    public bool IsNumeric => NumericMin is not null;
}

/// <summary>
/// 一次冻结的轴值快照。
///
/// <para>⚠️ **每拍冻结一次，拍内所有消费者共用同一份**（`context` 渲染尾块、请求参数、控制面预览）。
/// 这不是优化，是**正确性**：同一步内两次渲染必须逐字节相同，否则每步都破提示词前缀。
/// 随手现读会让"用户在半步中间改了值"变成「半新半旧」。</para>
///
/// <para>⚠️ <see cref="CapturedAt"/> 进快照而不是"调用时的时间"：它让"这份快照是哪一刻的"
/// 可被日志与对拍复用，而不是靠调用顺序猜。</para>
/// </summary>
internal sealed record AxisSnapshot(
    IReadOnlyDictionary<string, string> Values,
    DateTimeOffset CapturedAt)
{
    /// <summary>取一条轴的值；缺失返回 <see langword="null"/>（**不补默认值**——补了就看不出"没设过"）。</summary>
    public string? Get(string axis) => Values.TryGetValue(axis, out var v) ? v : null;

    /// <summary>数值轴的规范值；非数值轴或缺失 ⇒ <see langword="null"/>。</summary>
    public double? Number(string axis) => Axes.NumberOrNull(axis, Get(axis));
}
