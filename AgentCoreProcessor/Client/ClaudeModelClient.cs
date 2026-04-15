using AgentCoreProcessor.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Client
{
    /// <summary>
    /// Claude 原生 Messages API 实现。
    /// </summary>
    public class ClaudeModelClient : ModelClientBase
    {
        private const string DefaultAnthropicVersion = "2023-06-01";
        private const int DefaultMaxTokens = 4096;

        public ClaudeModelClient() : base() { }
        public ClaudeModelClient(ApiClientCfg cfg) : base(cfg) { }

        public override async Task<string> StreamChatAsync(Action<ApiResponse> onDelta, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(onDelta);

            var history = GetConversationHistory();

            // 提取 system 消息，拼接为顶层 system 字段
            var systemParts = history.Where(m => m.Role == "system").Select(m => m.Content).ToList();
            var systemText = systemParts.Count > 0 ? string.Join("\n\n", systemParts) : null;

            // 非 system 消息转为 Claude 格式
            var messages = new JArray();
            foreach (var msg in history.Where(m => m.Role != "system"))
            {
                messages.Add(new JObject
                {
                    ["role"] = msg.Role,
                    ["content"] = msg.Content
                });
            }

            // 构造请求体
            var body = new JObject
            {
                ["model"] = apiClientCfg.Model,
                ["messages"] = messages,
                ["max_tokens"] = apiClientCfg.MaxTokens ?? DefaultMaxTokens,
                ["stream"] = apiClientCfg.Stream
            };

            if (systemText != null)
                body["system"] = systemText;
            if (apiClientCfg.Temperature > 0)
                body["temperature"] = apiClientCfg.Temperature;
            if (apiClientCfg.TopP.HasValue)
                body["top_p"] = apiClientCfg.TopP.Value;

            // ExtraBody 注入（如 thinking 配置）
            if (apiClientCfg.ExtraBody != null)
            {
                foreach (var kv in apiClientCfg.ExtraBody)
                    body[kv.Key] = JToken.FromObject(kv.Value);
            }

            var json = body.ToString(Formatting.None);

            using var request = new HttpRequestMessage(HttpMethod.Post, apiClientCfg.ApiEndpoint);
            request.Headers.Add("x-api-key", apiClientCfg.ApiKey);
            request.Headers.Add("anthropic-version", apiClientCfg.AnthropicVersion ?? DefaultAnthropicVersion);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(responseStream);

            var fullContent = new StringBuilder();
            bool isThinkingBlock = false;
            int inputTokens = 0;
            int outputTokens = 0;

            // Claude SSE 解析
            string? currentEventType = null;

            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null) break;

                // event: 行
                if (line.StartsWith("event: ", StringComparison.Ordinal))
                {
                    currentEventType = line.Substring(7).Trim();
                    continue;
                }

                // data: 行
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                var payload = line.Substring(6);

                JObject data;
                try { data = JObject.Parse(payload); }
                catch { continue; }

                switch (currentEventType)
                {
                    case "message_start":
                        var msgUsage = data["message"]?["usage"];
                        if (msgUsage != null)
                            inputTokens = msgUsage["input_tokens"]?.Value<int>() ?? 0;
                        break;

                    case "content_block_start":
                        var blockType = data["content_block"]?["type"]?.ToString();
                        isThinkingBlock = blockType == "thinking";
                        break;

                    case "content_block_delta":
                        var deltaType = data["delta"]?["type"]?.ToString();
                        string? text = null;

                        if (deltaType == "thinking_delta")
                            text = data["delta"]?["thinking"]?.ToString();
                        else if (deltaType == "text_delta")
                            text = data["delta"]?["text"]?.ToString();

                        if (text != null)
                        {
                            var synth = BuildSyntheticResponse(
                                isThinkingBlock ? null : text,
                                isThinkingBlock ? text : null);

                            if (!isThinkingBlock)
                                fullContent.Append(text);

                            onDelta(synth);
                        }
                        break;

                    case "content_block_stop":
                        isThinkingBlock = false;
                        break;

                    case "message_delta":
                        var deltaUsage = data["usage"];
                        if (deltaUsage != null)
                            outputTokens = deltaUsage["output_tokens"]?.Value<int>() ?? 0;

                        // 发送带 usage 的最终 delta
                        var usageResponse = BuildSyntheticResponse(null, null);
                        usageResponse.Usage = new Usage
                        {
                            PromptTokens = inputTokens,
                            CompletionTokens = outputTokens,
                            TotalTokens = inputTokens + outputTokens
                        };
                        onDelta(usageResponse);
                        break;

                    case "message_stop":
                        goto done;
                }
            }

            done:
            return fullContent.ToString();
        }

        private static ApiResponse BuildSyntheticResponse(string? content, string? reasoning)
        {
            return new ApiResponse
            {
                Choices = new List<Choice>
                {
                    new Choice
                    {
                        Index = 0,
                        Delta = new Delta
                        {
                            Content = content,
                            ReasoningContent = reasoning
                        }
                    }
                }
            };
        }
    }
}
