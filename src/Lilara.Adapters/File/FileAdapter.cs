using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Logging;
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
        public string Id { get; }
        public string Platform => "File";

        public event Action<IncomingMessage>? OnMessageReceived;

        private readonly string inputDir;
        private readonly string outputDir;
        private readonly int pollIntervalMs;
        private CancellationTokenSource? cts;
        private int outputSeq = 0;

        private AdapterConnectionState state = AdapterConnectionState.Stopped;
        private DateTime? startedAt;
        private long messagesSent;
        private long messagesReceived;

        private readonly Func<string, Task<(string LocalPath, string Hash)>>? _archive;

        /// <summary>未提供归档委托时跳过输入附件；宿主负责注入媒体存储。</summary>
        public FileAdapter(string id, string baseDir, int pollIntervalMs = 2000, string? outputDir = null,
            Func<string, Task<(string LocalPath, string Hash)>>? archive = null)
        {
            _archive = archive;
            Id = id;
            this.inputDir = Path.Combine(baseDir, "input");
            this.outputDir = string.IsNullOrWhiteSpace(outputDir)
                ? Path.Combine(baseDir, "output")
                : outputDir;
            this.pollIntervalMs = pollIntervalMs;
        }

        public Task<string?> SendMessageAsync(OutgoingMessage message)
        {
            var seq = Interlocked.Increment(ref outputSeq);
            var ts = DateTime.Now.ToString("HHmmss");
            var safeChannel = SanitizeFileName(message.ChannelId);
            var fileName = $"{seq:D3}_{safeChannel}_{ts}.txt";
            var content = $"[{DateTime.Now:HH:mm:ss}] channelId={message.ChannelId}\n{message.Content}\n";
            File.WriteAllText(Path.Combine(outputDir, fileName), content);
            Interlocked.Increment(ref messagesSent);
            // 返回输出文件名作为消息 id：消息已成功写入 output/ 目录。
            // 之前恒返回 null，会被 speak/send_file 等工具按 sentIds.Count==0 误判为"发送失败"，
            // 引发模型反复重试 speak（runaway）。文件名按 seq 自增唯一，可用于溯源回复文件。
            return Task.FromResult<string?>(fileName);
        }

        public async Task StartAsync(CancellationToken ct = default)
        {
            state = AdapterConnectionState.Connected;
            startedAt = DateTime.Now;
            cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            Directory.CreateDirectory(inputDir);
            Directory.CreateDirectory(outputDir);

            var parentCtx = SignalContext.Current;
            using var lifeCtx = Signal.Continue(
                SignalContext.NewSignalId(), parentCtx?.CurrentSpanId,
                $"adapter:{Id}", LogGroup.Adapter, "FileAdapter",
                new { id = Id });

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
                    Signal.Warn(LogGroup.Adapter, "FileAdapter轮询异常", new { id = Id, error = ex.Message });
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

            var files = GetInputFiles();
            int count = 0;
            foreach (var file in files)
            {
                if (count > 0 && delayMs > 0)
                    await Task.Delay(delayMs);

                if (ProcessOneFile(file))
                    count++;
            }
            return count;
        }

        private int ProcessInputFiles()
        {
            var files = GetInputFiles();
            int count = 0;
            foreach (var file in files)
            {
                if (ProcessOneFile(file))
                    count++;
            }
            return count;
        }

        /// <summary>处理单个输入文件。返回 true 表示成功投递消息。</summary>
        private bool ProcessOneFile(string file)
        {
            try
            {
                var raw = File.ReadAllText(file).Trim();
                if (string.IsNullOrEmpty(raw)) { File.Delete(file); return false; }

                IncomingMessage msg;
                if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    msg = ParseJsonMessage(raw);
                else
                    msg = BuildDefaultMessage(raw);

                File.Delete(file);
                OnMessageReceived?.Invoke(msg);
                Interlocked.Increment(ref messagesReceived);
                return true;
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Adapter, "文件消息解析失败", new { file = Path.GetFileName(file), error = ex.Message });
                try { File.Delete(file); } catch { }
                File.WriteAllText(
                    Path.Combine(outputDir, $"{Interlocked.Increment(ref outputSeq):D3}_error_{DateTime.Now:HHmmss}.txt"),
                    $"[{DateTime.Now:HH:mm:ss}] 解析失败: {Path.GetFileName(file)}\n{ex.Message}\n");
                return false;
            }
        }

        private List<string> GetInputFiles()
        {
            return Directory.GetFiles(inputDir)
                .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                .ToList();
        }

        private IncomingMessage ParseJsonMessage(string json)
        {
            var dto = JsonConvert.DeserializeObject<FileMessage>(json) ?? new FileMessage();

            // 解析附件（本地路径）
            List<MessageAttachment>? attachments = null;
            if (dto.Attachments != null)
            {
                foreach (var fa in dto.Attachments)
                {
                    if (_archive is null || string.IsNullOrEmpty(fa.Path) || !File.Exists(fa.Path)) continue;
                    var type = (fa.Type?.ToLowerInvariant()) switch
                    {
                        "image" => AttachmentType.Image,
                        "audio" => AttachmentType.Audio,
                        "video" => AttachmentType.Video,
                        _ => AttachmentType.File
                    };
                    try
                    {
                        var (localPath, imgHash) = _archive(fa.Path).GetAwaiter().GetResult();
                        attachments ??= new List<MessageAttachment>();
                        attachments.Add(new MessageAttachment
                        {
                            Type = type,
                            LocalPath = localPath,
                            FileName = Path.GetFileName(localPath),
                            Hash = imgHash
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
            state = AdapterConnectionState.Stopped;
            return Task.CompletedTask;
        }

        public AdapterStatus GetStatus() => new()
        {
            Id = Id,
            Platform = Platform,
            Enabled = state != AdapterConnectionState.Stopped,
            State = state,
            StartedAt = startedAt,
            MessagesSent = Interlocked.Read(ref messagesSent),
            MessagesReceived = Interlocked.Read(ref messagesReceived)
        };
    }
}
