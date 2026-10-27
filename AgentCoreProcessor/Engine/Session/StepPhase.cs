// AgentCoreProcessor/Engine/Session/StepPhase.cs
using AgentCoreProcessor.Core;

namespace AgentCoreProcessor.Engine.Session;

/// <summary>
/// 一次突发（= `step`）内的粗阶段。**只用于诊断**，不参与任何判定。
///
/// <para>⚠️ 粗档只有 5 个，而 `RunAsync` 的完整阶段表有 12 条
/// （`docs/modularity/module-io-spec.md` §5.3）。之所以不一次做全：
/// 细档必须与方法边界一起定，否则会出现"phase 有 12 个值，但代码里只有 3 个地方赋值"
/// 的假精度。B 阶段按 20 部件切文件时，每个部件落地自己的那段，phase 再跟着细化。</para>
/// </summary>
internal enum StepPhase
{
    /// <summary>没有活跃突发（等输入 / 已收尾）。</summary>
    Idle = 0,

    /// <summary>等待激活（权重累积、唤醒信号）。对应阶段 1。</summary>
    Waiting = 1,

    /// <summary>排空输入端口并构建本轮消息（含中途注入与游标推进）。对应阶段 3、5。</summary>
    Draining = 2,

    /// <summary>调模型（含压缩提示注入与影子快照）。对应阶段 4、6、7。</summary>
    Inferring = 3,

    /// <summary>执行工具并解释结果。对应阶段 8、9、10。</summary>
    ExecutingTools = 4,

    /// <summary>收尾判定：回等待 / 终结。对应阶段 11、12。</summary>
    Judging = 5,
}

/// <summary>
/// 抽取 <see cref="RuntimeSession.RunAsync"/> 的片段时，用来**保留控制流语义**的返回值（B-4）。
///
/// <para>⚠️ 为什么必须有它：主循环里有 6 处 <c>continue</c> / <c>break</c>。
/// 抽出来的方法若是 <c>void</c>，这些跳转要么被吞掉、要么只能靠 <c>goto</c> 补——
/// 前者会**悄悄改变行为**（编译能过、单测全绿），后者把可读性毁掉。</para>
///
/// <para>四个值刻意按**目的地**而不是按"哪个关键字"命名：
/// <c>Next</c> 与 <c>Restart</c> 都是 <c>continue</c>，但语义不同——前者是"同一突发里再跑一轮"，
/// 后者是"回等待、保活握 claim"。混成一个值就会丢掉 <c>awaitingInput</c> 的切换。</para>
/// </summary>
internal enum StepAction
{
    /// <summary>继续本迭代的下一段。</summary>
    Proceed = 0,

    /// <summary>结束本迭代、进入下一迭代（同一突发内的下一次模型调用）。</summary>
    Next = 1,

    /// <summary>回等待（<c>awaitingInput = true</c>）后进入下一迭代，会话保活并继续握 claim。</summary>
    Restart = 2,

    /// <summary>退出主循环（会话终结），走 <c>finally</c> 释放 claim。</summary>
    Stop = 3,
}

/// <summary>
/// 一批工具调用的判定结果：这次突发是否收尾、会话是否终结。
/// <para>⚠️ 两个标志**不是互斥的**：<c>endSession</c> 为真时必然终结，
/// 而 <c>endBurst</c> 只表示"这一轮话说完了，回去等人"。</para>
/// </summary>
internal readonly record struct ToolBatchOutcome(bool EndBurst, bool EndSession)
{
    public static readonly ToolBatchOutcome None = new(false, false);

    public ToolBatchOutcome Merge(ToolBatchOutcome other) =>
        new(EndBurst || other.EndBurst, EndSession || other.EndSession);

    public ToolBatchOutcome WithEndBurst(bool endBurst) => this with { EndBurst = endBurst };
}


/// <summary>
/// 一次推理的**前置产物**（B-4e）：本轮要用的核引用与调用参数。
/// <para>⚠️ 两者必须**成对**产出：`roundCore` 是"本轮锁定的核"（外置切换可能在本轮中途发生），
/// 而 `invokeRequest` 里带着同一刻的工具表。分开取会拿到两个不同时刻的快照。</para>
/// </summary>
internal readonly record struct RoundContext(AgentInvokeRequest Request, AgentCore Core);
