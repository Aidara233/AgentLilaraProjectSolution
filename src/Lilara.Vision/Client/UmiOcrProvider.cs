using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Logging;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Client
{
    /// <summary>
    /// 本地 Umi-OCR (PaddleOCR) 提供者。通过 HTTP API 调用。
    /// 支持自动启动 Umi-OCR.exe 进程，并在 Dispose 时关闭。
    /// </summary>
    internal class UmiOcrProvider : IOcrProvider, IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private Process? _process;
        private bool _ownsProcess;

        public string Name => "Umi-OCR";

        /// <summary>
        /// 创建 UmiOcrProvider。如需自动启动进程，使用 <see cref="CreateWithAutoStart"/>。
        /// </summary>
        public UmiOcrProvider(string host = "127.0.0.1", int port = 1846)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _baseUrl = $"http://{host}:{port}";
        }

        /// <summary>
        /// 尝试自动启动 Umi-OCR 进程并等待就绪。
        /// </summary>
        /// <param name="exePath">Umi-OCR.exe 的完整路径</param>
        /// <param name="host">监听地址</param>
        /// <param name="port">监听端口</param>
        /// <returns>成功返回已就绪的 provider，失败返回 null</returns>
        public static async Task<UmiOcrProvider?> CreateWithAutoStartAsync(
            string exePath, string host = "127.0.0.1", int port = 1846)
        {
            var provider = new UmiOcrProvider(host, port);

            // 已在运行
            if (await provider.HealthCheckAsync())
            {
                Signal.Event(LogGroup.Engine, "Umi-OCR已在运行，无需启动",
                    new { host, port });
                return provider;
            }

            if (!File.Exists(exePath))
            {
                Signal.Warn(LogGroup.Engine, "Umi-OCR.exe 不存在，无法自动启动",
                    new { exePath });
                provider.Dispose();
                return null;
            }

            Signal.Event(LogGroup.Engine, "正在启动Umi-OCR进程...", new { exePath });

            try
            {
                var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = exePath,
                        WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    },
                    EnableRaisingEvents = true
                };

                proc.Start();

                // 轮询等待 HTTP 服务就绪（最多 30 秒）
                for (int i = 0; i < 60; i++)
                {
                    await Task.Delay(500);
                    if (proc.HasExited)
                    {
                        var errOutput = await proc.StandardError.ReadToEndAsync();
                        Signal.Warn(LogGroup.Engine, "Umi-OCR进程意外退出",
                            new { exitCode = proc.ExitCode, stderr = errOutput });
                        provider.Dispose();
                        proc.Dispose();
                        return null;
                    }

                    if (await provider.HealthCheckAsync())
                    {
                        provider._process = proc;
                        provider._ownsProcess = true;
                        proc.Exited += (_, _) =>
                            Signal.Warn(LogGroup.Engine, "Umi-OCR进程已退出",
                                new { exitCode = proc.ExitCode });
                        Signal.Event(LogGroup.Engine, "Umi-OCR进程已启动并就绪",
                            new { pid = proc.Id, host, port });
                        return provider;
                    }
                }

                Signal.Warn(LogGroup.Engine, "Umi-OCR启动超时（30秒未就绪）");
                try { proc.Kill(true); } catch { }
                proc.Dispose();
                provider.Dispose();
                return null;
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "Umi-OCR进程启动失败",
                    new { error = ex.Message });
                provider.Dispose();
                return null;
            }
        }

        public async Task<bool> HealthCheckAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
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

        public void Dispose()
        {
            _http?.Dispose();
            if (_ownsProcess && _process != null)
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        Signal.Event(LogGroup.Engine, "正在关闭Umi-OCR进程",
                            new { pid = _process.Id });
                        _process.Kill(true);
                    }
                }
                catch { }
                _process.Dispose();
            }
        }
    }
}
