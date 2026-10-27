// AgentCoreProcessor/Engine/Axes/Axes.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AgentCoreProcessor.Engine.Axes;

/// <summary>
/// 行为轴（`模块 I/O 规约` 的 `axes` 模块）——**轴的唯一真相来源**。
///
/// <para>⚠️ 它是一张**表**，不是几条散落的判断：轴名、中文名、合法值、默认值、进不进上下文、
/// 等级解释**放在一起**（见 <see cref="Spec"/>），因为它们必须同步演进——加一档而忘了改默认值
/// 或错误信息，正是这类表最常见的漂移。</para>
///
/// <para>⚠️ **本表由 AgentLilaraCode 的 `Kernel/Axes.cs` 裁剪而来，不是照抄**。裁剪理由：
/// 本项目<b>没有 plan 工具、没有计划树</b>（`Tool/Core/` 无 PlanTool），所以 `mode`（plan/act）
/// 不进表——迁进来就是一个永远只有 `act` 一档的轴；`network` 从四档压到两档，因为
/// `search` / `fetch` 的区分在本项目没有对应物（联网能力完全由会话类型的工具白名单承担）。</para>
///
/// <para>⚠️ **`permission` 与 `network` 目前 `InContext = false`、且没有机械效果**。
/// 这两条轴与本项目既有机制**重叠**：工具可见性由 `SessionTypeProfiles.IsAllowed(type, tool)`
/// 的会话类型白名单判定（`ToolExecutor.PermissionGroup` = 会话类型）。按已定决策 P2，
/// 先把轴表骨架立起来，等给约 150 个工具逐个打上权限类别标签之后，再把它们接进闸门。</para>
///
/// <para>⚠️ **未生效的轴绝不能进上下文**：`InContext = true` 会让模型读到"权限 off"这类描述，
/// 于是它自己把工具调用拒掉——那是**假机制**，比没有更糟。所以 `permission` / `network`
/// 在接线完成前一律 `InContext = false`；等闸门真接上了再翻成 `true`。</para>
///
/// <para>⚠️ **`effort` / `temperature` 是纯请求参数**（`InContext = false`）：
/// "给控制面看 ≠ 给模型看"，把它们塞进提示词才是错的（那是全系统每步都付全价的槽位）。</para>
///
/// <para>⚠️ **`temperature` 的默认值是 `0.7`，不是上游那个 `1.0`**。本项目
/// `Client/ApiClientCfg.Temperature` 的既有默认就是 `0.7`；若这里写 `1.0`，
/// "轴默认值"与"实际发出去的值"立刻分叉，A1 的"零行为变化"验收当场失效。</para>
/// </summary>
internal static class Axes
{
    public const string Initiative = "initiative";
    public const string Permission = "permission";
    public const string Safety = "safety";
    public const string Verify = "verify";
    public const string Network = "network";
    public const string Effort = "effort";
    public const string Temperature = "temperature";

    /// <summary>
    /// 一条**数值范围**轴的形状。<see cref="Values"/> 由它派生（<see cref="Grid"/>），不手抄。
    /// <see cref="Step"/> 同时是**合法性的判据**：落在网格之间的值（如 0.75）会被拒，
    /// 而不是悄悄吸到最近的档。
    /// </summary>
    public sealed record NumericRange(double Min, double Max, double Step);

    /// <summary>
    /// 轴的**全部**静态事实，按渲染顺序排。
    /// ⚠️ **单一张表**：轴名 / 中文名 / 合法值 / 默认值 / 进不进上下文 / 等级解释都从这里读。
    /// 拆成几个平行容器就会漂——`Names` 有序而 `Dictionary` 枚举序未定义。
    ///
    /// <para>⚠️ <see cref="Values"/> 的类型是 <see cref="IReadOnlyList{T}"/> 而**不是** `string[]`，
    /// 且每个值都被 <see cref="Array.AsReadOnly{T}"/> 包过一层：交出去的是**只读视图**，
    /// 调用方拿不到可写数组——否则改一处就等于改了**进程级静态表**，且**不报错**。</para>
    /// </summary>
    private sealed record Spec(
        string Name, string Label, IReadOnlyList<string> Values, string Default, bool InContext,
        NumericRange? Range = null, IReadOnlyDictionary<string, string>? Descriptions = null);

    /// <summary>温度的范围与步长。⚠️ **唯一一份**——`Values` 网格由它生成。</summary>
    private static readonly NumericRange TempRange = new(Min: 0, Max: 2, Step: 0.1);

    /// <summary>
    /// 数值范围轴的**值网格**。⚠️ 它是 `Values` 的唯一来源——手抄 21 个值就是把"范围"
    /// 复制成两份，改范围时必漏一处。
    ///
    /// <para>⚠️ **这条设计是为了保住 `ValuesOf` 的 null 契约**：若数值轴返回 `null`，
    /// 那么"未知轴"与"非枚举轴"两种 null 就混成一个，而调用方 `ValuesOf(axis)!` 会当场 NRE。</para>
    /// </summary>
    private static IReadOnlyList<string> Grid(NumericRange r)
    {
        var count = (int)Math.Round((r.Max - r.Min) / r.Step);
        var values = new string[count + 1];
        for (var i = 0; i <= count; i++) values[i] = Format(r.Min + i * r.Step, r.Step);
        return Array.AsReadOnly(values);
    }

    /// <summary>
    /// 数值 → 规范串。**唯一**的规范化拼法（`TryParse` 的数值分支与 <see cref="Grid"/> 共用），
    /// 所以「网格里的值」与「用户敲进来的值」规范化之后**逐字相同**。
    ///
    /// <para>⚠️ 小数位数**由步长追出来**（步长 0.1 ⇒ 一位小数），不是写死的 `"0.##"`：
    /// 后者会让 `1.0` 印成 `"1"`，于是网格看起来是 `0.9, 1, 1.1`——不一致。</para>
    ///
    /// <para>⚠️ **InvariantCulture 是硬要求**：区域设置会把小数点写成逗号，
    /// 于是 `state.json` 里出现 `"0,7"`，而它再也读不回来。</para>
    /// </summary>
    private static string Format(double v, double step)
    {
        var decimals = 0;
        for (var s = step; s < 1 - 1e-9 && decimals < 6; s *= 10) decimals++;
        return v.ToString("F" + decimals, CultureInfo.InvariantCulture);
    }

    /// <summary>数值范围轴的一行。⚠️ `Values` 从这里派生——别绕过它手写一行。</summary>
    private static Spec Numeric(string name, string label, NumericRange range, string @default) =>
        new(name, label, Grid(range), @default, InContext: false, Range: range);

    /// <summary>可见枚举轴：合法值与等级解释由同一组声明派生，避免另维护一份档位表。</summary>
    private static Spec Enum(string name, string label, string @default, bool inContext,
        params (string Value, string Description)[] levels) =>
        new(name, label, Array.AsReadOnly(levels.Select(l => l.Value).ToArray()), @default,
            InContext: inContext,
            Descriptions: new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
                levels.ToDictionary(l => l.Value, l => l.Description, StringComparer.Ordinal)));

    /// <summary>
    /// 轴的**唯一**一张表。顺序**就是**控制面的渲染顺序，别按字母排
    /// （提示词模板用的是 <see cref="RenderOrder"/>，那是另一件事）。
    /// </summary>
    private static readonly IReadOnlyList<Spec> Table =
    [
        // 主动性 = 停不停。⚠️ 本项目**没有**目标模式 / 计划树 / finish_goal，
        // 所以上游那四档里只有两档有对应物；默认取 persistent = 现有实际行为
        // （无工具调用时由 :1279 注入「继续工作」提醒），保证 A1 零行为变化。
        Enum(Initiative, "主动性", "persistent", inContext: true,
            ("task", "完成当前任务即停：给出 end_session 或 wait 就是正常终点，不要为继续而继续。"),
            ("persistent", "任务范围内持续推进：没有后续可执行动作之前不要停，直到明确 end_session 或 wait。")),

        // 权限 = 闸门。⚠️ **A1 阶段未接线、且不进上下文**（见类注释）：现有的可见性由
        // SessionTypeProfiles.IsAllowed(会话类型, 工具名) 决定。档位顺序是契约：
        // 低档是拒绝线、高档是询问线，重排会让将来的告警判错档位。
        Enum(Permission, "权限", "standard", inContext: false,
            ("off", "所有工具调用均被拒绝；只能依据已有上下文回答。"),
            ("readonly", "只读类工具可执行；消耗资源、写入和高危类工具均被拒绝。"),
            ("investigate", "只读和消耗资源类工具可执行；写入和高危类工具被拒绝。"),
            ("standard", "只读和消耗资源类工具可执行；写入类工具需要审批，高危类工具被拒绝。"),
            ("authorized", "只读、消耗资源和写入类工具可执行；高危类工具需要审批。"),
            ("trusted", "权限闸门允许所有工具类别，包括不可逆的高危操作；仍须遵守用户任务范围与明确约束。")),

        // 安全 = 纯提示。⚠️ 没有机械兜底——不设「生产 ⇒ 权限封顶」。
        // 默认落在非告警档：一个默认就自称生产环境的提醒装置，会在真项目上变成噪音、被学会无视。
        Enum(Safety, "安全", "dev", inContext: true,
            ("dev", "当前为开发环境，可在任务范围内试验和迭代；注意保留已有工作，避免无关改动。"),
            ("staging", "当前为迭代或预发布环境，关注兼容性、集成影响与回退方式，避免影响其他人的验证。"),
            ("prod", "当前为生产环境，优先最小改动、数据完整性与可回退性；执行前评估影响范围，对不可逆操作格外谨慎。")),

        Enum(Verify, "验证强度", "self", inContext: true,
            ("none", "不主动运行额外验证；完成改动后说明结果，并明确未执行验证。"),
            ("self", "完成改动后自行执行相关编译、测试或检查，并如实报告结果及未验证部分。"),
            ("strict", "执行任务所需的完整验证；验证未通过时不得声称完成，受阻无法验证时明确报告阻塞和未验证部分。")),

        // 联网 = 能力开关。⚠️ **A1 阶段未接线、且不进上下文**：本项目的联网能力完全由
        // 会话类型白名单决定（web_search / network 类工具是否在这个 type 的 Tools 里）。
        // 默认 off 是保守选择：真接线之前不该出现"能出网"的暗示。
        Enum(Network, "联网", "off", inContext: false,
            ("off", "不联网：搜索与抓取类工具均不可用，模型只能依据已有上下文回答。"),
            ("all", "允许搜索与抓取；仍受权限轴（消耗型工具）与会话类型白名单的约束。")),

        // 思考强度。⚠️ InContext = false（纯请求参数，模型不必被告知自己的推理预算）。
        Enum(Effort, "思考强度", "high", inContext: false,
            ("low", "低推理预算：优先速度与成本，适合明确、机械的任务。"),
            ("medium", "中等推理预算：常规任务的默认档。"),
            ("high", "较高推理预算：适合需要多步推理与工具编排的任务。"),
            ("xhigh", "很高推理预算：适合长链条分析与复杂重构。"),
            ("max", "最高推理预算：代价最高，只在确实需要时使用。")),

        // 温度。⚠️ 默认 0.7 = 本项目 ApiClientCfg.Temperature 的既有默认，
        // **不是**上游那个 1.0（那个默认在本项目会把既有会话的采样行为改掉）。
        Numeric(Temperature, "温度", TempRange, @default: "0.7"),
    ];

    /// <summary>按轴名找那一行。<see langword="null"/> 与未知轴都返回 <see langword="null"/>。</summary>
    private static Spec? Find(string? axis) =>
        axis is null ? null : Table.FirstOrDefault(s => s.Name == axis);

    /// <summary>
    /// 轴名，**按表序**。
    /// ⚠️ **从 <see cref="Table"/> 派生**，不是另写一份名单——另写就有第二个地方要同步，
    /// 而顺序不稳等于每步重排。
    ///
    /// <para>⚠️ <b>它是<b>人</b>看的序</b>（控制面照它铺面板）。
    /// <b>提示词模板用的不是它</b>，见 <see cref="RenderOrder"/>：那两处的读者不同，序的理由也不同。</para>
    /// </summary>
    public static readonly IReadOnlyList<string> Names = Table.Select(s => s.Name).ToList();

    /// <summary>
    /// 轴名，**按 ordinal 升序**——<b>提示词模板</b>的序。
    ///
    /// <para>⚠️ <b>为什么模板那一格不直接用 <see cref="Names"/></b>：表序是**人**排的（按语义分组），
    /// 而模板要的是「同一份数据、任何机器、任何区域设置 ⇒ 同样的字节」。把「模板的序」挂在
    /// 「人怎么排那张表」上，等于让下一次给表分组的人**顺手改掉前缀**。</para>
    ///
    /// <para>⚠️ <b>ordinal，不是文化序</b>：默认的 <c>OrderBy(x =&gt; x)</c> 走 ICU 文化序，
    /// <c>i</c> 与 <c>I</c> 在 <c>en-US</c> 与 <c>tr-TR</c> 下**序不同** ⇒ 同一份数据、两台机器、
    /// 两个顺序，而且是**静默破前缀**。</para>
    /// </summary>
    public static readonly IReadOnlyList<string> RenderOrder =
        [.. Table.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal)];

    /// <summary>
    /// 轴的中文名。
    /// <para>⚠️ **未知轴原样返回**（与 <see cref="ValuesOf"/> 的「未知 → null」同一套约定）；
    /// **`null` 返回空串**——`null` 是「轴名没给」，没有半截标签可回吐。</para>
    /// </summary>
    public static string LabelOf(string? axis) => Find(axis)?.Label ?? (axis ?? "");

    /// <summary>
    /// 这条轴的取值**进不进上下文**。
    /// <para>⚠️ **未知轴与 `null` 都返回 `false`**：本方法的唯一用途是判「要不要把它写进提示词」，
    /// 而一条**叫不出名字**的轴没有可写进上下文的合法取值——宁可**漏写**，不可**错写**。</para>
    /// </summary>
    public static bool InContext(string? axis) => Find(axis)?.InContext ?? false;

    /// <summary>当前等级的提示解释；未知等级和纯请求参数轴没有解释。</summary>
    public static string? DescriptionOf(string? axis, string? value) =>
        Find(axis)?.Descriptions is { } descriptions && value is not null &&
        TryParse(axis, value, out var canonical) ? descriptions.GetValueOrDefault(canonical) : null;

    /// <summary>
    /// ⚠️ **所有吃轴名的入口一律把 <see langword="null"/> 当「未知轴」**（返 false / null / 空串），
    /// 所以形参标成 `string?`——这不是防御性编程，是**可达性**在契约上的体现：
    /// 线上的 `{"axis":null}` 能一路落到这里，而 `Dictionary` 的 key 不允许为 null。
    /// </summary>
    public static bool IsKnown(string? axis) => Find(axis) is not null;

    /// <summary>
    /// 该轴的合法取值。<see langword="null"/> = **未知轴**（这条契约一个字没动）。
    /// 返回的是**只读视图**，改不动。
    /// <para>⚠️ **数值范围轴也走这里**，交出的是一张由范围生成的网格——它**不是** null。</para>
    /// </summary>
    public static IReadOnlyList<string>? ValuesOf(string? axis) => Find(axis)?.Values;

    /// <summary>
    /// 该轴是不是**数值范围**轴，是就给出范围，否则 <see langword="null"/>。
    /// <para>⚠️ **注意这里 null 的含义与 <see cref="ValuesOf"/> 不同**：那个 null 表示「**未知轴**」，
    /// 这个 null 表示「**已知，但不是数值轴**」。所以判「轴存不存在」要用 <see cref="IsKnown"/>。</para>
    /// </summary>
    public static NumericRange? RangeOf(string? axis) => Find(axis)?.Range;

    /// <summary>该轴的值**是不是数值**（控制面据此画数字输入而不是下拉）。未知轴返回 false。</summary>
    public static bool IsNumeric(string? axis) => Find(axis)?.Range is not null;

    /// <summary>合法值清单（错误信息用）。⚠️ 从表里拼，不手抄。</summary>
    public static string LegalValuesText(string? axis) => Find(axis) switch
    {
        null => "",
        // 数值轴**不能**把整张网格用 `|` 拼出来——21 个数印进一句错误信息就没法读了。
        // 给范围与步长（那才是用户要知道的东西）。
        { Range: { } r } => $"{r.Min}~{r.Max} 之间的数值（步长 {r.Step}）",
        { Values: var v } => string.Join("|", v),
    };

    /// <summary>
    /// 轴上的规范串 → <see cref="double"/>。不是数值轴 / 解析不出来 ⇒ <see langword="null"/>。
    /// <para>⚠️ 这是「轴 → 请求」那条路上**唯一**的转换点。别在构造器里各写一遍 `double.Parse`：
    /// 那样区域设置、越界判据、失败处理会各漂各的，而这三个恰恰是最容易悄悄出错的地方。</para>
    /// </summary>
    public static double? NumberOrNull(string? axis, string? canonical)
    {
        if (Find(axis)?.Range is not { } r || canonical is null) return null;

        return double.TryParse(canonical, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            && d >= r.Min && d <= r.Max
            ? d
            : null;
    }

    /// <summary>
    /// 校验并规范化（`PROD` → `prod`；`0.70` → `0.7`）。
    /// <para>⚠️ **两半的大小写策略是刻意不同的，别"统一"成一个：**</para>
    /// <list type="bullet">
    ///   <item><b>axis 敏感</b>（<see cref="Find"/> 用 `==`，即 `Ordinal`）——
    ///         轴名是协议标识符，`Safety` 不是 `safety`，应当**拒绝**而不是猜。</item>
    ///   <item><b>value 不敏感</b>（<see cref="StringComparison.OrdinalIgnoreCase"/>）——
    ///         取值是用户手打的，写 `Prod` 是常态。</item>
    /// </list>
    /// </summary>
    public static bool TryParse(string? axis, string value, out string canonical)
    {
        canonical = "";
        if (Find(axis) is not { } spec) return false;   // ⚠️ null 键会让 Dictionary 抛，见 IsKnown 的注释

        if (spec.Range is { } range) return TryParseNumeric(range, value, out canonical);

        foreach (var v in spec.Values)
        {
            if (!string.Equals(v, value, StringComparison.OrdinalIgnoreCase)) continue;
            canonical = v;
            return true;
        }
        return false;
    }

    /// <summary>数值分支：解析 → 查区间 → 复验「本来就在网格上」→ 规范化。</summary>
    private static bool TryParseNumeric(NumericRange r, string value, out string canonical)
    {
        canonical = "";

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return false;

        // ⚠️ **必须显式挡 NaN / ±Infinity**：`double.TryParse("NaN")` 是**成功**的，
        // 而接下来每一句与 NaN 的比较都是 `false` ⇒ 区间检查与网格检查会双双放行，
        // 于是 "NaN" 被当成合法值存进 state.json、再原样发去 API。
        if (!double.IsFinite(d)) return false;

        if (d < r.Min || d > r.Max) return false;

        // ⚠️ **要求「本来就在网格上」，不是「吸附过去」**：0.75（步长 0.1）必须**被拒**，
        // 不能悄悄存成 0.8——那是另一种行为，而用户不会知道。
        // 容差 1e-9 只用来吸收浮点噪声（7 * 0.1 是 0.7000000000000001），比半个步长小得多。
        var steps = Math.Round((d - r.Min) / r.Step);
        var snapped = r.Min + steps * r.Step;
        if (Math.Abs(snapped - d) > 1e-9) return false;

        canonical = Format(snapped, r.Step);
        return true;
    }

    /// <summary>
    /// 默认值。**每次返回新字典**：调用方要挂到会话上，共享可变实例会串会话。
    /// </summary>
    public static IReadOnlyDictionary<string, string> Defaults() =>
        Table.ToDictionary(s => s.Name, s => s.Default, StringComparer.Ordinal);

    /// <summary>
    /// 一条轴的完整定义（控制面 / 将来的插件轴注册用）。未知轴 ⇒ <see langword="null"/>。
    /// </summary>
    public static AxisDefinition? DefinitionOf(string? axis)
    {
        if (Find(axis) is not { } spec) return null;
        return new AxisDefinition(
            spec.Name, spec.Label, spec.Values, spec.Default, spec.InContext,
            spec.Descriptions, spec.Range?.Min, spec.Range?.Max, spec.Range?.Step);
    }
}
