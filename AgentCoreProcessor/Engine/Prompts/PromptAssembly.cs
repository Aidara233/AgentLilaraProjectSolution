// AgentCoreProcessor/Engine/Prompts/PromptAssembly.cs
using System;
using System.Collections.Generic;
using System.Linq;

namespace AgentCoreProcessor.Engine.Prompts;

/// <summary>
/// 装配结果里的一「块」。**顺序是一等契约**，所以 <see cref="Order"/> 是字段，不是列表下标。
///
/// <para>⚠️ 为什么要有 Order 而不能靠列表顺序：图的 `Link` 只表达"谁给谁数据"，
/// **不表达输出的排列顺序**。如果顺序只由节点求值顺序决定，那么优化器一调换两个节点，
/// 提示词字节就变了——而"同一步逐字节相同"是硬约束（缓存前缀）。把 Order 放进数据里，
/// 顺序就变成可断言、可测试、与执行顺序无关的东西。</para>
/// </summary>
internal sealed record PromptBlock(
    PromptSlot Slot,
    string Owner,
    string Key,
    string Text,
    bool Stable,
    int Order)
{
    /// <summary>块标识（渲染进正文头）。`Owner` = `core` 或 `plugin:<id>`。</summary>
    internal string Label => $"[{Owner}/{Key}]";
}

/// <summary>
/// 一次装配的**完整结果**：最前面的浮动对 + 三个槽位。
///
/// <para>⚠️ <see cref="Leading"/> 与 <see cref="Slots"/> 分开，是因为它们在协议上的位置不同：
/// <b>Leading 插在对话最前面</b>（ALC 的浮动上下文对：读数必须出现在第一条消息之前），
/// 而 <b>Slots 是系统/首要/尾三块</b>（进 system 消息与末尾那条 user 消息）。</para>
///
/// <para>⚠️ 合成一个列表再靠 `Slot` 区分**不行**：那样"最前面"这个位置就只能靠
/// `Order` 排序碰巧排对，而它是一条**协议位置**约束，不是排序偏好。</para>
/// </summary>
internal sealed record PromptAssembly(
    IReadOnlyList<PromptBlock> Leading,
    IReadOnlyList<PromptBlock> Slots)
{
    public static readonly PromptAssembly Empty = new([], []);

    /// <summary>
    /// 该槽位在 <see cref="Slots"/> 里的块，按 <see cref="PromptBlock.Order"/> 升序。
    /// 没有 ⇒ 空列表（**不返回 null**：调用方要能直接 `foreach`）。
    /// </summary>
    public IReadOnlyList<PromptBlock> BlocksOf(PromptSlot slot) =>
        Slots.Where(b => b.Slot == slot).OrderBy(b => b.Order).ToArray();

    /// <summary>
    /// 该槽位拼成的正文；没有块 ⇒ <see langword="null"/>（"没有块"与"空块"是两件事，
    /// 后者由调用方决定要不要造——见 ALC `SystemBlock.Wrap` 的「空的不能长得像有内容」）。
    /// </summary>
    public string? TextOf(PromptSlot slot)
    {
        var blocks = BlocksOf(slot);
        if (blocks.Count == 0) return null;

        var parts = new string[blocks.Count];
        for (var i = 0; i < blocks.Count; i++) parts[i] = $"{blocks[i].Label}\n{blocks[i].Text}";
        return string.Join("\n", parts);
    }

    /// <summary>
    /// 校验顺序契约：三个槽位各自 <see cref="PromptBlock.Order"/> 必须**严格递增且唯一**。
    ///
    /// <para>⚠️ 为什么"唯一"是硬要求：两块同一个 Order，排序结果就取决于 `OrderBy` 的稳定性，
    /// 而那是一个**实现细节**——今天稳定不代表明天稳定。同一份输入要出同一份字节，
    /// 就不能把顺序托付给它。</para>
    /// </summary>
    public IReadOnlyList<string> ValidateOrder()
    {
        var errors = new List<string>();

        foreach (var slot in new[] { PromptSlot.System, PromptSlot.Primary, PromptSlot.Floating })
        {
            var orders = Slots.Where(b => b.Slot == slot).Select(b => b.Order).ToArray();
            if (orders.Length == 0) continue;

            var seen = new HashSet<int>();
            foreach (var o in orders)
                if (!seen.Add(o))
                    errors.Add($"{slot} 槽位的 Order={o} 重复（同一序的两块，排序结果取决于实现细节）");
        }

        var leadingOrders = Leading.Select(b => b.Order).ToArray();
        if (leadingOrders.Length > 0)
        {
            var seen = new HashSet<int>();
            foreach (var o in leadingOrders)
                if (!seen.Add(o))
                    errors.Add($"Leading 的 Order={o} 重复");
        }

        return errors;
    }
}

/// <summary>
/// 顺序契约的**唯一分配器**：把「已排序的贡献列表」变成「带 Order 的块」。
///
/// <para>⚠️ 为什么需要它：Order 只能有**一个**产生者。让每个装配点各自从 0 数起，
/// 就会出现"两块都是 Order=0"——而 <see cref="PromptAssembly.ValidateOrder"/> 会拒掉它，
/// 于是错误在离现场很远的地方才爆出来。集中一处分配，现场就能保证不撞。</para>
/// </summary>
internal static class PromptOrder
{
    /// <summary>把一个槽位的贡献按 sortKey 排序后，从 <paramref name="start"/> 起连续编号。</summary>
    public static IReadOnlyList<PromptBlock> Assign(
        PromptSlot slot,
        IEnumerable<PromptContribution> contributions,
        int start = 0)
    {
        ArgumentNullException.ThrowIfNull(contributions);

        var ordered = contributions
            .Where(c => c.Slot == slot)
            .OrderBy(c => c.SortKey, StringComparer.Ordinal)
            .ToArray();

        var blocks = new PromptBlock[ordered.Length];
        for (var i = 0; i < ordered.Length; i++)
        {
            var c = ordered[i];
            blocks[i] = new PromptBlock(
                Slot: slot,
                Owner: string.Equals(c.PluginId, PromptContribution.CoreOwner, StringComparison.Ordinal)
                    ? PromptContribution.CoreOwner
                    : $"plugin:{c.PluginId}",
                Key: c.Key,
                Text: c.Text,
                Stable: c.Stable,
                Order: start + i);
        }
        return blocks;
    }

    /// <summary>
    /// 按 System → Primary → Floating 的顺序**一次性**装配三个槽位。
    /// <para>⚠️ 这个顺序（System → Primary → Floating）是**协议顺序**，不是渲染顺序偏好：
    /// 系统块进 system 消息、首要块进它的开头、浮动块进末尾那条 user 消息。</para>
    /// </summary>
    public static PromptAssembly Assemble(
        PromptContributionSnapshot snapshot,
        IReadOnlyList<PromptBlock>? leading = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var slots = new List<PromptBlock>();
        slots.AddRange(PromptOrder.Assign(PromptSlot.System, snapshot.System));
        slots.AddRange(PromptOrder.Assign(PromptSlot.Primary, snapshot.Primary));
        slots.AddRange(PromptOrder.Assign(PromptSlot.Floating, snapshot.Floating));

        return new PromptAssembly(
            leading ?? Array.Empty<PromptBlock>(),
            slots);
    }
}
