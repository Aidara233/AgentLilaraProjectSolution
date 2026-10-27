using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Logging;
using AgentCoreProcessor.Util;
using AgentLilara.PluginSDK.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Engine.Mind
{
    /// <summary>
    /// 伴随提取器（Express 模式，03-memory-engine.md 产生端）。
    /// 按消息攒批：攒够 N 条（默认 20）自动触发，或由 FlushAsync / TriggerIdleAsync（空闲补一次）显式触发。
    /// 每次触发 = 一轮轻量 LLM 调用（单轮、不开 session），产出语义三元组断言，
    /// 经 IWriterAccess.WriteBatchAsync 统一落库。
    /// 模型直接产出完整槽位（subject/predicate/object），关键词槽位由 Writer 建索引参与检索与冲突检测。
    /// 解析失败即提取失败（不兜底造断言）——消息仍在库里，可后续重提。
    /// LLM 调用入口由外部注入（IExtractionLlm，定义见 ExtractService.cs），本类不直接依赖模型客户端。
    /// </summary>
    internal class ExpressExtractor
    {
        /// <summary>默认攒批阈值。一段话题说完的量级，够 LLM 判断哪些值得留。</summary>
        public const int DefaultBatchSize = 20;

        /// <summary>空闲补提取的最低攒批数：低于此值不触发，避免为零星消息开调用。</summary>
        public const int IdleFlushMinCount = 3;

        /// <summary>空闲判定默认静默秒数：最后一条消息超过此时长没有新消息则补提取。</summary>
        public const int DefaultIdleSeconds = 120;

        /// <summary>可信度缺省值。默认假设所有信息都不一定为真。</summary>
        private const float DefaultCredibility = 0.7f;

        /// <summary>可信度夹取范围：不给 0/1 绝对值，留证据聚合与 Review 的调节空间。</summary>
        private const float MinCredibility = 0.05f;
        private const float MaxCredibility = 0.95f;

        /// <summary>合法槽位类型。</summary>
        private static readonly HashSet<string> ValidSlotTypes =
            new(StringComparer.OrdinalIgnoreCase) { "entity", "keyword", "attribute", "literal", "assertion" };

        /// <summary>合法谓词类型。</summary>
        private static readonly HashSet<string> ValidPredicateTypes =
            new(StringComparer.OrdinalIgnoreCase) { "relation", "attribute" };

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

        /// <summary>最后一条消息入批的时间（UTC）。供空闲判定用。</summary>
        private DateTime _lastMessageAt = DateTime.MinValue;

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
                _lastMessageAt = DateTime.UtcNow;
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
        /// 空闲补一次：把不足一攒批的尾巴也提取掉，保证沉默监听不漏尾。
        /// 需同时满足：攒批 ≥ <see cref="IdleFlushMinCount"/> 条，且静默超过 idleSeconds 秒。
        /// 由 SessionEngine 监控循环周期性调用。
        /// </summary>
        public async Task TriggerIdleAsync(int idleSeconds = DefaultIdleSeconds)
        {
            DateTime lastAt;
            int pending;
            lock (_bufferLock)
            {
                pending = _pending.Count;
                lastAt = _lastMessageAt;
            }

            if (pending < IdleFlushMinCount) return;
            if ((DateTime.UtcNow - lastAt).TotalSeconds < idleSeconds) return;

            await FlushAsync();
        }

        /// <summary>强制清空攒批（不看阈值与静默时长）。停机收尾用。</summary>
        public Task FlushAllAsync() => FlushAsync();

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
                // 伴随提取失败不阻断消息链路：LLM 异常 / 写库异常统一记录并吞掉
                Signal.Error(LogGroup.Memory, "伴随提取失败",
                    new { batchSize = batch.Count, error = ex.GetType().Name, message = ex.Message });
            }
            finally
            {
                _extractGate.Release();
            }
        }

        /// <summary>索引化消息列表。提取指令在 ExtractionPrompt.txt，这里只提供 {{MESSAGES}} 部分。</summary>
        private static string BuildContext(List<PendingMessage> batch)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < batch.Count; i++)
                sb.AppendLine($"[{i}] <{batch[i].SenderName}> {batch[i].Content}");
            return sb.ToString();
        }

        // ---- 解析（容错） ----

        /// <summary>
        /// 解析 LLM 输出为断言列表。解析失败返回空列表——消息仍在数据库里，不造假断言填库。
        /// 空 / "无" / 空数组按"无可提取"处理。
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
                Signal.Warn(LogGroup.Memory, "提取输出非 JSON，本批丢弃",
                    new { batchSize = batch.Count, preview = Preview(trimmed) });
                return new List<ClaimDto>();
            }

            var items = new List<JObject>();
            if (token is JArray array)
            {
                items.AddRange(array.OfType<JObject>());
            }
            else if (token is JObject obj)
            {
                // 容器 {"claims":[...]}，或单条 claim 对象
                if (obj["claims"] is JArray claimsArr)
                    items.AddRange(claimsArr.OfType<JObject>());
                else if (obj["subject"] != null)
                    items.Add(obj);
            }

            var result = new List<ClaimDto>();
            int dropped = 0;
            foreach (var item in items)
            {
                var claim = BuildClaimFromItem(item, batch);
                if (claim != null) result.Add(claim);
                else dropped++;
            }

            if (dropped > 0)
                Signal.Debug(LogGroup.Memory, "提取丢弃残缺断言",
                    new { dropped, kept = result.Count });

            return result;
        }

        /// <summary>
        /// 从单个 claim JSON 对象构造 ClaimDto。
        /// 消息定位失败或三槽位不齐 → 返回 null（丢弃该条，不兜底补全）。
        /// </summary>
        private static ClaimDto? BuildClaimFromItem(JObject item, List<PendingMessage> batch)
        {
            var msg = ResolveMessage(item, batch);
            if (msg == null) return null;

            // 三槽位必须齐全：任一缺失说明模型没按格式产出，丢弃
            var subject = ParseSlot(item["subject"], ValidSlotTypes, "keyword");
            var predicate = ParseSlot(item["predicate"], ValidPredicateTypes, "relation");
            var obj = ParseSlot(item["object"], ValidSlotTypes, "keyword");
            if (subject == null || predicate == null || obj == null) return null;

            var inferred = item["inferred"]?.ToObject<bool?>()
                        ?? item["is_inference"]?.ToObject<bool?>()
                        ?? false;

            var raw = item["raw"]?.ToString();
            if (string.IsNullOrWhiteSpace(raw))
                raw = $"{subject.Index} {predicate.Index} {obj.Index}";

            var strength = item["strength"]?.ToObject<float?>();
            if (strength.HasValue) strength = Math.Clamp(strength.Value, -1f, 1f);

            var credibility = item["credibility"]?.ToObject<float?>() ?? DefaultCredibility;
            credibility = Math.Clamp(credibility, MinCredibility, MaxCredibility);

            return new ClaimDto
            {
                Subject = subject,
                Predicate = predicate,
                Object = obj,
                Raw = raw,
                SourceJson = BuildSourceJson(msg),
                UtteranceId = BuildUtteranceId(msg),
                Strength = strength,
                Credibility = credibility,
                IsInference = inferred,
            };
        }

        /// <summary>定位 claim 引用的消息：优先 msg 序号，其次按 raw 文本反查。</summary>
        private static PendingMessage? ResolveMessage(JObject item, List<PendingMessage> batch)
        {
            var index = item["msg"]?.ToObject<int?>() ?? item["message_index"]?.ToObject<int?>();
            if (index.HasValue && index.Value >= 0 && index.Value < batch.Count)
                return batch[index.Value];

            var raw = item["raw"]?.ToString();
            if (!string.IsNullOrWhiteSpace(raw))
                return batch.FirstOrDefault(m => raw.Contains(m.Content, StringComparison.OrdinalIgnoreCase));
            return null;
        }

        /// <summary>
        /// 槽位解析：接受字符串（按 fallbackType 处理）或对象 { type, index, ref }。
        /// type 不在 validTypes 内时纠正为 fallbackType；index 为空返回 null。
        /// </summary>
        private static SlotDto? ParseSlot(JToken? token, HashSet<string> validTypes, string fallbackType)
        {
            if (token == null) return null;

            if (token.Type == JTokenType.String)
            {
                var text = token.ToString().Trim();
                if (text.Length == 0) return null;
                return new SlotDto { Type = fallbackType, Index = text };
            }

            if (token.Type == JTokenType.Object)
            {
                var obj = (JObject)token;
                var index = obj["index"]?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(index)) return null;

                var type = obj["type"]?.ToString();
                if (string.IsNullOrWhiteSpace(type) || !validTypes.Contains(type))
                    type = fallbackType;

                return new SlotDto
                {
                    Type = type.ToLowerInvariant(),
                    Index = index,
                    Ref = obj["ref"]?.ToObject<int?>(),
                };
            }

            return null;
        }

        /// <summary>日志用短预览。</summary>
        private static string Preview(string text)
            => text.Length <= 120 ? text : text[..120] + "…";

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
