using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine.Modules;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine
{
    public enum DelegationStatus
    {
        Submitted,
        Accepted,
        Queued,
        Rejected,
        Executing,
        Completed,
        Failed,
        Timeout
    }

    public class Delegation
    {
        public string DelegationId { get; set; } = Guid.NewGuid().ToString();
        public int SourceChannelId { get; set; }
        public string Description { get; set; } = "";
        public string? ContextSummary { get; set; }
        public int RequestingPersonId { get; set; }
        public DelegationStatus Status { get; set; } = DelegationStatus.Submitted;
        public string? EvaluationReason { get; set; }
        public string? Result { get; set; }
        public DateTime SubmittedAt { get; set; } = DateTime.Now;
        public DateTime? EvaluatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public bool Consumed { get; set; }
    }

    public class DelegationEvaluation
    {
        public DelegationStatus Verdict { get; set; }
        public string Reason { get; set; } = "";
    }

    internal class DelegationRegistry
    {
        private readonly ConcurrentDictionary<string, Delegation> delegations = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<DelegationEvaluation>> evaluationWaiters = new();
        private readonly string persistencePath;
        private readonly object persistLock = new();

        public Action? OnDelegationSubmitted { get; set; }
        public Action<int>? OnDelegationCompleted { get; set; }

        public DelegationRegistry(string storagePath)
        {
            persistencePath = Path.Combine(storagePath, "delegations.json");
            Load();
        }

        public Delegation Submit(Delegation delegation)
        {
            delegation.Status = DelegationStatus.Submitted;
            delegation.SubmittedAt = DateTime.Now;
            delegations[delegation.DelegationId] = delegation;
            Persist();
            OnDelegationSubmitted?.Invoke();
            FrameworkLogger.Log("DelegationRegistry", $"委托已提交: {delegation.DelegationId}, 描述: {delegation.Description.Truncate(60)}");
            return delegation;
        }

        public async Task<DelegationEvaluation?> WaitForEvaluationAsync(string delegationId, TimeSpan timeout)
        {
            var tcs = new TaskCompletionSource<DelegationEvaluation>();
            if (!evaluationWaiters.TryAdd(delegationId, tcs))
                return null;

            using var cts = new CancellationTokenSource(timeout);
            using var reg = cts.Token.Register(() => tcs.TrySetCanceled());

            try
            {
                return await tcs.Task;
            }
            catch (TaskCanceledException)
            {
                evaluationWaiters.TryRemove(delegationId, out _);
                if (delegations.TryGetValue(delegationId, out var d))
                {
                    d.Status = DelegationStatus.Timeout;
                    d.EvaluatedAt = DateTime.Now;
                    d.EvaluationReason = "评估超时";
                    Persist();
                }
                FrameworkLogger.Log("DelegationRegistry", $"委托评估超时: {delegationId}");
                return null;
            }
        }

        public bool ResolveEvaluation(string delegationId, DelegationEvaluation evaluation)
        {
            if (!delegations.TryGetValue(delegationId, out var delegation))
                return false;

            delegation.Status = evaluation.Verdict;
            delegation.EvaluationReason = evaluation.Reason;
            delegation.EvaluatedAt = DateTime.Now;
            Persist();

            if (evaluationWaiters.TryRemove(delegationId, out var tcs))
                tcs.TrySetResult(evaluation);

            FrameworkLogger.Log("DelegationRegistry",
                $"委托已评估: {delegationId} → {evaluation.Verdict}, 理由: {evaluation.Reason.Truncate(60)}");
            return true;
        }

        public void MarkExecuting(string delegationId)
        {
            if (delegations.TryGetValue(delegationId, out var d))
            {
                d.Status = DelegationStatus.Executing;
                Persist();
            }
        }

        public void MarkCompleted(string delegationId, string result)
        {
            if (delegations.TryGetValue(delegationId, out var d))
            {
                d.Status = DelegationStatus.Completed;
                d.Result = result;
                d.CompletedAt = DateTime.Now;
                Persist();
                OnDelegationCompleted?.Invoke(d.SourceChannelId);
                FrameworkLogger.Log("DelegationRegistry",
                    $"委托已完成: {delegationId}, 结果: {result.Truncate(80)}");
            }
        }

        public void MarkFailed(string delegationId, string error)
        {
            if (delegations.TryGetValue(delegationId, out var d))
            {
                d.Status = DelegationStatus.Failed;
                d.Result = error;
                d.CompletedAt = DateTime.Now;
                Persist();
                OnDelegationCompleted?.Invoke(d.SourceChannelId);
                FrameworkLogger.Log("DelegationRegistry",
                    $"委托失败: {delegationId}, 错误: {error.Truncate(80)}");
            }
        }

        public List<Delegation> GetPendingForEvaluation()
            => delegations.Values.Where(d => d.Status == DelegationStatus.Submitted).ToList();

        public List<Delegation> GetCompletedForChannel(int channelId)
            => delegations.Values
                .Where(d => d.SourceChannelId == channelId
                    && (d.Status == DelegationStatus.Completed || d.Status == DelegationStatus.Failed)
                    && !d.Consumed)
                .ToList();

        public List<Delegation> GetActiveForChannel(int channelId)
            => delegations.Values
                .Where(d => d.SourceChannelId == channelId
                    && (d.Status == DelegationStatus.Accepted
                        || d.Status == DelegationStatus.Queued
                        || d.Status == DelegationStatus.Executing))
                .ToList();

        public List<Delegation> GetAcceptedForExecution()
            => delegations.Values.Where(d => d.Status == DelegationStatus.Accepted).ToList();

        public void ConsumeCompleted(string delegationId)
        {
            if (delegations.TryGetValue(delegationId, out var d))
            {
                d.Consumed = true;
                Persist();
            }
        }

        public Delegation? Get(string delegationId)
            => delegations.TryGetValue(delegationId, out var d) ? d : null;

        public bool Cancel(string delegationId)
        {
            if (!delegations.TryRemove(delegationId, out _)) return false;
            evaluationWaiters.TryRemove(delegationId, out var tcs);
            tcs?.TrySetCanceled();
            Persist();
            FrameworkLogger.Log("DelegationRegistry", $"委托已取消: {delegationId}");
            return true;
        }

        public void Cleanup(TimeSpan maxAge)
        {
            var cutoff = DateTime.Now - maxAge;
            var toRemove = delegations.Values
                .Where(d => d.Consumed && d.CompletedAt < cutoff)
                .Select(d => d.DelegationId)
                .ToList();
            foreach (var id in toRemove)
                delegations.TryRemove(id, out _);
            if (toRemove.Count > 0) Persist();
        }

        private void Persist()
        {
            lock (persistLock)
            {
                try
                {
                    var dir = Path.GetDirectoryName(persistencePath);
                    if (dir != null && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    var json = JsonConvert.SerializeObject(delegations.Values.ToList(), Formatting.Indented);
                    File.WriteAllText(persistencePath, json);
                }
                catch (Exception ex)
                {
                    FrameworkLogger.LogError("DelegationRegistry", ex, "持久化失败");
                }
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(persistencePath)) return;
                var json = File.ReadAllText(persistencePath);
                var list = JsonConvert.DeserializeObject<List<Delegation>>(json);
                if (list == null) return;
                foreach (var d in list)
                {
                    // 恢复时：Submitted 的标记为 Timeout（没人在等了）
                    if (d.Status == DelegationStatus.Submitted)
                    {
                        d.Status = DelegationStatus.Timeout;
                        d.EvaluationReason = "系统重启，评估超时";
                    }
                    delegations[d.DelegationId] = d;
                }
                FrameworkLogger.Log("DelegationRegistry", $"已恢复 {list.Count} 条委托记录");
            }
            catch (Exception ex)
            {
                FrameworkLogger.LogError("DelegationRegistry", ex, "加载失败");
            }
        }
    }
}
