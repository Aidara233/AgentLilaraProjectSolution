using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Adapter
{
    /// <summary>
    /// 文件适配器。轮询输入文件，有内容就作为消息处理后清空；输出追加到输出文件。
    /// 用于自动化测试和外部工具交互。
    /// </summary>
    public class FileAdapter : IAdapter
    {
        public string Platform => "File";

        public event Action<IncomingMessage>? OnMessageReceived;

        private readonly string inputPath;
        private readonly string outputPath;
        private readonly int pollIntervalMs;
        private CancellationTokenSource? cts;

        public FileAdapter(string inputPath, string outputPath, int pollIntervalMs = 3000)
        {
            this.inputPath = inputPath;
            this.outputPath = outputPath;
            this.pollIntervalMs = pollIntervalMs;
        }

        public Task SendMessageAsync(OutgoingMessage message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message.Content}{Environment.NewLine}";
            File.AppendAllText(outputPath, line);
            return Task.CompletedTask;
        }

        public async Task StartAsync(CancellationToken ct = default)
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // 确保文件存在
            Directory.CreateDirectory(Path.GetDirectoryName(inputPath)!);
            if (!File.Exists(inputPath)) File.WriteAllText(inputPath, "");
            if (!File.Exists(outputPath)) File.WriteAllText(outputPath, "");

            File.AppendAllText(outputPath,
                $"[{DateTime.Now:HH:mm:ss}] [FileAdapter] 已启动，轮询 {inputPath}{Environment.NewLine}");

            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(pollIntervalMs, cts.Token);

                    var content = File.ReadAllText(inputPath).Trim();
                    if (string.IsNullOrEmpty(content)) continue;

                    // 清空输入文件
                    File.WriteAllText(inputPath, "");

                    var msg = new IncomingMessage
                    {
                        Platform = Platform,
                        PlatformUserId = "file-user",
                        ChannelId = "file",
                        Content = content,
                        Time = DateTime.Now,
                        IsPrivate = true
                    };

                    OnMessageReceived?.Invoke(msg);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    File.AppendAllText(outputPath,
                        $"[{DateTime.Now:HH:mm:ss}] [FileAdapter] 错误: {ex.Message}{Environment.NewLine}");
                }
            }

            File.AppendAllText(outputPath,
                $"[{DateTime.Now:HH:mm:ss}] [FileAdapter] 已停止{Environment.NewLine}");
        }

        public Task StopAsync()
        {
            cts?.Cancel();
            return Task.CompletedTask;
        }
    }
}
