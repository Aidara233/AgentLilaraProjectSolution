using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Util;
using AgentLilara.PluginSDK.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Engine.Mind
{
    /// <summary>
    /// 伴随提取器（Express 模式，03-memory-engine.md 产生端）。
    /// 按消息攒批：攒够 N 条（默认 5）自动触发，或由 FlushAsync / TriggerIdleAsync（空闲补一次）显式触发。
    /// 每次触发 = 一轮轻量 LLM 调用（单轮、不开 session），产出窄 Claim（"用户说了X"），
    /// 经 IWriterAccess.WriteBatchAsync 统一落库。
    /// 窄 Claim 结构：subject=发送者 entity，predicate="说了" relation，object=内容 literal，带 source 消息引用。
    /// LLM 调用入口由外部注入（IExtractionLlm，定义见 ExtractService.cs），本类不直接依赖模型客户端。
    /// </summary>
    internal class ExpressExtractor
    {
        /// <summary>默认攒批阈值。</summary>
        public const int DefaultBatchSize = 5;

        /// <summary>窄 Claim 的默认谓词。</summary>
        private const string PredicateSaid = "说了";

        private readonly IWriterAccess _writer;
        private readonly IExtractionLlm _llm;
        private readonly int _batchSize;

        /// <summary>攒批缓冲。</summary>
        private readonly List<PendingMessage> _pending = new();

        /// <summary>缓冲访问锁。</summary>
        private readonly object _bufferLock = new();

        /// <summary>提取串行化：同一时刻只允许一轮 LLM 调用。</summary>
        private readonly SemaphoreSlim _extractGate = new(1, 1);

        /// <summary>消息序号（给消息引用用，模拟数据库消息 id）。</summary>
        private long _messageSeq;

        /// <summary>已成功落库的提取批次计数（审计/WebUI 用）。</summary>
        private long _extractCount;

        public ExpressExtractor(IWriterAccess writer, IExtractionLlm llm, int batchSize = DefaultBatchSize)
        {
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _llm = llm ?? throw new ArgumentNullException(nameof(llm));
            _batchSize = batchSize > 0 ? batchSize : DefaultBatchSize;
        }

        /// <summary>当前攒批中的消息数。</summary>
        public int PendingCount
        {
            get { lock (_bufferLock) return _pending.Count; }
        }

        /// <summary>已触发的提取批次数。</summary>
        public long ExtractCount => Interlocked.Read(ref _extractCount);

        // ---- 攒批 ----

        /// <summary>
        /// 消息入批。攒够 N 条自动触发一次轻量提取。
        /// 注意：达到阈值时内部会 await 一轮 LLM 调用（单轮）；生产若需完全不阻塞消息链路，
        /// 可改为由调用方显式调用 TriggerIdleAsync / FlushAsync。
        /// </summary>
        public async Task AddMessageAsync(string channelId, string senderName, string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return;

            bool thresholdReached;
            var msg = new PendingMessage(channelId, senderName, content, Interlocked.Increment(ref _messageSeq));
            lock (_bufferLock)
            {
                _pending.Add(msg);
                thresholdReached = _pending.Count >= _batchSize;
            }

            if (thresholdReached)
                await FlushAsync(); // 攒够 N 条 → 触发一次轻量提取
        }

        /// <summary>
        /// 触发提取：把攒批中的消息取走并处理（达到阈值主动触发；也用于主动清空攒批尾巴）。
        /// </summary>
        public async Task FlushAsync()
        {
            var batch = TakeBatch();
            if (batch.Count == 0) return;
            await ExtractAndWriteAsync(batch);
        }

        /// <summary>
        /// 空闲补一次：频道进入空闲时调用，把不足一攒批的尾巴也提取掉，保证沉默监听不漏尾。
        /// </summary>
        public async Task TriggerIdleAsync()
        {
            await FlushAsync();
        }

        // ---- 内部 ----

        /// <summary>原子取走当前攒批全部消息并清空缓冲。</summary>
        private List<PendingMessage> TakeBatch()
        {
            lock (_bufferLock)
            {
                if (_pending.Count == 0) return new List<PendingMessage>();
                var batch = new List<PendingMessage>(_pending);
                _pending.Clear();
                return batch;
            }
        }

        /// <summary>
        /// 单轮 LLM 调用 → 解析（容错）→ 经 Writer 落库。
        /// 任何异常都吞掉并记录，不阻断上层消息处理。
        /// </summary>
        private async Task ExtractAndWriteAsync(List<PendingMessage> batch)
        {
            await _extractGate.WaitAsync();
            try
            {
                var context = BuildContext(batch);
                var output = await _llm.ExtractClaimsAsync(context);
                var claims = ParseLlmOutput(output, batch);
                if (claims.Count == 0) return;

                await _writer.WriteBatchAsync(new WriteBatchDto { Claims = claims });
                Interlocked.Increment(ref _extractCount);
            }
            catch (Exception ex)
            {
                // 伴随提取失败不阻断会话：LLM 异常 / 写库异常统一记录并吞掉
                Console.Error.WriteLine($"[ExpressExtractor] 伴随提取失败: {ex.Message}");
            }
            finally
            {
                _extractGate.Release();
            }
        }

        /// <summary>构造单轮 LLM 上下文：索引化消息 + 窄 Claim 输出说明。</summary>
        private static string BuildContext(List<PendingMessage> batch)
        {
            var sb = new StringBuilder();
            sb.AppendLine("以下是一段对话。请从每条消息中挑出值得长期记住的，输出为窄断言（\"用户说了X\"）。");
            sb.AppendLine("窄断言要求：主语=发送者，谓词=\"说了\"，宾语=消息内容（原文）。");
            sb.AppendLine("请只输出 JSON，不要任何解释。JSON 格式：");
            sb.AppendLine("{\"claims\":[{\"message_index\":0,\"raw\":\"用户说了……\",\"strength\":0.8}]}");
            sb.AppendLine("其中 message_index 对应该条消息在对话列表中的序号。如果没有任何值得记住的，输出 {\"claims\":[]}。");
            sb.AppendLine();
            sb.AppendLine("对话：");
            for (int i = 0; i < batch.Count; i++)
                sb.AppendLine($"[{i}] <{batch[i].SenderName}> {batch[i].Content}");
            return sb.ToString();
        }

        // ---- 解析（容错） ----

        /// <summary>
        /// 解析 LLM 输出。优先 JSON；LLM 可能返回非 JSON，此时回退为对整批消息产窄 Claim，
        /// 保证沉默监听不丢尾巴。空 / "无" / 空数组按"无可提取"处理（不产生回退噪音）。
        /// </summary>
        private static List<ClaimDto> ParseLlmOutput(string? output, List<PendingMessage> batch)
        {
            if (string.IsNullOrWhiteSpace(output)) return new List<ClaimDto>();

            var trimmed = output.Trim();
            if (trimmed is "无" or "无。" or "[]" or "{}") return new List<ClaimDto>();

            // 剥掉 markdown 代码围栏（```json ... ``` 或 ``` ... ```）
            trimmed = TextUtil.StripMarkdownCodeFence(trimmed);

            JToken token;
            try
            {
                token = JToken.Parse(trimmed);
            }
            catch
            {
                // 非 JSON → 容错回退窄 Claim
                return FallbackNarrowClaims(batch);
            }

            var items = new List<JObject>();
            if (token is JArray array)
            {
                items.AddRange(array.OfType<JObject>());
            }
            else if (token is JObject obj)
            {
                // 容器 {"claims":[...]}，或单条 claim 对象
                var claimsArr = obj["claims"] as JArray;
                if (claimsArr != null)
                    items.AddRange(claimsArr.OfType<JObject>());
                else if (obj["message_index"] != null || obj["raw"] != null || obj["subject"] != null)
                    items.Add(obj);
            }

            var result = new List<ClaimDto>();
            foreach (var item in items)
            {
                var claim = BuildClaimFromItem(item, batch);
                if (claim != null) result.Add(claim);
            }
            return result;
        }

        /// <summary>从单个 claim JSON 对象构造 ClaimDto。无法定位消息时返回 null（丢弃该条）。</summary>
        private static ClaimDto? BuildClaimFromItem(JObject item, List<PendingMessage> batch)
        {
            var msg = ResolveMessage(item, batch);
            if (msg == null) return null;

            // 默认窄 Claim 骨架
            var claim = BuildNarrowClaim(msg);

            // 允许模型显式覆盖槽位 / 字段（容错解析字符串或对象槽位）
            var subject = ParseSlot(item["subject"]);
            if (subject != null) claim.Subject = subject;
            var predicate = ParseSlot(item["predicate"]);
            if (predicate != null) claim.Predicate = predicate;
            var obj = ParseSlot(item["object"]);
            if (obj != null) claim.Object = obj;

            var raw = item["raw"]?.ToString();
            if (!string.IsNullOrWhiteSpace(raw)) claim.Raw = raw;
            claim.Strength = item["strength"]?.ToObject<float?>() ?? claim.Strength;
            claim.Credibility = item["credibility"]?.ToObject<float?>() ?? claim.Credibility;
            claim.IsInference = item["is_inference"]?.ToObject<bool>() ?? claim.IsInference;
            return claim;
        }

        /// <summary>窄 Claim（subject=发送者 entity，predicate="说了" relation，object=内容 literal，带 source 引用）。</summary>
        private static ClaimDto BuildNarrowClaim(PendingMessage m)
        {
            return new ClaimDto
            {
                Subject = new SlotDto { Type = "entity", Index = m.SenderName },
                Predicate = new SlotDto { Type = "relation", Index = PredicateSaid },
                Object = new SlotDto { Type = "literal", Index = m.Content },
                Raw = $"{m.SenderName}说：{m.Content}",
                SourceJson = BuildSourceJson(m),
                UtteranceId = BuildUtteranceId(m),
                Strength = 1.0f,
                Credibility = 1.0f, // 用户亲口说的 → stated ≈ 1.0
                IsInference = false,
            };
        }

        /// <summary>回退窄 Claim：LLM 输出不可用时，对整批消息逐条产窄 Claim。</summary>
        private static List<ClaimDto> FallbackNarrowClaims(List<PendingMessage> batch)
            => batch.Select(BuildNarrowClaim).ToList();

        /// <summary>定位 claim 引用的消息：优先 message_index，其次按 raw 文本反查。</summary>
        private static PendingMessage? ResolveMessage(JObject item, List<PendingMessage> batch)
        {
            var index = item["message_index"]?.ToObject<int?>();
            if (index.HasValue && index.Value >= 0 && index.Value < batch.Count)
                return batch[index.Value];

            var raw = item["raw"]?.ToString();
            if (!string.IsNullOrWhiteSpace(raw))
                return batch.FirstOrDefault(m => raw.Contains(m.Content, StringComparison.OrdinalIgnoreCase));
            return null;
        }

        /// <summary>槽位容错解析：接受字符串（默认 entity）或对象 { type, index, ref }。</summary>
        private static SlotDto? ParseSlot(JToken? token)
        {
            if (token == null) return null;
            if (token.Type == JTokenType.String)
            {
                var text = token.ToString().Trim();
                if (text.Length == 0) return null;
                return new SlotDto { Type = "entity", Index = text };
            }
            if (token.Type == JTokenType.Object)
            {
                var obj = (JObject)token;
                var index = obj["index"]?.ToString();
                if (string.IsNullOrWhiteSpace(index)) return null;
                var type = obj["type"]?.ToString();
                return new SlotDto
                {
                    Type = string.IsNullOrWhiteSpace(type) ? "entity" : type,
                    Index = index,
                    Ref = obj["ref"]?.ToObject<int?>(),
                };
            }
            return null;
        }

        /// <summary>source 消息引用（03 契约：who + ref{kind:message,id}，附 channel 便于追溯）。</summary>
        private static string BuildSourceJson(PendingMessage m)
        {
            var source = new JObject
            {
                ["who"] = m.SenderName,
                ["kind"] = "message",
                ["channel"] = m.ChannelId,
                ["ref"] = new JObject { ["kind"] = "message", ["id"] = m.Sequence },
            };
            return source.ToString(Formatting.None);
        }

        /// <summary>utterance_id：以频道 + 消息序号生成，同一条消息的多条断言可共享同源分组。</summary>
        private static string BuildUtteranceId(PendingMessage m) => $"expr_{m.ChannelId}_{m.Sequence}";

        /// <summary>攒批中的一条消息。</summary>
        private sealed class PendingMessage
        {
            public string ChannelId { get; }
            public string SenderName { get; }
            public string Content { get; }
            public long Sequence { get; }

            public PendingMessage(string channelId, string senderName, string content, long sequence)
            {
                ChannelId = channelId;
                SenderName = senderName;
                Content = content;
                Sequence = sequence;
            }
        }
    }
}
