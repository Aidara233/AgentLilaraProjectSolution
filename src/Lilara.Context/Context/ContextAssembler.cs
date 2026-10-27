// AgentCoreProcessor/Engine/Context/ContextAssembler.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AgentCoreProcessor.Engine.Axes;
using AgentCoreProcessor.Engine.Prompts;

namespace AgentCoreProcessor.Engine.Context;

/// <summary>
/// 上下文装配器 —— **四件事里最外面那一件：把各来源的贡献拼成 <see cref="PromptAssembly"/>**。
///
/// <para>⚠️ 它与另外三件事的分界（`module-io-spec.md` §1 D）：</para>
/// <list type="table">
///   <item><description><b>装配</b>（本类）：纯函数，输入是**已经取好的**文本与贡献，输出带 Order 的块。</description></item>
///   <item><description><b>贡献合并</b>：由 <see cref="PromptContributionSnapshot.Build"/> 负责（去重 + 排序 + 校验）。</description></item>
///   <item><description><b>结果回收</b>：<b>本项目不做</b>（已确认），后果见类尾注释。</description></item>
///   <item><description><b>层压缩</b>：由 <c>CompressionTierModule</c> 负责（L1/L2/L3），产出的是"提示文字"，
///     与本类只在一个地方相接：那段文字作为一条 Floating 贡献进来。</description></item>
/// </list>
///
/// <para>⚠️ **本类不读盘、不查库、不碰会话状态**。所有 IO 都在调用方（拍前的装载阶段，见决策 D5）。
/// 这不是洁癖：一旦这里能 await，`Solver` 的"拍内纯求解"就再也保证不了，
/// 而顺序契约（同一步逐字节相同）也就没有落脚点。</para>
///
/// <para>⚠️ <b>关于"结果回收不做"的后果，写在这里以免被忘记</b>：
/// 工具结果只增不减，压缩压力全部落到 L3 硬保底；长工具输出（一次 <c>dotnet build</c> 几百行）
/// 会一直占着上下文。缓解只能靠 L1/L2 提示催促模型自行收敛，而不是靠自动回收。</para>
/// </summary>
internal static class ContextAssembler
{
    /// <summary>一条"核心自产"的 System 贡献（稳定，进提示词前缀）。</summary>
    public static PromptContribution CoreSystem(string key, string text) => new()
    {
        PluginId = PromptContribution.CoreOwner,
        Key = key,
        Slot = PromptSlot.System,
        Text = text,
        Stable = true,
    };

    /// <summary>一条"核心自产"的 Primary 贡献（稳定）。</summary>
    public static PromptContribution CorePrimary(string key, string text) => new()
    {
        PluginId = PromptContribution.CoreOwner,
        Key = key,
        Slot = PromptSlot.Primary,
        Text = text,
        Stable = true,
    };

    /// <summary>一条"核心自产"的 Floating 贡献（不稳定，每步可变）。</summary>
    public static PromptContribution CoreFloating(string key, string text) => new()
    {
        PluginId = PromptContribution.CoreOwner,
        Key = key,
        Slot = PromptSlot.Floating,
        Text = text,
        Stable = false,
    };

    /// <summary>
    /// 轴表那一行：只含 <see cref="AxesCatalog.InContext"/> 的轴，按 <see cref="AxesCatalog.RenderOrder"/>（ordinal）排序。
    ///
    /// <para>⚠️ **只列进上下文的轴**：未接线的轴（`permission` / `network`）若出现在这里，
    /// 模型会读到"权限 off"并自行拒绝工具——那是假机制。</para>
    ///
    /// <para>⚠️ **用 RenderOrder 而不是 Names**：表序是给人看的（按语义分组），
    /// 模板要的是逐机一致的字节（见 <see cref="AxesCatalog.RenderOrder"/> 的注释）。</para>
    ///
    /// <para>没有可列的轴 ⇒ <see langword="null"/>（调用方据此不产出这一块，而不是产出一行空的）。</para>
    /// </summary>
    public static string? AxisTable(AxisSnapshot? axes)
    {
        if (axes is null) return null;

        var parts = new List<string>();
        foreach (var axis in AxesCatalog.RenderOrder)
        {
            if (!AxesCatalog.InContext(axis)) continue;

            // 取不到就不印这一行：轴值缺失是"老文件"那一格（AxisStore.Reset 会补齐），
            // 在这里编一个默认值会与 state.json 里那份分叉。
            if (axes.Get(axis) is not { } value) continue;

            parts.Add($"{AxesCatalog.LabelOf(axis)} {value}");
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>
    /// 轴带来的全部 Floating 贡献：一条轴表 + 每条轴一条等级解释。
    ///
    /// <para>⚠️ **解释只给当前选中的那一档**，不把其他档一起塞进去。ALC 的注释写得很清楚：
    /// "模型请求只携带当前选中等级的解释，位于浮动槽的轴表之后；不会同时塞入其他等级的说明"。
    /// 一次给全档会挤占每步都付全价的槽位，而且没有任何用处——模型不需要知道自己"本可以是别的"。</para>
    ///
    /// <para>⚠️ 字段键用 `axis-level.&lt;axis&gt;` 前缀（与 ALC 一致）：它是宿主保留键，
    /// 插件贡献不得占用。排序仍由 <see cref="PromptContribution.SortKey"/> 决定，所以
    /// `axes/axis-level.initiative` 会自然排在 `axes/axis-table` 之后。</para>
    /// </summary>
    public static IReadOnlyList<PromptContribution> AxisContributions(AxisSnapshot? axes)
    {
        var result = new List<PromptContribution>();
        if (axes is null) return result;

        if (AxisTable(axes) is { } table)
            result.Add(CoreFloating("axis-table", table));

        foreach (var axis in AxesCatalog.RenderOrder)
        {
            if (!AxesCatalog.InContext(axis)) continue;
            if (axes.Get(axis) is not { } value) continue;
            if (AxesCatalog.DescriptionOf(axis, value) is not { } explanation) continue;

            result.Add(CoreFloating(
                $"axis-level.{axis}",
                $"{AxesCatalog.LabelOf(axis)} ({axis}) {value}：{explanation}"));
        }

        return result;
    }

    /// <summary>
    /// 把一批贡献装配成 <see cref="PromptAssembly"/>。
    ///
    /// <para>⚠️ **校验失败不静默吞掉**：<paramref name="errors"/> 非空时，非法贡献已被剔除，
    /// 但调用方必须把它们报出去（注册期）或记日志（恢复期）。静默接受非法贡献，
    /// 就会在很久以后表现为"某天开始前缀天天破"。</para>
    /// </summary>
    public static PromptAssembly Assemble(
        IEnumerable<PromptContribution> contributions,
        IReadOnlyList<PromptBlock>? leading,
        out IReadOnlyList<string> errors)
    {
        var snapshot = PromptContributionSnapshot.Build(contributions, out errors);
        return PromptOrder.Assemble(snapshot, leading);
    }

    /// <summary>
    /// 便捷入口：轴贡献 + 额外贡献一起装配。额外的在前（数组顺序不影响结果，排序由 SortKey 决定）。
    /// </summary>
    public static PromptAssembly AssembleWithAxes(
        AxisSnapshot? axes,
        IEnumerable<PromptContribution>? extra,
        IReadOnlyList<PromptBlock>? leading,
        out IReadOnlyList<string> errors)
    {
        var all = new List<PromptContribution>();
        all.AddRange(AxisContributions(axes));
        if (extra is not null) all.AddRange(extra);

        return Assemble(all, leading, out errors);
    }

    /// <summary>
    /// 把装配结果渲染成"给人看的诊断串"——**不是发给模型的那一份**。
    /// 用于控制面/日志核对"这一步到底注入了什么"，以及测试里的可读断言。
    /// </summary>
    public static string Describe(PromptAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var sb = new StringBuilder();
        sb.Append("leading=").Append(assembly.Leading.Count);

        foreach (var slot in new[] { PromptSlot.System, PromptSlot.Primary, PromptSlot.Floating })
            sb.Append(" · ").Append(slot).Append('=').Append(assembly.BlocksOf(slot).Count);

        return sb.ToString();
    }
}
