using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine.Vision;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Client
{
    /// <summary>
    /// SiliconFlow 视觉模型实现。使用 OpenAI 兼容的 /v1/chat/completions 接口，
    /// 通过 base64 传图，调用 Qwen2.5-VL 系列模型生成图片描述。
    /// </summary>
    internal class SiliconFlowVisionProvider : IVisionProvider, IDisposable
    {
        private readonly HttpClient httpClient;
        private readonly string apiKey;
        private readonly string endpoint;
        private readonly string model;
        private readonly double temperature;
        private readonly int maxTokens;
        private readonly string? promptTemplate;

        public SiliconFlowVisionProvider(string apiKey,
            string endpoint = "https://api.siliconflow.cn/v1/chat/completions",
            string model = "Qwen/Qwen2.5-VL-72B-Instruct")
        {
            this.apiKey = apiKey;
            this.endpoint = endpoint;
            this.model = model;
            temperature = 0.3;
            maxTokens = 512;
            promptTemplate = null;
            httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        }

        public SiliconFlowVisionProvider(string apiKey, PhaseConfig config)
        {
            this.apiKey = apiKey;
            endpoint = config.Endpoint;
            model = config.Model;
            temperature = config.Temperature;
            maxTokens = config.MaxTokens;
            promptTemplate = config.PromptTemplate;
            httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        }

        public async Task<string> DescribeImageAsync(string imagePath, string? contextHint = null)
        {
            var imageBytes = await File.ReadAllBytesAsync(imagePath);
            var base64 = Convert.ToBase64String(imageBytes);
            var mimeType = GuessMimeType(imagePath);

            var promptText = BuildPrompt(contextHint);

            var messages = new JArray
            {
                new JObject
                {
                    ["role"] = "user",
                    ["content"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "text",
                            ["text"] = promptText
                        },
                        new JObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JObject
                            {
                                ["url"] = $"data:{mimeType};base64,{base64}"
                            }
                        }
                    }
                }
            };

            var body = new JObject
            {
                ["model"] = model,
                ["messages"] = messages,
                ["max_tokens"] = maxTokens,
                ["temperature"] = temperature
            };

            // Qwen3.6 等 thinking 模型需显式禁用，否则推理链消耗 token 导致 content 为空
            if (promptTemplate != null)
                body["enable_thinking"] = false;

            var json = body.ToString(Newtonsoft.Json.Formatting.None);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync();
            var responseObj = JObject.Parse(responseJson);

            var content = responseObj["choices"]?[0]?["message"]?["content"]?.ToString();
            return content ?? "[视觉模型未返回描述]";
        }

        private string BuildPrompt(string? contextHint)
        {
            // Phase 模式：prompt 由 VisionEngine 预填充后传入
            if (promptTemplate != null)
                return !string.IsNullOrEmpty(contextHint) ? contextHint : promptTemplate;

            // 旧模式：ctx.Vision 使用
            if (!string.IsNullOrEmpty(contextHint))
                return contextHint;

            return "用一两句中文描述这张图片中可见的内容。只描述你实际看到的视觉元素，不要推测含义、判断对错或补充图中没有的信息。如果有可见文字请提及。";
        }

        private static string GuessMimeType(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                _ => "image/jpeg"
            };
        }

        public void Dispose()
        {
            httpClient?.Dispose();
        }
    }
}
