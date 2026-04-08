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

            var engine = new MasterEngine(adapterManager);

            adapterManager.OnMessageReceived += msg =>
            {
                _ = engine.HandleMessageAsync(msg);
            };

            await adapterManager.StartAllAsync();

            return 0;
        }
    }
}
