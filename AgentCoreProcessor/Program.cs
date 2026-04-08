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

            var adapterManager = new AdapterManager();
            var consoleAdapter = new ConsoleAdapter();
            adapterManager.RegisterAdapter(consoleAdapter);

            var engine = new MasterEngine();

            // 暂时简单处理：收到消息后回显确认，为后续 MasterEngine 调度做准备
            adapterManager.OnMessageReceived += msg =>
            {
                Console.WriteLine($"[收到消息] 平台={msg.Platform}, 用户={msg.PlatformUserId}, 频道={msg.ChannelId}, 内容={msg.Content}");
            };

            await adapterManager.StartAllAsync();

            return 0;
        }
    }
}
