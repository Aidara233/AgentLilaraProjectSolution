using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Models;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine
{
    internal class ChannelContextPersistence
    {
        private const int FormatVersion = 3;
        private readonly string _filePath;

        public ChannelContextPersistence(int channelId)
        {
            var dir = Path.Combine(PathConfig.StoragePath, "ChannelContexts");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, $"channel_{channelId}.json");
        }

        /// <summary>Save full context state including all rounds.</summary>
        public void SaveContext(string? summary, string? mode, List<List<Message>> rounds,
            int lastConsumedMessageId, string? escalateReason)
        {
            try
            {
                var data = new
                {
                    FormatVersion,
                    UpdatedAt = DateTime.Now,
                    Summary = summary,
                    State = new
                    {
                        Mode = mode ?? "working",
                        LastConsumedMessageId = lastConsumedMessageId,
                        EscalateReason = escalateReason
                    },
                    Rounds = rounds.Select(r => r.ToList()).ToList()
                };
                var json = JsonConvert.SerializeObject(data, Formatting.Indented,
                    new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                var tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _filePath, overwrite: true);
            }
            catch { /* best-effort persistence, don't crash on IO errors */ }
        }

        /// <summary>Load context: (summary, mode, rounds, lastConsumedMessageId, escalateReason).</summary>
        public (string? Summary, string? Mode, List<List<Message>> Rounds,
            int LastConsumedMessageId, string? EscalateReason) LoadContext()
        {
            if (!File.Exists(_filePath))
                return (null, "working", new List<List<Message>>(), 0, null);

            try
            {
                var json = File.ReadAllText(_filePath);
                dynamic? wrapper = JsonConvert.DeserializeObject(json);
                if (wrapper == null)
                    return (null, "working", new List<List<Message>>(), 0, null);

                int? version = wrapper.FormatVersion;
                if (version == null || version < 1)
                {
                    File.Delete(_filePath);
                    return (null, "working", new List<List<Message>>(), 0, null);
                }

                string? summary = wrapper.Summary;
                string? mode = wrapper.State?.Mode ?? "working";
                int cursor = (int?)(wrapper.State?.LastConsumedMessageId) ?? 0;
                string? reason = wrapper.State?.EscalateReason;

                var rounds = new List<List<Message>>();
                if (wrapper.Rounds != null)
                {
                    if (version >= 2)
                    {
                        foreach (var round in wrapper.Rounds)
                        {
                            List<Message>? msgs = DeserializeMessages(round);
                            if (msgs is { Count: > 0 }) rounds.Add(msgs);
                        }
                    }
                }
                return (summary, mode, rounds, cursor, reason);
            }
            catch { return (null, "working", new List<List<Message>>(), 0, null); }
        }

        /// <summary>
        /// 频道级游标接力：只推进 LastConsumedMessageId，保留 summary/mode/rounds 不变。
        /// session 占有频道期间消费消息后调用，使持久化游标始终是频道级唯一真相——
        /// session 结束后 ChannelEngine 从同一游标恢复，不重读 session 已处理的消息。
        /// 仅当 newCursor 大于已存游标时写入（单调递增，避免回退）。
        /// </summary>
        public void AdvanceCursor(int newCursor)
        {
            if (newCursor <= 0) return;
            var (summary, mode, rounds, current, _) = LoadContext();
            if (newCursor <= current) return;
            SaveContext(summary, mode, rounds, newCursor, null);
        }

        private List<Message>? DeserializeMessages(dynamic obj)
            => (obj is Newtonsoft.Json.Linq.JToken jt) ? jt.ToObject<List<Message>>() : null;
    }
}
