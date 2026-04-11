using System;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Engine;

namespace AgentCoreProcessor
{
    class Program
    {
        private static async Task<int> Main(string[] args)
        {
            Console.InputEncoding = new UTF8Encoding(false);
            Console.OutputEncoding = Encoding.UTF8;

            PathConfig.Load();

            // debug 模式
            var debug = Array.Exists(args, a => a == "--debug");

            // 适配器管理器
            var adapterManager = new AdapterManager();

            // 适配器
            var consoleAdapter = new ConsoleAdapter();// 控制台适配器，主要调试用
            adapterManager.RegisterAdapter(consoleAdapter);// 注册

            // 主引擎
            var engine = new MasterEngine(adapterManager);

            // 初始化数据库（建表 + Repository）
            try
            {
                await engine.InitAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[错误] 数据库初始化失败：{ex.Message}");
                if (debug) Console.WriteLine(ex);
                return 1;
            }

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
