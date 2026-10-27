// AgentCoreProcessor/Core/AgentInvokeRequest.cs
using System.Collections.Generic;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Core;

/// <summary>
/// 一次模型调用所需的**本轮参数**。
///
/// <para>⚠️ 它存在的理由（H2）：推进器原来每迭代直写 <c>AgentCore.EngineType</c> 与
/// <c>AgentCore.AdditionalTools</c>——那是"改另一个对象的字段"，有两个后果：</para>
/// <list type="bullet">
///   <item>参数的生命周期与对象绑定，**调用结束后仍然生效**（下一次调用看到的是上一轮的值）；</item>
///   <item>并发/重入时两轮会互相覆盖，而覆盖是静默的（没有编译期提示）。</item>
/// </list>
///
/// <para>改成入参后：**参数只对这一次调用有效**，调用结束即消失。
/// 字段保留为回落值，只为兼容不经 <c>InvokeAsync</c> 的既有路径（如 <c>Agent.ChatAsync</c>）。</para>
///
/// <para>⚠️ <see cref="AdditionalTools"/> 传 <see langword="null"/> 表示"本轮没有额外工具"，
/// **不是**"沿用字段里的旧值"——这个区分靠 <c>request is null</c> 在调用侧判定，
/// 因为"整个请求为 null"（沿用字段）与"请求里这一项为 null"（本轮无额外工具）是两件事。</para>
/// </summary>
internal sealed record AgentInvokeRequest(
    string? EngineType = null,
    IReadOnlyList<ITool>? AdditionalTools = null);
