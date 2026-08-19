// AgentCoreProcessor/Engine/Session/SessionPersistence.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Models;
using AgentLilara.PluginSDK;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Engine.Session
{
    /// <summary>
    /// Session 三文件持久化封装：spec.json（一次写）/ state.json（每轮原子重写）/ history.jsonl（追加）。
    /// 崩溃恢复由 SessionEngine.RestoreAll 调用 Load() 后重建。
    /// </summary>
    internal class SessionPersistence
    {
        private const int SpecFormatVersion = 2;
        private const int StateFormatVersion = 2;

        private readonly string _dir;
        private readonly string _specPath;
        private readonly string _statePath;
        private readonly string _historyPath;

        public SessionPersistence(string sessionId)
        {
            _dir = Path.Combine(PathConfig.StoragePath, "Sessions", sessionId);
            Directory.CreateDirectory(_dir);
            _specPath = Path.Combine(_dir, "spec.json");
            _statePath = Path.Combine(_dir, "state.json");
            _historyPath = Path.Combine(_dir, "history.jsonl");
        }

        // ═══════════════════════════════════════════════════════════
        // spec.json：创建时一次写入（不可变）
        // ═══════════════════════════════════════════════════════════

        public Task SaveSpecAsync(CreateSessionSpec spec, DateTime createdAt)
        {
            var data = new
            {
                formatVersion = SpecFormatVersion,
                sessionId = Path.GetFileName(_dir),
                createdAt,
                type = spec.Type,
                additionalComponents = spec.AdditionalComponents,
                excludedComponents = spec.ExcludedComponents,
                resolvedComponentIds = spec.ResolvedComponentIds,
                channelId = spec.ChannelId,
                autoClaimChannel = spec.AutoClaimChannel,
                inputPorts = spec.InputPorts,
                outputPorts = spec.OutputPorts,
                initialState = spec.InitialState,
                initActions = spec.InitActions,
                parentSessionId = spec.ParentSessionId
            };
            var json = JsonConvert.SerializeObject(data, Formatting.Indented,
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
            return File.WriteAllTextAsync(_specPath, json);
        }

        // ═══════════════════════════════════════════════════════════
        // state.json：每轮原子重写（tmp+move）
        // ═══════════════════════════════════════════════════════════

        public async Task SaveStateAsync(SessionStateSnapshot state)
        {
            var data = new
            {
                formatVersion = StateFormatVersion,
                sessionId = state.SessionId,
                status = state.Status.ToString(),
                createdAt = state.CreatedAt,
                roundCount = state.RoundCount,
                waitUntil = state.WaitUntil,
                lastActivity = state.LastActivity,
                compressionTier = state.CompressionTier,
                activeCoreConfig = state.ActiveCoreConfig,
                connectedChannelId = state.ConnectedChannelId,
                connectedMode = state.ConnectedMode,
                channelCursors = state.ChannelCursors,
                childSessionIds = state.ChildSessionIds ?? Array.Empty<string>(),
                effectiveParentId = state.EffectiveParentId,
                forwardedTo = state.ForwardedTo
            };
            var json = JsonConvert.SerializeObject(data, Formatting.Indented,
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
            var tmp = _statePath + ".tmp";
            await File.WriteAllTextAsync(tmp, json);
            File.Move(tmp, _statePath, overwrite: true);
        }

        /// <summary>
        /// 仅改写 state.json 的 status 字段（保留其它字段），用于 RestoreAll 把被淘汰存档
        /// 标记为 Completed，避免下次启动重新尝试恢复已被最新存档接替的旧 session。
        /// </summary>
        public async Task UpdateStatusAsync(SessionStatus newStatus)
        {
            if (!File.Exists(_statePath)) return;
            var raw = await File.ReadAllTextAsync(_statePath);
            var jo = JObject.Parse(raw);
            jo["status"] = newStatus.ToString();
            var tmp = _statePath + ".tmp";
            await File.WriteAllTextAsync(tmp, jo.ToString(Formatting.Indented));
            File.Move(tmp, _statePath, overwrite: true);
        }

        /// <summary>
        /// 改写指定子 session 的 state.json 的 effectiveParentId 字段。
        /// </summary>
        /// <summary>
        /// 改写指定子 session 的 state.json 的 effectiveParentId 字段。
        /// </summary>
        public async Task UpdateEffectiveParentAsync(string childId, string newParentId)
        {
            var childDir = Path.Combine(PathConfig.StoragePath, "Sessions", childId);
            var childStatePath = Path.Combine(childDir, "state.json");
            if (!File.Exists(childStatePath)) return;
            var raw = await File.ReadAllTextAsync(childStatePath);
            var jo = JObject.Parse(raw);
            jo["effectiveParentId"] = newParentId;
            var tmp = childStatePath + ".tmp";
            await File.WriteAllTextAsync(tmp, jo.ToString(Formatting.Indented));
            File.Move(tmp, childStatePath, overwrite: true);
        }

        // ═══════════════════════════════════════════════════════════
        // history.jsonl：每轮追加（assistant + tool_result 一起写）
        // ═══════════════════════════════════════════════════════════

        public async Task AppendHistoryAsync(List<Message> newMessages)
        {
            if (newMessages == null || newMessages.Count == 0) return;
            var lines = new List<string>(newMessages.Count);
            foreach (var m in newMessages)
            {
                lines.Add(JsonConvert.SerializeObject(TrimForPersistence(m),
                    new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
            }
            await File.AppendAllLinesAsync(_historyPath, lines);
        }

        /// <summary>
        /// 序列化前裁剪超大数据，防止超大 history 文件膨胀（如 base64 图片本体）。
        /// - 图片 base64 超过阈值（64KB）时替换为占位符，仅在 history 中保留长度标记；
        /// - 保留图片路径（ImagePath），需要时按路径重新读取。
        /// - 同样处理 tool_use/tool_result 中的超大文本。
        /// </summary>
        private static Message TrimForPersistence(Message m)
        {
            const int MaxInlineSize = 64 * 1024; // 64KB 阈值，超过则截断内联数据
            if (m.ContentParts == null || m.ContentParts.Count == 0)
                return m;

            var trimmed = new Message
            {
                Role = m.Role,
                Content = m.Content,
                Name = m.Name,
                ContentParts = new List<ContentPart>(m.ContentParts.Count)
            };

            foreach (var p in m.ContentParts)
            {
                if (p.Type == "image")
                {
                    if (!string.IsNullOrEmpty(p.ImageBase64) && p.ImageBase64.Length > MaxInlineSize)
                    {
                        trimmed.ContentParts.Add(new ContentPart
                        {
                            Type = "image",
                            ImagePath = p.ImagePath,
                            MediaType = p.MediaType,
                            Text = p.Text,
                            // 不写 base64 本体，保留长度供日志/统计
                            ImageBase64 = $"[base64 已归档：{p.ImageBase64.Length} bytes]"
                        });
                    }
                    else
                    {
                        trimmed.ContentParts.Add(p);
                    }
                }
                else if (p.Type == "tool_use" || p.Type == "tool_result")
                {
                    if (!string.IsNullOrEmpty(p.ToolInput) && p.ToolInput.Length > MaxInlineSize)
                    {
                        var cp = new ContentPart
                        {
                            Type = p.Type,
                            ToolUseId = p.ToolUseId,
                            ToolName = p.ToolName,
                            IsError = p.IsError,
                            Text = p.Text,
                            ToolInput = $"[输入已归档：{p.ToolInput.Length} bytes]"
                        };
                        trimmed.ContentParts.Add(cp);
                    }
                    else
                    {
                        trimmed.ContentParts.Add(p);
                    }
                }
                else
                {
                    trimmed.ContentParts.Add(p);
                }
            }
            return trimmed;
        }

        // ═══════════════════════════════════════════════════════════
        // Load：全量读，返回 spec + state + history
        // 崩溃半截处理：丢弃末尾无配对 tool_result 的 assistant tool_use
        // ═══════════════════════════════════════════════════════════

        public (CreateSessionSpec Spec, SessionStateSnapshot State, List<Message> History) Load()
        {
            var spec = LoadSpec();
            var state = LoadState();
            var history = LoadHistory();
            return (spec, state, history);
        }

        private CreateSessionSpec LoadSpec()
        {
            if (!File.Exists(_specPath))
                throw new InvalidOperationException($"spec.json missing for session {Path.GetFileName(_dir)}");

            var json = File.ReadAllText(_specPath);
            var wrapper = JsonConvert.DeserializeObject<SpecWrapper>(json)
                ?? throw new InvalidOperationException($"spec.json corrupted for session {Path.GetFileName(_dir)}");

            var spec = new CreateSessionSpec
            {
                Type = string.IsNullOrEmpty(wrapper.Type) ? "channel" : wrapper.Type,
                AdditionalComponents = wrapper.AdditionalComponents,
                ExcludedComponents = wrapper.ExcludedComponents,
                ResolvedComponentIds = wrapper.ResolvedComponentIds,
                ChannelId = wrapper.ChannelId,
                AutoClaimChannel = wrapper.AutoClaimChannel,
                InputPorts = wrapper.InputPorts ?? new(),
                OutputPorts = wrapper.OutputPorts ?? new(),
                InitialState = wrapper.InitialState,
                InitActions = wrapper.InitActions ?? new(),
                ParentSessionId = wrapper.ParentSessionId
            };
            return spec;
        }

        private SessionStateSnapshot LoadState()
        {
            if (!File.Exists(_statePath))
            {
                // state.json 不存在视为 Crashed（spec 写完但 state 还没落地）
                return new SessionStateSnapshot
                {
                    SessionId = Path.GetFileName(_dir),
                    Status = SessionStatus.Crashed,
                    CreatedAt = DateTime.UtcNow,
                    RoundCount = 0,
                    LastActivity = DateTime.UtcNow
                };
            }

            try
            {
                var json = File.ReadAllText(_statePath);
                var wrapper = JsonConvert.DeserializeObject<StateWrapper>(json);
                if (wrapper == null)
                    return new SessionStateSnapshot
                    {
                        SessionId = Path.GetFileName(_dir),
                        Status = SessionStatus.Crashed,
                        CreatedAt = DateTime.UtcNow,
                        RoundCount = 0,
                        LastActivity = DateTime.UtcNow
                    };

                var status = Enum.TryParse<SessionStatus>(wrapper.Status, true, out var s)
                    ? s
                    : SessionStatus.Crashed;

                var cursors = wrapper.ChannelCursors ?? new Dictionary<int, int>();
                if (cursors.Keys.Any(k => k.GetType() != typeof(int)))
                {
                    // JSON 反序列化数字键可能为字符串，做一次重构
                    var rebuilt = new Dictionary<int, int>();
                    foreach (var kv in cursors)
                    {
                        if (int.TryParse(kv.Key.ToString(), out var k2))
                            rebuilt[k2] = kv.Value;
                    }
                    cursors = rebuilt;
                }

                return new SessionStateSnapshot
                {
                    SessionId = wrapper.SessionId ?? Path.GetFileName(_dir),
                    Status = status,
                    CreatedAt = wrapper.CreatedAt,
                    RoundCount = wrapper.RoundCount,
                    WaitUntil = wrapper.WaitUntil,
                    LastActivity = wrapper.LastActivity,
                    CompressionTier = wrapper.CompressionTier ?? "None",
                    ActiveCoreConfig = wrapper.ActiveCoreConfig ?? "",
                    ConnectedChannelId = wrapper.ConnectedChannelId,
                    ConnectedMode = wrapper.ConnectedMode,
                    ChannelCursors = cursors,
                    ChildSessionIds = wrapper.ChildSessionIds ?? Array.Empty<string>(),
                    EffectiveParentId = wrapper.EffectiveParentId,
                    ForwardedTo = wrapper.ForwardedTo
                };
            }
            catch
            {
                return new SessionStateSnapshot
                {
                    SessionId = Path.GetFileName(_dir),
                    Status = SessionStatus.Crashed,
                    CreatedAt = DateTime.UtcNow,
                    RoundCount = 0,
                    LastActivity = DateTime.UtcNow
                };
            }
        }

        private List<Message> LoadHistory(int maxLines = 1000)
        {
            var history = new List<Message>();
            if (!File.Exists(_historyPath)) return history;
            if (maxLines <= 0) maxLines = 1000;

            // 只读取尾部最近 N 行（超大 history 全量读取会膨胀内存；尾部是最近上下文，优先保留）
            var lines = File.ReadLines(_historyPath);
            var tail = new Queue<string>();
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (tail.Count >= maxLines) tail.Dequeue();
                tail.Enqueue(line);
            }

            foreach (var line in tail)
            {
                try
                {
                    var msg = JsonConvert.DeserializeObject<Message>(line);
                    if (msg != null)
                        history.Add(msg);
                }
                catch { /* 跳过损坏的行 */ }
            }

            // 崩溃半截处理：末尾 assistant 含 tool_use 但下一条不是对应的 tool_result → 丢弃
            if (history.Count > 0)
            {
                var last = history[^1];
                if (last.Role == "assistant"
                    && last.ContentParts != null
                    && last.ContentParts.Any(p => p.Type == "tool_use"))
                {
                    history.RemoveAt(history.Count - 1);
                }
            }

            return history;
        }

        // ═══════════════════════════════════════════════════════════
        // Archive：留档（Phase 1-6 范围内为空实现，不删除存档目录）
        // ═══════════════════════════════════════════════════════════

        public Task ArchiveAsync() => Task.CompletedTask;
    }

    /// <summary>
    /// Session 运行状态快照。每轮原子重写到 state.json。
    /// </summary>
    internal class SessionStateSnapshot
    {
        public string SessionId { get; set; } = "";
        public SessionStatus Status { get; set; } = SessionStatus.Creating;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public int RoundCount { get; set; }
        public DateTime? WaitUntil { get; set; }
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;
        public string CompressionTier { get; set; } = "None";
        public string ActiveCoreConfig { get; set; } = "";
        public string? ConnectedChannelId { get; set; }
        public string? ConnectedMode { get; set; }
        public Dictionary<int, int> ChannelCursors { get; set; } = new();
        public string[] ChildSessionIds { get; set; } = Array.Empty<string>();
        public string? EffectiveParentId { get; set; }
        public string? ForwardedTo { get; set; }
    }

    // 内部反序列化包装类型（仅用于 JsonConvert.DeserializeAnonymousType 替代）
    internal class SpecWrapper
    {
        [JsonProperty("formatVersion")] public int? FormatVersion;
        [JsonProperty("sessionId")] public string? SessionId;
        [JsonProperty("createdAt")] public DateTime CreatedAt;
        [JsonProperty("type")] public string? Type;
        [JsonProperty("additionalComponents")] public string[]? AdditionalComponents;
        [JsonProperty("excludedComponents")] public string[]? ExcludedComponents;
        [JsonProperty("resolvedComponentIds")] public string[]? ResolvedComponentIds;
        [JsonProperty("channelId")] public int? ChannelId;
        [JsonProperty("autoClaimChannel")] public bool AutoClaimChannel;
        [JsonProperty("inputPorts")] public Dictionary<string, PortSpec>? InputPorts;
        [JsonProperty("outputPorts")] public Dictionary<string, string>? OutputPorts;
        [JsonProperty("initialState")] public object? InitialState;
        [JsonProperty("initActions")] public List<ToolCall>? InitActions;
        [JsonProperty("parentSessionId")] public string? ParentSessionId;
    }

    internal class StateWrapper
    {
        [JsonProperty("formatVersion")] public int? FormatVersion;
        [JsonProperty("sessionId")] public string? SessionId;
        [JsonProperty("status")] public string? Status;
        [JsonProperty("createdAt")] public DateTime CreatedAt;
        [JsonProperty("roundCount")] public int RoundCount;
        [JsonProperty("waitUntil")] public DateTime? WaitUntil;
        [JsonProperty("lastActivity")] public DateTime LastActivity;
        [JsonProperty("compressionTier")] public string? CompressionTier;
        [JsonProperty("activeCoreConfig")] public string? ActiveCoreConfig;
        [JsonProperty("connectedChannelId")] public string? ConnectedChannelId;
        [JsonProperty("connectedMode")] public string? ConnectedMode;
        // JSON 键可能为字符串，反序列化为 string→int 便于解析
        [JsonProperty("channelCursors")]
        public Dictionary<int, int> ChannelCursors { get; set; } = new();
        [JsonProperty("childSessionIds")] public string[]? ChildSessionIds;
        [JsonProperty("effectiveParentId")] public string? EffectiveParentId;
        [JsonProperty("forwardedTo")] public string? ForwardedTo;
    }
}
