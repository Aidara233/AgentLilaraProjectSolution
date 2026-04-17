using System;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Adapter
{
    public class ConsoleAdapter : IAdapter
    {
        public string Platform => "Console";

        public event Action<IncomingMessage>? OnMessageReceived;

        private CancellationTokenSource? cts;

        public Task<string?> SendMessageAsync(OutgoingMessage message)
        {
            Console.WriteLine(message.Content);
            return Task.FromResult<string?>(null);
        }

        public async Task StartAsync(CancellationToken ct = default)
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            Console.WriteLine("[ConsoleAdapter] 已启动，输入消息开始对话，输入 exit 退出。");

            while (!cts.Token.IsCancellationRequested)
            {
                Console.Write("> ");
                var line = await Task.Run(() => Console.ReadLine(), cts.Token).ConfigureAwait(false);

                if (line == null || line.Equals("exit", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line)) continue;

                var msg = new IncomingMessage
                {
                    Platform = Platform,
                    PlatformUserId = "console-user",
                    ChannelId = "console",
                    Content = line,
                    Time = DateTime.Now,
                    IsPrivate = true
                };

                OnMessageReceived?.Invoke(msg);
            }

            Console.WriteLine("[ConsoleAdapter] 已停止。");
        }

        public Task StopAsync()
        {
            cts?.Cancel();
            return Task.CompletedTask;
        }
    }
}
