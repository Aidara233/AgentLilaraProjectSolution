// AgentCoreProcessor/Engine/Prompts/PromptContribution.cs
using System;
using System.Collections.Generic;
using System.Linq;

namespace AgentCoreProcessor.Engine.Prompts;

/// <summary>
/// 提示词槽位。**三块，判据各不相同**（`module-io-spec.md` §5.1 / ALC `PromptSlot`）：
///
/// <list type="table">
///   <item><description><b>System</b>：核心提供的恒定单文件。文件不在 ⇒ 回落内置模板。</description></item>
///   <item><description><b>Primary</b>：用户 / 项目 / 插件。**没有兜底内容** ⇒ 文件不在 = 无块。</description></item>
///   <item><description><b>Floating</b>：核心每步现算（轴表 + 插件浮动字段）。它不是文件，</description></item>
/// </list>
///
/// <para>⚠️ **不要把三块合成一个字符串**：它们的失效半径、来源、「空」的含义没有一条相同。
/// 合起来之后，"这一块为什么没有"就再也查不出来了。</para>
/// </summary>
internal enum PromptSlot
{
    System = 0,
    Primary = 1,
    Floating = 2,
}

/// <summary>
/// 一个结构化提示词字段。**插件只贡献字段，宿主负责排序和拼接**，避免自由文本互相覆盖。
///
/// <para>⚠️ <see cref="Stable"/> 是**稳定前缀**的声明：</para>
/// <list type="bullet">
///   <item><b>System / Primary 必须 `Stable = true`</b>——它们进的是提示词前缀，一变就整段失效，缓存全废。</item>
///   <item><b>Floating 必须 `Stable = false`</b>——它每步都可能变（当前轴值、计划树）。</item>
/// </list>
/// <para>这两条不是建议，是**注册期硬约束**（见 <see cref="Validate"/>）。写反了不会有运行期报错，
/// 只会在某一天突然发现"前缀每天都在破"——所以必须在进集合的那一刻就拒掉。</para>
///
/// <para>⚠️ **`Stable` 是作者契约，宿主不验证正文真的没变**（决策 D8，与 ALC 一致）。
/// 要随档位变化的内容一律放 Floating；放错了，宿主帮不了你。</para>
/// </summary>
internal sealed record PromptContribution
{
    /// <summary>
    /// 核心自己的贡献用的 `PluginId`。**不是特殊值判断，而是"核心也是一个贡献者"**：
    /// 让核心走与插件完全相同的校验/排序/去重路径，才不会出现"核心能写、插件不能"的第二套规则。
    /// </summary>
    public const string CoreOwner = "core";

    public required string PluginId { get; init; }
    public required string Key { get; init; }
    public required PromptSlot Slot { get; init; }
    public required string Text { get; init; }
    public bool Stable { get; init; }

    /// <summary>
    /// 合并序：`(pluginId, key)` 的 **ordinal** 串接。
    /// <para>⚠️ 用 **ordinal**，不是文化序——文化序会让同一份数据在两台机器上排出两个顺序
    /// （`i` 与 `I` 在 `en-US` / `tr-TR` 下序不同），而那是**静默破前缀**。</para>
    /// </summary>
    internal string SortKey => $"{PluginId}\u0000{Key}";

    /// <summary>字段标识（渲染进正文头，`[pluginId/key]`）。不允许跨插件伪造归属。</summary>
    internal string Label => $"[{PluginId}/{Key}]";

    /// <summary>
    /// 校验这一条贡献是否合法。返回错误说明，合法则 <see langword="null"/>。
    /// <para>⚠️ 单独抽出来而不是塞进构造：调用方需要"把错的挑出来报成一组"，
    /// 而不是"遇到第一条就抛掉整批"。</para>
    /// </summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(PluginId)) return "pluginId 不能为空";
        if (string.IsNullOrWhiteSpace(Key)) return "key 不能为空";
        if (Text is null) return "text 不能为 null";

        if (Slot is PromptSlot.System or PromptSlot.Primary && !Stable)
            return $"{Slot} 槽位的字段必须 Stable=true（它进提示词前缀，一变就整段失效）";

        if (Slot == PromptSlot.Floating && Stable)
            return "Floating 槽位的字段必须 Stable=false（它每步都可能变，声明成稳定会破前缀）";

        return null;
    }
}

/// <summary>
/// 一次装配用的**冻结快照**：三个槽位各自的贡献列表。
///
/// <para>⚠️ **每拍取一次并冻住，不是每次渲染时取**——渲染必须是纯函数，
/// 同一步内重复渲染要逐字节相同（ALC 的 `SystemBlock.Render` 那条铁律）。
/// 随手现取的话，用户在半步中间改了插件贡献就会「半新半旧 ⇒ 每步都破前缀」。</para>
///
/// <para>⚠️ **两个空列表用空集合表达「没有」，不用 `null`**：「没有块」与「不知道有没有块」
/// 是两件事，而后者在这个类型里不存在。</para>
/// </summary>
internal sealed record PromptContributionSnapshot(
    IReadOnlyList<PromptContribution> System,
    IReadOnlyList<PromptContribution> Primary,
    IReadOnlyList<PromptContribution> Floating)
{
    public static readonly PromptContributionSnapshot Empty = new([], [], []);

    /// <summary>取某个槽位的合并正文；该槽位没有贡献 ⇒ <see langword="null"/>。</summary>
    public string? Text(PromptSlot slot) => Join(For(slot));

    /// <summary>取某个槽位的贡献列表（已按 <see cref="PromptContribution.SortKey"/> 排好）。</summary>
    public IReadOnlyList<PromptContribution> For(PromptSlot slot) => slot switch
    {
        PromptSlot.System => System,
        PromptSlot.Primary => Primary,
        PromptSlot.Floating => Floating,
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "未知槽位"),
    };

    /// <summary>
    /// 合并一个槽位的贡献。
    ///
    /// <para>⚠️ **排序是契约的一部分**：按 <see cref="PromptContribution.SortKey"/> 的 ordinal 升序。
    /// 不排序而依赖列表顺序，等于让"谁先注册"决定提示词字节——那在插件加载顺序变化时会静默漂。</para>
    ///
    /// <para>⚠️ 每个字段前面带 `[pluginId/key]`：正文是自然语言，标识是**归属证据**。
    /// 去掉它，两个插件写同一件事时就无法判断是谁写的。</para>
    /// </summary>
    private static string? Join(IReadOnlyList<PromptContribution> fields)
    {
        if (fields.Count == 0) return null;

        var ordered = fields
            .OrderBy(f => f.SortKey, StringComparer.Ordinal)
            .ToArray();

        if (ordered.Length == 0) return null;

        var parts = new string[ordered.Length];
        for (var i = 0; i < ordered.Length; i++)
            parts[i] = $"{ordered[i].Label}\n{ordered[i].Text}";

        return string.Join("\n", parts);
    }

    /// <summary>
    /// 从一批未经排序的贡献构建快照：**先全量校验，再分组排序**。
    /// <para>返回 <paramref name="errors"/> 而不是抛：一批里可能有多条问题，
    /// 调用方（插件注册）需要把它们一次全报出来。</para>
    /// </summary>
    public static PromptContributionSnapshot Build(
        IEnumerable<PromptContribution> contributions,
        out IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(contributions);

        var errorList = new List<string>();
        var system = new List<PromptContribution>();
        var primary = new List<PromptContribution>();
        var floating = new List<PromptContribution>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var c in contributions)
        {
            if (c is null)
            {
                errorList.Add("贡献为 null");
                continue;
            }

            if (c.Validate() is { } problem)
            {
                errorList.Add($"{c.PluginId}/{c.Key}: {problem}");
                continue;
            }

            // 字段身份 = (slot, pluginId, key)。同一身份重复即拒绝——两段正文抢同一个格子，
            // 谁赢都说不清，而"说不清"正是这类表最难查的形态。
            if (!seen.Add($"{(int)c.Slot}\u0000{c.SortKey}"))
            {
                errorList.Add($"{c.PluginId}/{c.Key}: 槽位 {c.Slot} 上的字段重复");
                continue;
            }

            switch (c.Slot)
            {
                case PromptSlot.System: system.Add(c); break;
                case PromptSlot.Primary: primary.Add(c); break;
                case PromptSlot.Floating: floating.Add(c); break;
            }
        }

        errors = errorList;

        static IReadOnlyList<PromptContribution> Freeze(List<PromptContribution> list) =>
            list.OrderBy(f => f.SortKey, StringComparer.Ordinal).ToArray();

        return new PromptContributionSnapshot(Freeze(system), Freeze(primary), Freeze(floating));
    }
}
