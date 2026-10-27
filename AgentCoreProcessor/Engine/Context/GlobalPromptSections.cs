// AgentCoreProcessor/Engine/Context/GlobalPromptSections.cs
using System;
using System.Collections.Generic;
using AgentCoreProcessor.Component;
using AgentCoreProcessor.Logging;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Engine.Context;

/// <summary>
/// 从 **Global 组件**（单例）取提示词段。原来这两个方法是 <c>RuntimeSession</c> 的私有方法，
/// 它们做的事与"会话"无关，只与"上下文装配"有关，所以搬到上下文域。
///
/// <para>⚠️ **两者产物形态不同，不能合并**：</para>
/// <list type="table">
///   <item><description><b>每轮段</b>（<see cref="CollectPerRound"/>）→ 拼进 <c>[组件通知]</c> 用户消息，
///     与 Loop 组件的段合并。</description></item>
///   <item><description><b>会话开头参考段</b>（<see cref="CollectStartReference"/>）→ **独立一条用户消息**，
///     且**只在首轮**注入；恢复会话跳过（内容已随首轮持久化在存档历史里）。</description></item>
/// </list>
///
/// <para>⚠️ **异常一律吞掉并记 warn**：组件是插件代码，它抛异常不该让整个会话上下文构建失败。
/// 这是原有的既定行为，搬家时不能顺手"改好"——那是一次独立的行为变更。</para>
/// </summary>
internal static class GlobalPromptSections
{
    /// <summary>
    /// 每轮要追加的 Global 段。取不到宿主（<paramref name="services"/> 为 null 或未注册）⇒ 不追加。
    /// </summary>
    public static List<string> CollectPerRound(IServiceProvider? services, string sessionId, string loopType)
    {
        var result = new List<string>();
        try
        {
            var globalHost = services?.GetService(typeof(GlobalComponentHost)) as GlobalComponentHost;
            if (globalHost == null) return result;

            var loopInfo = new LoopInfo(sessionId, loopType);
            var sections = globalHost.BuildPromptSections(loopInfo);
            if (sections != null) result.AddRange(sections);
        }
        catch (Exception ex)
        {
            Signal.Warn(LogGroup.Engine, "Global 组件注入失败", new { sessionId, error = ex.Message });
        }
        return result;
    }

    /// <summary>
    /// 会话开头的一次性静态参考段（技能清单等）。没有 ⇒ <see langword="null"/>。
    /// </summary>
    public static string? CollectStartReference(IServiceProvider? services, string sessionId, string loopType)
    {
        try
        {
            var globalHost = services?.GetService(typeof(GlobalComponentHost)) as GlobalComponentHost;
            if (globalHost == null) return null;

            var loopInfo = new LoopInfo(sessionId, loopType);
            var section = globalHost.BuildStartPromptSections(loopInfo);
            return string.IsNullOrEmpty(section) ? null : section;
        }
        catch (Exception ex)
        {
            Signal.Warn(LogGroup.Engine, "会话开头参考注入失败", new { sessionId, error = ex.Message });
            return null;
        }
    }
}
