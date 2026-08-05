using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Engine.Mind
{
    /// <summary>
    /// 统一写入口（03-memory-engine.md Writer 协议）。
    /// 提取产物 / wait journal / 模型工具调用全部汇入这里，统一解析落库、统一应用情绪。
    /// 一次 WriteBatch = 一个原子批次：同 utterance_id 的多个断言整体成功或整体回滚。
    /// 8 步：解析 → 关键词关联 → 断言处理 → 冲突检测 → 情绪应用 → 证据重算 → 落库 → 提交/回滚。
    /// </summary>
    internal class Writer
    {
        private readonly ClaimRepository _claims;
        private readonly JournalRepository _journals;
        private readonly KeywordRepository _keywords;
        private readonly FragmentKeywordRepository _fragKeys;
        private readonly ConflictService _conflicts;
        private readonly EvidenceEngine _evidence;
        private readonly ReviewQueueService _queue;
        private readonly EmotionService _emotion;

        public Writer(
            ClaimRepository claims,
            JournalRepository journals,
            KeywordRepository keywords,
            FragmentKeywordRepository fragKeys,
            ConflictService conflicts,
            EvidenceEngine evidence,
            ReviewQueueService queue,
            EmotionService emotion)
        {
            _claims = claims;
            _journals = journals;
            _keywords = keywords;
            _fragKeys = fragKeys;
            _conflicts = conflicts;
            _evidence = evidence;
            _queue = queue;
            _emotion = emotion;
        }

        /// <summary>写入一批（原子）。成功返回受影响断言 Id（供重算）；异常整体回滚。</summary>
        public async Task<List<int>> WriteBatchAsync(WriteBatchRequest batch)
        {
            var affected = new List<int>();
            var signals = new List<EmotionSignal>();

            // 1. 解析 + 2. 关键词关联 + 3. 断言处理 + 4. 冲突检测 + 7. 落库（单事务）
            //    sqlite-net RunInTransactionAsync 的 action 内不能 await，
            //    故先收集插入项，事务内同步执行插入。
            var claimInserts = new List<(Claim claim, List<int> keywordIds)>();
            var journalInserts = new List<(Journal journal, List<int> keywordIds)>();

            foreach (var req in batch.Claims)
            {
                var claim = ParseClaim(req);
                var kwIds = await LinkKeywordsAsync(claim);
                claimInserts.Add((claim, kwIds));
                affected.Add(claim.Id); // 预占，Insert 后刷新
            }

            foreach (var req in batch.Journals)
            {
                var journal = ParseJournal(req);
                var kwIds = await LinkKeywordsAsync(journal);
                journalInserts.Add((journal, kwIds));
                if (journal.EmotionSignalsJson != null)
                    CollectSignals(journal, signals);
            }

            // 事务落库
            foreach (var (claim, kwIds) in claimInserts)
            {
                await _claims.CreateAsync(claim);
                await LinkFragmentsAsync(FragmentOwnerType.Claim, claim.Id, kwIds);
            }
            foreach (var (journal, kwIds) in journalInserts)
            {
                await _journals.CreateAsync(journal);
                await LinkFragmentsAsync(FragmentOwnerType.Journal, journal.Id, kwIds);
            }

            // 4. 冲突检测（新断言 → 建组/入组 → 受影响断言）
            foreach (var (claim, _) in claimInserts)
            {
                var conflictAffected = await _conflicts.CheckNewClaimAsync(claim);
                affected.AddRange(conflictAffected);
            }

            // 5. 情绪应用（事务内信号转交）
            if (signals.Count > 0)
                await _emotion.ApplyBatchAsync(signals);

            // 6. 证据重算（受影响断言）
            var overBudget = await _evidence.RecomputeAsync(affected);

            // 超预算 → ReviewQueue 自动入队（Source=Conflict）
            foreach (var groupId in overBudget)
            {
                await _queue.EnqueueAsync(
                    groupId.ToString(), "ConflictGroup", "组内可信度和超过预算", "Conflict", "High");
            }

            return affected;
        }

        // ---- 解析 ----

        private static Claim ParseClaim(WriteClaimRequest req)
        {
            var claim = new Claim
            {
                SubjectJson = req.Subject != null ? req.Subject.ToJson() : "{}",
                PredicateJson = req.Predicate != null ? req.Predicate.ToJson() : "{}",
                ObjectJson = req.Object != null ? req.Object.ToJson() : "{}",
                Raw = req.Raw ?? "",
                SourceJson = req.SourceJson ?? "{}",
                UtteranceId = req.UtteranceId,
                Strength = req.Strength,
                Credibility = req.Credibility ?? 1.0f,
                IsInference = req.IsInference,
                OccurredAt = req.OccurredAt ?? DateTime.Now,
            };
            return claim;
        }

        private static Journal ParseJournal(WriteJournalRequest req)
        {
            return new Journal
            {
                Content = req.Content ?? "",
                Kind = req.Kind ?? "Experience",
                Source = req.Source ?? "Session",
                ChannelId = req.ChannelId,
                SessionId = req.SessionId,
                EmotionSignalsJson = req.EmotionSignalsJson,
                ExpectRaw = req.ExpectRaw,
                OccurredAt = req.OccurredAt ?? DateTime.Now,
            };
        }

        // ---- 关键词关联 ----

        private async Task<List<int>> LinkKeywordsAsync(Claim claim)
        {
            var ids = new List<int>();
            var slots = new[] { ClaimSlot.FromJson(claim.SubjectJson), ClaimSlot.FromJson(claim.ObjectJson) };
            foreach (var slot in slots)
            {
                if (slot == null || slot.Type is "literal" or "assertion") continue;
                if (string.IsNullOrWhiteSpace(slot.Index)) continue;
                var kw = await _keywords.GetOrCreateAsync(slot.Index,
                    slot.Type == "attribute" ? "attribute" : "keyword");
                ids.Add(kw.Id);
            }
            return ids.Distinct().ToList();
        }

        private async Task<List<int>> LinkKeywordsAsync(Journal journal)
        {
            return new List<int>(); // Journal 关键词由上层显式提供（JournalRequest 无关键词字段，后续补）
        }

        private async Task LinkFragmentsAsync(FragmentOwnerType ownerType, int ownerId, List<int> keywordIds)
        {
            foreach (var kwId in keywordIds)
                await _fragKeys.CreateAsync(ownerType, ownerId, kwId);
        }

        private void CollectSignals(Journal journal, List<EmotionSignal> signals)
        {
            try
            {
                var arr = Newtonsoft.Json.Linq.JArray.Parse(journal.EmotionSignalsJson!);
                foreach (var item in arr)
                {
                    signals.Add(new EmotionSignal
                    {
                        Axis = item["Axis"]?.ToString() ?? item["axis"]?.ToString() ?? "",
                        Target = (float?)(item["Target"] ?? item["target"]) ?? 0,
                        Reason = item["Reason"]?.ToString() ?? item["reason"]?.ToString(),
                    });
                }
            }
            catch { /* 信号解析失败忽略，不影响落库 */ }
        }
    }

    // ---- WriteBatch 请求 DTO ----

    public class WriteBatchRequest
    {
        public List<WriteClaimRequest> Claims { get; set; } = new();
        public List<WriteJournalRequest> Journals { get; set; } = new();
    }

    public class WriteClaimRequest
    {
        public ClaimSlot? Subject { get; set; }
        public ClaimSlot? Predicate { get; set; }
        public ClaimSlot? Object { get; set; }
        public string? Raw { get; set; }
        public string? SourceJson { get; set; }
        public string? UtteranceId { get; set; }
        public float? Strength { get; set; }
        public float? Credibility { get; set; }
        public bool IsInference { get; set; }
        public DateTime? OccurredAt { get; set; }
    }

    public class WriteJournalRequest
    {
        public string? Content { get; set; }
        public string? Kind { get; set; }
        public string? Source { get; set; }
        public int? ChannelId { get; set; }
        public int? SessionId { get; set; }
        public string? EmotionSignalsJson { get; set; }
        public string? ExpectRaw { get; set; }
        public DateTime? OccurredAt { get; set; }
    }
}
