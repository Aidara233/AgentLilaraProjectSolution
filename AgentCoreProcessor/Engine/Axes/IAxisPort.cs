// AgentCoreProcessor/Engine/Axes/IAxisPort.cs
using System;
using System.Collections.Generic;

namespace AgentCoreProcessor.Engine.Axes;

/// <summary>
/// `axes` 模块的对外端口 —— **其他模块只经它读轴，不直接引用 <see cref="AxesCatalog"/> 静态表**。
///
/// <para>为什么要这一层：轴表是**唯一真相**，但它是一个静态类。让 `context` / `step` / 控制面
/// 直接调静态方法，等于把"这四条边"藏进编译器里看不见的地方——拆模块时无从发现，也无法在
/// 单测里替换。有了端口，"谁在什么时候读轴"变成一条**可枚举**的边（`module.axes.*`）。</para>
///
/// <para>⚠️ **本端口是只读的**：写轴（`setAxis`）走 <see cref="AxisStore"/>，因为写要落会话状态、
/// 要校验、要能整批拒绝。别在这里加 `Set`——那会让"谁能改轴"变成一个没人管的问题。</para>
/// </summary>
internal interface IAxisPort
{
    /// <summary>轴名，按**表序**（人看的序）。</summary>
    IReadOnlyList<string> Names { get; }

    /// <summary>轴名，按 **ordinal**（提示词模板的序——逐机一致的字节，见 <see cref="AxesCatalog.RenderOrder"/>）。</summary>
    IReadOnlyList<string> RenderOrder { get; }

    /// <summary>该轴的合法取值；未知轴 ⇒ <see langword="null"/>。</summary>
    IReadOnlyList<string>? ValuesOf(string? axis);

    /// <summary>轴的中文名；未知轴原样返回，<see langword="null"/> 返回空串。</summary>
    string LabelOf(string? axis);

    /// <summary>这条轴的取值进不进上下文（未生效的轴一律 false，见 <see cref="AxesCatalog"/> 类注释）。</summary>
    bool InContext(string? axis);

    /// <summary>当前等级的提示解释；未知等级 / 不进上下文的轴 ⇒ <see langword="null"/>。</summary>
    string? DescriptionOf(string? axis, string? value);

    /// <summary>轴是否存在。</summary>
    bool IsKnown(string? axis);

    /// <summary>是不是数值范围轴。</summary>
    bool IsNumeric(string? axis);

    /// <summary>规范串 → 数值；不是数值轴或解析不出来 ⇒ <see langword="null"/>。</summary>
    double? NumberOrNull(string? axis, string? canonical);

    /// <summary>校验并规范化（轴名敏感、取值不敏感）。</summary>
    bool TryParse(string? axis, string value, out string canonical);

    /// <summary>一条轴的完整定义；未知轴 ⇒ <see langword="null"/>。</summary>
    AxisDefinition? DefinitionOf(string? axis);

    /// <summary>默认值（**每次新字典**，共享可变实例会串会话）。</summary>
    IReadOnlyDictionary<string, string> Defaults();

    /// <summary>
    /// 冻结一份快照。⚠️ **每拍调一次，拍内共用**；不要在各消费者里各调一次
    /// （那样"同一步内两次渲染逐字节相同"就没有保证）。
    /// </summary>
    AxisSnapshot Freeze(IReadOnlyDictionary<string, string> values, DateTimeOffset at);
}
