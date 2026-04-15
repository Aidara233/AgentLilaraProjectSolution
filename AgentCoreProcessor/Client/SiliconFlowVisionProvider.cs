using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
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

        public SiliconFlowVisionProvider(string apiKey,
            string endpoint = "https://api.siliconflow.cn/v1/chat/completions",
            string model = "Qwen/Qwen2.5-VL-72B-Instruct")
        {
            this.apiKey = apiKey;
            this.endpoint = endpoint;
            this.model = model;
            httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        }

        public async Task<string> DescribeImageAsync(string imagePath, string? contextHint = null)
        {
            var imageBytes = await File.ReadAllBytesAsync(imagePath);
            var base64 = Convert.ToBase64String(imageBytes);
            var mimeType = GuessMimeType(imagePath);

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
                            ["text"] = BuildPrompt(contextHint)
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
                ["max_tokens"] = 512,
                ["temperature"] = 0.3
            };

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

        private static string BuildPrompt(string? contextHint)
        {
            if (string.IsNullOrEmpty(contextHint))
                return "请详细描述这张图片的内容。用中文回答，简洁但不遗漏关键信息。";

            return $"当前对话背景：{contextHint}\n\n请结合对话背景，描述这张图片中与对话相关的内容。如果图片与对话无关，则正常描述图片内容。用中文回答，简洁但不遗漏关键信息。";
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
