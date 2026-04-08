using AgentCoreProcessor.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Client
{
    public class AIApiClient : IDisposable
    {
        private readonly HttpClient httpClient;
        private ApiClientCfg apiClientCfg;

        /// <summary>
        /// 供外部（如 Processor）读写配置。setter 会同步更新内部状态。
        /// </summary>
        public ApiClientCfg Config
        {
            get => apiClientCfg;
            set => apiClientCfg = value ?? new ApiClientCfg();
        }

        public AIApiClient()
        {
            httpClient = new HttpClient();
            apiClientCfg = new ApiClientCfg();
        }

        public AIApiClient(ApiClientCfg apiClientCfg)
        {
            httpClient = new HttpClient();
            this.apiClientCfg = apiClientCfg ?? new ApiClientCfg();
        }

        public AIApiClient(string apiKey) : this(new ApiClientCfg { ApiKey = apiKey })
        {
        }

        public AIApiClient SetApiKey(string apiKey)
        {
            apiClientCfg.ApiKey = apiKey;
            return this;
        }

        public AIApiClient SetEndpoint(string endpoint)
        {
            apiClientCfg.ApiEndpoint = endpoint;
            return this;
        }

        public AIApiClient SetModel(string model)
        {
            apiClientCfg.Model = model;
            return this;
        }

        // 参数调整方法（已改为返回 AIApiClient）
        public AIApiClient SetTemperature(double temperature)
        {
            apiClientCfg.Temperature = Math.Max(0, Math.Min(2, temperature));
            return this;
        }

        public AIApiClient SetMaxTokens(int? maxTokens)
        {
            apiClientCfg.MaxTokens = maxTokens;
            return this;
        }

        public AIApiClient SetTopP(double? topP)
        {
            apiClientCfg.TopP = topP;
            return this;
        }

        public AIApiClient SetFrequencyPenalty(double? frequencyPenalty)
        {
            apiClientCfg.FrequencyPenalty = frequencyPenalty;
            return this;
        }

        public AIApiClient SetPresencePenalty(double? presencePenalty)
        {
            apiClientCfg.PresencePenalty = presencePenalty;
            return this;
        }

        public AIApiClient SetStream(bool stream)
        {
            apiClientCfg.Stream = stream;
            return this;
        }

        public AIApiClient SetN(int n)
        {
            apiClientCfg.N = Math.Max(1, n);
            return this;
        }

        // 历史对话管理方法（操作运行时历史，不影响预设消息）
        public AIApiClient AddMessage(string role, string content, string? name = null)
        {
            apiClientCfg.ConversationHistory.Add(new Message
            {
                Role = role,
                Content = content,
                Name = name
            });
            return this;
        }

        public AIApiClient AddUserMessage(string content, string? name = null)
        {
            AddMessage("user", content, name);
            return this;
        }

        public AIApiClient AddAssistantMessage(string content, string? name = null)
        {
            AddMessage("assistant", content, name);
            return this;
        }

        public AIApiClient AddSystemMessage(string content, string? name = null)
        {
            AddMessage("system", content, name);
            return this;
        }

        public AIApiClient ClearConversationHistory()
        {
            apiClientCfg.ConversationHistory.Clear();
            return this;
        }

        /// <summary>
        /// 返回完整的消息列表：预设消息模板 + 运行时对话历史。
        /// </summary>
        public List<Message> GetConversationHistory()
        {
            var merged = new List<Message>(apiClientCfg.PresetMessages.Count + apiClientCfg.ConversationHistory.Count);
            merged.AddRange(apiClientCfg.PresetMessages);
            merged.AddRange(apiClientCfg.ConversationHistory);
            return merged;
        }

        public AIApiClient SetConversationHistory(List<Message> history)
        {
            apiClientCfg.ConversationHistory.Clear();
            apiClientCfg.ConversationHistory.AddRange(history);
            return this;
        }

        public AIApiClient RemoveLastMessage()
        {
            if (apiClientCfg.ConversationHistory.Count > 0)
            {
                apiClientCfg.ConversationHistory.RemoveAt(apiClientCfg.ConversationHistory.Count - 1);
            }
            return this;
        }

        public int GetHistoryCount()
        {
            return apiClientCfg.PresetMessages.Count + apiClientCfg.ConversationHistory.Count;
        }

        /// <summary>
        /// 流式请求。逐 chunk 回调 onDelta，返回拼接后的完整 content 文本。
        /// </summary>
        public async Task<string> StreamChatAsync(Action<ApiResponse> onDelta, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(onDelta);

            var apiRequest = new ApiRequest
            {
                Model = apiClientCfg.Model,
                Messages = GetConversationHistory(),
                Temperature = apiClientCfg.Temperature,
                MaxTokens = apiClientCfg.MaxTokens,
                TopP = apiClientCfg.TopP,
                FrequencyPenalty = apiClientCfg.FrequencyPenalty,
                PresencePenalty = apiClientCfg.PresencePenalty,
                Stream = apiClientCfg.Stream,
                N = apiClientCfg.N,
                ExtraBody = apiClientCfg.ExtraBody,
            };

            var json = JsonConvert.SerializeObject(apiRequest);

            using var request = new HttpRequestMessage(HttpMethod.Post, apiClientCfg.ApiEndpoint);
            if (!string.IsNullOrWhiteSpace(apiClientCfg.ApiKey))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiClientCfg.ApiKey);
            }
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(responseStream);

            var fullContent = new StringBuilder();

            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null) break;

                line = line.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                const string dataPrefix = "data: ";
                var payload = line.StartsWith(dataPrefix, StringComparison.Ordinal)
                    ? line.Substring(dataPrefix.Length)
                    : line;

                if (payload == "[DONE]") break;

                try
                {
                    var apiResponse = JsonConvert.DeserializeObject<ApiResponse>(payload);
                    if (apiResponse == null) continue;

                    // 累积 content 文本
                    if (apiResponse.Choices is { Count: > 0 })
                    {
                        var delta = apiResponse.Choices[0].Delta;
                        if (delta?.Content != null)
                            fullContent.Append(delta.Content);
                    }

                    onDelta(apiResponse);
                }
                catch (JsonReaderException)
                {
                    // 无法解析的 SSE 片段，跳过
                }
            }

            return fullContent.ToString();
        }

        public void Dispose()
        {
            httpClient?.Dispose();
        }
    }
}