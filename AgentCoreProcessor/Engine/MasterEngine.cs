using System;
using System.IO;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;

namespace AgentCoreProcessor.Engine
{
    internal class MasterEngine
    {
        private static readonly string DefaultDatabasePath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "Storage", "Database");

        private string databaseDirectory;
        private readonly AdapterManager adapterManager;

        public string DatabaseDirectory
        {
            get => databaseDirectory;
            set => databaseDirectory = value;
        }

        public MasterEngine(AdapterManager adapterManager, string? databaseDirectory = null)
        {
            this.adapterManager = adapterManager;
            this.databaseDirectory = databaseDirectory ?? DefaultDatabasePath;
        }

        public async Task HandleMessageAsync(IncomingMessage message)
        {
            // TODO: AuthService 鉴权
            // TODO: SessionManager 获取上下文

            try
            {
                var worker = new WorkerEngine(message, adapterManager);
                var result = await worker.RunAsync();

                await adapterManager.SendMessageAsync(message.Platform, new OutgoingMessage
                {
                    ChannelId = message.ChannelId,
                    Content = result
                });
            }
            catch (Exception ex)
            {
                await adapterManager.SendMessageAsync(message.Platform, new OutgoingMessage
                {
                    ChannelId = message.ChannelId,
                    Content = $"[错误] 处理消息时发生异常：{ex.Message}"
                });
            }
        }
    }
}
