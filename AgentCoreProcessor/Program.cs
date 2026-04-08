using System;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Engine;

namespace AgentCoreProcessor
{
    class Program
    {
        private static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            var debug = Array.Exists(args, a => a == "--debug");

            var adapterManager = new AdapterManager();
            var consoleAdapter = new ConsoleAdapter();
            adapterManager.RegisterAdapter(consoleAdapter);

            var engine = new MasterEngine(adapterManager);

            if (debug)
            {
                Console.WriteLine("[调试模式] 输入一条消息，处理完毕后自动退出。");
                Console.Write("> ");
                var input = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(input))
                {
                    Console.WriteLine("[调试模式] 输入为空，退出。");
                    return 0;
                }

                var msg = new IncomingMessage
                {
                    Platform = "Console",
                    PlatformUserId = "debug-user",
                    ChannelId = "debug",
                    Content = input,
                    Time = DateTime.Now
                };

                try
                {
                    await engine.HandleMessageAsync(msg);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[调试模式] 异常详情：\n{ex}");
                }

                return 0;
            }

            // 正常模式
            adapterManager.OnMessageReceived += msg =>
            {
                _ = engine.HandleMessageAsync(msg);
            };

            await adapterManager.StartAllAsync();

            return 0;
        }
    }
}
