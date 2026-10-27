using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Client
{
    internal class SiliconFlowOcrProvider : IOcrProvider, IDisposable
    {
        private readonly HttpClient httpClient;
        private readonly string apiKey;
        private readonly string endpoint;
        private readonly string model;

        public string Name => "SiliconFlow-OCR";

        public SiliconFlowOcrProvider(string apiKey,
            string endpoint = "https://api.siliconflow.cn/v1/chat/completions",
            string model = "deepseek-ai/DeepSeek-OCR")
        {
            this.apiKey = apiKey;
            this.endpoint = endpoint;
            this.model = model;
            httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        }

        public async Task<OcrResult> RecognizeAsync(string imagePath)
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
                            ["type"] = "image_url",
                            ["image_url"] = new JObject
                            {
                                ["url"] = $"data:{mimeType};base64,{base64}"
                            }
                        },
                        new JObject
                        {
                            ["type"] = "text",
                            ["text"] = "OCR this image. Extract all visible text exactly as it appears. If there is no text in the image, respond with exactly: [NO_TEXT]"
                        }
                    }
                }
            };

            var body = new JObject
            {
                ["model"] = model,
                ["messages"] = messages,
                ["max_tokens"] = 2048,
                ["temperature"] = 0.1
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
            var content = responseObj["choices"]?[0]?["message"]?["content"]?.ToString()?.Trim();

            if (string.IsNullOrEmpty(content) || content.Contains("[NO_TEXT]"))
                return new OcrResult { HasText = false, Text = null };

            return new OcrResult { HasText = true, Text = content };
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

        public void Dispose() => httpClient?.Dispose();
    }
}
