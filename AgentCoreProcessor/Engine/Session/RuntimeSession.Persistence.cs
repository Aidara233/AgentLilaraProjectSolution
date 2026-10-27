// AgentCoreProcessor/Engine/Session/RuntimeSession.Persistence.cs
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
/// <see cref="RuntimeSession"/> 的部件：持久化（partial 文件，仅物理拆分）。
/// <para>每轮追加与状态落盘；字段由 ISnapshotPort 采集并检查归属，保留 v3 存档格式。
/// 资源重建仍按既有恢复顺序进行。</para>
/// </summary>
internal partial class RuntimeSession
{
    // ── 持久化 ──

    /// <summary>
    /// 每轮持久化：追加新增消息到 history.jsonl + 原子重写 state.json。
    /// best-effort，失败不阻塞会话循环。
    /// </summary>
    private async Task PersistRoundAsync(List<Message> newMessages)
    {
        if (_persister == null) return;
        try
        {
            await _persister.AppendHistoryAsync(newMessages);
            await _persister.SaveStateAsync(BuildStateSnapshot());
        }
        catch (Exception ex)
        {
            Signal.Warn(LogGroup.Engine, "持久化失败（不阻塞）",
                new { sessionId = SessionId, error = ex.Message, newCount = newMessages.Count });
        }
    }

    /// <summary>构造 state.json 快照对象。</summary>
    internal SessionStateSnapshot BuildStateSnapshot() => new SnapshotWriter().Capture(new ISnapshotPort[]
    {
        new SnapshotPort("lifecycle", CaptureLifecycleSnapshot),
        new SnapshotPort("step", CaptureStepSnapshot),
        new SnapshotPort("context", CaptureContextSnapshot),
        new SnapshotPort("core", CaptureCoreSnapshot),
        new SnapshotPort("channel", CaptureChannelSnapshot),
        new SnapshotPort("children", CaptureChildrenSnapshot),
        new SnapshotPort("axes", CaptureAxesSnapshot)
    });

    /// <summary>从 host 维护的各频道 state 取 LastConsumedMessageId 构造游标快照。</summary>
    private Dictionary<int, int> BuildChannelCursors()
    {
        var cursors = new Dictionary<int, int>();
        if (_host == null) return cursors;
        foreach (var cid in _host.ChannelIds)
        {
            try
            {
                var c = _host.StateFor(cid).LastConsumedMessageId;
                if (c > 0) cursors[cid] = c;
            }
            catch { /* skip */ }
        }
        return cursors;
    }

    private static int EstimateChars(Message m)
    {
        if (m.Content != null)
            return m.Content.Length;
        if (m.ContentParts != null)
            return m.ContentParts.Sum(p =>
                (p.Text?.Length ?? 0) + (p.ToolInput?.Length ?? 0) + (p.ToolName?.Length ?? 0));
        return 0;
    }

    public async Task PersistStateAsync()
    {
        if (_persister == null)
        {
            // 兼容路径：无 persister 时走旧 meta.json 写入（用于优雅退出场景的兜底）
            var dir = Path.Combine(PathConfig.StoragePath, "Sessions", SessionId);
            Directory.CreateDirectory(dir);
            var meta = new
            {
                status = Status.ToString(),
                createdAt = _createdAt,
                roundCount = _roundCount,
                componentIds = _componentIds
            };
            await File.WriteAllTextAsync(Path.Combine(dir, "meta.json"),
                JsonConvert.SerializeObject(meta, Formatting.Indented));
            return;
        }
        await _persister.SaveStateAsync(BuildStateSnapshot());
    }
}
