// AgentCoreProcessor/Engine/Context/ContextMutations.cs
using System.Collections.Generic;

namespace AgentCoreProcessor.Engine.Context;

/// <summary>
/// **上下文层对会话语义的变更意图**。
///
/// <para>⚠️ 它存在的唯一理由：上下文层原本会**直接改写工具表**（`_tools.Clear()` 只留 `compact`，
/// 并顺手改 `_agentCore.AdditionalTools`）。那是一条从 <c>context</c> 到 <c>builtin-tools</c> 的
/// **反向数据流**——拆模块之后它会变成一条看不见的回指，`context` 也会因此依赖工具层的内部集合。</para>
///
/// <para>改成"产出意图 + 由会话统一应用"之后：上下文层只说"我要锁工具表"，不碰任何工具集合；
/// 应用点只有一处（<c>RuntimeSession.ApplyContextMutation</c>），于是"谁能改工具表"是可枚举的。</para>
///
/// <para>⚠️ 与 <c>ActionIntent</c>（出到图外的意图）的区别：这里的意图**只改会话内部状态**，
/// 不出图、不发消息。两者是同一思想的两层，不要合并。</para>
/// </summary>
internal abstract record ContextMutation
{
    /// <summary>给日志/诊断用的名字（不含参数细节时用它）。</summary>
    public virtual string Kind => GetType().Name;
}

/// <summary>
/// 把工具表锁成"只剩一个工具"。L3 压缩触发时用：强制模型下一步只能调 <c>compact</c>。
/// </summary>
/// <param name="ToolName">要保留的工具名（当前恒为 <c>compact</c>）。</param>
internal sealed record LockToolTableTo(string ToolName) : ContextMutation
{
    public override string Kind => $"LockToolTableTo({ToolName})";
}

/// <summary>
/// 应用一批变更意图的结果——**只用于日志与断言**，不代表可以回滚。
/// </summary>
internal sealed record ContextMutationResult(
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Skipped)
{
    public static readonly ContextMutationResult Empty = new([], []);
}
