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

        /// <summary>Save compression result: replace summary and rounds.</summary>
        public void SaveCompressionResult(string summary, List<Message> retained, string mode,
            int lastConsumedMessageId)
        {
            // 按 assistant 回复分割 rounds
            var rounds = new List<List<Message>>();
            var current = new List<Message>();
            foreach (var m in retained)
            {
                current.Add(m);
                if (m.Role == "assistant")
                {
                    rounds.Add(current);
                    current = new List<Message>();
                }
            }
            if (current.Count > 0) rounds.Add(current);
            SaveContext(summary, mode, rounds, lastConsumedMessageId, null);
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

        private List<Message>? DeserializeMessages(dynamic obj)
            => (obj is Newtonsoft.Json.Linq.JToken jt) ? jt.ToObject<List<Message>>() : null;
    }
}
