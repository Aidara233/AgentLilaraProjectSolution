using AgentCoreProcessor.Core;

namespace AgentCoreProcessor.Engine.Session;

/// <summary>
/// 一次推理的**前置产物**（B-4e）：本轮要用的核引用与调用参数。
/// <para>⚠️ 两者必须**成对**产出：`roundCore` 是"本轮锁定的核"（外置切换可能在本轮中途发生），
/// 而 `invokeRequest` 里带着同一刻的工具表。分开取会拿到两个不同时刻的快照。</para>
/// </summary>
internal readonly record struct RoundContext(AgentInvokeRequest Request, AgentCore Core);
