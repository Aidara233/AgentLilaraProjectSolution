using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Adapter
{
    /// <summary>
    /// 文件适配器。轮询 input/ 目录读取消息文件，回复写入 output/ 目录。
    /// 支持 .json（带元数据）和 .txt（纯文本，缺省元数据）两种输入格式。
    /// 用于自动化测试和 AI 调试。
    /// </summary>
    public class FileAdapter : IAdapter
    {
        public string Platform => "File";

        public event Action<IncomingMessage>? OnMessageReceived;

        private readonly string inputDir;
        private readonly string outputDir;
        private readonly int pollIntervalMs;
        private CancellationTokenSource? cts;
        private int outputSeq = 0;

        public FileAdapter(string baseDir, int pollIntervalMs = 2000)
        {
            this.inputDir = Path.Combine(baseDir, "input");
            this.outputDir = Path.Combine(baseDir, "output");
            this.pollIntervalMs = pollIntervalMs;
        }

        public Task SendMessageAsync(OutgoingMessage message)
        {
            var seq = Interlocked.Increment(ref outputSeq);
            var ts = DateTime.Now.ToString("HHmmss");
            var safeChannel = SanitizeFileName(message.ChannelId);
            var fileName = $"{seq:D3}_{safeChannel}_{ts}.txt";
            var content = $"[{DateTime.Now:HH:mm:ss}] channelId={message.ChannelId}\n{message.Content}\n";
            File.WriteAllText(Path.Combine(outputDir, fileName), content);
            return Task.CompletedTask;
        }

        public async Task StartAsync(CancellationToken ct = default)
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            Directory.CreateDirectory(inputDir);
            Directory.CreateDirectory(outputDir);

            // 启动标记
            File.WriteAllText(
                Path.Combine(outputDir, $"000_system_{DateTime.Now:HHmmss}.txt"),
                $"[{DateTime.Now:HH:mm:ss}] FileAdapter 已启动\n轮询目录: {inputDir}\n");

            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(pollIntervalMs, cts.Token);
                    ProcessInputFiles();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    File.WriteAllText(
                        Path.Combine(outputDir, $"{Interlocked.Increment(ref outputSeq):D3}_error_{DateTime.Now:HHmmss}.txt"),
                        $"[{DateTime.Now:HH:mm:ss}] FileAdapter 错误: {ex.Message}\n");
                }
            }
        }

        /// <summary>一次性读取 input 目录所有文件并投递消息。返回处理的消息数。</summary>
        public int ProcessInputOnce()
        {
            Directory.CreateDirectory(inputDir);
            Directory.CreateDirectory(outputDir);
            return ProcessInputFiles();
        }

        /// <summary>逐条投递消息，每条之间等待指定毫秒。用于模拟真实对话节奏。</summary>
        public async Task<int> ProcessInputWithDelayAsync(int delayMs)
        {
            Directory.CreateDirectory(inputDir);
            Directory.CreateDirectory(outputDir);

            var files = Directory.GetFiles(inputDir)
                .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                             || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                .ToList();

            int count = 0;
            foreach (var file in files)
            {
                if (count > 0 && delayMs > 0)
                    await Task.Delay(delayMs);

                try
                {
                    var raw = File.ReadAllText(file).Trim();
                    if (string.IsNullOrEmpty(raw)) { File.Delete(file); continue; }

                    IncomingMessage msg;
                    if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        msg = ParseJsonMessage(raw);
                    else
                        msg = BuildDefaultMessage(raw);

                    File.Delete(file);
                    OnMessageReceived?.Invoke(msg);
                    count++;
                }
                catch (Exception ex)
                {
                    try { File.Delete(file); } catch { }
                    File.WriteAllText(
                        Path.Combine(outputDir, $"{Interlocked.Increment(ref outputSeq):D3}_error_{DateTime.Now:HHmmss}.txt"),
                        $"[{DateTime.Now:HH:mm:ss}] 解析失败: {Path.GetFileName(file)}\n{ex.Message}\n");
                }
            }
            return count;
        }

        private int ProcessInputFiles()
        {
            var files = Directory.GetFiles(inputDir)
                .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                .ToList();

            int count = 0;
            foreach (var file in files)
            {
                try
                {
                    var raw = File.ReadAllText(file).Trim();
                    if (string.IsNullOrEmpty(raw)) { File.Delete(file); continue; }

                    IncomingMessage msg;
                    if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        msg = ParseJsonMessage(raw);
                    else
                        msg = BuildDefaultMessage(raw);

                    File.Delete(file);
                    OnMessageReceived?.Invoke(msg);
                    count++;
                }
                catch (Exception ex)
                {
                    // 解析失败的文件也删除，避免反复报错
                    try { File.Delete(file); } catch { }
                    File.WriteAllText(
                        Path.Combine(outputDir, $"{Interlocked.Increment(ref outputSeq):D3}_error_{DateTime.Now:HHmmss}.txt"),
                        $"[{DateTime.Now:HH:mm:ss}] 解析失败: {Path.GetFileName(file)}\n{ex.Message}\n");
                }
            }
            return count;
        }

        private static IncomingMessage ParseJsonMessage(string json)
        {
            var dto = JsonConvert.DeserializeObject<FileMessage>(json) ?? new FileMessage();

            // 解析附件（本地路径）
            List<MessageAttachment>? attachments = null;
            if (dto.Attachments != null)
            {
                foreach (var fa in dto.Attachments)
                {
                    if (string.IsNullOrEmpty(fa.Path) || !File.Exists(fa.Path)) continue;
                    var type = (fa.Type?.ToLowerInvariant()) switch
                    {
                        "image" => AttachmentType.Image,
                        "audio" => AttachmentType.Audio,
                        "video" => AttachmentType.Video,
                        _ => AttachmentType.File
                    };
                    try
                    {
                        var localPath = ImageStorage.CopyToStorageAsync(fa.Path).GetAwaiter().GetResult();
                        attachments ??= new List<MessageAttachment>();
                        attachments.Add(new MessageAttachment
                        {
                            Type = type,
                            LocalPath = localPath,
                            FileName = Path.GetFileName(localPath)
                        });
                    }
                    catch { /* 复制失败跳过 */ }
                }
            }

            return new IncomingMessage
            {
                Platform = dto.Platform ?? "File",
                PlatformUserId = dto.UserId ?? "file-user",
                ChannelId = dto.ChannelId ?? "file",
                Content = dto.Content ?? "",
                DisplayName = dto.DisplayName,
                Nickname = dto.Nickname,
                IsPrivate = dto.IsPrivate ?? true,
                IsMentioned = dto.IsMentioned ?? false,
                ReplyTo = dto.ReplyTo,
                Time = !string.IsNullOrEmpty(dto.Time) && DateTime.TryParse(dto.Time, out var parsed) ? parsed : DateTime.Now,
                Attachments = attachments
            };
        }

        private static IncomingMessage BuildDefaultMessage(string content)
        {
            return new IncomingMessage
            {
                Platform = "File",
                PlatformUserId = "file-user",
                ChannelId = "file",
                Content = content,
                IsPrivate = true,
                Time = DateTime.Now
            };
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }

        private class FileMessage
        {
            public string? Platform { get; set; }
            public string? UserId { get; set; }
            public string? ChannelId { get; set; }
            public string? Content { get; set; }
            public string? DisplayName { get; set; }
            public string? Nickname { get; set; }
            public bool? IsPrivate { get; set; }
            public bool? IsMentioned { get; set; }
            public string? ReplyTo { get; set; }
            public string? Time { get; set; }
            public List<FileAttachment>? Attachments { get; set; }
        }

        private class FileAttachment
        {
            public string? Type { get; set; }
            public string? Path { get; set; }
        }

        public Task StopAsync()
        {
            cts?.Cancel();
            return Task.CompletedTask;
        }
    }
}
