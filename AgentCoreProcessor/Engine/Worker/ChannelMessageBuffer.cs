using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Logging;
using AgentCoreProcessor.Models;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 消息缓冲：滑动窗口聚合、定时器、图片收集。
    /// </summary>
    partial class ChannelEngine
    {
        // ---- 消息缓冲 ----
        private readonly object bufferLock = new();
        private DateTime lastBufferTime;
        private int _bufferedMessageCount;

        // ---- 缓冲定时器 ----
        private CancellationTokenSource? _bufferTimerCts;
        private DateTime? _bufferFirstMessageTime;
        private bool _bufferTriggered;

        // ---- 未消费的图片路径 ----
        private readonly List<(string Path, string? Hash, string? Category)> pendingImageInfos = new();
        private readonly HashSet<string> _pendingPhase2Hashes = new();

        /// <summary>启动/续期缓冲计时器（3s 滑动窗口，10s 上限）。</summary>
        private void ScheduleBufferSignal()
        {
            _bufferTimerCts?.Cancel();
            _bufferTimerCts = new CancellationTokenSource();
            var cts = _bufferTimerCts;

            _bufferFirstMessageTime ??= DateTime.Now;

            var elapsed = (DateTime.Now - _bufferFirstMessageTime.Value).TotalSeconds;
            var remaining = ctx.ImpulseConfig.BufferMaxDelaySeconds - elapsed;
            var delay = Math.Min(ctx.ImpulseConfig.BufferWindowSeconds, Math.Max(remaining, 0.1));

            _ = Task.Delay(TimeSpan.FromSeconds(delay), cts.Token)
                .ContinueWith(_ => FlushBuffer(), TaskContinuationOptions.NotOnCanceled);
        }

        /// <summary>缓冲到期：等待 Vision Phase 2 就绪后开闸。</summary>
        private async void FlushBuffer()
        {
            _bufferFirstMessageTime = null;
            _bufferTriggered = false;

            // 等待 @+图 触发的 Phase 2 完成（最多 5s）
            if (_pendingPhase2Hashes.Count > 0)
            {
                var deadline = DateTime.Now.AddSeconds(5);
                while (_pendingPhase2Hashes.Count > 0 && DateTime.Now < deadline)
                {
                    var done = new List<string>();
                    foreach (var hash in _pendingPhase2Hashes)
                    {
                        var record = await ImageStorage.GetByHashAsync(hash);
                        if (record != null && record.Phase >= 2)
                            done.Add(hash);
                    }
                    foreach (var h in done)
                        _pendingPhase2Hashes.Remove(h);
                    if (_pendingPhase2Hashes.Count > 0)
                        await Task.Delay(200);
                }
                _pendingPhase2Hashes.Clear();
            }

            gate.Signal();
        }

        private void CollectImagePaths(IncomingMessage msg)
        {
            if (msg.Attachments == null) return;
            foreach (var a in msg.Attachments)
            {
                if (a.Type == AttachmentType.Image && !string.IsNullOrEmpty(a.LocalPath))
                    pendingImageInfos.Add((a.LocalPath!, a.Hash, a.Category));
            }
        }

        private void ResolveImagePresentation(
            List<(string Path, string? Hash, string? Category)> images)
        {
            foreach (var (path, hash, category) in images)
            {
                if (!string.IsNullOrEmpty(hash))
                    _ = ImageStorage.IncrementSeenCountAsync(hash);
            }
        }
    }
}
