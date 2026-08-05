using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Engine.Mind
{
    /// <summary>
    /// 检索服务（01 检索章节）。关键词 + 时间窗口，不做多跳图遍历。
    /// 排序两级：命中关键词数量降序（绝对优先级）→ c_actual（当前用锚点近似）降序（次优先级），
    /// 时间倒序作为同分 tiebreaker（Journal 按时间排列）。
    /// 返回二跳以内子证据（支持/反对边），同 utterance_id 的 Claim 并排返回。
    /// 检索中立：方法签名不带频道偏置参数，频道过滤由上层显式传条件。
    /// </summary>
    internal class RetrieveService
    {
        private readonly ClaimRepository _claims;
        private readonly JournalRepository _journals;
        private readonly KeywordRepository _keywords;
        private readonly FragmentKeywordRepository _fragmentKeywords;

        public RetrieveService(
            ClaimRepository claims,
            JournalRepository journals,
            KeywordRepository keywords,
            FragmentKeywordRepository fragmentKeywords)
        {
            _claims = claims;
            _journals = journals;
            _keywords = keywords;
            _fragmentKeywords = fragmentKeywords;
        }

        /// <summary>
        /// 主检索：
        /// 1) query 拆分关键词（空格 / 简单中文标点；非空白连续串按整串）；
        /// 2) 每个关键词 GetOrCreateAsync（keyword role）+ UseCount++；
        /// 3) 按关键词查关联，统计每个 owner 命中的关键词数；
        /// 4) Claims + Journals 混合，按命中数降序 → 可信度降序 → 时间倒序，取前 N；
        /// 5) 每条 Claim 补 c_actual（当前用 Credibility 锚点近似）与二跳内子证据；
        /// 6) 同 utterance_id 的 Claim 并排。
        /// </summary>
        public async Task<RetrieveResult> SearchAsync(string query, int limit = 20)
        {
            var timestamp = DateTime.Now;
            var result = new RetrieveResult
            {
                Query = query,
                Timestamp = timestamp,
                Hits = new List<RetrieveHit>()
            };

            // 1. 关键词拆分（空格 / 简单中文标点，非空白连续串保持整串）
            var terms = SplitKeywords(query);
            if (terms.Count == 0) return result;

            // 2. 每个关键词 GetOrCreateAsync（keyword role）+ UseCount++
            var keywords = new List<Keyword>(terms.Count);
            foreach (var term in terms)
            {
                var kw = await _keywords.GetOrCreateAsync(term, "keyword");
                await _keywords.IncrementUseCountAsync(kw.Id);
                keywords.Add(kw);
            }

            // 3. 查关联，统计每个 owner 命中的关键词数。
            // 用 FragmentKeywordRepository.GetByKeywordAsync（GetOwnerIdsByKeywordAsync 的上游），
            // 保留 OwnerType 区分 Claim/Journal，避免两类表 Id 空间碰撞导致误配。
            var claimMatch = new Dictionary<int, int>();
            var journalMatch = new Dictionary<int, int>();
            foreach (var kw in keywords)
            {
                foreach (var link in await _fragmentKeywords.GetByKeywordAsync(kw.Id))
                {
                    if (link.OwnerType == FragmentOwnerType.Claim)
                        claimMatch[link.OwnerId] = claimMatch.GetValueOrDefault(link.OwnerId) + 1;
                    else
                        journalMatch[link.OwnerId] = journalMatch.GetValueOrDefault(link.OwnerId) + 1;
                }
            }

            if (claimMatch.Count == 0 && journalMatch.Count == 0)
                return result; // 无命中

            // 4. 加载实体 + 预索引证据边（子证据需要全量 Claim 视角）
            var allClaims = await _claims.GetAllAsync();
            var allJournals = await _journals.GetAllAsync();
            var claimById = allClaims.ToDictionary(c => c.Id);
            var journalById = allJournals.ToDictionary(j => j.Id);
            var edgesByRef = BuildEdgeIndex(allClaims);

            var hits = new List<RetrieveHit>(claimMatch.Count + journalMatch.Count);
            foreach (var kv in claimMatch)
            {
                if (claimById.TryGetValue(kv.Key, out var c))
                    hits.Add(BuildClaimHit(c, kv.Value, edgesByRef));
            }
            foreach (var kv in journalMatch)
            {
                if (journalById.TryGetValue(kv.Key, out var j))
                    hits.Add(BuildJournalHit(j, kv.Value));
            }

            // 5. 排序 + 同源并排 + 取前 N
            var ordered = hits
                .OrderByDescending(h => h.MatchCount)
                .ThenByDescending(h => h.Credibility)
                .ThenByDescending(h => h.OccurredAt)
                .ToList();

            result.Hits = GroupUtterancesAndTruncate(ordered, limit);
            return result;
        }

        /// <summary>
        /// 按单个关键词精确查。同样补子证据 + 同源并排，取前 N。
        /// </summary>
        public async Task<List<RetrieveHit>> GetByKeywordAsync(string keyword, int limit = 20)
        {
            if (string.IsNullOrWhiteSpace(keyword)) return new List<RetrieveHit>();

            var kw = await _keywords.GetOrCreateAsync(keyword, "keyword");
            await _keywords.IncrementUseCountAsync(kw.Id);

            // 类型区分的关联查询（单关键词命中数恒为 1）
            var claimIds = new HashSet<int>();
            var journalIds = new HashSet<int>();
            foreach (var link in await _fragmentKeywords.GetByKeywordAsync(kw.Id))
            {
                if (link.OwnerType == FragmentOwnerType.Claim) claimIds.Add(link.OwnerId);
                else journalIds.Add(link.OwnerId);
            }

            if (claimIds.Count == 0 && journalIds.Count == 0)
                return new List<RetrieveHit>();

            var allClaims = await _claims.GetAllAsync();
            var allJournals = await _journals.GetAllAsync();
            var claimById = allClaims.ToDictionary(c => c.Id);
            var journalById = allJournals.ToDictionary(j => j.Id);
            var edgesByRef = BuildEdgeIndex(allClaims);

            var hits = new List<RetrieveHit>(claimIds.Count + journalIds.Count);
            foreach (var id in claimIds)
            {
                if (claimById.TryGetValue(id, out var c))
                    hits.Add(BuildClaimHit(c, 1, edgesByRef));
            }
            foreach (var id in journalIds)
            {
                if (journalById.TryGetValue(id, out var j))
                    hits.Add(BuildJournalHit(j, 1));
            }

            var ordered = hits
                .OrderByDescending(h => h.MatchCount)
                .ThenByDescending(h => h.Credibility)
                .ThenByDescending(h => h.OccurredAt)
                .ToList();

            return GroupUtterancesAndTruncate(ordered, limit);
        }

        // ---- 私有辅助 ----

        /// <summary>关键词拆分分隔符：空白 + 常见中英文标点（简单分词，非空白连续串保持整串）。</summary>
        private static readonly char[] KeywordSeparators =
        {
            ' ', '\t', '\n', '\r',
            '，', '。', '、', '；', '：', '？', '！',
            ',', ';', ':', '?', '!'
        };

        private static List<string> SplitKeywords(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<string>();
            return query
                .Split(KeywordSeparators, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .Distinct()
                .ToList();
        }

        /// <summary>
        /// 构建支持/反对边索引：ref 断言 Id → 引用它的边断言列表。
        /// 边 = 谓词为"支持"的断言（反对 = 支持 + 负 strength，见 05），
        /// 且 subject/object 为 assertion 槽位（带 Ref）。
        /// </summary>
        private static Dictionary<int, List<Claim>> BuildEdgeIndex(List<Claim> allClaims)
        {
            var index = new Dictionary<int, List<Claim>>();
            foreach (var c in allClaims)
            {
                if (!c.PredicateJson.Contains("支持")) continue;
                var subj = ClaimSlot.FromJson(c.SubjectJson);
                var obj = ClaimSlot.FromJson(c.ObjectJson);
                if (subj?.Ref.HasValue == true) AddEdgeRef(index, subj.Ref.Value, c);
                if (obj?.Ref.HasValue == true) AddEdgeRef(index, obj.Ref.Value, c);
            }
            return index;
        }

        private static void AddEdgeRef(Dictionary<int, List<Claim>> index, int refId, Claim edge)
        {
            if (!index.TryGetValue(refId, out var list))
            {
                list = new List<Claim>();
                index[refId] = list;
            }
            list.Add(edge);
        }

        /// <summary>收集二跳以内的支持/反对边，返回边的 Raw（作为子证据摘要）。</summary>
        private static List<string> CollectSubEvidence(int targetId, Dictionary<int, List<Claim>> edgesByRef)
        {
            var evidence = new List<string>();
            var visited = new HashSet<int> { targetId }; // 目标自身不算证据，但参与去环
            var current = new HashSet<int> { targetId };

            // 二跳：第一轮取直接引用目标的边，第二轮取引用这些边的边
            for (int hop = 0; hop < 2; hop++)
            {
                var next = new HashSet<int>();
                foreach (var id in current)
                {
                    if (!edgesByRef.TryGetValue(id, out var edges)) continue;
                    foreach (var edge in edges)
                    {
                        if (!visited.Add(edge.Id)) continue;
                        if (!string.IsNullOrWhiteSpace(edge.Raw))
                            evidence.Add(edge.Raw);
                        next.Add(edge.Id);
                    }
                }
                if (next.Count == 0) break;
                current = next;
            }

            return evidence;
        }

        private static RetrieveHit BuildClaimHit(Claim c, int matchCount, Dictionary<int, List<Claim>> edgesByRef)
        {
            return new RetrieveHit
            {
                Id = c.Id,
                Kind = "Claim",
                Raw = c.Raw,
                MatchCount = matchCount,
                Credibility = c.Credibility,   // c_actual 当前用锚点近似；复杂网络现算留给后续
                IsInference = c.IsInference,
                UtteranceId = c.UtteranceId,
                SubEvidence = CollectSubEvidence(c.Id, edgesByRef),
                OccurredAt = c.OccurredAt ?? c.RecordedAt
            };
        }

        private static RetrieveHit BuildJournalHit(Journal j, int matchCount)
        {
            return new RetrieveHit
            {
                Id = j.Id,
                Kind = "Journal",
                Raw = j.Content,
                MatchCount = matchCount,
                Credibility = 0f,               // Journal 无可信度字段
                IsInference = false,
                UtteranceId = null,
                SubEvidence = new List<string>(),
                OccurredAt = j.OccurredAt
            };
        }

        /// <summary>
        /// 同 utterance_id 的 Claim 并排：按排序顺序取前 limit 条，
        /// 若命中 Claim 带有同源分组，则把同源兄弟整组带出（可能略超 limit，以保证并排语义）。
        /// </summary>
        private static List<RetrieveHit> GroupUtterancesAndTruncate(List<RetrieveHit> ordered, int limit)
        {
            var result = new List<RetrieveHit>();
            var visited = new HashSet<(string Kind, int Id)>();

            foreach (var hit in ordered)
            {
                if (result.Count >= limit) break;
                if (!visited.Add((hit.Kind, hit.Id))) continue;

                result.Add(hit);

                if (hit.Kind == "Claim" && !string.IsNullOrEmpty(hit.UtteranceId))
                {
                    foreach (var sib in ordered.Where(x =>
                        x.Kind == "Claim" &&
                        x.UtteranceId == hit.UtteranceId &&
                        visited.Add((x.Kind, x.Id))))
                    {
                        result.Add(sib);
                    }
                }
            }

            return result;
        }
    }

    /// <summary>检索结果。</summary>
    internal class RetrieveResult
    {
        /// <summary>混合结果（Claim / Journal）。</summary>
        public List<RetrieveHit> Hits { get; set; } = new List<RetrieveHit>();

        /// <summary>原始查询。</summary>
        public string Query { get; set; } = "";

        /// <summary>检索时间。</summary>
        public DateTime Timestamp { get; set; }
    }

    /// <summary>单条检索命中。</summary>
    internal class RetrieveHit
    {
        /// <summary>Id（Claim.Id 或 Journal.Id）。</summary>
        public int Id { get; set; }

        /// <summary>类型："Claim" | "Journal"。</summary>
        public string Kind { get; set; } = "";

        /// <summary>Claim 的 Raw / Journal 的 Content。</summary>
        public string? Raw { get; set; }

        /// <summary>命中关键词数。</summary>
        public int MatchCount { get; set; }

        /// <summary>c_actual（当前近似锚点 Credibility；Journal 恒 0）。</summary>
        public float Credibility { get; set; }

        /// <summary>推断标记（仅 Claim 有意义）。</summary>
        public bool IsInference { get; set; }

        /// <summary>同源分组（仅 Claim 有意义）。</summary>
        public string? UtteranceId { get; set; }

        /// <summary>二跳内子证据（支持/反对边的 Raw 或摘要）。</summary>
        public List<string> SubEvidence { get; set; } = new List<string>();

        /// <summary>发生时间（Claim 缺失时回退记录时间）。</summary>
        public DateTime OccurredAt { get; set; }
    }
}
