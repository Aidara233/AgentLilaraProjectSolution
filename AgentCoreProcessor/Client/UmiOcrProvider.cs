using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Logging;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Client
{
    /// <summary>
    /// 本地 Umi-OCR (PaddleOCR) 提供者。通过 HTTP API 调用。
    /// Umi-OCR 需在后台运行，默认监听 127.0.0.1:1846。
    /// </summary>
    internal class UmiOcrProvider : IOcrProvider, IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl;

        public string Name => "Umi-OCR";

        public UmiOcrProvider(string host = "127.0.0.1", int port = 1846)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _baseUrl = $"http://{host}:{port}";
        }

        /// <summary>
        /// 健康检查：GET /api/ocr/get_options，成功返回 true。
        /// </summary>
        public async Task<bool> HealthCheckAsync()
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
                var response = await _http.GetAsync($"{_baseUrl}/api/ocr/get_options", cts.Token);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<OcrResult> RecognizeAsync(string imagePath)
        {
            var imageBytes = await File.ReadAllBytesAsync(imagePath);
            var base64 = Convert.ToBase64String(imageBytes);

            var body = new JObject
            {
                ["base64"] = base64,
                ["options"] = new JObject
                {
                    ["data.format"] = "text"
                }
            };

            var json = body.ToString(Newtonsoft.Json.Formatting.None);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/ocr");
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync();
            var obj = JObject.Parse(responseJson);
            var code = obj["code"]?.Value<int>() ?? -1;

            if (code == 100)
            {
                var text = obj["data"]?.ToString()?.Trim();
                if (string.IsNullOrEmpty(text))
                    return new OcrResult { HasText = false, Text = null };
                return new OcrResult { HasText = true, Text = text };
            }

            var errMsg = obj["data"]?.ToString() ?? "未知错误";
            Signal.Warn(LogGroup.Engine, "Umi-OCR返回错误", new { code, error = errMsg });
            return new OcrResult { HasText = false, Text = null };
        }

        public void Dispose() => _http?.Dispose();
    }
}
