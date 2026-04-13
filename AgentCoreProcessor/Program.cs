using System;
using System.IO;
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

            var debug = Array.Exists(args, a => a == "--debug");
            var fileMode = Array.Exists(args, a => a == "--file");

            // 事件总线
            var eventBus = new EventBus();

            // 适配器管理器
            var adapterManager = new AdapterManager(eventBus);

            // 适配器
            if (fileMode)
            {
                var fileDir = Path.Combine(PathConfig.StoragePath, "FileAdapter");
                var fileAdapter = new FileAdapter(
                    Path.Combine(fileDir, "input.txt"),
                    Path.Combine(fileDir, "output.txt"),
                    pollIntervalMs: 3000);
                adapterManager.RegisterAdapter(fileAdapter);
            }
            else
            {
                var consoleAdapter = new ConsoleAdapter();
                adapterManager.RegisterAdapter(consoleAdapter);
            }

            // 主引擎
            var engine = new MasterEngine(adapterManager, eventBus);

            // 初始化数据库（建表 + Repository）+ 订阅事件总线
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
                    Time = DateTime.Now,
                    IsPrivate = true
                };

                try
                {
                    // debug 模式同步等待，直接调用 HandleEventAsync
                    await engine.HandleEventAsync(new MessageEvent { Message = msg, Time = msg.Time });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[调试模式] 异常详情：\n{ex}");
                }

                return 0;
            }

            // 正常模式：适配器消息已通过 EventBus 自动路由到 MasterEngine
            await adapterManager.StartAllAsync();

            return 0;
        }
    }
}
