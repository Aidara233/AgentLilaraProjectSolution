using AgentCoreProcessor.Models;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Client
{
    /// <summary>
    /// OpenAI 兼容协议实现（DeepSeek、SiliconFlow 等）。
    /// </summary>
    public class OpenAIModelClient : ModelClientBase
    {
        public OpenAIModelClient() : base() { }
        public OpenAIModelClient(ApiClientCfg cfg) : base(cfg) { }

        public override async Task<string> StreamChatAsync(Action<ApiResponse> onDelta, CancellationToken ct = default)
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
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiClientCfg.ApiKey);
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
    }
}