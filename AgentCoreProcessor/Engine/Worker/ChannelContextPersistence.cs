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
        private const int FormatVersion = 2;
        private readonly string _filePath;

        public ChannelContextPersistence(int channelId)
        {
            var dir = Path.Combine(PathConfig.StoragePath, "ChannelContexts");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, $"channel_{channelId}.json");
        }

        /// <summary>Save full context state including all rounds.</summary>
        public void SaveContext(string? summary, string? mode, List<List<Message>> rounds)
        {
            try
            {
                var data = new
                {
                    FormatVersion,
                    UpdatedAt = DateTime.Now,
                    Summary = summary,
                    State = new { Mode = mode ?? "working" },
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
        public void SaveCompressionResult(string summary, List<Message> retained, string mode)
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
            SaveContext(summary, mode, rounds);
        }

        /// <summary>Load context: (summary, mode, rounds). Each round is a flat list of messages in order.</summary>
        public (string? Summary, string? Mode, List<List<Message>> Rounds) LoadContext()
        {
            if (!File.Exists(_filePath))
                return (null, "working", new List<List<Message>>());

            try
            {
                var json = File.ReadAllText(_filePath);
                dynamic? wrapper = JsonConvert.DeserializeObject(json);
                if (wrapper == null)
                    return (null, "working", new List<List<Message>>());

                int? version = wrapper.FormatVersion;
                if (version == null || version < 1)
                {
                    File.Delete(_filePath);
                    return (null, "working", new List<List<Message>>());
                }

                string? summary = wrapper.Summary;
                string? mode = wrapper.State?.Mode ?? "working";

                var rounds = new List<List<Message>>();
                if (wrapper.Rounds != null)
                {
                    if (version >= 2)
                    {
                        // V2: rounds 是 Message[] 的数组，保持原始顺序
                        foreach (var round in wrapper.Rounds)
                        {
                            var msgs = DeserializeMessages(round);
                            if (msgs != null && msgs.Count > 0) rounds.Add(msgs);
                        }
                    }
                    else
                    {
                        // V1 兼容：rounds 是 { User: [], Assistant: [] } 格式
                        foreach (var round in wrapper.Rounds)
                        {
                            var msgs = new List<Message>();
                            if (round.User != null)
                            {
                                var userMsgs = DeserializeMessages(round.User);
                                if (userMsgs != null) msgs.AddRange(userMsgs);
                            }
                            if (round.Assistant != null)
                            {
                                var asstMsgs = DeserializeMessages(round.Assistant);
                                if (asstMsgs != null) msgs.AddRange(asstMsgs);
                            }
                            if (msgs.Count > 0) rounds.Add(msgs);
                        }
                    }
                }
                return (summary, mode, rounds);
            }
            catch { return (null, "working", new List<List<Message>>()); }
        }

        private List<Message>? DeserializeMessages(dynamic obj)
            => JsonConvert.DeserializeObject<List<Message>>(JsonConvert.SerializeObject(obj));
    }
}
