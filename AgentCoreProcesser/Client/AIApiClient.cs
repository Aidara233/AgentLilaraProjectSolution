using AgentCoreProcesser.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcesser.Client
{
    public class AIApiClient : IDisposable
    {
        public HttpClient httpClient;
        public ApiClientCfg apiClientCfg;

        public AIApiClient()
        {
            httpClient = new HttpClient();
            apiClientCfg = new ApiClientCfg();
            if (!string.IsNullOrWhiteSpace(apiClientCfg.ApiKey))
            {
                httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiClientCfg.ApiKey}");
            }
        }

        public AIApiClient(ApiClientCfg apiClientCfg)
        {
            httpClient = new HttpClient();
            this.apiClientCfg = apiClientCfg ?? new ApiClientCfg();
            if (!string.IsNullOrWhiteSpace(this.apiClientCfg.ApiKey))
            {
                httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {this.apiClientCfg.ApiKey}");
            }
        }

        public AIApiClient(string apiKey) : this(new ApiClientCfg { ApiKey = apiKey })
        {
        }

        // API配置相关方法（已改为返回 AIApiClient 以支持链式调用）
        public AIApiClient SetApiKey(string apiKey)
        {
            apiClientCfg.ApiKey = apiKey;
            httpClient.DefaultRequestHeaders.Remove("Authorization");
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            }
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

        // 历史对话管理方法
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

        public List<Message> GetConversationHistory()
        {
            return new List<Message>(apiClientCfg.ConversationHistory);
        }

        // SetConversationHistory 也改为返回 AIApiClient 以支持链式调用
        public AIApiClient SetConversationHistory(List<Message> history)
        {
            apiClientCfg.ConversationHistory.Clear();
            apiClientCfg.ConversationHistory.AddRange(history);
            return this;
        }

        public void RemoveLastMessage()
        {
            if (apiClientCfg.ConversationHistory.Count > 0)
            {
                apiClientCfg.ConversationHistory.RemoveAt(apiClientCfg.ConversationHistory.Count - 1);
            }
        }

        public int GetHistoryCount()
        {
            return apiClientCfg.ConversationHistory.Count;
        }

        // 流式响应处理（使用 Newtonsoft.Json.Linq 替代 System.Text.Json）
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
                N = apiClientCfg.N
            };

            var jsonConvert = JsonConvert.SerializeObject(apiRequest);

            using var request = new HttpRequestMessage(HttpMethod.Post, apiClientCfg.ApiEndpoint);
            if (!string.IsNullOrWhiteSpace(apiClientCfg.ApiKey))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiClientCfg.ApiKey);
            }
            request.Content = new StringContent(jsonConvert, Encoding.UTF8, "application/json");

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(responseStream);

            var fullBuilder = new StringBuilder();
            // OpenAI stream uses SSE-like format: lines like "data: {...}\n\n"
            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null) break;

                line = line.Trim();
                if (string.IsNullOrEmpty(line))
                {
                    // 空行表示一个事件结束，继续等待下一事件
                    continue;
                }

                const string dataPrefix = "data: ";
                if (!line.StartsWith(dataPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    // 有时可能不会有 "data: " 前缀，尝试直接处理
                }

                var payload = line.StartsWith(dataPrefix) ? line.Substring(dataPrefix.Length) : line;

                if (payload == "[DONE]")
                {
                    break;
                }

                try
                {
                    ApiResponse? apiResponse = JsonConvert.DeserializeObject<ApiResponse>(payload);
                    if (apiResponse != null)
                    {
                        onDelta(apiResponse);
                    }
                }
                catch (JsonReaderException)
                {
                    // 忽略无法解析的片段（保持与原来忽略 JsonException 的行为）
                }
                catch (Exception)
                {
                    // 其他解析或运行时错误也忽略，以保持流式处理的健壮性
                }
            }

            return fullBuilder.ToString();
        }

        public void Dispose()
        {
            httpClient?.Dispose();
        }
    }
}