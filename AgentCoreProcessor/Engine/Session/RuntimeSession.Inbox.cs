// AgentCoreProcessor/Engine/Session/RuntimeSession.Inbox.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AgentCoreProcessor.Component;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine.Axes;
using AgentCoreProcessor.Engine.Context;
using AgentCoreProcessor.Engine.Graph;
using AgentCoreProcessor.Engine.Messaging;
using AgentCoreProcessor.Engine.Modules;
using AgentCoreProcessor.Logging;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;
using AgentCoreProcessor.Util;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine.Session;

/// <summary>
/// <see cref="RuntimeSession"/> 的部件：Inbox（partial 文件，仅物理拆分，类型仍是一个）。
/// <para>⚠️ 拆文件的目的是让一个部件一个文件可读、可对照 <c>session-decomposition.md</c> 的 20 部件表；
/// 真正的类型边界（各自独立类）要到 C 阶段拆 csproj 时才动。</para>
/// </summary>
internal partial class RuntimeSession
{
    /// <summary>
    /// 频道级游标接力：把本频道已处理到的最大 message id 写回持久化游标。
    /// session 占有期间推进同一份 ChannelContextPersistence 游标，使其始终是频道级唯一真相——
    /// session 结束后 ExpressEngine 从同一游标恢复，不重读 session 已处理的消息。
    /// </summary>
    private void AdvanceChannelCursor(int channelId, int maxMessageId)
    {
        if (channelId <= 0 || maxMessageId <= 0) return;
        try { new ChannelContextPersistence(channelId).AdvanceCursor(maxMessageId); }
        catch { /* best-effort，落地失败不中断会话 */ }
    }


    /// <summary>
    /// 从所有输入端口收集消息，转换为 (channelId, UserMessage, isMention) 元组列表。
    /// EngineIncomingMessage → UserMessage：最小化转换（无 SenderName，但含 ChannelId/Content/UserId/PlatformMessageId）。
    /// isMention：消息来自 "mention" 端口（Weight >= 100，被 @ 提及）。
    /// </summary>
    private List<(int channelId, UserMessage msg, bool isMention)> DrainInputPortsRaw(CancellationToken ct)
    {
        var result = new List<(int channelId, UserMessage msg, bool isMention)>();
        foreach (var (name, port) in _inputs)
        {
            bool isMentionPort = name.Contains("mention", StringComparison.OrdinalIgnoreCase);
            while (port.TryPeek(out var data))
            {
                port.ReceiveAsync(ct).GetAwaiter().GetResult();
                if (data is UserMessage existing)
                {
                    result.Add((existing.ChannelId, existing, isMentionPort));
                }
                else if (data is EngineIncomingMessage em)
                {
                    var um = new UserMessage
                    {
                        ChannelId = em.ChannelId,
                        UserId = em.SenderUserId,
                        Content = em.Content,
                        PlatformMessageId = em.MessageId,
                        Time = em.Timestamp == default ? DateTime.UtcNow : TimeUtil.NormalizeToUtc(em.Timestamp),
                        IsFromBot = false,
                        SenderName = ""
                    };
                    result.Add((em.ChannelId, um, isMentionPort));
                }
                else if (data is string s)
                {
                    var um = new UserMessage { Content = s, ChannelId = 0, Time = DateTime.UtcNow };
                    result.Add((0, um, false));
                }
                else if (data != null)
                {
                    var um = new UserMessage { Content = JsonConvert.SerializeObject(data), ChannelId = 0, Time = DateTime.UtcNow };
                    result.Add((0, um, false));
                }
            }
            if (port is MessageInputPort mp)
                mp.ResetQueueCount();
        }
        _activationShadow?.Synchronize(_inputs);
        return result;
    }

    private void ApplyRoundPricing()
    {
        if (_roundCount <= FreeRounds || _currency == null) return;
        var price = (decimal)Math.Ceiling((_roundCount - FreeRounds) / 10.0);
        if (_currency.Balance >= price)
        {
            try { _currency.Spend(price, $"Session {SessionId} round {_roundCount}"); }
            catch { /* 扣费失败不中断 */ }
        }
        else
        {
            InjectMessage(new { type = "warning", text = $"余额不足（当前 {_currency.Balance:F0} 币），无法续期。请收尾。" });
        }
    }
}
