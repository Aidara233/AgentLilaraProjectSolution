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
        private const int FormatVersion = 1;
        private readonly string _filePath;

        public ChannelContextPersistence(int channelId)
        {
            var dir = Path.Combine(PathConfig.StoragePath, "ChannelContexts");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, $"channel_{channelId}.json");
        }

        /// <summary>
        /// Append one round (user+assistant messages) to existing context file.
        /// Loads current state, appends, atomically writes back.
        /// </summary>
        public void AppendRound(List<Message> userMsgs, List<Message> asstMsgs)
        {
            var (summary, mode, rounds) = LoadContext();
            rounds.Add(userMsgs.Concat(asstMsgs).ToList());
            SaveContext(summary, mode, rounds);
        }

        /// <summary>Save compression result: replace summary and rounds.</summary>
        public void SaveCompressionResult(string summary, List<Message> retained, string mode)
        {
            var rounds = new List<List<Message>>();
            for (int i = 0; i < retained.Count; i += 2)
            {
                var pair = new List<Message> { retained[i] };
                if (i + 1 < retained.Count) pair.Add(retained[i + 1]);
                rounds.Add(pair);
            }
            SaveContext(summary, mode, rounds);
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
                    Rounds = rounds.Select(r => new
                    {
                        User = r.Where(m => m.Role == "user").ToList(),
                        Assistant = r.Where(m => m.Role == "assistant").ToList()
                    }).ToList()
                };
                var json = JsonConvert.SerializeObject(data, Formatting.Indented,
                    new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                var tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _filePath, overwrite: true);
            }
            catch { /* best-effort persistence, don't crash on IO errors */ }
        }

        /// <summary>Load context: (summary, mode, rounds). Each round is a flat list of messages.</summary>
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
                if (version == null || version < FormatVersion)
                {
                    File.Delete(_filePath);
                    return (null, "working", new List<List<Message>>());
                }

                string? summary = wrapper.Summary;
                string? mode = wrapper.State?.Mode ?? "working";

                var rounds = new List<List<Message>>();
                if (wrapper.Rounds != null)
                {
                    foreach (var round in wrapper.Rounds)
                    {
                        var msgs = new List<Message>();
                        if (round.User != null)
                            msgs.AddRange(DeserializeMessages(round.User));
                        if (round.Assistant != null)
                            msgs.AddRange(DeserializeMessages(round.Assistant));
                        if (msgs.Count > 0) rounds.Add(msgs);
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
