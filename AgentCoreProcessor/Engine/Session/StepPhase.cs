// AgentCoreProcessor/Engine/Session/StepPhase.cs
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
